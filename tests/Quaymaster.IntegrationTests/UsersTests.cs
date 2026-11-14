using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class UsersTests : IDisposable
{
    private readonly QmFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static object NewUser(string name, string role = "viewer", string password = "viewer password 1") =>
        new { username = name, password, role };

    [Fact]
    public async Task Admin_Can_Create_List_Update_Reset_Password_And_Delete()
    {
        var admin = await _factory.NewAdminAsync();

        var created = await admin.PostAsync("/users", NewUser("bob"));
        created.Status.Should().Be(201);
        created.Json.GetProperty("username").GetString().Should().Be("bob");
        created.Json.GetProperty("role").GetString().Should().Be("viewer");
        created.Json.GetProperty("logsRead").GetBoolean().Should().BeFalse();
        created.Json.GetProperty("disabled").GetBoolean().Should().BeFalse();
        created.Json.EnumerateObject().Select(p => p.Name).Should().Equal("id", "username", "role", "logsRead", "disabled", "createdAt");
        var id = created.Json.GetProperty("id").GetInt64();

        var list = await admin.GetAsync("/users");
        list.Status.Should().Be(200);
        list.Json.EnumerateArray().Select(u => u.GetProperty("username").GetString()).Should().Equal("admin", "bob");

        var patched = await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { logsRead = true });
        patched.Status.Should().Be(200);
        patched.Json.GetProperty("logsRead").GetBoolean().Should().BeTrue();

        var reset = await admin.PostAsync($"/users/{id}/password", new { password = "freshly reset password" });
        reset.Status.Should().Be(200);
        reset.Json.GetRawText().Should().Be("{\"ok\":true}");
        (await _factory.NewClient().LoginAsync("bob", "viewer password 1")).Status.Should().Be(401);
        (await _factory.NewClient().LoginAsync("bob", "freshly reset password")).Status.Should().Be(200);

        var deleted = await admin.SendAsync(HttpMethod.Delete, $"/users/{id}");
        deleted.Status.Should().Be(200);
        deleted.Json.GetRawText().Should().Be("{\"ok\":true}");
        (await admin.GetAsync("/users")).Json.GetArrayLength().Should().Be(1);
        (await _factory.NewClient().LoginAsync("bob", "freshly reset password")).Status.Should().Be(401);
    }

    [Fact]
    public async Task Viewers_Cannot_Manage_Users_And_Anonymous_Callers_Get_401()
    {
        var admin = await _factory.NewAdminAsync();
        await admin.PostAsync("/users", NewUser("vera"));
        var viewer = _factory.NewClient();
        (await viewer.LoginAsync("vera", "viewer password 1")).Status.Should().Be(200);

        var list = await viewer.GetAsync("/users");
        list.Status.Should().Be(403);
        list.ErrorCode.Should().Be("forbidden");
        list.ErrorMessage.Should().Be("Administrator role required");
        (await viewer.PostAsync("/users", NewUser("x"))).Status.Should().Be(403);
        (await viewer.SendAsync(HttpMethod.Delete, "/users/1")).Status.Should().Be(403);
        (await viewer.GetAsync("/auth/me")).Json.GetProperty("user").GetProperty("role").GetString().Should().Be("viewer");

        var anonymous = await _factory.NewClient().GetAsync("/users", auth: false);
        anonymous.Status.Should().Be(401);
        anonymous.ErrorCode.Should().Be("unauthorized");
    }

    [Fact]
    public async Task Duplicate_Usernames_Conflict_Regardless_Of_Case()
    {
        var admin = await _factory.NewAdminAsync();

        (await admin.PostAsync("/users", NewUser("Dora"))).Status.Should().Be(201);
        var dup = await admin.PostAsync("/users", NewUser("dORA"));

        dup.Status.Should().Be(409);
        dup.ErrorCode.Should().Be("conflict");
        dup.ErrorMessage.Should().Be("Username already exists");
        (await admin.PostAsync("/users", NewUser("ADMIN"))).Status.Should().Be(409);
    }

    [Fact]
    public async Task Create_Validates_Username_Password_And_Role()
    {
        var admin = await _factory.NewAdminAsync();

        var badName = await admin.PostAsync("/users", NewUser("no spaces!"));
        badName.Status.Should().Be(400);
        badName.ErrorMessage.Should().Be("Username must be 1-64 characters: letters, digits, . _ @ -");

        var weak = await admin.PostAsync("/users", NewUser("erin", password: "tooshort"));
        weak.Status.Should().Be(400);
        weak.ErrorMessage.Should().Be("Password must be at least 12 characters long");

        var role = await admin.PostAsync("/users", new { username = "erin", password = "viewer password 1", role = "root" });
        role.Status.Should().Be(400);
        role.Details.Should().Equal(("role", "Invalid enum value. Expected 'admin' | 'viewer', received 'root'"));
    }

    [Fact]
    public async Task Changing_A_Users_Privileges_Revokes_Their_Sessions_Immediately()
    {
        var admin = await _factory.NewAdminAsync();
        var created = await admin.PostAsync("/users", NewUser("finn"));
        var id = created.Json.GetProperty("id").GetInt64();
        var viewer = _factory.NewClient();
        await viewer.LoginAsync("finn", "viewer password 1");
        (await viewer.GetAsync("/auth/me")).Status.Should().Be(200);

        (await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { logsRead = true })).Status.Should().Be(200);

        (await viewer.GetAsync("/auth/me")).Status.Should().Be(401);
        var back = _factory.NewClient();
        (await back.LoginAsync("finn", "viewer password 1")).Json.GetProperty("user").GetProperty("logsRead").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_A_User_Blocks_Login_And_Kills_Sessions()
    {
        var admin = await _factory.NewAdminAsync();
        var id = (await admin.PostAsync("/users", NewUser("gus"))).Json.GetProperty("id").GetInt64();
        var gus = _factory.NewClient();
        await gus.LoginAsync("gus", "viewer password 1");

        (await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { disabled = true })).Json.GetProperty("disabled").GetBoolean().Should().BeTrue();

        (await gus.GetAsync("/auth/me")).Status.Should().Be(401);
        (await _factory.NewClient().LoginAsync("gus", "viewer password 1")).Status.Should().Be(401);
    }

    [Fact]
    public async Task The_Last_Active_Admin_Is_Protected_From_Demotion_Disabling_And_Deletion()
    {
        var admin = await _factory.NewAdminAsync();
        var me = (await admin.GetAsync("/auth/me")).Json.GetProperty("user").GetProperty("id").GetInt64();

        var demote = await admin.SendAsync(HttpMethod.Patch, $"/users/{me}", new { role = "viewer" });
        demote.Status.Should().Be(409);
        demote.ErrorCode.Should().Be("conflict");
        demote.ErrorMessage.Should().Be("Cannot demote or disable the last active admin");
        (await admin.SendAsync(HttpMethod.Patch, $"/users/{me}", new { disabled = true })).Status.Should().Be(409);
        var delete = await admin.SendAsync(HttpMethod.Delete, $"/users/{me}");
        delete.Status.Should().Be(409);
        delete.ErrorMessage.Should().Be("Cannot delete the last active admin");

        // add a second admin: now the first can be demoted, and the second is the last one
        var second = (await admin.PostAsync("/users", NewUser("root2", "admin", "second admin pass"))).Json.GetProperty("id").GetInt64();
        (await admin.SendAsync(HttpMethod.Patch, $"/users/{second}", new { role = "admin" })).Status.Should().Be(200);
        var other = _factory.NewClient();
        await other.LoginAsync("root2", "second admin pass");
        (await other.SendAsync(HttpMethod.Patch, $"/users/{me}", new { role = "viewer" })).Status.Should().Be(200);
        (await other.SendAsync(HttpMethod.Delete, $"/users/{second}")).Status.Should().Be(409);
        (await admin.GetAsync("/auth/me")).Status.Should().Be(401, "demotion revoked the demoted user's session");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public async Task Malformed_Ids_Are_Validation_Errors(string id)
    {
        var admin = await _factory.NewAdminAsync();

        var response = await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { disabled = true });

        response.Status.Should().Be(400);
        response.ErrorCode.Should().Be("validation");
        response.Details.Should().ContainSingle().Which.Path.Should().Be("id");
    }

    [Fact]
    public async Task Unknown_Users_Are_404()
    {
        var admin = await _factory.NewAdminAsync();

        var patch = await admin.SendAsync(HttpMethod.Patch, "/users/999", new { disabled = true });
        patch.Status.Should().Be(404);
        patch.ErrorCode.Should().Be("not_found");
        patch.ErrorMessage.Should().Be("User not found");
        (await admin.SendAsync(HttpMethod.Delete, "/users/999")).Status.Should().Be(404);
        (await admin.PostAsync("/users/999/password", new { password = "some long password" })).Status.Should().Be(404);
    }
}
