namespace Quaymaster.Application.Configuration;

/// <summary>
/// The validated, typed form of the complete <c>QM_*</c> environment surface (see .env.example).
/// Produced once at startup by <see cref="QmConfigLoader"/>; immutable afterwards.
/// </summary>
public sealed record QmConfig
{
    // --- Network
    public required string Host { get; init; }
    public required int Port { get; init; }
    /// <summary>Exact browser origins accepted on state-changing requests. Empty = derived from <see cref="Host"/>/<see cref="Port"/>.</summary>
    public required IReadOnlyList<string> PublicOrigins { get; init; }
    /// <summary>Explicit IPs/CIDRs of reverse proxies whose X-Forwarded-For is trusted.</summary>
    public required IReadOnlyList<string> TrustProxy { get; init; }
    public required bool CookieSecure { get; init; }

    // --- Storage and secrets
    public required string DataDir { get; init; }
    public required SecretKey SecretKey { get; init; }
    public required AdminBootstrap? Admin { get; init; }

    // --- Docker
    public required string DockerSocket { get; init; }
    public required string DockerBin { get; init; }
    public required string? DockerRootMount { get; init; }
    public required IReadOnlyList<string> StackPaths { get; init; }
    public required bool StackAutoMounts { get; init; }
    public required HelperOptions Helper { get; init; }

    // --- Safety
    public required bool ReadOnly { get; init; }
    public required IReadOnlyList<string> ManagedScope { get; init; }
    public required IReadOnlyList<string> VisibleScope { get; init; }
    public required IReadOnlyList<string> ProtectedNames { get; init; }
    public required string? SelfContainer { get; init; }
    public required bool RedactAllEnv { get; init; }

    // --- Sessions and streams
    public required SessionOptions Session { get; init; }
    public required SseOptions Sse { get; init; }
    public required int ScryptLog2N { get; init; }

    // --- Monitoring and retention
    public required StatsOptions Stats { get; init; }
    public required int EventRetentionDays { get; init; }
    public required int AuditRetentionDays { get; init; }
    public required long DiskFloorBytes { get; init; }
    public required TimeSpan DfMinInterval { get; init; }

    // --- Process
    public required string LogLevel { get; init; }
    public required bool DisableBackground { get; init; }
    public required string? WebDir { get; init; }

    // --- Updates and cleanup
    public required IReadOnlyList<string> RegistryInsecure { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> RegistryTokenRealms { get; init; }
    public required TimeSpan CheckTtl { get; init; }
    public required TimeSpan RollbackRetention { get; init; }
    public required TimeSpan CleanupMinAge { get; init; }
    public required TimeSpan ApprovalExpiry { get; init; }

    /// <summary>Absolute path of the SQLite database file.</summary>
    public string DatabaseFile => Path.Combine(DataDir, "quaymaster.db");

    /// <summary>Name of the session cookie: <c>__Host-qm_session</c> when served over HTTPS, else <c>qm_session</c>.</summary>
    public string CookieName => CookieSecure ? "__Host-qm_session" : "qm_session";
}

public sealed record SessionOptions(TimeSpan IdleTimeout, TimeSpan AbsoluteLifetime);

public sealed record SseOptions(int MaxPerSession, int MaxTotal, int LogBytesPerSecond);

public sealed record StatsOptions(TimeSpan Interval, int RetentionHours);

public sealed record HelperOptions(bool Enabled, string? Image, IReadOnlyDictionary<string, string> Labels);

/// <summary>First-run admin from the environment (applied only while the users table is empty).</summary>
public sealed record AdminBootstrap(string User, string? PasswordHash, string? PasswordFile);

/// <summary>The master key. <see cref="ToString"/> never reveals it.</summary>
public sealed class SecretKey
{
    private readonly byte[] _bytes;

    public SecretKey(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public override string ToString() => "[redacted]";
}
