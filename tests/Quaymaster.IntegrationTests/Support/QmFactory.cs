using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Quaymaster.IntegrationTests.Support;

/// <summary>
/// A full Quaymaster host in memory (no Docker, no network): its own data directory + SQLite database, the Testing
/// environment (cheap scrypt) and a fake clock. Settings are QM_* variables, exactly as in production.
/// </summary>
public sealed class QmFactory : WebApplicationFactory<Program>
{
    public const string Origin = "http://localhost";
    public const string AdminPassword = "correct horse battery";

    private readonly Dictionary<string, string> _settings;
    private readonly Action<IServiceCollection>? _configureServices;

    public QmFactory()
        : this(null)
    {
    }

    internal QmFactory(Dictionary<string, string>? overrides, Action<IServiceCollection>? configureServices = null)
    {
        DataDir = Path.Combine(Path.GetTempPath(), "qm-it-" + Guid.NewGuid().ToString("N"));
        _configureServices = configureServices;
        _settings = new Dictionary<string, string>
        {
            ["QM_SECRET_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray()),
            ["QM_DATA_DIR"] = DataDir,
            ["QM_PUBLIC_ORIGIN"] = Origin,
            ["QM_SCRYPT_LOG2_N"] = "10",
            ["QM_DISABLE_BACKGROUND"] = "true",
            ["QM_LOG_LEVEL"] = "warn",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            _settings[key] = value;
        }
    }

    public string DataDir { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

    public string DatabaseFile => Path.Combine(DataDir, "quaymaster.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            _configureServices?.Invoke(services);
        });
    }

    public ApiClient NewClient() => new(CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }), this);

    public string ReadSetupToken() => File.ReadAllText(Path.Combine(DataDir, "setup-token")).Trim();

    /// <summary>A client that has completed first-run setup and is logged in as the admin.</summary>
    public async Task<ApiClient> NewAdminAsync(string username = "admin")
    {
        var client = NewClient();
        (await client.SetupAsync(username, AdminPassword)).Status.Should().Be(200);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDir))
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort: temp dir
            }
        }
    }
}
