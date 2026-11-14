using FluentAssertions;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Updates;
using Xunit;

namespace Quaymaster.UnitTests.Updates;

public class ImageReferenceTests
{
    private static readonly string D1 = "sha256:" + new string('a', 64);

    [Theory]
    [InlineData("nginx", "docker.io", "library/nginx", "latest", null)]
    [InlineData("nginx:1.27", "docker.io", "library/nginx", "1.27", null)]
    [InlineData("bitnami/redis:7", "docker.io", "bitnami/redis", "7", null)]
    [InlineData("docker.io/library/nginx:1", "docker.io", "library/nginx", "1", null)]
    [InlineData("index.docker.io/nginx", "docker.io", "library/nginx", "latest", null)]
    [InlineData("registry-1.docker.io/team/app:x", "docker.io", "team/app", "x", null)]
    [InlineData("ghcr.io/hadieht/quaymaster:0.2.1", "ghcr.io", "hadieht/quaymaster", "0.2.1", null)]
    [InlineData("GHCR.io/Owner/app", "ghcr.io", "Owner/app", "latest", null)]
    [InlineData("127.0.0.1:5055/qmtest-app:latest", "127.0.0.1:5055", "qmtest-app", "latest", null)]
    [InlineData("localhost/app", "localhost", "app", "latest", null)]
    public void Parses_name_tag_and_registry(string input, string registry, string repo, string tag, string? digest)
    {
        if (input.Contains("Owner", StringComparison.Ordinal))
        {
            ImageReferences.Parse(input).Should().BeNull("repository names are lower-case");
            return;
        }

        var r = ImageReferences.Parse(input);
        r.Should().NotBeNull();
        r!.Registry.Should().Be(registry);
        r.Repo.Should().Be(repo);
        r.Tag.Should().Be(tag);
        r.Digest.Should().Be(digest);
    }

