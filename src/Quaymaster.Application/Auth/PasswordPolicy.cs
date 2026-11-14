using System.Text.RegularExpressions;
using Quaymaster.Application.Errors;

namespace Quaymaster.Application.Auth;

/// <summary>Username and password rules enforced by the use cases (in addition to the request validators).</summary>
public static partial class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 1024;

    public static void ValidateStrength(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinLength)
        {
            throw AppException.BadRequest($"Password must be at least {MinLength} characters long");
        }

        if (password.Length > MaxLength)
        {
            throw AppException.BadRequest("Password is too long");
        }
    }

    public static void ValidateUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        if (!UsernameRegex().IsMatch(username))
        {
            throw AppException.BadRequest("Username must be 1-64 characters: letters, digits, . _ @ -");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._@-]{1,64}$")]
    private static partial Regex UsernameRegex();
}
