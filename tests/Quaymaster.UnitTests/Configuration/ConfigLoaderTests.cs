using FluentAssertions;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Configuration;

namespace Quaymaster.UnitTests.Configuration;

public sealed class ConfigLoaderTests
{
    private const string Key = "QM_SECRET_KEY";
    private static readonly string Base64Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray());
    private static readonly string HexKey = Convert.ToHexString(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray());

    private sealed class Files(Dictionary<string, string>? content = null) : IFileReader
    {
        public string ReadAllText(string path) =>
            content is not null && content.TryGetValue(path, out var text) ? text : throw new FileNotFoundException(path);
    }

    private static Dictionary<string, string?> Env(params (string, string?)[] pairs)
    {
        var env = new Dictionary<string, string?> { [Key] = Base64Key };
        foreach (var (k, v) in pairs)
        {
            env[k] = v;
        }

        return env;
    }

    private static QmConfig Load(Dictionary<string, string?> env, Dictionary<string, string>? files = null, bool weak = false) =>
        QmConfigLoader.Load(env, new Files(files), weak);

    private static string[] Problems(Dictionary<string, string?> env, bool weak = false, Dictionary<string, string>? files = null)
    {
        var act = () => Load(env, files, weak);
        return act.Should().Throw<ConfigurationException>().Which.Problems.ToArray();
    }

    [Fact]
    public void Only_The_Secret_Key_Is_Required_And_Everything_Else_Has_The_Original_Default()
    {
        var c = Load(Env());

        c.Host.Should().Be("127.0.0.1");
        c.Port.Should().Be(8787);
        c.DataDir.Should().Be(Path.GetFullPath("./data"));
        c.CookieSecure.Should().BeFalse();
        c.CookieName.Should().Be("qm_session");
        c.PublicOrigins.Should().BeEmpty();
        c.TrustProxy.Should().BeEmpty();
        c.ReadOnly.Should().BeFalse();
        c.DockerSocket.Should().Be("/var/run/docker.sock");
        c.DockerBin.Should().Be("docker");
        c.Session.IdleTimeout.Should().Be(TimeSpan.FromMinutes(30));
        c.Session.AbsoluteLifetime.Should().Be(TimeSpan.FromHours(12));
        c.Sse.Should().Be(new SseOptions(8, 32, 524288));
        c.ScryptLog2N.Should().Be(17);
        c.Stats.Interval.Should().Be(TimeSpan.FromSeconds(10));
        c.Stats.RetentionHours.Should().Be(48);
        c.EventRetentionDays.Should().Be(14);
        c.AuditRetentionDays.Should().Be(365);
        c.DiskFloorBytes.Should().Be(5368709120);
        c.DfMinInterval.Should().Be(TimeSpan.FromSeconds(21600));
        c.LogLevel.Should().Be("info");
        c.DisableBackground.Should().BeFalse();
        c.CheckTtl.Should().Be(TimeSpan.FromMinutes(360));
        c.RollbackRetention.Should().Be(TimeSpan.FromDays(7));
        c.CleanupMinAge.Should().Be(TimeSpan.FromHours(24));
        c.ApprovalExpiry.Should().Be(TimeSpan.FromHours(72));
        c.StackAutoMounts.Should().BeTrue();
        c.Helper.Enabled.Should().BeTrue();
        c.Admin.Should().BeNull();
        c.DatabaseFile.Should().EndWith("quaymaster.db");
    }

    [Fact]
    public void Every_Documented_Variable_Is_Accepted()
    {
        var env = Env(
            ("QM_HOST", "0.0.0.0"), ("QM_PORT", "9000"), ("QM_PUBLIC_ORIGIN", "https://a.example,http://b.example:8080"),
            ("QM_TRUST_PROXY", "10.0.0.0/8, 172.16.0.1"), ("QM_COOKIE_SECURE", "true"), ("QM_DATA_DIR", "/tmp/qm"),
            ("QM_ADMIN_USER", "root"), ("QM_ADMIN_PASSWORD_FILE", "/run/pw"), ("QM_DOCKER_SOCKET", "/s.sock"),
            ("QM_DOCKER_BIN", "/usr/bin/docker"), ("QM_STACK_PATHS", "/srv/stacks,/opt/x"), ("QM_DOCKER_ROOT_MOUNT", "/hostroot"),
            ("QM_READ_ONLY", "yes"), ("QM_MANAGED_SCOPE", "web-*,db"), ("QM_PROTECTED", "proxy"), ("QM_SELF_CONTAINER", "abc123"),
            ("QM_REDACT_ALL_ENV", "1"), ("QM_SESSION_IDLE_MINUTES", "5"), ("QM_SESSION_ABSOLUTE_HOURS", "2"),
            ("QM_SSE_MAX_PER_SESSION", "3"), ("QM_SSE_MAX_TOTAL", "9"), ("QM_LOG_FOLLOW_BYTES_PER_SEC", "2048"),
            ("QM_SCRYPT_LOG2_N", "15"), ("QM_STATS_INTERVAL_SECONDS", "30"), ("QM_STATS_RETENTION_HOURS", "6"),
            ("QM_EVENT_RETENTION_DAYS", "3"), ("QM_AUDIT_RETENTION_DAYS", "90"), ("QM_DISK_FLOOR_BYTES", "1000"),
            ("QM_DF_MIN_INTERVAL_SECONDS", "120"), ("QM_LOG_LEVEL", "debug"), ("QM_DISABLE_BACKGROUND", "true"),
            ("QM_VISIBLE_SCOPE", "demo-*"), ("QM_WEB_DIR", "/app/web"), ("QM_REGISTRY_INSECURE", "localhost:5000"),
            ("QM_CHECK_TTL_MINUTES", "60"), ("QM_ROLLBACK_RETENTION_DAYS", "0"), ("QM_CLEANUP_MIN_AGE_HOURS", "1"),
            ("QM_APPROVAL_EXPIRY_HOURS", "48"), ("QM_STACK_AUTO_MOUNTS", "false"), ("QM_HELPER_ENABLED", "false"),
            ("QM_HELPER_IMAGE", "alpine:3.20"), ("QM_HELPER_LABELS", "a=b,c=d"),
            ("QM_REGISTRY_TOKEN_REALMS", "registry.example.com=auth.example.com"), ("QM_SECRET_KEY_FILE", "/ignored"));

        var c = Load(env);

        c.Port.Should().Be(9000);
        c.PublicOrigins.Should().Equal("https://a.example", "http://b.example:8080");
        c.TrustProxy.Should().Equal("10.0.0.0/8", "172.16.0.1");
        c.CookieName.Should().Be("__Host-qm_session");
        c.Admin.Should().Be(new AdminBootstrap("root", null, "/run/pw"));
        c.StackPaths.Should().Equal("/srv/stacks", "/opt/x");
        c.ReadOnly.Should().BeTrue();
        c.ManagedScope.Should().Equal("web-*", "db");
        c.Session.IdleTimeout.Should().Be(TimeSpan.FromMinutes(5));
        c.AuditRetentionDays.Should().Be(90);
        c.RollbackRetention.Should().Be(TimeSpan.Zero);
        c.Helper.Should().BeEquivalentTo(new HelperOptions(false, "alpine:3.20", new Dictionary<string, string> { ["a"] = "b", ["c"] = "d" }));
        c.RegistryTokenRealms["registry.example.com"].Should().Equal("auth.example.com");
        c.LogLevel.Should().Be("debug");
        c.DisableBackground.Should().BeTrue();
        c.WebDir.Should().Be("/app/web");
    }

    [Fact]
    public void Every_Variable_In_The_Original_Env_Example_Is_Known()
    {
        // .env.example of the original (QM_* names only; comments stripped)
        var documented = new[]
        {
            "QM_HOST", "QM_PORT", "QM_PUBLIC_ORIGIN", "QM_TRUST_PROXY", "QM_COOKIE_SECURE", "QM_DATA_DIR", "QM_SECRET_KEY",
            "QM_SECRET_KEY_FILE", "QM_ADMIN_USER", "QM_ADMIN_PASSWORD_HASH", "QM_ADMIN_PASSWORD_FILE", "QM_DOCKER_SOCKET",
            "QM_DOCKER_BIN", "QM_STACK_PATHS", "QM_DOCKER_ROOT_MOUNT", "QM_READ_ONLY", "QM_MANAGED_SCOPE", "QM_PROTECTED",
            "QM_SELF_CONTAINER", "QM_REDACT_ALL_ENV", "QM_SESSION_IDLE_MINUTES", "QM_SESSION_ABSOLUTE_HOURS",
            "QM_SSE_MAX_PER_SESSION", "QM_SSE_MAX_TOTAL", "QM_LOG_FOLLOW_BYTES_PER_SEC", "QM_SCRYPT_LOG2_N",
            "QM_STATS_INTERVAL_SECONDS", "QM_STATS_RETENTION_HOURS", "QM_EVENT_RETENTION_DAYS", "QM_AUDIT_RETENTION_DAYS",
            "QM_DISK_FLOOR_BYTES", "QM_DF_MIN_INTERVAL_SECONDS", "QM_LOG_LEVEL", "QM_DISABLE_BACKGROUND", "QM_VISIBLE_SCOPE",
            "QM_WEB_DIR", "QM_REGISTRY_INSECURE", "QM_CHECK_TTL_MINUTES", "QM_ROLLBACK_RETENTION_DAYS",
            "QM_CLEANUP_MIN_AGE_HOURS", "QM_APPROVAL_EXPIRY_HOURS",
        };

        QmConfigLoader.Known.Should().Contain(documented);
    }

    [Fact]
    public void Missing_Secret_Key_Fails_Fast_With_A_Clear_Message()
    {
        var env = new Dictionary<string, string?>();

        Problems(env).Should().Equal("QM_SECRET_KEY (or QM_SECRET_KEY_FILE) is required");
    }

    [Fact]
    public void Unknown_Qm_Variables_Are_Rejected_As_Typos()
    {
        Problems(Env(("QM_PROT", "1"), ("QM_READONLY", "true"), ("NOT_QM", "x")))
            .Should().Equal("QM_PROT: unknown variable (typo?)", "QM_READONLY: unknown variable (typo?)");
    }

    [Fact]
    public void All_Problems_Are_Reported_At_Once_Without_Echoing_Values()
    {
        var problems = Problems(Env(("QM_PORT", "99999"), ("QM_SESSION_IDLE_MINUTES", "0"), ("QM_READ_ONLY", "maybe"), ("QM_LOG_LEVEL", "loud")));

        problems.Should().HaveCount(4);
        problems.Should().Contain(p => p.StartsWith("QM_PORT:", StringComparison.Ordinal));
        problems.Should().Contain(p => p.StartsWith("QM_SESSION_IDLE_MINUTES:", StringComparison.Ordinal));
        problems.Should().Contain(p => p.StartsWith("QM_READ_ONLY:", StringComparison.Ordinal));
        problems.Should().Contain(p => p.StartsWith("QM_LOG_LEVEL:", StringComparison.Ordinal));
        string.Join('\n', problems).Should().NotContain("99999").And.NotContain("maybe");
    }

    [Theory]
    [InlineData("QM_PORT", "0")]
    [InlineData("QM_PORT", "65536")]
    [InlineData("QM_PORT", "http")]
    [InlineData("QM_SESSION_IDLE_MINUTES", "1441")]
    [InlineData("QM_SESSION_ABSOLUTE_HOURS", "721")]
    [InlineData("QM_SSE_MAX_PER_SESSION", "101")]
    [InlineData("QM_SSE_MAX_TOTAL", "0")]
    [InlineData("QM_LOG_FOLLOW_BYTES_PER_SEC", "1023")]
    [InlineData("QM_STATS_INTERVAL_SECONDS", "1")]
    [InlineData("QM_EVENT_RETENTION_DAYS", "366")]
    [InlineData("QM_AUDIT_RETENTION_DAYS", "29")] // the audit table refuses to delete younger rows
    [InlineData("QM_AUDIT_RETENTION_DAYS", "3651")]
    [InlineData("QM_DISK_FLOOR_BYTES", "-1")]
    [InlineData("QM_DF_MIN_INTERVAL_SECONDS", "59")]
    [InlineData("QM_SCRYPT_LOG2_N", "21")]
    [InlineData("QM_CHECK_TTL_MINUTES", "0")]
    [InlineData("QM_APPROVAL_EXPIRY_HOURS", "0")]
    [InlineData("QM_LOG_LEVEL", "verbose")]
    [InlineData("QM_COOKIE_SECURE", "sometimes")]
    public void Out_Of_Range_Values_Are_Rejected(string name, string value) =>
        Problems(Env((name, value)), weak: true).Should().ContainSingle(p => p.StartsWith(name + ":", StringComparison.Ordinal));

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData(" yes ", true)]
    public void Booleans_Accept_The_Original_Spellings(string value, bool expected) =>
        Load(Env(("QM_READ_ONLY", value))).ReadOnly.Should().Be(expected);

    [Fact]
    public void Empty_Values_Mean_Unset_So_Compose_Templates_Work()
    {
        var c = Load(Env(("QM_PORT", ""), ("QM_PUBLIC_ORIGIN", ""), ("QM_READ_ONLY", ""), ("QM_SELF_CONTAINER", ""), ("QM_ADMIN_USER", "")));

        c.Port.Should().Be(8787);
        c.PublicOrigins.Should().BeEmpty();
        c.SelfContainer.Should().BeNull();
        c.Admin.Should().BeNull();
    }

    [Fact]
    public void Secret_Key_Accepts_Hex_And_Base64_And_Hides_Itself()
    {
        Load(Env((Key, HexKey))).SecretKey.Bytes.Length.Should().Be(32);
        var c = Load(Env((Key, Base64Key)));

        c.SecretKey.Bytes.Length.Should().Be(32);
        c.SecretKey.ToString().Should().Be("[redacted]");
        c.ToString().Should().NotContain(Base64Key);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("this is a passphrase and not a key at all!!")]
    [InlineData("YWJjZA==")] // valid base64, only 4 bytes
    public void Weak_Secret_Keys_Are_Rejected(string key) =>
        Problems(Env((Key, key))).Should().ContainSingle().Which.Should().StartWith("QM_SECRET_KEY must be at least 32 random bytes");

    [Fact]
    public void Repeated_Byte_Keys_Are_Rejected_For_Low_Entropy() =>
        Problems(Env((Key, new string('a', 64)))).Should().ContainSingle().Which.Should().Contain("too little entropy");

    [Fact]
    public void Secret_Key_Can_Come_From_A_File()
    {
        var env = Env((Key, null), ("QM_SECRET_KEY_FILE", "/run/secrets/key"));
        env.Remove(Key);

        Load(env, new Dictionary<string, string> { ["/run/secrets/key"] = Base64Key + "\n" }).SecretKey.Bytes.Length.Should().Be(32);
    }

    [Fact]
    public void Unreadable_Secret_Key_File_Is_A_Clear_Error()
    {
        var env = Env(("QM_SECRET_KEY_FILE", "/nope"));
        env.Remove(Key);

        Problems(env).Should().ContainSingle().Which.Should().StartWith("QM_SECRET_KEY_FILE: cannot read /nope");
    }

    [Theory]
    [InlineData("QM_ADMIN_USER", "root", "QM_ADMIN_USER needs QM_ADMIN_PASSWORD_HASH or QM_ADMIN_PASSWORD_FILE")]
    [InlineData("QM_ADMIN_PASSWORD_HASH", "scrypt$x", "QM_ADMIN_USER is required when QM_ADMIN_PASSWORD_HASH/QM_ADMIN_PASSWORD_FILE is set")]
    [InlineData("QM_ADMIN_PASSWORD_FILE", "/pw", "QM_ADMIN_USER is required when QM_ADMIN_PASSWORD_HASH/QM_ADMIN_PASSWORD_FILE is set")]
    public void Admin_Bootstrap_Variables_Must_Be_Consistent(string name, string value, string expected) =>
        Problems(Env((name, value))).Should().Equal(expected);

    [Fact]
    public void Admin_Hash_And_File_Are_Mutually_Exclusive() =>
        Problems(Env(("QM_ADMIN_USER", "a"), ("QM_ADMIN_PASSWORD_HASH", "h"), ("QM_ADMIN_PASSWORD_FILE", "/f")))
            .Should().Equal("Set only one of QM_ADMIN_PASSWORD_HASH and QM_ADMIN_PASSWORD_FILE");

    [Theory]
    [InlineData("https://qm.example.org/")]
    [InlineData("https://qm.example.org/path")]
    [InlineData("qm.example.org")]
    [InlineData("ftp://qm.example.org")]
    [InlineData("http://qm.example.org:80")]
    [InlineData("HTTP://QM.example.org")]
    public void Public_Origins_Must_Be_Bare_Origins(string origin) =>
        Problems(Env(("QM_PUBLIC_ORIGIN", origin))).Should().ContainSingle(p => p.StartsWith("QM_PUBLIC_ORIGIN:", StringComparison.Ordinal));

    [Theory]
    [InlineData("true")]
    [InlineData("*")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void Trusting_Every_Proxy_Is_Refused(string value) =>
        Problems(Env(("QM_TRUST_PROXY", value))).Should().ContainSingle().Which.Should().Contain("explicit IP/CIDR list");

    [Theory]
    [InlineData("proxy.local")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0/8")]
    public void Trusted_Proxies_Must_Be_Ips_Or_Cidrs(string value) =>
        Problems(Env(("QM_TRUST_PROXY", value))).Should().ContainSingle().Which.Should().Contain("is not an IP or CIDR");

    [Fact]
    public void Weak_Scrypt_Is_Only_Allowed_In_The_Testing_Environment()
    {
        Problems(Env(("QM_SCRYPT_LOG2_N", "10"))).Should().ContainSingle().Which.Should().Contain("below 14");
        Load(Env(("QM_SCRYPT_LOG2_N", "10")), weak: true).ScryptLog2N.Should().Be(10);
        Load(Env(("QM_SCRYPT_LOG2_N", "14"))).ScryptLog2N.Should().Be(14);
    }

    [Fact]
    public void Stack_Paths_Must_Be_Absolute() =>
        Problems(Env(("QM_STACK_PATHS", "/srv/ok,relative/dir"))).Should().ContainSingle().Which.Should().Contain("\"relative/dir\" is not an absolute path");

    [Fact]
    public void Helper_Labels_And_Image_And_Registry_Realms_Are_Validated()
    {
        var problems = Problems(Env(("QM_HELPER_LABELS", "novalue"), ("QM_HELPER_IMAGE", "bad image!"), ("QM_REGISTRY_TOKEN_REALMS", "registry.example.com")));

        problems.Should().HaveCount(3);
    }

    [Fact]
    public void Exception_Message_Lists_Every_Problem_On_Its_Own_Line()
    {
        var act = () => Load(new Dictionary<string, string?> { ["QM_PORT"] = "x" });

        var message = act.Should().Throw<ConfigurationException>().Which.Message;

        message.Should().StartWith("Invalid configuration:").And.Contain("  - QM_PORT:").And.Contain("  - QM_SECRET_KEY");
    }
}
