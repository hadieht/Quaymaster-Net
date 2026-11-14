namespace Quaymaster.Domain.Identity;

/// <summary>A dashboard account. Mutated only through the methods below so the invariants live in one place.</summary>
public sealed class User
{
    // EF Core materialisation.
    private User() { }

    public User(string username, string passwordHash, Role role, bool logsRead, DateTimeOffset now)
    {
        Username = username;
        PasswordHash = passwordHash;
        Role = role;
        LogsRead = logsRead;
        CreatedAt = now;
        PasswordChangedAt = now;
    }

    public long Id { get; private set; }
    public string Username { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public Role Role { get; private set; }

    /// <summary>The stored flag. Use <see cref="EffectiveLogsRead"/> for what the account may actually do.</summary>
    public bool LogsRead { get; private set; }

    public bool Disabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset PasswordChangedAt { get; private set; }

    /// <summary>Admins can always read logs; viewers only when granted (default off).</summary>
    public bool EffectiveLogsRead => Role == Role.Admin || LogsRead;

    /// <summary>An enabled administrator: the population the "last admin" rule protects.</summary>
    public bool IsActiveAdmin => Role == Role.Admin && !Disabled;

    public bool Can(Capability permission) => Role == Role.Admin || permission switch
    {
        Capability.Read => true,
        Capability.Logs => LogsRead,
        _ => false,
    };

    public void Apply(UserPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Role is { } role)
        {
            Role = role;
        }

        if (patch.LogsRead is { } logsRead)
        {
            LogsRead = logsRead;
        }

        if (patch.Disabled is { } disabled)
        {
            Disabled = disabled;
        }
    }

    public void SetPasswordHash(string hash, DateTimeOffset now)
    {
        PasswordHash = hash;
        PasswordChangedAt = now;
    }
}

/// <summary>A partial update of the privileged attributes of a user (all optional).</summary>
public sealed record UserPatch(Role? Role = null, bool? LogsRead = null, bool? Disabled = null)
{
    /// <summary>True when applying the patch changes anything privilege-related (=> sessions are revoked).</summary>
    public bool IsEmpty => Role is null && LogsRead is null && Disabled is null;
}
