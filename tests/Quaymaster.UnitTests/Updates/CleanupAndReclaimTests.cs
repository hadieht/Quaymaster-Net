using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Safety;
using Quaymaster.Domain.Storage;
using Quaymaster.Domain.Updates;
using Xunit;

namespace Quaymaster.UnitTests.Updates;

public class QuarantineServiceTests
{
    private sealed class MemoryStore : IQuarantineStore
    {
        public List<QuarantineRow> Rows { get; } = [];

        public Task<QuarantineRow?> FindAsync(string target, string digest, CancellationToken ct) =>
            Task.FromResult(Rows.FirstOrDefault(r => r.Target == target && r.Digest == digest));

        public Task UpsertAsync(QuarantineRow row, CancellationToken ct)
        {
            Rows.RemoveAll(r => r.Target == row.Target && r.Digest == row.Digest);
            Rows.Add(row);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QuarantineRow>> ListActiveAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<QuarantineRow>>([.. Rows.Where(r => r.ClearedAt is null)]);

        public Task<int> ClearAsync(string target, string? digest, long now, CancellationToken ct)
        {
            var n = 0;
            for (var i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                if (r.Target == target && r.ClearedAt is null && (digest is null || r.Digest == digest))
                {
                    Rows[i] = r with { ClearedAt = now };
                    n++;
                }
            }

            return Task.FromResult(n);
        }
    }

    private const string T = "docker.io/library/nginx:latest";
    private static readonly string D1 = "sha256:" + new string('1', 64);
    private static readonly string D2 = "sha256:" + new string('2', 64);

    private static (QuarantineService Svc, MemoryStore Store, FakeTimeProvider Time) Make()
    {
        var store = new MemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        return (new QuarantineService(store, time), store, time);
    }

    [Fact]
    public async Task A_failed_pair_is_quarantined_once_and_only_that_digest_is_affected()
    {
        var (svc, _, _) = Make();
        (await svc.AddAsync(T, D1, "run1", "health check failed", default)).Should().BeTrue();
        (await svc.AddAsync(T, D1, "run2", "again", default)).Should().BeFalse("already active");
        (await svc.IsQuarantinedAsync(T, D1, default)).Should().BeTrue();
        (await svc.IsQuarantinedAsync(T, D2, default)).Should().BeFalse("a newer remote digest is a different pair");
        (await svc.IsQuarantinedAsync("docker.io/library/redis:latest", D1, default)).Should().BeFalse();
        (await svc.ListAsync(default)).Should().ContainSingle().Which.Should().Match<QuarantineEntry>(e => e.RunId == "run1" && e.Reason == "health check failed");
    }

