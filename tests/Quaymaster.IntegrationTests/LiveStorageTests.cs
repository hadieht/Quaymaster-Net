using System.Diagnostics;
using System.Text.Json;
using Quaymaster.IntegrationTests.Support;
using Xunit.Abstractions;

namespace Quaymaster.IntegrationTests;

/// <summary>
/// T3 against the REAL daemon and registry. Excluded in CI without Docker with <c>--filter "Category!=Live"</c>; without a reachable
/// daemon they pass vacuously. Safety rules for these tests (the host has real data):
/// <list type="bullet">
/// <item>every mutation targets only resources this test created, labelled <c>quaymaster.test=true</c> and named
/// <c>qmlivethrowaway…</c> / <c>qmlive-…</c> / a 64-hex name starting <c>0123456789abcdef</c>;</item>
/// <item>QM_MANAGED_SCOPE is set to exactly those names, so the server itself refuses anything else;</item>
/// <item>the bulk cleanup executes only after its preview was verified to contain exactly the throwaway volume;</item>
/// <item>everything is removed again in <c>finally</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Live")]
[Collection("Live")]
public sealed class LiveStorageTests(ITestOutputHelper output) : IDisposable
{
    private const string Socket = "/var/run/docker.sock";
    private QmFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private static bool DaemonReachable() => File.Exists(Socket) && Docker("version", "--format", "{{.Server.Version}}").Code == 0;

    private static (int Code, string Out) Docker(params string[] args) => DockerIn(null, args);

