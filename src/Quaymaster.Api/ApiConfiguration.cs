using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Configuration;

namespace Quaymaster.Api;

/// <summary>Bridges <see cref="IConfiguration"/> (environment variables first, appsettings.json for non-secrets) to <see cref="QmConfig"/>.</summary>
public static class ApiConfiguration
{
    /// <summary>The environment name in which QM_SCRYPT_LOG2_N below 14 is accepted (fast hashing for tests).</summary>
    public const string TestingEnvironment = "Testing";

    public static QmConfig Load(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Only top-level QM_* keys; the environment-variable provider and appsettings.json both land here.
        var variables = configuration.GetChildren()
            .Where(c => c.Key.StartsWith("QM_", StringComparison.Ordinal) && c.Value is not null)
            .ToDictionary(c => c.Key, c => (string?)c.Value, StringComparer.Ordinal);
        return QmConfigLoader.Load(variables, new PhysicalFileReader(), environment.IsEnvironment(TestingEnvironment));
    }

    private sealed class PhysicalFileReader : IFileReader
    {
        public string ReadAllText(string path) => File.ReadAllText(path);
    }
}
