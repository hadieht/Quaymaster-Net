using System.Globalization;
using System.Text.RegularExpressions;
using Quaymaster.Application.Abstractions;

namespace Quaymaster.Application.Configuration;

/// <summary>Thrown for any invalid configuration; the message lists every problem (never a secret value).</summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(IReadOnlyList<string> problems)
        : base("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Parses and validates the <c>QM_*</c> variables into <see cref="QmConfig"/>. Pure: the caller hands in the
/// variables (from IConfiguration / the process environment) so this is testable without touching global state.
/// Rules mirror the original server: unknown <c>QM_*</c> names are rejected (typo protection), every problem is
/// reported at once, and messages name variables only. An empty value counts as "unset" (so
/// <c>QM_PORT=</c> in a compose template falls back to the default).
/// </summary>
public static partial class QmConfigLoader
{
    private static readonly string[] KnownNames =
    [
        "QM_HOST", "QM_PORT", "QM_DATA_DIR", "QM_SECRET_KEY", "QM_SECRET_KEY_FILE", "QM_ADMIN_USER",
        "QM_ADMIN_PASSWORD_HASH", "QM_ADMIN_PASSWORD_FILE", "QM_COOKIE_SECURE", "QM_PUBLIC_ORIGIN", "QM_TRUST_PROXY",
        "QM_READ_ONLY", "QM_MANAGED_SCOPE", "QM_VISIBLE_SCOPE", "QM_PROTECTED", "QM_SELF_CONTAINER", "QM_STACK_PATHS",
        "QM_STACK_AUTO_MOUNTS", "QM_HELPER_ENABLED", "QM_HELPER_IMAGE", "QM_HELPER_LABELS", "QM_DOCKER_SOCKET",
        "QM_DOCKER_ROOT_MOUNT", "QM_REDACT_ALL_ENV", "QM_LOG_LEVEL", "QM_SESSION_IDLE_MINUTES",
        "QM_SESSION_ABSOLUTE_HOURS", "QM_SSE_MAX_PER_SESSION", "QM_SSE_MAX_TOTAL", "QM_LOG_FOLLOW_BYTES_PER_SEC",
        "QM_STATS_INTERVAL_SECONDS", "QM_STATS_RETENTION_HOURS", "QM_EVENT_RETENTION_DAYS", "QM_AUDIT_RETENTION_DAYS",
        "QM_DISK_FLOOR_BYTES", "QM_DF_MIN_INTERVAL_SECONDS", "QM_SCRYPT_LOG2_N", "QM_DOCKER_BIN", "QM_WEB_DIR",
        "QM_DISABLE_BACKGROUND", "QM_REGISTRY_INSECURE", "QM_REGISTRY_TOKEN_REALMS", "QM_CHECK_TTL_MINUTES",
        "QM_ROLLBACK_RETENTION_DAYS", "QM_CLEANUP_MIN_AGE_HOURS", "QM_APPROVAL_EXPIRY_HOURS",
    ];

    private static readonly string[] LogLevels = ["fatal", "error", "warn", "info", "debug", "trace", "silent"];

    /// <summary>Every supported variable name (for docs and the unknown-name check).</summary>
    public static IReadOnlyCollection<string> Known => KnownNames;

    /// <param name="variables">All configuration key/values; only <c>QM_*</c> keys are inspected.</param>
    /// <param name="files">Reads secret files (QM_SECRET_KEY_FILE).</param>
    /// <param name="allowWeakScrypt">Permit QM_SCRYPT_LOG2_N &lt; 14 (test environments only).</param>
    public static QmConfig Load(IReadOnlyDictionary<string, string?> variables, IFileReader files, bool allowWeakScrypt = false)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(files);
        var problems = new List<string>();
        var known = new HashSet<string>(KnownNames, StringComparer.Ordinal);
        foreach (var key in variables.Keys.Where(k => k.StartsWith("QM_", StringComparison.Ordinal) && !known.Contains(k)).Order(StringComparer.Ordinal))
        {
            problems.Add($"{key}: unknown variable (typo?)");
        }

        var env = new Env(variables, problems);

        var host = env.Str("QM_HOST") ?? "127.0.0.1";
        var port = env.Int("QM_PORT", 8787, 1, 65535);
        var dataDir = env.Str("QM_DATA_DIR") ?? "./data";
        var cookieSecure = env.Bool("QM_COOKIE_SECURE", false);
        var publicOrigins = env.List("QM_PUBLIC_ORIGIN");
        var trustProxy = env.List("QM_TRUST_PROXY");
        var readOnly = env.Bool("QM_READ_ONLY", false);
        var managedScope = env.List("QM_MANAGED_SCOPE");
        var visibleScope = env.List("QM_VISIBLE_SCOPE");
        var protectedNames = env.List("QM_PROTECTED");
        var stackPaths = env.List("QM_STACK_PATHS");
        var stackAutoMounts = env.Bool("QM_STACK_AUTO_MOUNTS", true);
        var helperEnabled = env.Bool("QM_HELPER_ENABLED", true);
        var helperImage = env.Str("QM_HELPER_IMAGE");
        var helperLabelItems = env.List("QM_HELPER_LABELS");
        var logLevel = (env.Str("QM_LOG_LEVEL") ?? "info");
        if (!LogLevels.Contains(logLevel, StringComparer.Ordinal))
        {
            problems.Add($"QM_LOG_LEVEL: must be one of {string.Join(", ", LogLevels)}");
            logLevel = "info";
        }

        var idleMinutes = env.Int("QM_SESSION_IDLE_MINUTES", 30, 1, 24 * 60);
        var absoluteHours = env.Int("QM_SESSION_ABSOLUTE_HOURS", 12, 1, 24 * 30);
        var ssePerSession = env.Int("QM_SSE_MAX_PER_SESSION", 8, 1, 100);
        var sseTotal = env.Int("QM_SSE_MAX_TOTAL", 32, 1, 1000);
        var logBytes = env.Int("QM_LOG_FOLLOW_BYTES_PER_SEC", 512 * 1024, 1024, 100 * 1024 * 1024);
        var statsInterval = env.Int("QM_STATS_INTERVAL_SECONDS", 10, 2, 3600);
        var statsRetention = env.Int("QM_STATS_RETENTION_HOURS", 168, 1, 24 * 30);
        var eventRetention = env.Int("QM_EVENT_RETENTION_DAYS", 14, 1, 365);
        var auditRetention = env.Int("QM_AUDIT_RETENTION_DAYS", 365, 30, 3650);
        var diskFloor = env.Long("QM_DISK_FLOOR_BYTES", 5L * 1024 * 1024 * 1024, 0, long.MaxValue);
        var dfInterval = env.Int("QM_DF_MIN_INTERVAL_SECONDS", 6 * 3600, 60, 7 * 86400);
        var scryptLog2N = env.Int("QM_SCRYPT_LOG2_N", 17, 10, 20);
        var checkTtl = env.Int("QM_CHECK_TTL_MINUTES", 360, 1, 7 * 24 * 60);
        var rollbackRetention = env.Int("QM_ROLLBACK_RETENTION_DAYS", 7, 0, 365);
        var cleanupMinAge = env.Int("QM_CLEANUP_MIN_AGE_HOURS", 24, 0, 24 * 365);
        var approvalExpiry = env.Int("QM_APPROVAL_EXPIRY_HOURS", 72, 1, 24 * 30);
        var registryInsecure = env.List("QM_REGISTRY_INSECURE");
        var realmItems = env.List("QM_REGISTRY_TOKEN_REALMS");
        var disableBackground = env.Bool("QM_DISABLE_BACKGROUND", false);

        // --- secret key -------------------------------------------------------------------------
        SecretKey? secretKey = null;
        var rawKey = env.Str("QM_SECRET_KEY");
        var keyFile = env.Str("QM_SECRET_KEY_FILE");
        if (rawKey is null && keyFile is not null)
        {
            try
            {
                rawKey = files.ReadAllText(keyFile).Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"QM_SECRET_KEY_FILE: cannot read {keyFile}: {e.Message}");
            }
        }

        if (string.IsNullOrEmpty(rawKey))
        {
            if (keyFile is null)
            {
                problems.Add("QM_SECRET_KEY (or QM_SECRET_KEY_FILE) is required");
            }
        }
        else
        {
            try
            {
                secretKey = new SecretKey(DecodeSecretKey(rawKey));
            }
            catch (ConfigurationValueException e)
            {
                problems.Add(e.Message);
            }
        }

        // --- helper labels / paths ----------------------------------------------------------------
        var helperLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in helperLabelItems)
        {
            var m = HelperLabelRegex().Match(kv);
            if (!m.Success)
            {
                problems.Add($"QM_HELPER_LABELS: \"{kv}\" is not key=value (letters, digits, . _ -)");
            }
            else
            {
                helperLabels[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        if (helperImage is not null && !HelperImageRegex().IsMatch(helperImage))
        {
            problems.Add("QM_HELPER_IMAGE: not a valid image reference");
        }

        foreach (var p in stackPaths.Where(p => !Path.IsPathFullyQualified(p)))
        {
            problems.Add($"QM_STACK_PATHS: \"{p}\" is not an absolute path");
        }

        // --- admin bootstrap ------------------------------------------------------------------------
        var adminUser = env.Str("QM_ADMIN_USER");
        var adminHash = env.Str("QM_ADMIN_PASSWORD_HASH");
        var adminFile = env.Str("QM_ADMIN_PASSWORD_FILE");
        if (adminHash is not null && adminFile is not null)
        {
            problems.Add("Set only one of QM_ADMIN_PASSWORD_HASH and QM_ADMIN_PASSWORD_FILE");
        }

        if ((adminHash is not null || adminFile is not null) && adminUser is null)
        {
            problems.Add("QM_ADMIN_USER is required when QM_ADMIN_PASSWORD_HASH/QM_ADMIN_PASSWORD_FILE is set");
        }

        if (adminUser is not null && adminHash is null && adminFile is null)
        {
            problems.Add("QM_ADMIN_USER needs QM_ADMIN_PASSWORD_HASH or QM_ADMIN_PASSWORD_FILE");
        }

        // --- origins / proxies -------------------------------------------------------------------------
        foreach (var o in publicOrigins)
        {
            if (!Uri.TryCreate(o, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                problems.Add($"QM_PUBLIC_ORIGIN: \"{o}\" is not a valid http(s) URL");
            }
            else if (!string.Equals(uri.GetLeftPart(UriPartial.Authority), o, StringComparison.Ordinal))
            {
                problems.Add($"QM_PUBLIC_ORIGIN: \"{o}\" must be a bare origin like https://qm.example.org");
            }
        }

        foreach (var t in trustProxy)
        {
            if (t is "true" or "*" or "0.0.0.0/0" or "::/0")
            {
                problems.Add("QM_TRUST_PROXY must be an explicit IP/CIDR list, never \"true\", \"*\" or 0.0.0.0/0");
            }
            else if (!IsIpOrCidr(t))
            {
                problems.Add($"QM_TRUST_PROXY: \"{t}\" is not an IP or CIDR");
            }
        }

        if (scryptLog2N < 14 && !allowWeakScrypt)
        {
            problems.Add("QM_SCRYPT_LOG2_N below 14 is only allowed in the Testing environment");
        }

        var realms = ParseTokenRealms(realmItems, problems);

        if (problems.Count > 0 || secretKey is null)
        {
            throw new ConfigurationException(problems);
        }

        return new QmConfig
        {
            Host = host,
            Port = port,
            PublicOrigins = publicOrigins,
            TrustProxy = trustProxy,
            CookieSecure = cookieSecure,
            DataDir = Path.GetFullPath(dataDir),
            SecretKey = secretKey,
            Admin = adminUser is null ? null : new AdminBootstrap(adminUser, adminHash, adminFile),
            DockerSocket = env.Str("QM_DOCKER_SOCKET") ?? "/var/run/docker.sock",
            DockerBin = env.Str("QM_DOCKER_BIN") ?? "docker",
            DockerRootMount = env.Str("QM_DOCKER_ROOT_MOUNT"),
            StackPaths = stackPaths.Select(Path.GetFullPath).ToArray(),
            StackAutoMounts = stackAutoMounts,
            Helper = new HelperOptions(helperEnabled, helperImage, helperLabels),
            ReadOnly = readOnly,
            ManagedScope = managedScope,
            VisibleScope = visibleScope,
            ProtectedNames = protectedNames,
            SelfContainer = env.Str("QM_SELF_CONTAINER"),
            RedactAllEnv = env.Bool("QM_REDACT_ALL_ENV", false),
            Session = new SessionOptions(TimeSpan.FromMinutes(idleMinutes), TimeSpan.FromHours(absoluteHours)),
            Sse = new SseOptions(ssePerSession, sseTotal, logBytes),
            ScryptLog2N = scryptLog2N,
            Stats = new StatsOptions(TimeSpan.FromSeconds(statsInterval), statsRetention),
            EventRetentionDays = eventRetention,
            AuditRetentionDays = auditRetention,
            DiskFloorBytes = diskFloor,
            DfMinInterval = TimeSpan.FromSeconds(dfInterval),
            LogLevel = logLevel,
            DisableBackground = disableBackground,
            WebDir = env.Str("QM_WEB_DIR") is { } web ? Path.GetFullPath(web) : null,
            RegistryInsecure = registryInsecure,
            RegistryTokenRealms = realms,
            CheckTtl = TimeSpan.FromMinutes(checkTtl),
            RollbackRetention = TimeSpan.FromDays(rollbackRetention),
            CleanupMinAge = TimeSpan.FromHours(cleanupMinAge),
            ApprovalExpiry = TimeSpan.FromHours(approvalExpiry),
        };
    }

    /// <summary>
    /// Decodes a key given as hex or base64 (&gt;= 32 decoded bytes, &gt;= 8 distinct byte values).
    /// Passphrases are rejected on purpose.
    /// </summary>
    public static byte[] DecodeSecretKey(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var v = raw.Trim();
        byte[]? bytes = null;
        if (v.Length % 2 == 0 && v.Length > 0 && HexRegex().IsMatch(v))
        {
            bytes = Convert.FromHexString(v);
        }
        else if (Base64Regex().IsMatch(v))
        {
            var s = v.TrimEnd('=').Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
            try
            {
                bytes = Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                bytes = null;
            }
        }

        if (bytes is null || bytes.Length < 32)
        {
            throw new ConfigurationValueException(
                "QM_SECRET_KEY must be at least 32 random bytes encoded as hex or base64 (generate one with: openssl rand -base64 32)");
        }

        if (bytes.Distinct().Count() < 8)
        {
            throw new ConfigurationValueException("QM_SECRET_KEY has too little entropy (repeated bytes)");
        }

        return bytes;
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseTokenRealms(IReadOnlyList<string> items, List<string> problems)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var parts = item.Split('=').Select(s => s.Trim().ToLowerInvariant()).ToArray();
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || !ValidHost(parts[0]) || !ValidHost(parts[1]) || parts[1].Contains(':', StringComparison.Ordinal))
            {
                problems.Add($"QM_REGISTRY_TOKEN_REALMS: invalid entry \"{item}\" (expected registry.host=token.host)");
                continue;
            }

            result[parts[0]] = result.TryGetValue(parts[0], out var existing) ? [.. existing, parts[1]] : [parts[1]];
        }

        return result;
    }

    private static bool ValidHost(string host) =>
        HostRegex().IsMatch(host) && host.Split(':')[0].Split('.')[^1].Any(char.IsAsciiLetter);

    private static bool IsIpOrCidr(string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var addr = slash < 0 ? value : value[..slash];
        if (!System.Net.IPAddress.TryParse(addr, out var ip))
        {
            return false;
        }

        // .NET accepts legacy shorthand such as "10.0.0"; an explicit proxy list must be unambiguous.
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && addr.Count(c => c == '.') != 3)
        {
            return false;
        }
        if (slash < 0)
        {
            return true;
        }
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(value[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) && prefix <= max;
    }

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex("^[A-Za-z0-9+/_-]+={0,2}$")]
    private static partial Regex Base64Regex();

    [GeneratedRegex("^([A-Za-z0-9][A-Za-z0-9._-]{0,127})=([A-Za-z0-9._-]{0,128})$")]
    private static partial Regex HelperLabelRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:@/-]{0,299}$")]
    private static partial Regex HelperImageRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+(:\d{1,5})?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostRegex();

    private sealed class ConfigurationValueException(string message) : Exception(message);

    /// <summary>Typed accessors that record problems instead of throwing.</summary>
    private sealed class Env(IReadOnlyDictionary<string, string?> variables, List<string> problems)
    {
        public string? Str(string name) =>
            variables.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        public bool Bool(string name, bool fallback)
        {
            if (!variables.TryGetValue(name, out var raw) || raw is null)
            {
                return fallback;
            }
            switch (raw.Trim().ToLowerInvariant())
            {
                case "true" or "1" or "yes": return true;
                case "false" or "0" or "no": return false;
                case "": return fallback;
                default:
                    problems.Add($"{name}: must be one of true, false, 1, 0, yes, no");
                    return fallback;
            }
        }

        public int Int(string name, int fallback, int min, int max) => (int)Long(name, fallback, min, max);

        public long Long(string name, long fallback, long min, long max)
        {
            var raw = Str(name);
            if (raw is null)
            {
                return fallback;
            }
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                problems.Add($"{name}: must be an integer between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}");
                return fallback;
            }

            return value;
        }

        public string[] List(string name) =>
            Str(name) is { } v
                ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
    }
}
