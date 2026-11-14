using System.Text.Json;
using System.Text.Json.Serialization;
using Quaymaster.Application.Abstractions;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Application.Audit;

/// <summary>
/// Default <see cref="IAuditLog"/>: serialises the parameters, redacts them (whole-object walk plus known-value
/// scrubbing, exactly like every other outbound path), stamps the time and appends through <see cref="IAuditRepository"/>.
/// There is deliberately no update or delete path besides the retention prune.
/// </summary>
public sealed class AuditLog(IAuditRepository repository, TimeProvider time, int retentionDays, Scrubber? scrubber = null) : IAuditLog
{
    private static readonly JsonSerializerOptions ParamOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task RecordAsync(AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        string? paramsJson = null;
        if (record.Params is not null)
        {
            var redacted = Redactor.RedactDeep(JsonSerializer.SerializeToNode(record.Params, ParamOptions));
            paramsJson = redacted?.ToJsonString(ParamOptions);
            if (paramsJson is not null && scrubber is not null)
            {
                paramsJson = scrubber.Scrub(paramsJson);
            }
        }

        var entry = new AuditEntry(
            time.GetUtcNow(),
            record.Actor,
            record.Ip,
            record.Action,
            record.Target,
            paramsJson,
            record.Outcome,
            record.DurationMs);
        return repository.AddAsync(entry, ct);
    }

    public Task<int> PruneAsync(CancellationToken ct = default) =>
        repository.PruneAsync(time.GetUtcNow().AddDays(-retentionDays), ct);
}
