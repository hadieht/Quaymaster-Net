using System.Text.Json;
using System.Text.Json.Nodes;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class StorageCleanupApiTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private static readonly string Anon = new('a', 64);
    private static readonly string NetId = new('b', 64);

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null)
    {
        _h = await DockerHarness.CreateAsync(settings);
        var d = _h.Docker;
        d.VolumeList.Add(Vol(Anon, "2026-01-01T00:00:00Z"));
        d.VolumeList.Add(Vol("scratch_tmp", "2026-02-01T00:00:00Z"));
        d.VolumeList.Add(Vol("shop_cache", "2026-02-01T00:00:00Z", "shop"));
        d.VolumeList.Add(Vol("ghost_db", "2026-02-01T00:00:00Z", "ghost"));
        d.VolumeList.Add(Vol("brand_new", _h.Factory.Time.GetUtcNow().AddHours(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        d.VolumeSizes[Anon] = 5000;
        d.VolumeSizes["scratch_tmp"] = 7000;
        d.VolumeSizes["shop_cache"] = 300;
        d.VolumeSizes["ghost_db"] = 400;
        d.NetworkList.Add(Net("tmpnet", NetId, null));
        d.NetworkList.Add(Net("proj_net", new string('c', 64), "shop"));
        return _h;
    }

    private static JsonObject Vol(string name, string created, string? project = null) =>
        new()
        {
            ["Name"] = name,
            ["Driver"] = "local",
            ["Mountpoint"] = $"/var/lib/docker/volumes/{name}/_data",
            ["CreatedAt"] = created,
            ["Labels"] = project is null ? new JsonObject() : new JsonObject { ["com.docker.compose.project"] = project },
            ["Scope"] = "local",
        };

    private static JsonObject Net(string name, string id, string? project) =>
        new()
        {
            ["Id"] = id,
            ["Name"] = name,
            ["Driver"] = "bridge",
            ["Scope"] = "local",
            ["Internal"] = false,
            ["Labels"] = project is null ? new JsonObject() : new JsonObject { ["com.docker.compose.project"] = project },
            ["Created"] = "2026-01-01T00:00:00Z",
        };

    private static JsonObject Mounting(string name, string volume) =>
        FakeDocker.Summary(new string('9', 64).Replace('9', 'e'), name, "alpine:3", null, null, "exited", "Exited (0) 1 hour ago") is { } c
            ? Mount(c, volume)
            : throw new InvalidOperationException();

    private static JsonObject Mount(JsonObject container, string volume)
    {
        container["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "volume", ["Name"] = volume });
        return container;
    }

    private static Dictionary<string, JsonElement> Candidates(JsonElement preview) =>
        preview.GetProperty("candidates").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);

    private static Dictionary<string, string> Protected(JsonElement preview) =>
        preview.GetProperty("protected").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("reason").GetString()!);

    [Fact]
    public async Task Preview_for_volumes_returns_sizes_a_typed_confirmation_phrase_and_protects_compose_data()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "anonymous_volumes", "named_volumes" } });
        r.Status.Should().Be(200, r.Body);
        var p = r.Json;
        DockerHarness.Keys(p).Should().BeEquivalentTo("previewId", "expiresAt", "categories", "includeComposeProjectVolumes", "minAgeHours", "candidates", "protected", "estimatedBytes", "confirmPhrase", "deletesData");
        Guid.TryParse(p.GetProperty("previewId").GetString(), out _).Should().BeTrue();
        p.GetProperty("confirmPhrase").GetString().Should().Be("delete 2 volumes");
        p.GetProperty("deletesData").GetBoolean().Should().BeTrue();
        p.GetProperty("estimatedBytes").GetInt64().Should().Be(12_000);
        p.GetProperty("includeComposeProjectVolumes").GetBoolean().Should().BeFalse();
        var c = Candidates(p);
        c.Keys.Should().BeEquivalentTo(Anon, "scratch_tmp");
        DockerHarness.Keys(c[Anon]).Should().BeEquivalentTo("id", "kind", "category", "name", "size", "createdAt", "ageHours", "anonymous", "composeProject", "lastKnownUser", "note");
        c[Anon].GetProperty("id").GetString().Should().Be("volume:" + Anon);
        c[Anon].GetProperty("category").GetString().Should().Be("anonymous_volumes");
        c[Anon].GetProperty("anonymous").GetBoolean().Should().BeTrue();
        c[Anon].GetProperty("size").GetInt64().Should().Be(5000);
        c["scratch_tmp"].GetProperty("category").GetString().Should().Be("named_volumes");
        var prot = Protected(p);
        prot["shop_data"].Should().Contain("belongs to Compose project \"shop\"").And.Contain("opt in explicitly");
        prot["shop_cache"].Should().Contain("Compose project \"shop\"");
        prot["ghost_db"].Should().Contain("cannot find").And.Contain("opt in explicitly");
        prot["brand_new"].Should().Contain("younger than the minimum age");
        h.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Networks_free_no_disk_and_need_no_typed_phrase()
    {
        var h = await HarnessAsync();
        var p = (await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "unused_networks" } })).Json;
        p.GetProperty("confirmPhrase").ValueKind.Should().Be(JsonValueKind.Null);
        p.GetProperty("deletesData").GetBoolean().Should().BeFalse();
        p.GetProperty("estimatedBytes").GetInt64().Should().Be(0);
        var c = Candidates(p);
        c.Keys.Should().BeEquivalentTo("tmpnet");
        c["tmpnet"].GetProperty("kind").GetString().Should().Be("network");
        c["tmpnet"].GetProperty("id").GetString().Should().Be("network:" + NetId);
        c["tmpnet"].GetProperty("size").GetInt64().Should().Be(0);
        var prot = Protected(p);
        prot["shop_default"].Should().Contain("containers are attached");
        prot["proj_net"].Should().Contain("Compose project \"shop\"");
        prot.Keys.Should().NotContain("bridge", "the daemon's own networks are not listed");

        var id = p.GetProperty("previewId").GetString()!;
        var done = await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id });
        done.Status.Should().Be(200, done.Body);
        done.Json.GetProperty("removed").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("tmpnet");
        h.Docker.Mutations.Should().Equal("rmn " + NetId);
    }

    [Fact]
    public async Task Compose_project_volumes_are_included_only_on_explicit_opt_in()
    {
        var h = await HarnessAsync();
        var p = (await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "named_volumes" }, includeComposeProjectVolumes = true })).Json;
        p.GetProperty("includeComposeProjectVolumes").GetBoolean().Should().BeTrue();
        Candidates(p).Keys.Should().BeEquivalentTo("scratch_tmp", "shop_cache", "ghost_db", "shop_data");
        p.GetProperty("confirmPhrase").GetString().Should().Be("delete 4 volumes");
    }

    [Fact]
    public async Task Execute_needs_the_typed_phrase_for_volumes_and_reports_a_measured_reclaim()
    {
        var h = await HarnessAsync();
        var p = (await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "anonymous_volumes", "named_volumes", "unused_networks" } })).Json;
        var id = p.GetProperty("previewId").GetString()!;

        var byId = await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id });
        byId.Status.Should().Be(409);
        byId.ErrorCode.Should().Be("confirmation_required");
        byId.ErrorMessage.Should().Contain("permanently deletes data").And.Contain("delete 2 volumes");
        byId.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be("delete 2 volumes");
        (await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = "DELETE 2 VOLUMES" })).Status.Should().Be(409);
        h.Docker.Mutations.Should().BeEmpty();

        var r = await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = "delete 2 volumes" });
        r.Status.Should().Be(200, r.Body);
        DockerHarness.Keys(r.Json).Should().BeEquivalentTo("removed", "skipped", "reclaim");
        var removed = r.Json.GetProperty("removed").EnumerateArray().ToList();
        removed.Select(e => e.GetProperty("name").GetString()).Should().BeEquivalentTo(Anon, "scratch_tmp", "tmpnet");
        DockerHarness.Keys(removed[0]).Should().BeEquivalentTo("id", "name", "kind", "bytes");
        removed.Single(e => e.GetProperty("name").GetString() == "scratch_tmp").GetProperty("bytes").GetInt64().Should().Be(7000);
        r.Json.GetProperty("skipped").GetArrayLength().Should().Be(0);
        h.Docker.Mutations.Should().BeEquivalentTo("rmv " + Anon, "rmv scratch_tmp", "rmn " + NetId);

        var reclaim = r.Json.GetProperty("reclaim");
        reclaim.GetProperty("estimatedBytes").GetInt64().Should().Be(12_000);
        reclaim.GetProperty("dfDeltaBytes").GetInt64().Should().Be(12_000);
        reclaim.GetProperty("freeSpaceDeltaBytes").GetInt64().Should().Be(12_000);
        reclaim.GetProperty("reclaimedBytes").GetInt64().Should().Be(12_000);
        reclaim.GetProperty("basis").GetString().Should().Be("df");

        var audit = (await h.Admin.GetAsync("/audit?limit=200")).Json.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        audit.Should().Contain("storage.cleanup.preview").And.Contain("storage.cleanup.execute").And.Contain("volume.remove").And.Contain("network.remove");
    }

    [Fact]
    public async Task Every_item_is_re_checked_at_execute_time()
    {
        var h = await HarnessAsync();
        var p = (await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "anonymous_volumes", "named_volumes", "unused_networks" } })).Json;
        var id = p.GetProperty("previewId").GetString()!;

        // a container now mounts the anonymous volume, scratch_tmp is recreated, tmpnet gets a member
        h.Docker.Containers.Add(Mounting("late", Anon));
        h.Docker.VolumeList.OfType<JsonObject>().Single(v => v["Name"]!.GetValue<string>() == "scratch_tmp")["CreatedAt"] = "2026-11-01T00:00:00Z";
        var late = FakeDocker.Summary(new string('f', 64), "net-user", "alpine:3", null, null, "exited", "Exited (0) 1 hour ago");
        late["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject { ["tmpnet"] = new JsonObject { ["NetworkID"] = NetId } } };
        h.Docker.Containers.Add(late);

        var r = await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = "delete 2 volumes" });
        r.Status.Should().Be(200, r.Body);
        r.Json.GetProperty("removed").GetArrayLength().Should().Be(0);
        var skipped = r.Json.GetProperty("skipped").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("reason").GetString()!);
        skipped[Anon].Should().Contain("in use by container late");
        skipped["scratch_tmp"].Should().Be("was recreated since the preview");
        skipped["tmpnet"].Should().Contain("containers are attached: net-user");
        h.Docker.Mutations.Should().BeEmpty();
        r.Json.GetProperty("reclaim").GetProperty("reclaimedBytes").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Previews_bind_to_one_admin_expire_and_execute_once()
    {
        var h = await HarnessAsync();
        var id = (await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "unused_networks" } })).Json.GetProperty("previewId").GetString()!;
        (await h.Admin.PostAsync("/users", new { username = "admin2", password = "second admin pw 1", role = "admin" })).Status.Should().Be(201);
        var other = h.Factory.NewClient();
        await other.LoginAsync("admin2", "second admin pw 1");
        (await other.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(403);
        (await h.Viewer.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(403);

        var unknown = Guid.NewGuid().ToString();
        var none = await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = unknown, confirm = unknown });
        none.Status.Should().Be(409);
        none.ErrorCode.Should().Be("conflict");

        (await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(200);
        (await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(409);

        var id2 = (await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "unused_networks" } })).Json.GetProperty("previewId").GetString()!;
        h.Factory.Time.Advance(TimeSpan.FromMinutes(11));
        (await h.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id2, confirm = id2 })).ErrorMessage.Should().Contain("No valid preview");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"categories\":[]}")]
    [InlineData("{\"categories\":[\"bogus\"]}")]
    [InlineData("{\"categories\":[\"unused_networks\",\"anonymous_volumes\",\"named_volumes\",\"named_volumes\"]}")]
    [InlineData("{\"categories\":[\"unused_networks\"],\"minAgeHours\":-5}")]
    [InlineData("{\"categories\":[\"unused_networks\"],\"includeComposeProjectVolumes\":\"yes\"}")]
    [InlineData("{\"categories\":[\"unused_networks\"],\"x\":1}")]
    public async Task Invalid_storage_preview_bodies_are_400(string body)
    {
        var h = await HarnessAsync();
        var r = await h.Admin.SendAsync(HttpMethod.Post, "/storage/cleanup/preview", rawBody: body);
        r.Status.Should().Be(400, r.Body);
        r.ErrorCode.Should().Be("validation");
    }

    [Fact]
    public async Task Storage_cleanup_is_admin_only_and_execute_is_refused_in_read_only_mode()
    {
        var h = await HarnessAsync();
        (await h.Viewer.PostAsync("/storage/cleanup/preview", new { categories = new[] { "unused_networks" } })).Status.Should().Be(403);
        (await h.Factory.NewClient().PostAsync("/storage/cleanup/preview", new { categories = new[] { "unused_networks" } })).Status.Should().Be(401);
        h.Dispose();

        var ro = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var id = (await ro.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "unused_networks" } })).Json.GetProperty("previewId").GetString()!;
        var r = await ro.Admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
        ro.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task The_managed_scope_limits_what_a_preview_may_offer()
    {
        var h = await HarnessAsync(new() { ["QM_MANAGED_SCOPE"] = "scratch*" });
        var p = (await h.Admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "anonymous_volumes", "named_volumes", "unused_networks" } })).Json;
        Candidates(p).Keys.Should().BeEquivalentTo("scratch_tmp");
        Protected(p)[Anon].Should().Contain("outside QM_MANAGED_SCOPE");
        Protected(p)["tmpnet"].Should().Contain("outside QM_MANAGED_SCOPE");
    }

    // ---- single removal ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_volume_needs_its_exact_name_and_reports_the_reclaim()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/volumes/scratch_tmp/remove", new { })).Status.Should().Be(400);
        var wrong = await h.Admin.PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch" });
        wrong.Status.Should().Be(409);
        wrong.ErrorCode.Should().Be("confirmation_required");
        wrong.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be("scratch_tmp");
        h.Docker.Mutations.Should().BeEmpty();

        var r = await h.Admin.PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch_tmp" });
        r.Status.Should().Be(200, r.Body);
        r.Json.GetProperty("kind").GetString().Should().Be("volume");
        r.Json.GetProperty("name").GetString().Should().Be("scratch_tmp");
        r.Json.GetProperty("action").GetString().Should().Be("deleted");
        r.Json.GetProperty("removedTags").GetArrayLength().Should().Be(0);
        r.Json.GetProperty("remainingTags").GetArrayLength().Should().Be(0);
        r.Json.GetProperty("reclaim").GetProperty("estimatedBytes").GetInt64().Should().Be(7000);
        r.Json.GetProperty("reclaim").GetProperty("reclaimedBytes").GetInt64().Should().Be(7000);
        r.Json.GetProperty("reclaim").GetProperty("basis").GetString().Should().Be("df");
        h.Docker.Mutations.Should().Equal("rmv scratch_tmp");
    }

    [Fact]
    public async Task A_volume_any_container_references_is_never_removed()
    {
        var h = await HarnessAsync();
        h.Docker.Containers.Add(Mounting("keeper", "scratch_tmp"));
        var r = await h.Admin.PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch_tmp" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("conflict");
        r.ErrorMessage.Should().Contain("in use by container keeper").And.Contain("a stopped container counts");
        h.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Compose_project_volumes_need_the_acknowledge_flag_on_top_of_the_typed_name()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/volumes/shop_cache/remove", new { confirm = "shop_cache" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("conflict");
        r.Json.GetProperty("error").GetProperty("details").GetProperty("requires").GetString().Should().Be("acknowledgeComposeProject");
        h.Docker.Mutations.Should().BeEmpty();

        var ok = await h.Admin.PostAsync("/volumes/shop_cache/remove", new { confirm = "shop_cache", acknowledgeComposeProject = true });
        ok.Status.Should().Be(200, ok.Body);
        h.Docker.Mutations.Should().Equal("rmv shop_cache");
    }

    [Fact]
    public async Task Volume_removal_validates_authorises_and_respects_read_only_and_scope()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/volumes/ghost/remove", new { confirm = "ghost" })).Status.Should().Be(404);
        (await h.Admin.PostAsync("/volumes/bad%3Bname/remove", new { confirm = "x" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch_tmp", extra = 1 })).Status.Should().Be(400);
        (await h.Viewer.PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch_tmp" })).Status.Should().Be(403);
        (await h.Factory.NewClient().PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch_tmp" })).Status.Should().Be(401);
        h.Dispose();

        var ro = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var r = await ro.Admin.PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch_tmp" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
        ro.Dispose();

        var scoped = await HarnessAsync(new() { ["QM_MANAGED_SCOPE"] = "shop*" });
        var s = await scoped.Admin.PostAsync("/volumes/scratch_tmp/remove", new { confirm = "scratch_tmp" });
        s.Status.Should().Be(409);
        s.ErrorCode.Should().Be("out_of_scope");
        scoped.Docker.Mutations.Should().BeEmpty();
        (await scoped.Admin.GetAsync("/audit?action=volume.remove")).Json.GetProperty("entries")[0].GetProperty("outcome").GetString().Should().Be("denied");
    }

    [Fact]
    public async Task Removing_a_network_needs_its_name_and_refuses_attached_and_compose_networks()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/networks/tmpnet/remove", new { })).Status.Should().Be(400);
        var wrong = await h.Admin.PostAsync("/networks/tmpnet/remove", new { confirm = "tmp" });
        wrong.Status.Should().Be(409);
        wrong.ErrorCode.Should().Be("confirmation_required");
        wrong.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be("tmpnet");

        var attached = await h.Admin.PostAsync("/networks/shop_default/remove", new { confirm = "shop_default" });
        attached.Status.Should().Be(409);
        attached.ErrorMessage.Should().Contain("containers are attached");
        var compose = await h.Admin.PostAsync("/networks/proj_net/remove", new { confirm = "proj_net" });
        compose.Status.Should().Be(409);
        compose.ErrorMessage.Should().Contain("defined by Compose project \"shop\"");
        h.Docker.Mutations.Should().BeEmpty();

        var ok = await h.Admin.PostAsync($"/networks/{NetId}/remove", new { confirm = "tmpnet" });
        ok.Status.Should().Be(200, ok.Body);
        ok.Json.GetProperty("kind").GetString().Should().Be("network");
        ok.Json.GetProperty("name").GetString().Should().Be("tmpnet");
        ok.Json.GetProperty("action").GetString().Should().Be("deleted");
        var reclaim = ok.Json.GetProperty("reclaim");
        reclaim.GetProperty("reclaimedBytes").GetInt64().Should().Be(0);
        reclaim.GetProperty("basis").GetString().Should().Be("estimate");
        reclaim.GetProperty("note").GetString().Should().Be("Networks use no disk space.");
        h.Docker.Mutations.Should().Equal("rmn " + NetId);
    }

    [Fact]
    public async Task Predefined_networks_unknown_networks_and_viewers_are_handled()
    {
        var h = await HarnessAsync();
        h.Docker.NetworkList.Add(Net("bridge", new string('d', 64), null));
        var predefined = await h.Admin.PostAsync("/networks/bridge/remove", new { confirm = "bridge" });
        predefined.Status.Should().Be(409);
        predefined.ErrorMessage.Should().Contain("predefined Docker network");
        (await h.Admin.PostAsync("/networks/ghost/remove", new { confirm = "ghost" })).Status.Should().Be(404);
        (await h.Viewer.PostAsync("/networks/tmpnet/remove", new { confirm = "tmpnet" })).Status.Should().Be(403);
        h.Docker.Mutations.Should().BeEmpty();
        h.Dispose();

        var ro = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var r = await ro.Admin.PostAsync("/networks/tmpnet/remove", new { confirm = "tmpnet" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
    }
}
