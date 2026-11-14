using Microsoft.Data.Sqlite;
using Quaymaster.Api.Cli;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class SecurityTests : IDisposable
{
    private readonly QmFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ---- rate limiting ------------------------------------------------------------------------------

    [Fact]
    public async Task Login_Is_Limited_To_20_Attempts_Per_Ip_Then_429_With_Retry_After_Even_For_Correct_Credentials()
    {
        await _factory.NewAdminAsync();
        var client = _factory.NewClient();

        for (var i = 0; i < 20; i++)
        {
            (await client.LoginAsync($"user{i}", "wrong password")).Status.Should().Be(401, $"attempt {i}");
        }

        var limited = await client.LoginAsync("admin", QmFactory.AdminPassword);

        limited.Status.Should().Be(429);
        limited.ErrorCode.Should().Be("rate_limited");
        limited.ErrorMessage.Should().Be("Too many attempts from this address");
        int.Parse(limited.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture).Should().BeInRange(1, 300);
        limited.Json.GetRawText().Should().StartWith("{\"error\":{\"code\":\"rate_limited\"");

        _factory.Time.Advance(TimeSpan.FromMinutes(6));
        (await client.LoginAsync("admin", QmFactory.AdminPassword)).Status.Should().Be(200);
    }

    [Fact]
    public async Task Repeated_Failures_For_One_Account_Lock_That_Pair()
    {
        await _factory.NewAdminAsync();
        var client = _factory.NewClient();
        for (var i = 0; i < 5; i++)
        {
            var attempt = client.LoginAsync("admin", "wrong password");
            while (!attempt.IsCompleted)
            {
                _factory.Time.Advance(TimeSpan.FromMilliseconds(250)); // let the progressive delay elapse, well inside the lock
                await Task.Delay(5);
            }

            await attempt;
        }

        var locked = await client.LoginAsync("admin", QmFactory.AdminPassword);

        locked.Status.Should().Be(429);
        locked.ErrorMessage.Should().Be("Too many failed attempts; try again later");
    }

    [Fact]
    public async Task Setup_Token_Guessing_Is_Limited_To_5_Attempts_Per_Ip()
    {
        var client = _factory.NewClient();
        for (var i = 0; i < 5; i++)
        {
            (await client.SetupAsync("admin", QmFactory.AdminPassword, token: "guess" + i)).Status.Should().Be(403);
        }

        var limited = await client.SetupAsync("admin", QmFactory.AdminPassword);

        limited.Status.Should().Be(429);
        limited.Header("Retry-After").Should().Be("3600");
        limited.ErrorCode.Should().Be("rate_limited");
    }

    // ---- CSRF / Origin ---------------------------------------------------------------------------------

    [Fact]
    public async Task State_Changes_Need_An_Exact_Origin()
    {
        var admin = await _factory.NewAdminAsync();

        var missing = await admin.PostAsync("/users", new { username = "x", password = "long enough password", role = "viewer" }, origin: false);
        missing.Status.Should().Be(403);
        missing.ErrorCode.Should().Be("origin");
        missing.ErrorMessage.Should().Be("Missing or unexpected Origin header");

        foreach (var bad in new[] { "http://evil.example", "http://localhost/", "http://localhost:81", "https://localhost" })
        {
            var response = await admin.SendAsync(HttpMethod.Post, "/auth/logout", originOverride: bad);
            response.Status.Should().Be(403, bad);
            response.ErrorCode.Should().Be("origin");
        }

        (await admin.GetAsync("/auth/me")).Status.Should().Be(200, "the session survived every rejected request");
    }

    [Fact]
    public async Task Authenticated_State_Changes_Need_The_Session_Bound_Csrf_Token()
    {
        var admin = await _factory.NewAdminAsync();
        var other = _factory.NewClient();
        (await other.LoginAsync("admin", QmFactory.AdminPassword)).Status.Should().Be(200);
        var body = new { username = "x", password = "long enough password", role = "viewer" };

        var missing = await admin.PostAsync("/users", body, csrf: false);
        missing.Status.Should().Be(403);
        missing.ErrorCode.Should().Be("csrf");
        missing.ErrorMessage.Should().Be("Missing or invalid X-QM-CSRF token");
        (await admin.SendAsync(HttpMethod.Post, "/users", body, csrfOverride: "deadbeef")).ErrorCode.Should().Be("csrf");
        (await admin.SendAsync(HttpMethod.Post, "/users", body, csrfOverride: other.Csrf)).ErrorCode.Should().Be("csrf");
        (await admin.PostAsync("/users", body)).Status.Should().Be(201);
    }

    [Fact]
    public async Task Public_Endpoints_Need_An_Origin_But_No_Csrf_And_Safe_Methods_Need_Neither()
    {
        await _factory.NewAdminAsync();
        var client = _factory.NewClient();

        var noOrigin = await client.PostAsync("/auth/login", new { username = "admin", password = QmFactory.AdminPassword }, auth: false, origin: false);
        noOrigin.Status.Should().Be(403);
        noOrigin.ErrorCode.Should().Be("origin");

        (await client.LoginAsync("admin", QmFactory.AdminPassword)).Status.Should().Be(200);
        (await client.SendAsync(HttpMethod.Get, "/auth/me", origin: false, csrf: false)).Status.Should().Be(200);
    }

    [Fact]
    public async Task Authentication_Is_Checked_Before_Csrf_So_Anonymous_Callers_Get_401()
    {
        var response = await _factory.NewClient().PostAsync("/users", new { }, auth: false, origin: true, csrf: false);

        response.Status.Should().Be(401);
        response.ErrorCode.Should().Be("unauthorized");
    }

    // ---- headers ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Security_Headers_Match_The_Original_On_Every_Kind_Of_Response()
    {
        var admin = await _factory.NewAdminAsync();
        var responses = new[]
        {
            await admin.GetAsync("~/healthz"), await admin.GetAsync("/auth/me"), await admin.GetAsync("/nope"),
            await admin.GetAsync("~/missing.js", auth: false), await _factory.NewClient().GetAsync("/users", auth: false),
        };

        foreach (var response in responses)
        {
            response.Header("Content-Security-Policy").Should().Be(
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'");
            response.Header("X-Frame-Options").Should().Be("DENY");
            response.Header("X-Content-Type-Options").Should().Be("nosniff");
            response.Header("Referrer-Policy").Should().Be("no-referrer");
            response.Header("Cross-Origin-Resource-Policy").Should().Be("same-origin");
            response.Header("Cross-Origin-Opener-Policy").Should().Be("same-origin");
            response.Header("Permissions-Policy").Should().Be("camera=(), microphone=(), geolocation=()");
            response.Header("Strict-Transport-Security").Should().BeNull("HSTS only with QM_COOKIE_SECURE");
            response.Header("Server").Should().BeNull();
        }

        responses[0].Header("Cache-Control").Should().Be("no-store");
        responses[1].Header("Cache-Control").Should().Be("no-store");
        responses[2].Header("Cache-Control").Should().Be("no-store");
        responses[4].Header("Cache-Control").Should().Be("no-store");
    }

    // ---- audit + schema ----------------------------------------------------------------------------------------------

    private List<(string Actor, string Action, string Outcome, string? Target, string? Params)> AuditRows()
    {
        using var connection = new SqliteConnection($"Data Source={_factory.DatabaseFile};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT actor, action, outcome, target, params FROM audit_log ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, string?, string?)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static object? Scalar(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task Every_Auth_And_User_Event_Is_Audited_Without_Secrets_Or_Raw_Failed_Usernames()
    {
        var admin = await _factory.NewAdminAsync();
        var id = (await admin.PostAsync("/users", new { username = "audited", password = "viewer password 1", role = "viewer" })).Json.GetProperty("id").GetInt64();
        await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { disabled = true });
        await admin.PostAsync($"/users/{id}/password", new { password = "some other password" });
        await admin.SendAsync(HttpMethod.Delete, $"/users/{id}");
        await _factory.NewClient().LoginAsync("typed-a-password-here", "x");
        await admin.PostAsync("/auth/password", new { current = QmFactory.AdminPassword, next = "a brand new password" });
        await admin.PostAsync("/auth/logout");

        var rows = AuditRows();

        rows.Select(r => r.Action).Should().Equal(
            "auth.setup", "user.create", "user.update", "user.set_password", "user.delete", "auth.login", "auth.password_change", "auth.logout");
        rows.Should().OnlyContain(r => r.Outcome == "success" || r.Action == "auth.login");
        var failed = rows.Single(r => r.Action == "auth.login");
        failed.Outcome.Should().Be("failure");
        failed.Actor.Should().MatchRegex("^unknown:[0-9a-f]{12}$");
        failed.Params.Should().Be("{\"reason\":\"invalid credentials\"}");
        rows.Single(r => r.Action == "user.update").Params.Should().Be("{\"disabled\":true}");
        rows.Single(r => r.Action == "user.create").Params.Should().Be("{\"role\":\"viewer\"}");
        string.Join('\n', rows).Should().NotContain("typed-a-password-here").And.NotContain("viewer password 1").And.NotContain("brand new");
    }

    [Fact]
    public async Task The_Audit_Log_Is_Append_Only_At_The_Database_Level()
    {
        await _factory.NewAdminAsync();
        using var connection = new SqliteConnection($"Data Source={_factory.DatabaseFile}");
        connection.Open();

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE audit_log SET actor = 'tampered' WHERE id > 0";
        update.Invoking(c => c.ExecuteNonQuery()).Should().Throw<SqliteException>().WithMessage("*append-only*");

        using var remove = connection.CreateCommand();
        remove.CommandText = "DELETE FROM audit_log WHERE id > 0";
        remove.Invoking(c => c.ExecuteNonQuery()).Should().Throw<SqliteException>().WithMessage("*younger than 30 days*");
    }

    [Fact]
    public void The_Baseline_Migration_Reproduces_The_Original_Schema()
    {
        using var client = _factory.CreateClient(); // starts the host => migrations run
        var tables = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={_factory.DatabaseFile};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        string[] original =
        [
            "audit_log", "events", "image_checks", "ignored_targets", "intents", "kv", "notification_channels", "notification_dedupe",
            "notification_deliveries", "operations", "policies", "quarantine", "registry_credentials", "rollback_points", "run_steps",
            "runs", "schedule_occurrences", "schedules", "secret_store", "sessions", "staged_images", "stats_rollups", "users", "windows",
        ];
        tables.Should().Contain(original);
        tables.Except(original).Should().BeEquivalentTo(["__EFMigrationsHistory", "__EFMigrationsLock", "sqlite_sequence"], "24 original tables + EF bookkeeping tables");
        Scalar(_factory.DatabaseFile, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger'").Should().Be(2L);
        Scalar(_factory.DatabaseFile, "SELECT migrationid FROM __EFMigrationsHistory")!.ToString().Should().EndWith("_Baseline");
        Scalar(_factory.DatabaseFile, "SELECT sql FROM sqlite_master WHERE name='users'")!.ToString().Should().Contain("COLLATE NOCASE").And.Contain("CHECK (role IN ('admin','viewer'))");
        Scalar(_factory.DatabaseFile, "SELECT sql FROM sqlite_master WHERE name='stats_rollups'")!.ToString().Should().Contain("WITHOUT ROWID");
        Scalar(_factory.DatabaseFile, "PRAGMA journal_mode").Should().Be("wal");
    }

    // ---- startup / CLI ------------------------------------------------------------------------------------------------

    [Fact]
    public void Invalid_Configuration_Fails_Fast_Before_The_Host_Starts()
    {
        using var factory = new QmFactory(new() { ["QM_SECRET_KEY"] = "", ["QM_PORT"] = "70000" });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_Second_Instance_On_The_Same_Data_Directory_Is_Refused()
    {
        await _factory.NewAdminAsync();
        using var second = new QmFactory(new() { ["QM_DATA_DIR"] = _factory.DataDir });

        var act = () => second.CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("Another Quaymaster instance");
    }

    [Fact]
    public async Task Housekeeping_Purges_Expired_Sessions_When_Background_Tasks_Are_Enabled()
    {
        using var factory = new QmFactory(new() { ["QM_DISABLE_BACKGROUND"] = "false" });
        var client = await factory.NewAdminAsync();
        Scalar(factory.DatabaseFile, "SELECT COUNT(*) FROM sessions").Should().Be(1L);

        factory.Time.Advance(TimeSpan.FromMinutes(31));
        factory.Time.Advance(TimeSpan.FromMinutes(10)); // housekeeping tick (every 10 minutes)

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((long)Scalar(factory.DatabaseFile, "SELECT COUNT(*) FROM sessions")! != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Scalar(factory.DatabaseFile, "SELECT COUNT(*) FROM sessions").Should().Be(0L);
        client.Cookie.Should().NotBeNull();
    }

    [Fact]
    public async Task Cli_Gen_Secret_And_Hash_Password_Have_Parity_With_The_Original()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        static string? Env(string name) => name == "QM_SCRYPT_LOG2_N" ? "10" : null;

        (await CliRunner.RunAsync(["gen-secret"], TextReader.Null, output, errors, Env, () => null)).Should().Be(0);
        output.ToString().Trim().Should().MatchRegex("^QM_SECRET_KEY=[A-Za-z0-9+/]{43}=$");

        output.GetStringBuilder().Clear();
        (await CliRunner.RunAsync(["hash-password", "a perfectly fine password"], TextReader.Null, output, errors, Env, () => null)).Should().Be(0);
        var line = output.ToString().Trim();
        line.Should().MatchRegex(@"^QM_ADMIN_PASSWORD_HASH='scrypt\$1024\$8\$1\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+'$");
        var hash = line["QM_ADMIN_PASSWORD_HASH='".Length..^1];
        using var hasher = new Quaymaster.Infrastructure.Security.ScryptPasswordHasher(10);
        (await hasher.VerifyAsync("a perfectly fine password", hash)).Should().BeTrue();

        output.GetStringBuilder().Clear();
        (await CliRunner.RunAsync(["hash-password"], new StringReader("from stdin password\n"), output, errors, Env, () => null)).Should().Be(0);
        output.ToString().Should().StartWith("QM_ADMIN_PASSWORD_HASH='scrypt$");

        (await CliRunner.RunAsync(["hash-password", "short"], TextReader.Null, output, errors, Env, () => null)).Should().Be(1);
        errors.ToString().Should().Contain("error: Password must be at least 12 characters long");
        (await CliRunner.RunAsync(["bogus"], TextReader.Null, output, errors, Env, () => null)).Should().Be(2);
    }
}