    [Fact]
    public async Task Clearing_a_digest_releases_only_that_pair_and_clearing_a_target_releases_all_its_digests()
    {
        var (svc, _, _) = Make();
        await svc.AddAsync(T, D1, null, null, default);
        await svc.AddAsync(T, D2, null, null, default);
        (await svc.ClearAsync(T, D1, default)).Should().Be(1);
        (await svc.IsQuarantinedAsync(T, D1, default)).Should().BeFalse();
        (await svc.IsQuarantinedAsync(T, D2, default)).Should().BeTrue();
        (await svc.ClearAsync(T, null, default)).Should().Be(1);
        (await svc.ClearAsync(T, null, default)).Should().Be(0, "nothing left to clear");
        (await svc.ListAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_cleared_pair_can_be_quarantined_again()
    {
        var (svc, _, time) = Make();
        await svc.AddAsync(T, D1, "run1", "first", default);
        await svc.ClearAsync(T, D1, default);
        time.Advance(TimeSpan.FromHours(1));
        (await svc.AddAsync(T, D1, "run3", "second", default)).Should().BeTrue();
        (await svc.IsQuarantinedAsync(T, D1, default)).Should().BeTrue();
        (await svc.ListAsync(default)).Should().ContainSingle().Which.RunId.Should().Be("run3");
    }

    [Fact]
    public async Task The_list_is_newest_first()
    {
        var (svc, _, time) = Make();
        await svc.AddAsync("a:1", D1, null, null, default);
        time.Advance(TimeSpan.FromMinutes(1));
        await svc.AddAsync("b:1", D1, null, null, default);
        (await svc.ListAsync(default)).Select(e => e.Target).Should().Equal("b:1", "a:1");
    }
}

public class ReclaimMathTests
{
    [Fact]
    public void The_df_delta_is_the_headline_when_both_snapshots_have_it()
    {
        var r = ReclaimMath.Compute(new UsageSnapshot(10_000, 500), new UsageSnapshot(7_000, 3_600), 3_000, null);
        r.Basis.Should().Be("df");
        r.DfDeltaBytes.Should().Be(3_000);
        r.FreeSpaceDeltaBytes.Should().Be(3_100);
        r.ReclaimedBytes.Should().Be(3_000);
        r.EstimatedBytes.Should().Be(3_000);
        r.Caveat.Should().BeNull();
    }

    [Fact]
    public void Without_df_the_free_space_growth_is_used_when_positive_else_the_estimate()
    {
        ReclaimMath.Compute(new UsageSnapshot(null, 100), new UsageSnapshot(null, 900), 5, null).Should().Match<ReclaimReport>(r => r.Basis == "statfs" && r.ReclaimedBytes == 800);
        ReclaimMath.Compute(new UsageSnapshot(null, 900), new UsageSnapshot(null, 100), 5, null).Should().Match<ReclaimReport>(r => r.Basis == "estimate" && r.ReclaimedBytes == 5 && r.FreeSpaceDeltaBytes == -800);
        ReclaimMath.Compute(new UsageSnapshot(null, null), new UsageSnapshot(null, null), 42, null).Should().Match<ReclaimReport>(r => r.Basis == "estimate" && r.ReclaimedBytes == 42 && r.FreeSpaceDeltaBytes == null && r.DfDeltaBytes == null);
    }

    [Fact]
    public void A_df_that_grew_is_a_zero_delta_not_a_negative_one()
    {
        var r = ReclaimMath.Compute(new UsageSnapshot(1_000, 0), new UsageSnapshot(5_000, 0), 300, null);
        r.DfDeltaBytes.Should().Be(0);
        r.ReclaimedBytes.Should().Be(0);
        r.Caveat.Should().Contain("grew meanwhile");
    }

    [Fact]
    public void The_headline_never_exceeds_what_the_removed_items_occupied()
    {
        var r = ReclaimMath.Compute(new UsageSnapshot(9_000_000, 0), new UsageSnapshot(1_000, 0), 100, 250);
        r.DfDeltaBytes.Should().Be(8_999_000, "the raw measurement is still reported");
        r.ReclaimedBytes.Should().Be(250);
        r.Caveat.Should().Contain("capped");
    }

    [Fact]
    public void Nothing_removed_means_nothing_reclaimed_whatever_else_changed()
    {
        ReclaimMath.Compute(new UsageSnapshot(9_000, 0), new UsageSnapshot(1_000, 0), 0, 0).ReclaimedBytes.Should().Be(0);
    }

    [Fact]
    public void The_note_explains_the_measurement()
    {
        ReclaimMath.Compute(new UsageSnapshot(1, 1), new UsageSnapshot(1, 1), 0, null).Note.Should().Contain("Reclaimed =");
    }
}

public class ImageCleanupRulesTests
{
    private static readonly long Now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const long Hour = 3_600_000;

    private static SafetyPolicy Policy(params string[] scope) => new(new SafetyOptions(false, scope, [], []));

    private static ImageContext Ctx(
        SafetyPolicy? policy = null,
        long minAgeHours = 24,
        LabelFilter? filter = null,
        Dictionary<string, string>? usedBy = null,
        string[]? rollbackIds = null,
        string[]? projectRefs = null) =>
        new(
            policy ?? Policy(),
            Now,
            minAgeHours * Hour,
            filter,
            usedBy ?? [],
            new HashSet<string>(rollbackIds ?? []),
            new HashSet<string>(projectRefs ?? []));

    private static ImageFacts Image(string id = "sha256:aaa", string[]? tags = null, Dictionary<string, string>? labels = null, long ageHours = 100) =>
        new(id, tags ?? ["old:1"], labels ?? [], Now - (ageHours * Hour));

    [Fact]
    public void An_old_unused_tagged_image_is_a_candidate()
    {
        ImageCleanupRules.ProtectReason(Image(), Ctx()).Should().BeNull();
    }

    [Fact]
    public void An_image_used_by_any_container_is_protected_and_names_it()
    {
        ImageCleanupRules.ProtectReason(Image(), Ctx(usedBy: new() { ["sha256:aaa"] = "web" })).Should().Be("in use by container web");
    }

    [Fact]
    public void Rollback_points_compose_projects_and_internal_tags_protect()
    {
        ImageCleanupRules.ProtectReason(Image(), Ctx(rollbackIds: ["sha256:aaa"])).Should().Contain("rollback point within retention");
        ImageCleanupRules.ProtectReason(Image(tags: ["nginx:1.27"]), Ctx(projectRefs: ["docker.io/library/nginx:1.27"])).Should().Contain("Compose project");
        ImageCleanupRules.ProtectReason(Image(tags: ["nginx:1.27"]), Ctx(projectRefs: ["nginx:1.27"])).Should().Contain("Compose project");
        ImageCleanupRules.ProtectReason(Image(tags: ["old:1", "quaymaster-staged/abc:def"]), Ctx()).Should().Contain("staged/rollback tag");
    }

    [Theory]
    [InlineData(10, 24, true)]
    [InlineData(23, 24, true)]
    [InlineData(24, 24, false)]
    [InlineData(100, 24, false)]
    [InlineData(1, 0, false)]
    public void The_minimum_age_is_inclusive_of_exactly_the_limit(long age, long min, bool protectedByAge)
    {
        var reason = ImageCleanupRules.ProtectReason(Image(ageHours: age), Ctx(minAgeHours: min));
        (reason is not null).Should().Be(protectedByAge);
        if (protectedByAge)
        {
            reason.Should().Contain("minimum age");
        }
    }

    [Fact]
    public void The_label_filter_matches_key_or_key_equals_value()
    {
        var labels = new Dictionary<string, string> { ["team"] = "a", ["tier"] = "x" };
        ImageCleanupRules.ProtectReason(Image(labels: labels), Ctx(filter: LabelFilter.Parse("team"))).Should().BeNull();
        ImageCleanupRules.ProtectReason(Image(labels: labels), Ctx(filter: LabelFilter.Parse("team=a"))).Should().BeNull();
        ImageCleanupRules.ProtectReason(Image(labels: labels), Ctx(filter: LabelFilter.Parse("team=b"))).Should().Contain("label filter team=b");
        ImageCleanupRules.ProtectReason(Image(), Ctx(filter: LabelFilter.Parse("team"))).Should().Contain("label filter team");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a.b-c_d=")]
    [InlineData("a=b.c")]
    public void Valid_label_filters_parse(string raw) => LabelFilter.Parse(raw).Should().NotBeNull();

    [Theory]
    [InlineData("a b")]
    [InlineData("a=b=c")]
    [InlineData("=x")]
    [InlineData("a;rm")]
    public void Invalid_label_filters_are_rejected(string raw) => ((Func<object?>)(() => LabelFilter.Parse(raw))).Should().Throw<FormatException>();

    [Fact]
    public void With_a_managed_scope_only_matching_repositories_are_candidates_and_untagged_images_need_a_label_filter()
    {
        var scoped = Ctx(policy: Policy("old*"));
        ImageCleanupRules.ProtectReason(Image(tags: ["old:1"]), scoped).Should().BeNull();
        ImageCleanupRules.ProtectReason(Image(tags: ["registry.io/team/oldish:2"]), scoped).Should().BeNull("scope globs match the repository's base name");
        ImageCleanupRules.ProtectReason(Image(tags: ["other:1"]), scoped).Should().Contain("outside QM_MANAGED_SCOPE");
        ImageCleanupRules.ProtectReason(Image(tags: []), scoped).Should().Contain("untagged image");
        ImageCleanupRules.ProtectReason(Image(tags: [], labels: new() { ["x"] = "1" }), Ctx(policy: Policy("old*"), filter: LabelFilter.Parse("x"))).Should().BeNull();
    }

    [Fact]
    public void ScopeProblem_is_null_without_a_scope()
    {
        ImageCleanupRules.ScopeProblem(Policy(), ["anything:1"], null).Should().BeNull();
        ImageCleanupRules.ScopeProblem(Policy(), [], null).Should().BeNull();
    }

    private static RollbackPoint Rb(string tag, long expiresInHours, string? project = "shop") =>
        new(tag, "run1", project, "web", null, "nginx:1", "sha256:aaa", Now - (50 * Hour), Now + (expiresInHours * Hour), false);

    [Fact]
    public void Expired_rollback_and_staged_tags_become_candidates_only_after_retention()
    {
        var retention = 7 * 24 * Hour;
        var p = Policy();
        ImageCleanupRules.ExpiredTag("quaymaster-rollback/a:1", Rb("quaymaster-rollback/a:1", -1), null, retention, Now, p, null).Protect.Should().BeNull();
        ImageCleanupRules.ExpiredTag("quaymaster-rollback/a:1", Rb("quaymaster-rollback/a:1", 5), null, retention, Now, p, null).Protect.Should().Contain("within retention");
        ImageCleanupRules.ExpiredTag("quaymaster-rollback/a:1", null, null, retention, Now, p, null).Protect.Should().Contain("no rollback record");
        var oldStaged = new StagedImage("quaymaster-staged/a:1", "x", "sha256:b", null, Now - (8 * 24 * Hour));
        var newStaged = oldStaged with { CreatedAt = Now - Hour };
        ImageCleanupRules.ExpiredTag(oldStaged.Tag, null, oldStaged, retention, Now, p, null).Protect.Should().BeNull();
        ImageCleanupRules.ExpiredTag(newStaged.Tag, null, newStaged, retention, Now, p, null).Protect.Should().Contain("staged image within retention");
        ImageCleanupRules.ExpiredTag(oldStaged.Tag, null, oldStaged, retention, Now, p, null).AgeHours.Should().Be(8 * 24);
    }

    [Fact]
    public void An_expired_rollback_tag_outside_the_scope_is_kept()
    {
        var retention = 7 * 24 * Hour;
        ImageCleanupRules.ExpiredTag("quaymaster-rollback/a:1", Rb("quaymaster-rollback/a:1", -1, "billing"), null, retention, Now, Policy("shop"), null).Protect.Should().Contain("outside QM_MANAGED_SCOPE");
        ImageCleanupRules.ExpiredTag("quaymaster-rollback/a:1", Rb("quaymaster-rollback/a:1", -1, "shop"), null, retention, Now, Policy("shop"), null).Protect.Should().BeNull();
    }

    [Fact]
    public void ProtectedTag_guards_compose_named_tags_and_internal_tags_within_retention()
    {
        var retention = 7 * 24 * Hour;
        var refs = new HashSet<string> { "docker.io/library/nginx:1.27" };
        ImageCleanupRules.ProtectedTag("nginx:1.27", null, null, retention, Now, refs).Should().Contain("named by a discovered Compose project");
        ImageCleanupRules.ProtectedTag("redis:7", null, null, retention, Now, refs).Should().BeNull();
        ImageCleanupRules.ProtectedTag("quaymaster-rollback/a:1", Rb("quaymaster-rollback/a:1", 3), null, retention, Now, refs).Should().Contain("rollback point within retention");
        ImageCleanupRules.ProtectedTag("quaymaster-rollback/a:1", null, null, retention, Now, refs).Should().Contain("no rollback record");
        ImageCleanupRules.ProtectedTag("quaymaster-rollback/a:1", Rb("quaymaster-rollback/a:1", -3), null, retention, Now, refs).Should().BeNull();
    }

    [Fact]
    public void UniqueSize_subtracts_shared_layers_and_falls_back_when_unknown()
    {
        ImageCleanupRules.UniqueSize(1000, 300, 5).Should().Be(700);
        ImageCleanupRules.UniqueSize(1000, null, 5).Should().Be(1000);
        ImageCleanupRules.UniqueSize(null, null, 5).Should().Be(5);
        ImageCleanupRules.UniqueSize(100, 500, 5).Should().Be(0);
    }
}

public class StorageRulesTests
{
    private static SafetyPolicy Policy(string[]? scope = null, SelfInfo? self = null)
    {
        var p = new SafetyPolicy(new SafetyOptions(false, scope ?? [], [], []));
        if (self is not null)
        {
            p.Self = self;
        }

        return p;
    }

    private static VolumeFacts Vol(string name, string? project = null, string? created = "2026-01-01T00:00:00Z") =>
        new(name, project is null ? new Dictionary<string, string>() : new() { ["com.docker.compose.project"] = project }, created);

    private static readonly ComposeOwnership Own = new(true, new HashSet<string> { "shop" }, new Dictionary<string, string> { ["shop_data"] = "shop" }, new Dictionary<string, string> { ["shop_default"] = "shop" });

    [Fact]
    public void A_plain_unused_volume_is_eligible_and_a_used_one_is_not()
    {
        StorageRules.VolumeRefusal(Policy(), Vol("scratch"), [], ComposeOwnership.Empty, false).Should().BeNull();
        var r = StorageRules.VolumeRefusal(Policy(), Vol("scratch"), ["db"], ComposeOwnership.Empty, false)!;
        r.Kind.Should().Be(RefusalKind.Conflict);
        r.Reason.Should().Contain("in use by container db").And.Contain("a stopped container counts");
    }

    [Fact]
    public void Compose_volumes_need_an_explicit_opt_in_even_when_the_project_cannot_be_found()
    {
        var known = StorageRules.VolumeRefusal(Policy(), Vol("shop_data", "shop"), [], Own, false)!;
        known.ComposeOptIn.Should().BeTrue();
        known.Reason.Should().Contain("still exists on disk");
        var unknown = StorageRules.VolumeRefusal(Policy(), Vol("ghost_db", "ghost"), [], Own, false)!;
        unknown.ComposeOptIn.Should().BeTrue();
        unknown.Reason.Should().Contain("cannot find");
        StorageRules.VolumeRefusal(Policy(), Vol("shop_data", "shop"), [], Own, true).Should().BeNull();
        StorageRules.VolumeRefusal(Policy(), Vol("shop_data"), [], Own, false)!.ComposeOptIn.Should().BeTrue("declared by a project's config even without labels");
    }

    [Fact]
    public void When_discovery_failed_compose_volumes_are_kept_even_with_the_opt_in()
    {
        var failed = new ComposeOwnership(false, new HashSet<string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
        var r = StorageRules.VolumeRefusal(Policy(), Vol("x", "p"), [], failed, true)!;
        r.Reason.Should().Contain("could not be discovered");
        r.ComposeOptIn.Should().BeFalse();
        StorageRules.VolumeRefusal(Policy(), Vol("plain"), [], failed, false).Should().BeNull("non-compose volumes do not depend on discovery");
    }

    [Fact]
    public void Self_protection_and_scope_come_before_everything_else()
    {
        var self = new SelfInfo(true, "abc", "qm", "qm", ["qm_default"], ["qm_data"], null);
        var p = Policy(self: self);
        StorageRules.VolumeRefusal(p, Vol("qm_data"), [], ComposeOwnership.Empty, true)!.Kind.Should().Be(RefusalKind.SelfProtected);
        StorageRules.NetworkRefusal(p, new NetworkFacts("id", "qm_default", new Dictionary<string, string>(), false, 0, null), [], ComposeOwnership.Empty)!.Kind.Should().Be(RefusalKind.SelfProtected);
        var scoped = Policy(["shop_*"]);
        StorageRules.VolumeRefusal(scoped, Vol("other"), [], ComposeOwnership.Empty, false)!.Kind.Should().Be(RefusalKind.OutOfScope);
        StorageRules.VolumeRefusal(scoped, Vol("shop_tmp"), [], ComposeOwnership.Empty, false).Should().BeNull();
        StorageRules.VolumeRefusal(scoped, Vol("shop_tmp", "billing"), [], ComposeOwnership.Empty, true)!.Kind.Should().Be(RefusalKind.OutOfScope, "the project must be in scope too");
    }

    [Fact]
    public void Networks_with_attached_containers_predefined_and_compose_networks_are_refused()
    {
        NetworkFacts Net(string name, int attached = 0, bool ingress = false, string? project = null) =>
            new("id-" + name, name, project is null ? new Dictionary<string, string>() : new() { ["com.docker.compose.project"] = project }, ingress, attached, null);

        StorageRules.NetworkRefusal(Policy(), Net("free"), [], ComposeOwnership.Empty).Should().BeNull();
        StorageRules.NetworkRefusal(Policy(), Net("bridge"), [], ComposeOwnership.Empty)!.Reason.Should().Be("predefined Docker network");
        StorageRules.NetworkRefusal(Policy(), Net("ing", ingress: true), [], ComposeOwnership.Empty)!.Reason.Should().Be("predefined Docker network");
        StorageRules.NetworkRefusal(Policy(), Net("busy", attached: 2), [], ComposeOwnership.Empty)!.Reason.Should().Contain("attached");
        StorageRules.NetworkRefusal(Policy(), Net("stopped"), ["db"], ComposeOwnership.Empty)!.Reason.Should().Contain("db");
        StorageRules.NetworkRefusal(Policy(), Net("shop_default", project: "shop"), [], Own)!.Reason.Should().Contain("Compose project \"shop\"");
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null, true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "shop", false)]
    [InlineData("named", null, false)]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", null, false)]
    public void Anonymous_means_a_64_hex_name_without_a_compose_project(string name, string? project, bool anonymous) =>
        StorageRules.IsAnonymous(name, project is null ? new Dictionary<string, string>() : new() { ["com.docker.compose.project"] = project }).Should().Be(anonymous);

    [Fact]
    public void HoursSince_handles_missing_zero_and_invalid_dates()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        StorageRules.HoursSince("2026-11-14T00:00:00Z", now).Should().Be(12);
        StorageRules.HoursSince(null, now).Should().BeNull();
        StorageRules.HoursSince("0001-01-01T00:00:00Z", now).Should().BeNull();
        StorageRules.HoursSince("garbage", now).Should().BeNull();
    }
}
