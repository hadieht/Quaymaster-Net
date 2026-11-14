namespace Quaymaster.Domain.Auditing;

public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
}

public static class AuditOutcomeNames
{
    public static string ToWire(this AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Failure => "failure",
        AuditOutcome.Denied => "denied",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static AuditOutcome Parse(string value) => value switch
    {
        "success" => AuditOutcome.Success,
        "failure" => AuditOutcome.Failure,
        "denied" => AuditOutcome.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

/// <summary>One immutable audit row. There is deliberately no mutator: the log is append-only.</summary>
public sealed class AuditEntry
{
    private AuditEntry() { }

    public AuditEntry(
        DateTimeOffset timestamp,
        string actor,
        string? ip,
        string action,
        string? target,
        string? paramsJson,
        AuditOutcome outcome,
        long? durationMs)
    {
        Timestamp = timestamp;
        Actor = actor;
        Ip = ip;
        Action = action;
        Target = target;
        ParamsJson = paramsJson;
        Outcome = outcome;
        DurationMs = durationMs;
    }

    public long Id { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public string Actor { get; private set; } = "";
    public string? Ip { get; private set; }
    public string Action { get; private set; } = "";
    public string? Target { get; private set; }
    public string? ParamsJson { get; private set; }
    public AuditOutcome Outcome { get; private set; }
    public long? DurationMs { get; private set; }
}
