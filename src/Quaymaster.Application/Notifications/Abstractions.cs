using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Quaymaster.Domain.Notifications;

namespace Quaymaster.Application.Notifications;

/// <summary>The outbound facade (HTTP + SMTP) so tests can substitute a fake. Errors are exceptions whose message is short and URL-free.</summary>
public interface INotificationTransport
{
    /// <summary>Performs the request under the outbound policy; returns the final HTTP status. Bodies are never returned.</summary>
    Task<int> SendRequestAsync(OutboundHttpRequest request, CancellationToken ct);

    Task SendMailAsync(MailMessage mail, CancellationToken ct);
}

public sealed record ChannelRow(
    string Id,
    string Type,
    string Name,
    bool Enabled,
    string MinSeverity,
    IReadOnlyList<string> Events,
    string SecretName,
    long CreatedAt,
    long UpdatedAt);

public sealed record DeliveryRow(
    long Id,
    long Ts,
    string ChannelId,
    string? ChannelName,
    string EventType,
    string Severity,
    string? DedupeKey,
    string Title,
    string Status,
    int Attempts,
    long? NextAttemptAt,
    string? LastError,
    int? HttpStatus,
    string EventJson);

public sealed record DeliveryResultUpdate(string Status, int Attempts, long? NextAttemptAt, string? LastError, int? HttpStatus);

/// <summary>Persistence for channels (config encrypted in <c>secret_store</c>), the delivery log and the de-dupe table.</summary>
public interface INotificationStore
{
    Task<IReadOnlyList<ChannelRow>> ListChannelsAsync(CancellationToken ct);

    Task<ChannelRow?> GetChannelAsync(string id, CancellationToken ct);

    Task<bool> ChannelNameExistsAsync(string name, string? exceptId, CancellationToken ct);

    /// <summary>Inserts the row and stores <paramref name="configJson"/> encrypted under <see cref="ChannelRow.SecretName"/> (one transaction).</summary>
    Task InsertChannelAsync(ChannelRow row, string configJson, CancellationToken ct);

    /// <summary>Updates the row; when <paramref name="configJson"/> is not null the encrypted config is replaced in the same transaction.</summary>
    Task UpdateChannelAsync(ChannelRow row, string? configJson, CancellationToken ct);

    Task DeleteChannelAsync(ChannelRow row, CancellationToken ct);

    Task<string?> GetConfigJsonAsync(string secretName, CancellationToken ct);

    Task<long> InsertDeliveryAsync(DeliveryRow row, CancellationToken ct);

    Task<DeliveryRow?> GetDeliveryAsync(long id, CancellationToken ct);

    Task UpdateDeliveryResultAsync(long id, DeliveryResultUpdate update, CancellationToken ct);

    Task<IReadOnlyList<DeliveryRow>> ListDeliveriesAsync(int limit, string? channelId, long? beforeId, CancellationToken ct);

    Task<IReadOnlyList<DeliveryRow>> DueRetriesAsync(long now, int limit, CancellationToken ct);

    Task PruneAsync(long olderThan, CancellationToken ct);

    Task<long?> LastSentAsync(string key, CancellationToken ct);

    Task MarkSentAsync(string key, long now, CancellationToken ct);

    Task BumpSuppressedAsync(string key, CancellationToken ct);

    Task<int> SuppressedCountAsync(string key, CancellationToken ct);
}

// ---- wire DTOs (shared/src/notifications.ts) -------------------------------------------------------------------------

public sealed record ChannelFieldDto(
    string Name,
    string Label,
    string Kind,
    bool Required,
    bool Secret,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Help);

public sealed record ChannelTypeInfoDto(string Type, string Label, IReadOnlyList<ChannelFieldDto> Fields);

public sealed record ChannelInfoDto(
    string Id,
    string Type,
    string Name,
    bool Enabled,
    string MinSeverity,
    IReadOnlyList<string> Events,
    JsonObject Config,
    string CreatedAt,
    string UpdatedAt);

public sealed record DeliveryDto(
    long Id,
    string Time,
    string ChannelId,
    string? ChannelName,
    string EventType,
    string Severity,
    string Title,
    string Status,
    int Attempts,
    string? NextAttemptAt,
    string? LastError,
    int? HttpStatus);

public sealed record ChannelTestResultDto(bool Ok, int? HttpStatus, string? Error);

public sealed record ChannelCreateInput(string Type, string Name, bool? Enabled, string? MinSeverity, IReadOnlyList<string>? Events, JsonObject Config);

public sealed record ChannelPatchInput(string? Name, bool? Enabled, string? MinSeverity, IReadOnlyList<string>? Events, JsonObject? Config);
