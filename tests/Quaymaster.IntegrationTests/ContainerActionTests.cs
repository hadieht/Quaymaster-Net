using Quaymaster.Application.Docker;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class ContainerActionTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null) =>
        _h = await DockerHarness.CreateAsync(settings);

    [Fact]
    public async Task Viewers_Cannot_Mutate_And_Unknown_Actions_Are_400()
    {
        var h = await HarnessAsync();
        (await h.Viewer.PostAsync("/containers/web/actions/restart", new { })).Status.Should().Be(403);
        (await h.LogsViewer.PostAsync("/containers/web/actions/restart", new { })).Status.Should().Be(403);
        var unknown = await h.Admin.PostAsync("/containers/web/actions/explode", new { });
        unknown.Status.Should().Be(400);
        unknown.ErrorMessage.Should().Contain("explode");
        h.Docker.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Body_Is_Strict()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/containers/web/actions/restart", new { force = true });
        r.Status.Should().Be(400);
        r.ErrorCode.Should().Be("validation");
        (await h.Admin.PostAsync("/containers/web/actions/stop", new { timeoutSeconds = 9999 })).Status.Should().Be(400);
        h.Docker.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("start", ContainerAction.Start)]
    [InlineData("stop", ContainerAction.Stop)]
    [InlineData("restart", ContainerAction.Restart)]
    [InlineData("pause", ContainerAction.Pause)]
    [InlineData("unpause", ContainerAction.Unpause)]
    public async Task Lifecycle_Actions_Reach_The_Engine_By_Full_Id(string action, ContainerAction expected)
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync($"/containers/web/actions/{action}", new { });
        r.Status.Should().Be(200, r.Body);
        r.Json.GetProperty("ok").GetBoolean().Should().BeTrue();
        h.Docker.Commands.Should().ContainSingle().Which.Should().Match<ContainerCommand>(c => c.ContainerId == FakeDocker.WebId && c.Action == expected);
    }

    [Fact]
    public async Task Kill_Passes_A_Validated_Signal()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/containers/web/actions/kill", new { signal = "SIGTERM" })).Status.Should().Be(200);
        h.Docker.Commands.Single().Signal.Should().Be("SIGTERM");
        var bad = await h.Admin.PostAsync("/containers/web/actions/kill", new { signal = "sig;rm" });
        bad.Status.Should().Be(400);
        h.Docker.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task Remove_Requires_The_Container_Name_As_Confirmation()
    {
        var h = await HarnessAsync();
        var missing = await h.Admin.PostAsync("/containers/solo/actions/remove", new { });
        missing.Status.Should().Be(409);
        missing.ErrorCode.Should().Be("confirmation_required");
        (await h.Admin.PostAsync("/containers/solo/actions/remove", new { confirm = "wrong" })).Status.Should().Be(409);
        h.Docker.Commands.Should().BeEmpty();

        var ok = await h.Admin.PostAsync("/containers/solo/actions/remove", new { confirm = "solo" });
        ok.Status.Should().Be(200, ok.Body);
        h.Docker.Commands.Should().ContainSingle().Which.Action.Should().Be(ContainerAction.Remove);
    }

    [Fact]
    public async Task Standalone_Recreate_Is_Unsupported()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/containers/solo/actions/recreate", new { confirm = "solo" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("unsupported");
        h.Docker.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Compose_Container_Recreate_Needs_Confirmation()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/containers/web/actions/recreate", new { });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("confirmation_required");
        h.Docker.ComposeRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_Only_Mode_Refuses_Every_Mutation_And_Audits_It()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var r = await h.Admin.PostAsync("/containers/web/actions/restart", new { });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
        h.Docker.Commands.Should().BeEmpty();
        (await h.Admin.GetAsync("/system/mode")).Json.GetProperty("readOnly").GetBoolean().Should().BeTrue();
        (await h.Admin.GetAsync("/containers")).Json.EnumerateArray().Should().OnlyContain(c => !c.GetProperty("mutable").GetBoolean());
    }

    [Fact]
    public async Task Containers_Outside_The_Managed_Scope_Are_Not_Mutable()
    {
        var h = await HarnessAsync(new() { ["QM_MANAGED_SCOPE"] = "shop" });
        // project "shop" is in scope, the standalone container is not
        (await h.Admin.PostAsync("/containers/web/actions/restart", new { })).Status.Should().Be(200);
        var r = await h.Admin.PostAsync("/containers/solo/actions/restart", new { });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("out_of_scope");
        var list = (await h.Admin.GetAsync("/containers")).Json.EnumerateArray().ToList();
        list.Single(c => c.GetProperty("name").GetString() == "solo").GetProperty("mutable").GetBoolean().Should().BeFalse();
        list.Single(c => c.GetProperty("name").GetString() == "web").GetProperty("mutable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Protected_Containers_Are_Refused_With_A_Reason()
    {
        var h = await HarnessAsync(new() { ["QM_PROTECTED"] = "db" });
        var r = await h.Admin.PostAsync("/containers/db/actions/stop", new { });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("self_protected");
        h.Docker.Commands.Should().BeEmpty();
        var list = (await h.Admin.GetAsync("/containers")).Json.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "db");
        list.GetProperty("mutable").GetBoolean().Should().BeFalse();
        list.GetProperty("protectedReason").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Mutations_Are_Audited()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/containers/web/actions/restart", new { })).Status.Should().Be(200);
        var audit = await h.Admin.GetAsync("/audit?limit=20");
        audit.Status.Should().Be(200);
        var entries = audit.Json.GetProperty("entries").EnumerateArray().ToList();
        entries.Should().Contain(e => e.GetProperty("action").GetString()!.Contains("restart") && e.GetProperty("outcome").GetString() == "success");
    }
}