    [Fact]
    public void A_digest_reference_has_no_default_tag_and_keeps_an_explicit_one()
    {
        var pinned = ImageReferences.Parse($"nginx@{D1}")!;
        pinned.Tag.Should().BeNull();
        pinned.Digest.Should().Be(D1);
        var both = ImageReferences.Parse($"nginx:1.27@{D1}")!;
        both.Tag.Should().Be("1.27");
        both.Digest.Should().Be(D1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nginx@sha256:short")]
    [InlineData("nginx:")]
    [InlineData("nginx:bad tag")]
    [InlineData("UPPER/case")]
    [InlineData("a//b")]
    [InlineData("-leading")]
    [InlineData("nginx:-1")]
    public void Rejects_invalid_references(string input) => ImageReferences.Parse(input).Should().BeNull();

    [Fact]
    public void A_port_in_the_registry_is_not_mistaken_for_a_tag()
    {
        var r = ImageReferences.Parse("reg.example.com:5000/team/app")!;
        r.Registry.Should().Be("reg.example.com:5000");
        r.Repo.Should().Be("team/app");
        r.Tag.Should().Be("latest");
    }

    [Fact]
    public void Docker_hub_resolves_to_registry_1_for_API_calls_and_keeps_library_images_short_for_the_engine()
    {
        var hub = ImageReferences.Parse("nginx:1")!;
        ImageReferences.ApiHost(hub).Should().Be("registry-1.docker.io");
        ImageReferences.EngineName(hub).Should().Be("nginx");
        ImageReferences.EngineName(ImageReferences.Parse("bitnami/redis")!).Should().Be("bitnami/redis");
        var other = ImageReferences.Parse("ghcr.io/o/a")!;
        ImageReferences.ApiHost(other).Should().Be("ghcr.io");
        ImageReferences.EngineName(other).Should().Be("ghcr.io/o/a");
        ImageReferences.Format(ImageReferences.Parse($"docker.io/library/nginx:1@{D1}")!).Should().Be($"nginx:1@{D1}");
    }

    [Fact]
    public void Cache_keys_are_canonical_so_aliases_share_one_entry()
    {
        ImageReferences.CacheKey(ImageReferences.Parse("nginx")!).Should().Be("docker.io/library/nginx:latest");
        ImageReferences.CacheKey(ImageReferences.Parse("docker.io/library/nginx:latest")!).Should().Be("docker.io/library/nginx:latest");
    }

    [Fact]
    public void DigestsForRepo_only_returns_digests_of_the_same_repository()
    {
        var r = ImageReferences.Parse("nginx:1")!;
        var other = "sha256:" + new string('b', 64);
        ImageReferences.DigestsForRepo([$"nginx@{D1}", $"redis@{other}", $"ghcr.io/x/nginx@{other}"], r).Should().Equal(D1);
    }

    [Fact]
    public void Staged_and_rollback_tags_are_valid_lowercase_repositories_and_deterministic()
    {
        var r = ImageReferences.Parse("ghcr.io/Owner/app")?.Repo; // upper-case is invalid, so this is null
        r.Should().BeNull();
        var img = ImageReferences.Parse("ghcr.io/owner/app:latest")!;
        var tag = ImageReferences.StagedTag(img, D1);
        tag.Should().StartWith("quaymaster-staged/").And.EndWith(":aaaaaaaaaaaa");
        ImageReferences.StagedTag(img, D1).Should().Be(tag);
        ImageReferences.StagedTag(ImageReferences.Parse("ghcr.io/owner/other")!, D1).Should().NotBe(tag);
        var rb = ImageReferences.RollbackTag("shop", "web", "Run-7!");
        rb.Should().StartWith("quaymaster-rollback/").And.EndWith(":run-7");
        ImageReferences.IsInternalTag(tag).Should().BeTrue();
        ImageReferences.IsInternalTag(rb).Should().BeTrue();
        ImageReferences.IsInternalTag("nginx:1").Should().BeFalse();
        ImageReferences.Parse(tag).Should().NotBeNull("an internal tag must itself be a valid reference");
        ImageReferences.Parse(rb).Should().NotBeNull();
    }

    [Fact]
    public void RepoBaseName_is_the_last_path_segment()
    {
        ImageReferences.RepoBaseName(ImageReferences.Parse("ghcr.io/o/team/app")!).Should().Be("app");
    }
}

public class UpdateClassifierTests
{
    private static readonly string Old = "sha256:" + new string('1', 64);
    private static readonly string New = "sha256:" + new string('2', 64);
    private static readonly string Plat = "sha256:" + new string('3', 64);

    private static ClassifyInput Input(
        string raw = "nginx:1",
        string[]? local = null,
        string? remoteDigest = null,
        RemoteError? error = null,
        string[]? platforms = null,
        bool staged = false,
        bool isId = false) =>
        new(
            ImageReferences.Parse(raw),
            raw,
            local ?? [],
            new RemoteLookup(remoteDigest, remoteDigest is null ? "registry" : "registry", error, 0),
            platforms ?? [],
            staged,
            isId);

    private static string State(ClassifyInput i) => UpdateClassifier.Classify(i).State;

    [Fact]
    public void Same_digest_is_up_to_date()
    {
        var c = UpdateClassifier.Classify(Input(local: [$"nginx@{Old}"], remoteDigest: Old));
        c.State.Should().Be(UpdateStates.UpToDate);
        c.LocalDigest.Should().Be(Old);
        c.Reason.Should().BeNull();
    }

    [Fact]
    public void Different_digest_is_an_available_update_and_a_staged_one_when_the_new_image_is_present()
    {
        State(Input(local: [$"nginx@{Old}"], remoteDigest: New)).Should().Be(UpdateStates.UpdateAvailable);
        var staged = UpdateClassifier.Classify(Input(local: [$"nginx@{Old}"], remoteDigest: New, staged: true));
        staged.State.Should().Be(UpdateStates.UpdateStaged);
        staged.Reason.Should().Contain("tag has not moved");
    }

    [Fact]
    public void An_image_pulled_by_platform_digest_is_up_to_date_when_the_index_lists_that_platform()
    {
        State(Input(local: [$"nginx@{Plat}"], remoteDigest: New, platforms: [Plat])).Should().Be(UpdateStates.UpToDate);
    }

    [Fact]
    public void Several_local_digests_match_if_any_equals_the_remote()
    {
        State(Input(local: [$"nginx@{Old}", $"nginx@{New}"], remoteDigest: New)).Should().Be(UpdateStates.UpToDate);
    }

    [Fact]
    public void A_digest_pinned_reference_has_nothing_to_update()
    {
        var c = UpdateClassifier.Classify(Input(raw: $"nginx@{Old}", remoteDigest: New));
        c.State.Should().Be(UpdateStates.PinnedDigest);
        c.Reason.Should().NotBeNull();
    }

    [Fact]
    public void An_image_id_or_unparseable_reference_is_unknown_with_a_reason()
    {
        UpdateClassifier.Classify(Input(raw: "sha256:" + new string('a', 64), isId: true)).Should().Match<Classification>(c => c.State == UpdateStates.Unknown && c.Reason!.Contains("image ID"));
        UpdateClassifier.Classify(Input(raw: "Not A Ref")).Should().Match<Classification>(c => c.State == UpdateStates.Unknown && c.Reason!.Contains("cannot parse"));
    }

    [Fact]
    public void No_local_digest_and_not_found_remotely_means_built_locally()
    {
        State(Input(error: new RemoteError("not_found", "x"))).Should().Be(UpdateStates.LocalOnly);
        State(Input(error: new RemoteError("unauthorized", "denied"))).Should().Be(UpdateStates.LocalOnly);
    }

    [Fact]
    public void A_remote_digest_without_a_local_one_from_this_repository_is_local_only()
    {
        State(Input(local: [$"other@{Old}"], remoteDigest: New)).Should().Be(UpdateStates.LocalOnly);
    }

    [Fact]
    public void A_registry_failure_with_a_known_local_digest_is_unknown_not_an_update()
    {
        var c = UpdateClassifier.Classify(Input(local: [$"nginx@{Old}"], error: new RemoteError("network", "timeout")));
        c.State.Should().Be(UpdateStates.Unknown);
        c.Reason.Should().Be("timeout");
        c.LocalDigest.Should().Be(Old);
    }

    [Fact]
    public void All_six_states_are_reachable()
    {
        var states = new[]
        {
            State(Input(local: [$"nginx@{Old}"], remoteDigest: Old)),
            State(Input(local: [$"nginx@{Old}"], remoteDigest: New)),
            State(Input(local: [$"nginx@{Old}"], remoteDigest: New, staged: true)),
            State(Input(error: new RemoteError("not_found", "x"))),
            State(Input(raw: $"nginx@{Old}")),
            State(Input(raw: "nginx", isId: true)),
        };
        states.Should().BeEquivalentTo(UpdateStates.UpToDate, UpdateStates.UpdateAvailable, UpdateStates.UpdateStaged, UpdateStates.LocalOnly, UpdateStates.PinnedDigest, UpdateStates.Unknown);
    }
}
