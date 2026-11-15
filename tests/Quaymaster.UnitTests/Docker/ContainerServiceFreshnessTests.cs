using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Redaction;
using Quaymaster.Domain.Safety;

namespace Quaymaster.UnitTests.Docker;

/// <summary>Red-team F-05 / F-09: the safety reads are fresh (never joined to an older fetch) and never visibility-filtered.</summary>
public class ContainerServiceFreshnessTests
{
    private sealed class NoFiles : IFileReader
    {
        public string ReadAllText(string path) => throw new FileNotFoundException(path);
    }

    private static JsonObject Raw(string id, string name, string project) => new()
    {
        ["Id"] = id,
        ["Names"] = new JsonArray("/" + name),
        ["Image"] = "alpine:latest",
        ["ImageID"] = "sha256:" + new string('a', 64),
        ["State"] = "running",
        ["Status"] = "Up",
        ["Created"] = 1,
        ["Ports"] = new JsonArray(),
        ["Labels"] = new JsonObject { ["com.docker.compose.project"] = project, ["com.docker.compose.service"] = "svc" },
        ["Mounts"] = new JsonArray(),
        ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject() },
    };

    private static ContainerService Build(IDockerEngine engine, SafetyOptions options)
    {
        var gateway = Substitute.For<IMutationGateway>();
        gateway.Policy.Returns(new SafetyPolicy(options));
        var ignore = Substitute.For<IIgnoreList>();
        var config = QmConfigLoader.Load(
            new Dictionary<string, string?> { ["QM_SECRET_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray()) },
            new NoFiles());
        return new ContainerService(engine, gateway, ignore, new Scrubber(), config, () => null!, new FakeTimeProvider());
    }

    [Fact]
    public async Task F05_A_fresh_read_does_not_join_a_fetch_that_started_before_it()
    {
        var oldList = new JsonArray(Raw(new string('1', 64), "old-1", "shop"));
        var newList = new JsonArray(Raw(new string('2', 64), "new-1", "shop"));
        var gate = new TaskCompletionSource<JsonArray>();
        var calls = 0;
        var engine = Substitute.For<IDockerEngine>();
        engine.ListContainersAsync(default).ReturnsForAnyArgs(_ => ++calls == 1 ? gate.Task : Task.FromResult(newList));
        var svc = Build(engine, new SafetyOptions(false, [], [], []));

        var inflight = svc.ListRawAllAsync(false, default); // e.g. the stats sampler's poll, started before the recreate
        var fresh = await svc.ListRawAllAsync(true, default).WaitAsync(TimeSpan.FromSeconds(5));

        fresh.Select(c => c.Name).Should().Equal(["new-1"], "a read after a mutation must reflect the mutation");
        gate.SetResult(oldList);
        (await inflight).Select(c => c.Name).Should().Equal("old-1");
    }

    [Fact]
    public async Task F09_The_safety_list_ignores_QM_VISIBLE_SCOPE()
    {
        var engine = Substitute.For<IDockerEngine>();
        engine.ListContainersAsync(default).ReturnsForAnyArgs(Task.FromResult(new JsonArray(
            Raw(new string('1', 64), "qm-visible-1", "qm-visible"), Raw(new string('2', 64), "other-1", "other"))));
        var svc = Build(engine, new SafetyOptions(false, [], ["qm-*"], []));

        (await svc.ListRawAllAsync(true, default)).Select(c => c.Name).Should().Equal("qm-visible-1");
        (await svc.ListEverythingFreshAsync(default)).Select(c => c.Name).Should().BeEquivalentTo("qm-visible-1", "other-1");
    }
}
