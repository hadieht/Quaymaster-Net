using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Safety;

namespace Quaymaster.UnitTests.Maintenance;

/// <summary>Red-team F-02: the maintenance <c>up</c> passes the same project / container fence as a direct action.</summary>
public class RunEnvironmentFenceTests
{
    private static readonly string SideId = new('7', 64);

    private static ContainerInfo Container(string id, string name, string service) => new(
        id, name, "alpine:latest", "sha256:" + new string('a', 64), "running", "Up", 0, [],
        new Dictionary<string, string> { ["com.docker.compose.project"] = "shop", ["com.docker.compose.service"] = service },
        [], new Dictionary<string, string?>());

    private static (RunEnvironment Env, IMutationGateway Gateway) Build(Func<ContainerRef, bool> refuse)
    {
        var compose = Substitute.For<IComposeService>();
        compose.TargetAsync(default!, default).ReturnsForAnyArgs(new ComposeTarget("shop", "/srv/shop", ["/srv/shop/compose.yaml"]));
        compose.ResolvedConfigForSafetyAsync(default!, default).ReturnsForAnyArgs(new JsonObject { ["services"] = new JsonObject() });
        var containers = Substitute.For<IContainerService>();
        containers.ListEverythingFreshAsync(default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ContainerInfo>>([Container(SideId, "shop-side-1", "side")]));
        var gateway = Substitute.For<IMutationGateway>();
        gateway.AssertContainerAsync(default!, default).ReturnsForAnyArgs(c =>
            refuse(c.ArgAt<ContainerRef>(0)) ? throw new AppException(409, ErrorCode.SelfProtected, "Refused: listed in QM_PROTECTED") : Task.CompletedTask);
        gateway.RunComposeAsync(default!, default, default).ReturnsForAnyArgs(Task.FromResult(new ComposeRunResult(0, string.Empty, string.Empty, false)));
        var env = new RunEnvironment(compose, containers, Substitute.For<IDockerEngine>(), Substitute.For<IStackFiles>(), gateway, null!);
        return (env, gateway);
    }

    [Fact]
    public async Task Up_refuses_when_any_container_of_the_service_is_protected_and_never_runs_compose()
    {
        var (env, gateway) = Build(c => c.Id == SideId);

        var act = () => env.UpAsync("shop", "side", true, false, _ => { }, default);

        (await act.Should().ThrowAsync<AppException>()).Which.Message.Should().Contain("QM_PROTECTED");
        await gateway.DidNotReceiveWithAnyArgs().RunComposeAsync(default!, default, default);
    }

    [Fact]
    public async Task Up_asserts_the_project_and_runs_compose_when_nothing_is_refused()
    {
        var (env, gateway) = Build(_ => false);

        var r = await env.UpAsync("shop", "side", true, false, _ => { }, default);

        r.Ok.Should().BeTrue();
        await gateway.Received(1).AssertProjectAsync("shop", Arg.Any<ProjectRefs?>(), true, Arg.Any<CancellationToken>());
        await gateway.Received(1).RunComposeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<ComposeRunOptions?>(), Arg.Any<CancellationToken>());
    }
}
