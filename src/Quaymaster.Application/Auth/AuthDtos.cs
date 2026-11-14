using System.Globalization;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Application.Auth;

// ---- responses (wire shape = shared/src/api.ts) -------------------------------------------------

public sealed record UserInfoDto(long Id, string Username, string Role, bool LogsRead, bool Disabled, string CreatedAt)
{
    public static UserInfoDto From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserInfoDto(
            user.Id,
            user.Username,
            user.Role.ToWire(),
            user.EffectiveLogsRead,
            user.Disabled,
            Iso.Format(user.CreatedAt));
    }
}

public sealed record AuthStatusDto(bool SetupRequired, bool Authenticated);

public sealed record SessionInfoDto(UserInfoDto User, string CsrfToken, string ExpiresAt, string IdleExpiresAt);

public sealed record OkDto(bool Ok = true);

public sealed record OkNoteDto(bool Ok, string Note);

// ---- requests (strict: unknown properties are rejected; every field optional here so the validators
// ---- can report "Required" like the original zod schemas) ------------------------------------------------

public sealed record LoginRequest(string? Username, string? Password);

public sealed record SetupRequest(string? Token, string? Username, string? Password);

public sealed record ChangePasswordRequest(string? Current, string? Next);

public sealed record CreateUserRequest(string? Username, string? Password, string? Role, bool? LogsRead);

public sealed record PatchUserRequest(string? Role, bool? LogsRead, bool? Disabled);

public sealed record SetPasswordRequest(string? Password);

/// <summary>The caller of a use case, for the audit trail.</summary>
public sealed record Actor(string Name, string? Ip);

/// <summary>JavaScript-style ISO-8601 timestamps (<c>2026-01-02T03:04:05.678Z</c>), as the original emits.</summary>
public static class Iso
{
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