    private static (int Code, string Out) DockerIn(string? stdin, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }

            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit(180_000);
            return (p.ExitCode, (o.Result + e.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, string.Empty);
        }
    }

    private async Task<ApiClient> AdminAsync(string scope)
    {
        _factory = new QmFactory(new Dictionary<string, string>
        {
            ["QM_MANAGED_SCOPE"] = scope,
            ["QM_READ_ONLY"] = "false",
            ["QM_DOCKER_SOCKET"] = Socket,
            ["QM_STACK_PATHS"] = "/nonexistent-qm-test",
        });
        _factory.Time.SetUtcNow(DateTimeOffset.UtcNow); // the daemon stamps real times
        return await _factory.NewAdminAsync();
    }

    private static string Rand(int n) => Guid.NewGuid().ToString("N")[..n];

    private static async Task<JsonElement> WaitOperationAsync(ApiClient admin, string id)
    {
        for (var i = 0; i < 1200; i++)
        {
            var op = (await admin.GetAsync($"/operations/{id}")).Json.Clone();
            if (op.GetProperty("status").GetString() != "running")
            {
                return op;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("operation did not finish");
    }

    [Fact]
    public async Task Pulls_alpine_by_digest_into_a_staged_tag_without_moving_the_users_tag()
    {
        if (!DaemonReachable() || Docker("image", "inspect", "alpine:latest", "--format", "{{.Id}}").Code != 0)
        {
            return; // never needed to pull anything but the staged copy; alpine must already be the user's tag
        }

        var before = Docker("image", "inspect", "alpine:latest", "--format", "{{.Id}}").Out;
        var admin = await AdminAsync("alpine");
        string? stagedTag = null;
        try
        {
            var r = await admin.PostAsync("/images/pull", new { @ref = "alpine:latest" });
            r.Status.Should().Be(202, r.Body);
            var op = await WaitOperationAsync(admin, r.Json.GetProperty("operationId").GetString()!);
            var text = op.GetProperty("output").GetString() + op.GetProperty("error").GetString();
            output.WriteLine(text);
            if (op.GetProperty("status").GetString() == "failed" && (text.Contains("request failed", StringComparison.Ordinal) || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || text.Contains("429", StringComparison.Ordinal)))
            {
                output.WriteLine("registry not reachable from here: skipping the rest");
                return;
            }

            op.GetProperty("status").GetString().Should().Be("succeeded", text);
            var staged = (await admin.GetAsync("/images/staged")).Json.GetProperty("staged").EnumerateArray().Single();
            stagedTag = staged.GetProperty("tag").GetString()!;
            var digest = staged.GetProperty("digest").GetString()!;
            output.WriteLine($"staged {stagedTag} digest {digest}");
            stagedTag.Should().StartWith("quaymaster-staged/");
            staged.GetProperty("ref").GetString().Should().Be("docker.io/library/alpine:latest");
            digest.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
            text.Should().Contain($"registry digest for alpine:latest: {digest}");

            // the staged tag really exists in the daemon, was pulled by digest, and the user's tag did not move
            var repoDigests = Docker("image", "inspect", stagedTag, "--format", "{{json .RepoDigests}}").Out;
            repoDigests.Should().Contain($"alpine@{digest}");
            Docker("image", "inspect", "alpine:latest", "--format", "{{.Id}}").Out.Should().Be(before, "the user's tag must not move");

            // and update detection can now tell the difference
            var updates = (await admin.PostAsync("/updates/check", new { })).Json.GetProperty("images");
            output.WriteLine(updates.GetRawText());
        }
        finally
        {
            if (stagedTag is not null)
            {
                output.WriteLine("cleanup: " + Docker("rmi", stagedTag).Out); // untag only; deletes the image only if nothing else tags it
            }
        }
    }

    [Fact]
    public async Task Untag_then_delete_cycle_on_a_throwaway_image_with_a_measured_reclaim()
    {
        if (!DaemonReachable() || Docker("image", "inspect", "alpine:latest", "--format", "{{.Id}}").Code != 0)
        {
            return;
        }

        var repo = "qmlivethrowaway" + Rand(6);
        var build = DockerIn(
            "FROM alpine\nLABEL quaymaster.test=true\nRUN head -c 3000000 /dev/urandom > /blob\n",
            "build", "--pull=false", "-t", $"{repo}:1", "-");
        if (build.Code != 0)
        {
            output.WriteLine("build failed, skipping: " + build.Out);
            return;
        }

        try
        {
            Docker("tag", $"{repo}:1", $"{repo}:2").Code.Should().Be(0);
            var id = Docker("image", "inspect", $"{repo}:1", "--format", "{{.Id}}").Out;
            var admin = await AdminAsync(repo + "*");

            var wrong = await admin.PostAsync($"/images/{repo}:1/remove", new { confirm = "wrong" });
            wrong.Status.Should().Be(409);
            wrong.ErrorCode.Should().Be("confirmation_required");

            // untag one of two tags: the image stays
            var untag = await admin.PostAsync($"/images/{repo}:1/remove", new { confirm = $"{repo}:2", tag = $"{repo}:2" });
            untag.Status.Should().Be(200, untag.Body);
            untag.Json.GetProperty("action").GetString().Should().Be("untagged");
            output.WriteLine("untag reclaim: " + untag.Json.GetProperty("reclaim").GetRawText());
            Docker("image", "inspect", $"{repo}:2").Code.Should().NotBe(0);
            Docker("image", "inspect", $"{repo}:1", "--format", "{{.Id}}").Out.Should().Be(id);

            // delete the image
            var del = await admin.PostAsync($"/images/{repo}:1/remove", new { confirm = $"{repo}:1" });
            del.Status.Should().Be(200, del.Body);
            del.Json.GetProperty("action").GetString().Should().Be("deleted");
            var reclaim = del.Json.GetProperty("reclaim");
            output.WriteLine("delete reclaim: " + reclaim.GetRawText());
            Docker("image", "inspect", $"{repo}:1").Code.Should().NotBe(0, "the image is gone");
            reclaim.GetProperty("estimatedBytes").GetInt64().Should().BeGreaterThan(2_500_000, "the 3 MB blob is the image's own layer");
            reclaim.GetProperty("dfDeltaBytes").ValueKind.Should().Be(JsonValueKind.Number, "df was measured before and after");
            reclaim.GetProperty("reclaimedBytes").GetInt64().Should().BeInRange(0, 10_000_000);

            // a preview against the real daemon: scoped to the throwaway name, so it lists nothing real and removes nothing
            var preview = await admin.PostAsync("/images/cleanup/preview", new { categories = new[] { "unused_images", "dangling_images" } });
            preview.Status.Should().Be(200, preview.Body);
            preview.Json.GetProperty("candidates").EnumerateArray().Should().BeEmpty("nothing real is in the managed scope");
        }
        finally
        {
            Docker("rmi", "-f", $"{repo}:1");
            Docker("rmi", "-f", $"{repo}:2");
        }
    }

    [Fact]
    public async Task Anonymous_volume_cleanup_preview_then_execute_on_throwaway_data_and_single_volume_and_network_removal()
    {
        if (!DaemonReachable() || Docker("image", "inspect", "alpine:latest", "--format", "{{.Id}}").Code != 0)
        {
            return;
        }

        var anon = "0123456789abcdef" + Rand(32) + Rand(16);
        var named = "qmlive-" + Rand(8);
        var net = "qmlive-net-" + Rand(8);
        try
        {
            Docker("volume", "create", "--label", "quaymaster.test=true", anon).Code.Should().Be(0);
            Docker("volume", "create", "--label", "quaymaster.test=true", named).Code.Should().Be(0);
            Docker("network", "create", "--label", "quaymaster.test=true", net).Code.Should().Be(0);
            foreach (var v in new[] { anon, named })
            {
                Docker("run", "--rm", "--label", "quaymaster.test=true", "-v", $"{v}:/d", "--entrypoint", "sh", "alpine", "-c", "head -c 2000000 /dev/urandom > /d/blob").Code.Should().Be(0);
            }

            var admin = await AdminAsync("0123456789abcdef*,qmlive-*");

            // ---- bulk: anonymous volumes, preview then execute
            var preview = await admin.PostAsync("/storage/cleanup/preview", new { categories = new[] { "anonymous_volumes" }, minAgeHours = 0 });
            preview.Status.Should().Be(200, preview.Body);
            output.WriteLine("preview: " + preview.Json.GetRawText());
            var names = preview.Json.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
            names.Should().Equal(anon);   // abort BEFORE executing if anything else (real) would be removed
            preview.Json.GetProperty("confirmPhrase").GetString().Should().Be("delete 1 volume");
            preview.Json.GetProperty("candidates")[0].GetProperty("size").GetInt64().Should().BeGreaterThan(1_500_000);

            var id = preview.Json.GetProperty("previewId").GetString()!;
            (await admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = id })).Status.Should().Be(409);
            Docker("volume", "inspect", anon).Code.Should().Be(0, "a wrong confirmation removes nothing");

            var exec = await admin.PostAsync("/storage/cleanup/execute", new { previewId = id, confirm = "delete 1 volume" });
            exec.Status.Should().Be(200, exec.Body);
            output.WriteLine("execute: " + exec.Json.GetRawText());
            exec.Json.GetProperty("removed").EnumerateArray().Select(e => e.GetProperty("name").GetString()).Should().Equal(anon);
            exec.Json.GetProperty("skipped").GetArrayLength().Should().Be(0);
            Docker("volume", "inspect", anon).Code.Should().NotBe(0, "the volume is gone");
            var reclaim = exec.Json.GetProperty("reclaim");
            reclaim.GetProperty("estimatedBytes").GetInt64().Should().BeGreaterThan(1_500_000);
            reclaim.GetProperty("dfDeltaBytes").ValueKind.Should().Be(JsonValueKind.Number);
            reclaim.GetProperty("reclaimedBytes").GetInt64().Should().BeInRange(0, 10_000_000);

            // ---- single volume
            (await admin.PostAsync($"/volumes/{named}/remove", new { confirm = "nope" })).Status.Should().Be(409);
            var one = await admin.PostAsync($"/volumes/{named}/remove", new { confirm = named });
            one.Status.Should().Be(200, one.Body);
            output.WriteLine("volume: " + one.Json.GetProperty("reclaim").GetRawText());
            Docker("volume", "inspect", named).Code.Should().NotBe(0);

            // ---- single network
            (await admin.PostAsync($"/networks/{net}/remove", new { confirm = "nope" })).Status.Should().Be(409);
            var rn = await admin.PostAsync($"/networks/{net}/remove", new { confirm = net });
            rn.Status.Should().Be(200, rn.Body);
            rn.Json.GetProperty("reclaim").GetProperty("reclaimedBytes").GetInt64().Should().Be(0);
            Docker("network", "inspect", net).Code.Should().NotBe(0);

            // ---- the server refuses everything outside the managed scope, whatever the typed confirmation says
            var real = Docker("volume", "ls", "-q").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(v => !v.StartsWith("0123456789abcdef", StringComparison.Ordinal) && !v.StartsWith("qmlive-", StringComparison.Ordinal));
            if (real is not null)
            {
                var refused = await admin.PostAsync($"/volumes/{real}/remove", new { confirm = real });
                refused.Status.Should().Be(409);
                refused.ErrorCode.Should().Be("out_of_scope");
                Docker("volume", "inspect", real).Code.Should().Be(0, "real data is untouched");
            }
        }
        finally
        {
            Docker("volume", "rm", "-f", anon);
            Docker("volume", "rm", "-f", named);
            Docker("network", "rm", net);
        }
    }
}
