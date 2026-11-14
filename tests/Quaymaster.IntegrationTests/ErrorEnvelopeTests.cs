using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Users;
using Quaymaster.Domain.Identity;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class ErrorEnvelopeTests : IDisposable
{
    private readonly QmFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Missing_Fields_Return_A_400_With_The_Details_Array()
    {
        var client = _factory.NewClient();

        var response = await client.PostAsync("/auth/login", new { }, auth: false);

        response.Status.Should().Be(400);
        response.ErrorCode.Should().Be("validation");
        response.ErrorMessage.Should().Be("Invalid request");
        response.Details.Should().Equal(("username", "Required"), ("password", "Required"));
        response.Json.GetRawText().Should().Be(
            "{\"error\":{\"code\":\"validation\",\"message\":\"Invalid request\",\"details\":[{\"path\":\"username\",\"message\":\"Required\"},{\"path\":\"password\",\"message\":\"Required\"}]}}");
    }

    [Fact]
    public async Task Unknown_Properties_Are_Rejected_Like_A_Strict_Zod_Schema()
    {
        var response = await _factory.NewClient().PostAsync("/auth/login", new { username = "a", password = "b", isAdmin = true }, auth: false);

        response.Status.Should().Be(400);
        response.ErrorCode.Should().Be("validation");
        response.Details.Should().ContainSingle().Which.Message.Should().Contain("isAdmin");
    }

    [Fact]
    public async Task Property_Names_Are_Case_Sensitive()
    {
        var response = await _factory.NewClient().SendAsync(HttpMethod.Post, "/auth/login", rawBody: "{\"Username\":\"a\",\"Password\":\"b\"}", auth: false);

        response.Status.Should().Be(400);
        response.ErrorCode.Should().Be("validation");
    }

    [Fact]
    public async Task Wrong_Types_And_Lengths_Name_The_Offending_Field()
    {
        var client = _factory.NewClient();

        var wrongType = await client.SendAsync(HttpMethod.Post, "/auth/login", rawBody: "{\"username\":5,\"password\":\"x\"}", auth: false);
        wrongType.Status.Should().Be(400);
        wrongType.Details.Should().ContainSingle().Which.Path.Should().Be("username");

        var tooLong = await client.PostAsync("/auth/login", new { username = new string('u', 65), password = "x" }, auth: false);
        tooLong.Details.Should().Equal(("username", "String must contain at most 64 character(s)"));

        var empty = await client.PostAsync("/auth/login", new { username = "", password = "x" }, auth: false);
        empty.Details.Should().Equal(("username", "String must contain at least 1 character(s)"));
    }

    [Fact]
    public async Task Malformed_Json_Is_A_Plain_400_Validation_Error()
    {
        var response = await _factory.NewClient().SendAsync(HttpMethod.Post, "/auth/login", rawBody: "{\"username\": ", auth: false);

        response.Status.Should().Be(400);
        response.ErrorCode.Should().Be("validation");
        response.ErrorMessage.Should().Be("Bad request");
    }

    [Fact]
    public async Task A_Body_That_Is_Not_An_Object_Is_Rejected()
    {
        var response = await _factory.NewClient().SendAsync(HttpMethod.Post, "/auth/login", rawBody: "[1,2]", auth: false);

        response.Status.Should().Be(400);
        response.ErrorCode.Should().Be("validation");
    }

    [Fact]
    public async Task Non_Json_Content_Type_Is_415()
    {
        var response = await _factory.NewClient().SendAsync(
            HttpMethod.Post, "/auth/login", rawBody: "username=a&password=b", contentType: "application/x-www-form-urlencoded", auth: false);

        response.Status.Should().Be(415);
        response.ErrorCode.Should().Be("validation");
        response.ErrorMessage.Should().Be("Content-Type must be application/json");
    }

    [Fact]
    public async Task Oversized_Bodies_Are_Rejected_With_413()
    {
        var huge = "{\"username\":\"" + new string('a', 70 * 1024) + "\",\"password\":\"x\"}";

        var response = await _factory.NewClient().SendAsync(HttpMethod.Post, "/auth/login", rawBody: huge, auth: false);

        response.Status.Should().Be(413);
        response.ErrorCode.Should().Be("validation");
        response.ErrorMessage.Should().Be("Request body too large");
    }

    [Fact]
    public async Task An_Empty_Body_Is_Treated_As_An_Empty_Object()
    {
        // the web client sends Content-Type: application/json with no body for logout etc.
        var admin = await _factory.NewAdminAsync();
        var login = await _factory.NewClient().SendAsync(HttpMethod.Post, "/auth/login", rawBody: "", auth: false);

        login.Status.Should().Be(400);
        login.Details.Select(d => d.Path).Should().Equal("username", "password");
        (await admin.PostAsync("/auth/logout")).Status.Should().Be(200);
    }

    [Fact]
    public async Task Unknown_Api_Routes_Return_The_404_Envelope_For_Authenticated_Callers()
    {
        var admin = await _factory.NewAdminAsync();

        var missing = await admin.GetAsync("/definitely-not-a-route");

        missing.Status.Should().Be(404);
        missing.ErrorCode.Should().Be("not_found");
        missing.ErrorMessage.Should().Be("Route not found");
        missing.Json.GetRawText().Should().Be("{\"error\":{\"code\":\"not_found\",\"message\":\"Route not found\"}}");
        missing.Header("Content-Type").Should().StartWith("application/json");
    }

    [Fact]
    public async Task A_Known_Route_With_The_Wrong_Method_Is_Also_A_404_Envelope()
    {
        var admin = await _factory.NewAdminAsync();

        var response = await admin.SendAsync(HttpMethod.Delete, "/auth/me");

        response.Status.Should().Be(404);
        response.ErrorCode.Should().Be("not_found");
    }

    [Fact]
    public async Task Unknown_Non_Api_Paths_Are_404_Envelopes_When_No_Ui_Is_Built()
    {
        var client = _factory.NewClient();

        foreach (var path in new[] { "~/nope.js", "~/some/client/route", "~/" })
        {
            var response = await client.GetAsync(path, auth: false);
            response.Status.Should().Be(404, path);
            response.ErrorCode.Should().Be("not_found");
        }
    }

    [Fact]
    public async Task Encoded_And_Backslash_Paths_Cannot_Slip_Past_The_Auth_Guard()
    {
        var client = _factory.NewClient();

        foreach (var path in new[] { "/api/v1/users", "/%61pi/v1/users", "/api/%76%31/users", "/API/v1/users", "/api/v1/users/", "/api/v1/%75sers", "//api/v1/users" })
        {
            var response = await client.SendAsync(HttpMethod.Get, "~" + path, auth: false);
            (response.Status is 401 or 404).Should().BeTrue($"{path} -> {response.Status}");
            response.Body.Should().NotContain("username", path);
        }
    }

    [Fact]
    public async Task Unexpected_Exceptions_Become_A_Generic_500_Without_Leaking_Details()
    {
        using var factory = new QmFactory(
            null,
            services => services.AddSingleton<IUserService>(sp => new ThrowingListUserService(ActivatorUtilities.CreateInstance<UserService>(sp))));
        var admin = await factory.NewAdminAsync();

        var response = await admin.GetAsync("/users");

        response.Status.Should().Be(500);
        response.ErrorCode.Should().Be("internal");
        response.ErrorMessage.Should().Be("Internal server error");
        response.Body.Should().NotContain("shadow").And.NotContain("InvalidOperation").And.NotContain("   at ");
        response.Json.GetProperty("error").TryGetProperty("details", out _).Should().BeFalse();
    }

    private sealed class ThrowingListUserService(IUserService inner) : IUserService
    {
        public Task<IReadOnlyList<UserInfoDto>> ListAsync(CancellationToken ct) =>
            throw new InvalidOperationException("secret detail: /etc/shadow and connection string");

        public Task<User> CreateAccountAsync(string username, string password, Role role, bool logsRead, CancellationToken ct) =>
            inner.CreateAccountAsync(username, password, role, logsRead, ct);

        public Task<User> CreateAccountWithHashAsync(string username, string passwordHash, Role role, CancellationToken ct) =>
            inner.CreateAccountWithHashAsync(username, passwordHash, role, ct);

        public Task<UserInfoDto> CreateAsync(string username, string password, Role role, bool logsRead, Actor actor, CancellationToken ct) =>
            inner.CreateAsync(username, password, role, logsRead, actor, ct);

        public Task<UserInfoDto> UpdateAsync(long id, UserPatch patch, Actor actor, CancellationToken ct) => inner.UpdateAsync(id, patch, actor, ct);

        public Task DeleteAsync(long id, Actor actor, CancellationToken ct) => inner.DeleteAsync(id, actor, ct);

        public Task SetPasswordAsync(long id, string password, Actor actor, CancellationToken ct) => inner.SetPasswordAsync(id, password, actor, ct);
    }
}
