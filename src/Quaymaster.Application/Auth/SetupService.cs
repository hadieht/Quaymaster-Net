using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Security;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Identity;
using Quaymaster.Application.Users;

namespace Quaymaster.Application.Auth;

public enum SetupOutcome
{
    AlreadyInitialised,
    EnvAdminCreated,
    SetupTokenIssued,
}

public sealed record SetupOptions(AdminBootstrap? Admin);

public interface ISetupService
{
    /// <summary>True while the users table is empty.</summary>
    Task<bool> IsRequiredAsync(CancellationToken ct);

    /// <summary>Startup: creates the env admin or issues the one-time token.</summary>
    Task<SetupOutcome> InitializeAsync(CancellationToken ct);

    /// <summary>Completes first-run setup, consuming the token, and logs the new admin in.</summary>
    Task<(string Token, AuthContext Context)> CompleteAsync(
        string token, string username, string password, string ip, string? userAgent, CancellationToken ct);
}

/// <summary>
/// First-run lifecycle. If an admin is configured through the environment and the users table is empty, that admin
/// is created (once; a password changed in the UI is never reverted by a restart). Otherwise a one-time setup token is
/// announced on stderr (outside the logging pipeline, so redaction cannot mask it), written to
/// <c>&lt;data&gt;/setup-token</c> (0600), valid for 60 minutes, 5 attempts per IP, deleted on success. A new token is
/// generated on every start and after expiry.
/// </summary>
public sealed class SetupService : ISetupService, IDisposable
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(60);

    private readonly IUserRepository _users;
    private readonly IUserService _userService;
    private readonly ISessionService _sessions;
    private readonly IPasswordHasher _hasher;
    private readonly IAuditLog _audit;
    private readonly ISetupTokenStore _store;
    private readonly ISetupAnnouncer _announcer;
    private readonly IFileReader _files;
    private readonly SetupOptions _options;
    private readonly TimeProvider _time;
    private readonly AttemptBudget _budget;
    private readonly Lock _gate = new();
    private string? _token;
    private DateTimeOffset _issuedAt;
    private ITimer? _renewal;

    public SetupService(
        IUserRepository users,
        IUserService userService,
        ISessionService sessions,
        IPasswordHasher hasher,
        IAuditLog audit,
        ISetupTokenStore store,
        ISetupAnnouncer announcer,
        IFileReader files,
        SetupOptions options,
        TimeProvider time)
    {
        _users = users;
        _userService = userService;
        _sessions = sessions;
        _hasher = hasher;
        _audit = audit;
        _store = store;
        _announcer = announcer;
        _files = files;
        _options = options;
        _time = time;
        _budget = new AttemptBudget(5, TokenLifetime, time);
    }

    public async Task<bool> IsRequiredAsync(CancellationToken ct) => await _users.CountAsync(ct) == 0;

    public async Task<SetupOutcome> InitializeAsync(CancellationToken ct)
    {
        if (!await IsRequiredAsync(ct))
        {
            _store.Delete();
            return SetupOutcome.AlreadyInitialised;
        }

        if (_options.Admin is { } admin)
        {
            string hash;
            if (admin.PasswordHash is { } configured)
            {
                if (!_hasher.IsValidHash(configured))
                {
                    throw new InvalidOperationException(
                        "QM_ADMIN_PASSWORD_HASH is not a valid hash (expected scrypt$N$r$p$saltBase64$hashBase64)");
                }

                hash = configured;
            }
            else
            {
                var password = _files.ReadAllText(admin.PasswordFile!).TrimEnd('\r', '\n');
                PasswordPolicy.ValidateStrength(password);
                hash = await _hasher.HashAsync(password, ct);
            }

            await _userService.CreateAccountWithHashAsync(admin.User, hash, Role.Admin, ct);
            return SetupOutcome.EnvAdminCreated;
        }

        Issue();
        return SetupOutcome.SetupTokenIssued;
    }

    private void Issue()
    {
        lock (_gate)
        {
            _token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
            _issuedAt = _time.GetUtcNow();
            var file = _store.Write(_token);
            _announcer.Announce(
                $"{Environment.NewLine}=== Quaymaster first-run setup ==={Environment.NewLine}" +
                $"One-time setup token (valid 60 minutes): {_token}{Environment.NewLine}" +
                $"Also written to {file}. Open the setup page and enter it together with your new admin credentials.{Environment.NewLine}{Environment.NewLine}");
            _renewal?.Dispose();
            _renewal = _time.CreateTimer(
                _ => OnRenewal(),
                null,
                TokenLifetime + TimeSpan.FromSeconds(1),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void OnRenewal()
    {
        try
        {
            if (_users.CountAsync(CancellationToken.None).GetAwaiter().GetResult() == 0)
            {
                Issue();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Database / file system unavailable: the next restart issues a fresh token.
        }
    }

    public async Task<(string Token, AuthContext Context)> CompleteAsync(
        string token, string username, string password, string ip, string? userAgent, CancellationToken ct)
    {
        if (!await IsRequiredAsync(ct))
        {
            throw AppException.Conflict("Setup has already been completed");
        }
        _budget.Take(ip);

        string claimed;
        lock (_gate)
        {
            if (_token is null || _time.GetUtcNow() - _issuedAt > TokenLifetime)
            {
                throw new AppException(403, ErrorCode.Forbidden, "Setup token expired; restart Quaymaster to issue a new one");
            }

            if (!ConstantTimeEquals(token, _token))
            {
                throw AppException.Forbidden("Invalid setup token");
            }

            // Claim the token BEFORE any await (hashing yields), so concurrent redemptions of the same token
            // cannot each create an admin. Restored below if creation fails.
            claimed = _token;
            _token = null;
        }

        try
        {
            var user = await _userService.CreateAccountAsync(username, password, Role.Admin, false, ct);
            _store.Delete();
            lock (_gate)
            {
                _renewal?.Dispose();
                _renewal = null;
            }

            await _audit.RecordAsync(new AuditRecord(user.Username, "auth.setup", AuditOutcome.Success, ip), ct);
            return await _sessions.CreateAsync(user, ip, userAgent, ct);
        }
        catch
        {
            if (await IsRequiredAsync(CancellationToken.None))
            {
                lock (_gate)
                {
                    _token ??= claimed;
                }
            }

            throw;
        }
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _renewal?.Dispose();
            _renewal = null;
        }
    }
}
