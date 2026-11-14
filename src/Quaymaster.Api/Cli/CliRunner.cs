using System.Security.Cryptography;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Infrastructure.Security;

namespace Quaymaster.Api.Cli;

/// <summary>
/// Operator helper commands (no Docker access, no network, no database) - parity with the original cli.ts:
/// <code>
/// gen-secret                  print a fresh QM_SECRET_KEY line
/// hash-password [password]    print QM_ADMIN_PASSWORD_HASH='...' (password from the argument, stdin, or a hidden prompt)
/// </code>
/// Run with <c>dotnet run --project src/Quaymaster.Api -- gen-secret</c> (or the published binary).
/// </summary>
public static class CliRunner
{
    private static readonly string[] Commands = ["gen-secret", "hash-password"];

    public static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 && Commands.Contains(args[0], StringComparer.Ordinal);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> env,
        Func<string?> promptPassword)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(promptPassword);
        try
        {
            switch (args.Count > 0 ? args[0] : null)
            {
                case "gen-secret":
                    await stdout.WriteLineAsync($"QM_SECRET_KEY={Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}");
                    return 0;

                case "hash-password":
                    var password = args.Count > 1 ? args[1] : promptPassword() ?? (await stdin.ReadToEndAsync()).TrimEnd('\r', '\n');
                    PasswordPolicy.ValidateStrength(password);
                    var log2N = int.TryParse(env("QM_SCRYPT_LOG2_N"), out var parsed) ? parsed : 17;
                    if (log2N is < 10 or > 20)
                    {
                        throw new ArgumentOutOfRangeException(nameof(env), "QM_SCRYPT_LOG2_N must be between 10 and 20");
                    }

                    using (var hasher = new ScryptPasswordHasher(log2N))
                    {
                        // single quotes: the hash contains `$`, which Docker Compose and shells would otherwise expand
                        await stdout.WriteLineAsync($"QM_ADMIN_PASSWORD_HASH='{await hasher.HashAsync(password)}'");
                    }

                    return 0;

                default:
                    await stderr.WriteLineAsync("usage: quaymaster gen-secret | hash-password [password]");
                    return args.Count > 0 ? 2 : 0;
            }
        }
        catch (Exception e) when (e is AppException or ArgumentException)
        {
            await stderr.WriteLineAsync($"error: {e.Message}");
            return 1;
        }
    }
}
