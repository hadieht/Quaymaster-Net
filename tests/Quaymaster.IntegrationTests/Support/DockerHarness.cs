using System.Text.Json;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests.Support;

/// <summary>A host wired to <see cref="FakeDocker"/> with an admin, a viewer and a viewer allowed to read logs.</summary>
public sealed class DockerHarness : IDisposable
{
    private DockerHarness(FakeDocker docker, FakeRegistry registry, QmFactory factory, ApiClient admin, ApiClient viewer, ApiClient logsViewer)
    {
        Docker = docker;
        Registry = registry;
        Factory = factory;
        Admin = admin;
        Viewer = viewer;
        LogsViewer = logsViewer;
    }

    public FakeDocker Docker { get; }

    public FakeRegistry Registry { get; }

    public QmFactory Factory { get; }

    public ApiClient Admin { get; }

    public ApiClient Viewer { get; }

    public ApiClient LogsViewer { get; }

    public static async Task<DockerHarness> CreateAsync(Dictionary<string, string>? settings = null, FakeDocker? docker = null, FakeRegistry? registry = null)
    {
        docker ??= new FakeDocker();
        registry ??= new FakeRegistry();
        var env = new Dictionary<string, string>
        {
            ["QM_MANAGED_SCOPE"] = "*",
            ["QM_STACK_PATHS"] = "/srv",
        };
        foreach (var (k, v) in settings ?? [])
        {
            env[k] = v;
        }

        var factory = new QmFactory(env, services =>
        {
            docker.Install(services);
            registry.Install(services);
        });
        var admin = await factory.NewAdminAsync();
        (await admin.PostAsync("/users", new { username = "viewer", password = "viewer password 1", role = "viewer" })).Status.Should().Be(201);
        (await admin.PostAsync("/users", new { username = "reader", password = "reader password 1", role = "viewer", logsRead = true })).Status.Should().Be(201);
        var viewer = factory.NewClient();
        (await viewer.LoginAsync("viewer", "viewer password 1")).Status.Should().Be(200);
        var reader = factory.NewClient();
        (await reader.LoginAsync("reader", "reader password 1")).Status.Should().Be(200);
        return new DockerHarness(docker, registry, factory, admin, viewer, reader);
    }

    public void Dispose() => Factory.Dispose();

    public static IEnumerable<string> Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name);
}
