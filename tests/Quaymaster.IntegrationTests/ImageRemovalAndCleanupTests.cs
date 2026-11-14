using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Updates;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class ImageRemovalApiTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private static readonly string OldId = "sha256:" + new string('1', 64);

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null)
    {
        _h = await DockerHarness.CreateAsync(settings);
        _h.Docker.ImageList.Add(new FakeDocker.Img { Id = OldId, Tags = ["old:1", "old:2"], Size = 5000, Shared = 1000 });
        return _h;
    }

    [Fact]
    public async Task Removal_needs_the_exact_tag_or_short_id_as_confirmation()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/images/old:1/remove", new { })).Status.Should().Be(400);
        var wrong = await h.Admin.PostAsync("/images/old:1/remove", new { confirm = "nope" });
        wrong.Status.Should().Be(409);
        wrong.ErrorCode.Should().Be("confirmation_required");
        wrong.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be("old:1");

        var wrongTag = await h.Admin.PostAsync("/images/old:1/remove", new { confirm = "old:1", tag = "old:2" });
        wrongTag.Status.Should().Be(409);
        wrongTag.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be("old:2");
        h.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Untagging_one_of_two_tags_keeps_the_image_and_reports_nothing_reclaimed()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/images/old:1/remove", new { confirm = "old:2", tag = "old:2" });
        r.Status.Should().Be(200, r.Body);
        r.Json.GetProperty("kind").GetString().Should().Be("image");
        r.Json.GetProperty("name").GetString().Should().Be("old:2");
        r.Json.GetProperty("action").GetString().Should().Be("untagged");
        r.Json.GetProperty("removedTags").EnumerateArray().Select(t => t.GetString()).Should().Equal("old:2");
        r.Json.GetProperty("remainingTags").EnumerateArray().Select(t => t.GetString()).Should().Equal("old:1");
        r.Json.GetProperty("reclaim").GetProperty("reclaimedBytes").GetInt64().Should().Be(0);
        h.Docker.Mutations.Should().Equal("rmi old:2");
        h.Docker.ImageList.Should().Contain(i => i.Id == OldId);
        DockerHarness.Keys(r.Json).Should().BeEquivalentTo("kind", "name", "action", "removedTags", "remainingTags", "reclaim");
    }

    [Fact]
    public async Task Deleting_the_last_tags_removes_the_image_and_measures_what_was_reclaimed()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/images/old:1/remove", new { confirm = FakeDocker.ShortOf(OldId) });
        r.Status.Should().Be(200, r.Body);
        r.Json.GetProperty("action").GetString().Should().Be("deleted");
        r.Json.GetProperty("removedTags").EnumerateArray().Select(t => t.GetString()).Should().Equal("old:1", "old:2");
        r.Json.GetProperty("remainingTags").GetArrayLength().Should().Be(0);
        h.Docker.Mutations.Should().Equal("rmi old:1", "rmi old:2");

        var reclaim = r.Json.GetProperty("reclaim");
        DockerHarness.Keys(reclaim).Should().BeEquivalentTo("estimatedBytes", "freeSpaceDeltaBytes", "dfDeltaBytes", "reclaimedBytes", "basis", "note", "caveat");
        reclaim.GetProperty("estimatedBytes").GetInt64().Should().Be(4000, "size minus layers shared with other images");
        reclaim.GetProperty("dfDeltaBytes").GetInt64().Should().Be(4000);
        reclaim.GetProperty("freeSpaceDeltaBytes").GetInt64().Should().Be(4000);
        reclaim.GetProperty("reclaimedBytes").GetInt64().Should().Be(4000);
        reclaim.GetProperty("basis").GetString().Should().Be("df");
        reclaim.GetProperty("caveat").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_image_used_by_a_container_is_never_deleted_not_even_a_stopped_one()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/images/nginx:1.27/remove", new { confirm = "nginx:1.27" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("conflict");
        r.ErrorMessage.Should().Contain("is used by containers").And.Contain("web").And.Contain("solo");
        h.Docker.Mutations.Should().BeEmpty();
        (await h.Admin.GetAsync("/audit?action=image.remove")).Json.GetProperty("entries")[0].GetProperty("outcome").GetString().Should().Be("denied");
    }

    [Fact]
    public async Task Removing_a_tag_a_container_was_created_from_is_refused_even_when_other_tags_remain()
    {
        var h = await HarnessAsync();
        h.Docker.Containers.Add(FakeDocker.Summary(new string('7', 64), "olduser", "old:2", null, null, "exited", "Exited (0) 1 hour ago"));
        h.Docker.ImageList[1].Tags.Add("old:3");
        var r = await h.Admin.PostAsync("/images/old:1/remove", new { confirm = "old:2", tag = "old:2" });
        r.Status.Should().Be(409);
        r.ErrorMessage.Should().Contain("container olduser was created from old:2");
        h.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tag_the_image_does_not_carry_unknown_and_unsafe_references_are_rejected()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/images/old:1/remove", new { confirm = "old:9", tag = "old:9" })).Status.Should().Be(409);
        (await h.Admin.PostAsync("/images/ghost:1/remove", new { confirm = "ghost:1" })).Status.Should().Be(404);
        (await h.Admin.PostAsync("/images/a%3Bb/remove", new { confirm = "x" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/images/old:1/remove", new { confirm = "old:1", tag = "bad;tag" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/images/old:1/remove", new { confirm = "old:1", extra = 1 })).Status.Should().Be(400);
        h.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task References_with_slashes_arrive_percent_encoded()
    {
        var h = await HarnessAsync();
        h.Docker.ImageList.Add(new FakeDocker.Img { Id = "sha256:" + new string('2', 64), Tags = ["reg.io/team/app:v1"], Size = 100 });
        var r = await h.Admin.PostAsync("/images/reg.io%2Fteam%2Fapp%3Av1/remove", new { confirm = "reg.io/team/app:v1" });
        r.Status.Should().Be(200, r.Body);
        h.Docker.Mutations.Should().Equal("rmi reg.io/team/app:v1");
    }

    [Fact]
    public async Task Removal_is_admin_only_refused_in_read_only_mode_and_bound_by_the_managed_scope()
    {
        var h = await HarnessAsync();
        (await h.Viewer.PostAsync("/images/old:1/remove", new { confirm = "old:1" })).Status.Should().Be(403);
        (await h.Factory.NewClient().PostAsync("/images/old:1/remove", new { confirm = "old:1" })).Status.Should().Be(401);
        h.Dispose();

        var ro = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var r = await ro.Admin.PostAsync("/images/old:1/remove", new { confirm = FakeDocker.ShortOf(OldId) });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
        ro.Docker.Mutations.Should().BeEmpty();
        ro.Dispose();

        var scoped = await HarnessAsync(new() { ["QM_MANAGED_SCOPE"] = "keep*" });
        var s = await scoped.Admin.PostAsync("/images/old:1/remove", new { confirm = FakeDocker.ShortOf(OldId) });
        s.Status.Should().Be(409);
        s.ErrorCode.Should().Be("out_of_scope");
        scoped.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rollback_point_within_retention_and_its_image_are_protected()
    {
        var h = await HarnessAsync();
        var now = h.Factory.Time.GetUtcNow().ToUnixTimeMilliseconds();
        const string tag = "quaymaster-rollback/abc:run1";
        h.Docker.ImageList[1].Tags.Add(tag);
        await h.Factory.Services.GetRequiredService<IRollbackStore>().PutAsync(new RollbackPoint(tag, "run1", "shop", "web", null, "old:1", OldId, now, now + 86_400_000, false), default);

        var whole = await h.Admin.PostAsync("/images/old:1/remove", new { confirm = FakeDocker.ShortOf(OldId) });
        whole.Status.Should().Be(409);
        whole.ErrorMessage.Should().Contain("rollback point within retention");
        var tagOnly = await h.Admin.PostAsync($"/images/{Uri.EscapeDataString(tag)}/remove", new { confirm = tag, tag });
        tagOnly.Status.Should().Be(409);
        h.Docker.Mutations.Should().BeEmpty();
    }
}

public sealed class ImageCleanupApiTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private static string Id(char c) => "sha256:" + new string(c, 64);

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null)
    {
        // no managed scope by default: with a scope, untagged images and internal tags need a label filter (tested below)
        var env = new Dictionary<string, string> { ["QM_MANAGED_SCOPE"] = string.Empty };
        foreach (var (k, v) in settings ?? [])
        {
            env[k] = v;
        }

        _h = await DockerHarness.CreateAsync(env);
        var now = _h.Factory.Time.GetUtcNow();
        _h.Docker.ImageList.AddRange(
        [
            new FakeDocker.Img { Id = Id('1'), Tags = [], Size = 4000, Created = now.AddDays(-30) },                       // dangling, old
            new FakeDocker.Img { Id = Id('2'), Tags = ["unused:1"], Size = 6000, Shared = 1000, Created = now.AddDays(-30) }, // unused, old
            new FakeDocker.Img { Id = Id('3'), Tags = ["fresh:1"], Size = 700, Created = now.AddHours(-1) },                // too young
            new FakeDocker.Img { Id = Id('4'), Tags = ["labelled:1"], Size = 800, Created = now.AddDays(-30), Labels = { ["keep"] = "yes" } },
        ]);
        return _h;
    }

    private static JsonElement Candidate(JsonElement preview, string id) =>
        preview.GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Preview_lists_candidates_with_sizes_and_everything_it_protects_with_the_reason()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/images/cleanup/preview", new { });
        r.Status.Should().Be(200, r.Body);
        var p = r.Json;
        Guid.TryParse(p.GetProperty("previewId").GetString(), out _).Should().BeTrue();
        p.GetProperty("expiresAt").GetString().Should().EndWith("Z");
        p.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).Should().Equal("dangling_images", "unused_images", "stopped_containers", "expired_rollback");
        p.GetProperty("minAgeHours").GetInt32().Should().Be(24);
        DockerHarness.Keys(p).Should().BeEquivalentTo("previewId", "expiresAt", "categories", "minAgeHours", "candidates", "protected", "reportOnly", "totalBytes");

        var dangling = Candidate(p, "image:" + Id('1'));
        dangling.GetProperty("category").GetString().Should().Be("dangling_images");
        dangling.GetProperty("size").GetInt64().Should().Be(4000);
        dangling.GetProperty("virtualSize").GetInt64().Should().Be(4000);
        dangling.GetProperty("ageHours").GetInt32().Should().Be(30 * 24);
        var unused = Candidate(p, "image:" + Id('2'));
        unused.GetProperty("category").GetString().Should().Be("unused_images");
        unused.GetProperty("name").GetString().Should().Be("unused:1");
        unused.GetProperty("size").GetInt64().Should().Be(5000, "unique size = size - shared");
        unused.GetProperty("virtualSize").GetInt64().Should().Be(6000);
        var solo = Candidate(p, "container:" + FakeDocker.SoloId);
        solo.GetProperty("category").GetString().Should().Be("stopped_containers");
        solo.GetProperty("size").ValueKind.Should().Be(JsonValueKind.Null);

        var prot = p.GetProperty("protected").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("reason").GetString()!);
        prot["nginx:1.27"].Should().StartWith("in use by container");
        prot["fresh:1"].Should().Contain("younger than the minimum age (24 h)");
        prot.Keys.Should().NotContain(new[] { "web", "db" }, "running Compose containers are not even stopped-container candidates");
        p.GetProperty("totalBytes").GetInt64().Should().Be(4000 + 5000 + 800);
        p.GetProperty("reportOnly").GetProperty("unusedVolumes").ValueKind.Should().Be(JsonValueKind.Array);
        p.GetProperty("reportOnly").GetProperty("unusedNetworks").ValueKind.Should().Be(JsonValueKind.Array);
        h.Docker.Mutations.Should().BeEmpty("a preview never removes anything");
    }

    [Fact]
    public async Task Categories_minimum_age_and_label_filter_narrow_the_candidates()
    {
        var h = await HarnessAsync();
        var onlyDangling = (await h.Admin.PostAsync("/images/cleanup/preview", new { categories = new[] { "dangling_images" } })).Json;
        onlyDangling.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().Equal("image:" + Id('1'));

        var young = (await h.Admin.PostAsync("/images/cleanup/preview", new { categories = new[] { "unused_images" }, minAgeHours = 0 })).Json;
        young.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().Contain("fresh:1");
        young.GetProperty("minAgeHours").GetInt32().Should().Be(0);

        var labelled = (await h.Admin.PostAsync("/images/cleanup/preview", new { labelFilter = "keep=yes", categories = new[] { "unused_images", "dangling_images" } })).Json;
        labelled.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().Equal("labelled:1");
        labelled.GetProperty("protected").EnumerateArray().Select(c => c.GetProperty("reason").GetString()).Should().Contain(x => x!.Contains("label filter keep=yes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"categories\":[\"bogus\"]}")]
    [InlineData("{\"categories\":[\"dangling_images\",\"unused_images\",\"stopped_containers\",\"expired_rollback\",\"dangling_images\"]}")]
    [InlineData("{\"minAgeHours\":-1}")]
    [InlineData("{\"minAgeHours\":999999}")]
    [InlineData("{\"labelFilter\":\"a b\"}")]
    [InlineData("{\"labelFilter\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("{\"surprise\":true}")]
    public async Task Invalid_preview_bodies_are_400(string body)
    {
        var h = await HarnessAsync();
        var r = await h.Admin.SendAsync(HttpMethod.Post, "/images/cleanup/preview", rawBody: body);
        r.Status.Should().Be(400, r.Body);
        r.ErrorCode.Should().Be("validation");
    }

    [Fact]
    public async Task Execute_removes_only_the_previewed_candidates_and_reports_the_reclaim()
    {
        var h = await HarnessAsync();
        var p = (await h.Admin.PostAsync("/images/cleanup/preview", new { })).Json;
        var id = p.GetProperty("previewId").GetString()!;

        var r = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id });
        r.Status.Should().Be(200, r.Body);
        DockerHarness.Keys(r.Json).Should().BeEquivalentTo("removed", "skipped", "reclaimedBytes", "reclaim");
        r.Json.GetProperty("removed").EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().BeEquivalentTo("image:" + Id('1'), "image:" + Id('2'), "image:" + Id('4'), "container:" + FakeDocker.SoloId);
        r.Json.GetProperty("skipped").GetArrayLength().Should().Be(0);
        h.Docker.ImageList.Select(i => i.Id).Should().BeEquivalentTo(FakeDocker.NginxId, Id('3'));
        h.Docker.Commands.Should().ContainSingle().Which.Should().Match<ContainerCommand>(c => c.Action == ContainerAction.Remove && c.ContainerId == FakeDocker.SoloId);

        var reclaim = r.Json.GetProperty("reclaim");
        reclaim.GetProperty("basis").GetString().Should().Be("df");
        reclaim.GetProperty("dfDeltaBytes").GetInt64().Should().Be(4000 + 5000 + 800);
        reclaim.GetProperty("reclaimedBytes").GetInt64().Should().Be(9800);
        r.Json.GetProperty("reclaimedBytes").GetInt64().Should().Be(9800);

        var audit = (await h.Admin.GetAsync("/audit?limit=200")).Json.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        audit.Should().Contain("cleanup.preview").And.Contain("cleanup.execute").And.Contain("image.remove").And.Contain("container.remove");
    }

    [Fact]
    public async Task Execute_binds_to_the_preview_the_confirmation_and_the_admin_who_made_it()
    {
        var h = await HarnessAsync();
        var id = (await h.Admin.PostAsync("/images/cleanup/preview", new { })).Json.GetProperty("previewId").GetString()!;

        var wrong = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = "yes" });
        wrong.Status.Should().Be(409);
        wrong.ErrorCode.Should().Be("confirmation_required");
        wrong.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be(id);
        (await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = "not-a-uuid", confirm = "x" })).Status.Should().Be(400);

        var unknown = Guid.NewGuid().ToString();
        var none = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = unknown, confirm = unknown });
        none.Status.Should().Be(409);
        none.ErrorCode.Should().Be("conflict");

        (await h.Admin.PostAsync("/users", new { username = "admin2", password = "second admin pw 1", role = "admin" })).Status.Should().Be(201);
        var other = h.Factory.NewClient();
        (await other.LoginAsync("admin2", "second admin pw 1")).Status.Should().Be(200);
        var stolen = await other.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id });
        stolen.Status.Should().Be(403);
        h.Docker.Mutations.Should().BeEmpty();

        (await h.Viewer.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(403);
        (await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(200);
        (await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(409, "a preview executes once");
    }

    [Fact]
    public async Task A_preview_expires_after_ten_minutes()
    {
        var h = await HarnessAsync();
        var id = (await h.Admin.PostAsync("/images/cleanup/preview", new { })).Json.GetProperty("previewId").GetString()!;
        h.Factory.Time.Advance(TimeSpan.FromMinutes(11));
        var r = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id });
        r.Status.Should().Be(409);
        r.ErrorMessage.Should().Contain("No valid preview");
        h.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_candidate_is_re_verified_right_before_it_is_removed()
    {
        var h = await HarnessAsync();
        var id = (await h.Admin.PostAsync("/images/cleanup/preview", new { })).Json.GetProperty("previewId").GetString()!;

        // after the preview a container starts on the unused image, and the labelled image gains a rollback tag
        h.Docker.Containers.Add(FakeDocker.Summary(new string('8', 64), "late", "unused:1", null, null, "running", "Up 1 second"));
        h.Docker.Containers.OfType<System.Text.Json.Nodes.JsonObject>().Last()["ImageID"] = Id('2');
        h.Docker.ImageList.Single(i => i.Id == Id('4')).Tags.Add("quaymaster-staged/zzz:yyy");

        var r = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id });
        r.Status.Should().Be(200, r.Body);
        var skipped = r.Json.GetProperty("skipped").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("reason").GetString()!);
        skipped["image:" + Id('2')].Should().Be("now used by a container");
        skipped["image:" + Id('4')].Should().Be("carries a rollback/staged tag");
        r.Json.GetProperty("removed").EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().BeEquivalentTo("image:" + Id('1'), "container:" + FakeDocker.SoloId);
        h.Docker.ImageList.Select(i => i.Id).Should().Contain(new[] { Id('2'), Id('4') });
    }

    [Fact]
    public async Task Nothing_removed_means_nothing_reclaimed_whatever_else_changed_on_the_host()
    {
        var h = await HarnessAsync();
        var id = (await h.Admin.PostAsync("/images/cleanup/preview", new { categories = new[] { "unused_images" } })).Json.GetProperty("previewId").GetString()!;
        foreach (var img in h.Docker.ImageList.Where(i => i.Id != FakeDocker.NginxId).ToList())
        {
            h.Docker.Containers.Add(FakeDocker.Summary(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), "c" + img.Id[7..9], "x", null, null, "running", "Up"));
            h.Docker.Containers.OfType<System.Text.Json.Nodes.JsonObject>().Last()["ImageID"] = img.Id;
        }

        var r = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id });
        r.Json.GetProperty("removed").GetArrayLength().Should().Be(0);
        r.Json.GetProperty("reclaimedBytes").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task In_read_only_mode_previews_work_but_execute_is_refused_with_the_read_only_code()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var id = (await h.Admin.PostAsync("/images/cleanup/preview", new { })).Json.GetProperty("previewId").GetString()!;
        var r = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
        h.Docker.Mutations.Should().BeEmpty();
        h.Docker.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_is_admin_only()
    {
        var h = await HarnessAsync();
        (await h.Viewer.PostAsync("/images/cleanup/preview", new { })).Status.Should().Be(403);
        (await h.Factory.NewClient().PostAsync("/images/cleanup/preview", new { })).Status.Should().Be(401);
    }

    [Fact]
    public async Task With_a_managed_scope_untagged_images_need_a_label_filter_and_out_of_scope_images_are_kept()
    {
        var h = await HarnessAsync(new() { ["QM_MANAGED_SCOPE"] = "unused*,labelled*" });
        var plain = (await h.Admin.PostAsync("/images/cleanup/preview", new { })).Json;
        plain.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().BeEquivalentTo("unused:1", "labelled:1");
        var prot = plain.GetProperty("protected").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("reason").GetString()!);
        prot[FakeDocker.ShortOf(Id('1'))].Should().Contain("untagged image, and QM_MANAGED_SCOPE is set without a label filter");
        prot["fresh:1"].Should().Contain("outside QM_MANAGED_SCOPE");

        var filtered = (await h.Admin.PostAsync("/images/cleanup/preview", new { labelFilter = "keep", categories = new[] { "dangling_images" } })).Json;
        filtered.GetProperty("protected").EnumerateArray().Select(e => e.GetProperty("reason").GetString()).Should().NotContain(x => x!.Contains("untagged image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expired_staged_images_are_cleaned_by_tag_and_those_within_retention_are_kept()
    {
        var h = await HarnessAsync();
        var now = h.Factory.Time.GetUtcNow().ToUnixTimeMilliseconds();
        const string oldTag = "quaymaster-staged/aaa:111111111111";
        const string newTag = "quaymaster-staged/bbb:222222222222";
        h.Docker.ImageList.Add(new FakeDocker.Img { Id = Id('5'), Tags = [oldTag], Size = 900 });
        h.Docker.ImageList.Add(new FakeDocker.Img { Id = Id('6'), Tags = [newTag], Size = 900 });
        var staged = h.Factory.Services.GetRequiredService<IStagedStore>();
        await staged.PutAsync(new StagedImage(oldTag, "x", "sha256:a", Id('5'), now - (30L * 24 * 3600 * 1000)), default);
        await staged.PutAsync(new StagedImage(newTag, "x", "sha256:b", Id('6'), now - 3_600_000), default);

        var p = (await h.Admin.PostAsync("/images/cleanup/preview", new { categories = new[] { "expired_rollback" } })).Json;
        p.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().Equal("tag:" + oldTag);
        p.GetProperty("protected").EnumerateArray().Single().GetProperty("reason").GetString().Should().Contain("within retention");

        var id = p.GetProperty("previewId").GetString()!;
        var r = await h.Admin.PostAsync("/images/cleanup/execute", new { previewId = id, confirm = id });
        r.Json.GetProperty("removed").GetArrayLength().Should().Be(1);
        h.Docker.Mutations.Should().Equal($"rmi {oldTag}");
        (await staged.GetAsync(oldTag, default)).Should().BeNull();
        (await staged.GetAsync(newTag, default)).Should().NotBeNull();
    }
}
