using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentValidation;
using Quaymaster.Api.Http;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Notifications;
using Quaymaster.Application.Validation;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Api.Endpoints;

/// <summary>
/// <c>/notifications/*</c>: channel kinds, channel CRUD + test send, delivery log. Reads: any authenticated user; writes and
/// the test send: admin. Secrets in a channel configuration are write-only (masked in every response).
/// </summary>
public static partial class NotificationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var admin = api.MapGroup(string.Empty).RequireCapability(Capability.Write);

        api.MapGet("/notifications/types", (INotificationService svc) => svc.Types());
        api.MapGet("/notifications/channels", (HttpContext http, INotificationService svc) => svc.ListChannelsAsync(http.RequestAborted));
        api.MapGet("/notifications/channels/{id}", (HttpContext http, string id, INotificationService svc) => svc.GetChannelAsync(ChannelId(id), http.RequestAborted));

        admin.MapPost("/notifications/channels", async (HttpContext http, IValidator<ChannelCreateBody> validator, INotificationService svc) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            var created = await svc.CreateChannelAsync(
                new ChannelCreateInput(b.Type!, b.Name!, b.Enabled, b.MinSeverity, b.Events, ToObject(b.Config!.Value)),
                http.ActorOf(),
                http.RequestAborted);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        });

        admin.MapPatch("/notifications/channels/{id}", async (HttpContext http, string id, IValidator<ChannelPatchBody> validator, INotificationService svc) =>
        {
            var channelId = ChannelId(id);
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await svc.UpdateChannelAsync(
                channelId,
                new ChannelPatchInput(b.Name, b.Enabled, b.MinSeverity, b.Events, b.Config is { } c ? ToObject(c) : null),
                http.ActorOf(),
                http.RequestAborted);
        });

        admin.MapDelete("/notifications/channels/{id}", async (HttpContext http, string id, INotificationService svc) =>
        {
            await svc.DeleteChannelAsync(ChannelId(id), http.ActorOf(), http.RequestAborted);
            return Results.NoContent();
        });

        admin.MapPost("/notifications/channels/{id}/test", (HttpContext http, string id, INotificationService svc) =>
            svc.TestChannelAsync(ChannelId(id), http.ActorOf(), http.RequestAborted));

        api.MapGet("/notifications/deliveries", async (HttpContext http, INotificationService svc) =>
        {
            var unknown = http.Request.Query.Keys.Where(k => k is not ("limit" or "channelId" or "beforeId")).ToList();
            if (unknown.Count > 0)
            {
                throw AppException.BadRequest("Invalid request", new[] { new ValidationIssue(string.Empty, $"Unrecognized key(s) in object: {string.Join(", ", unknown.Select(k => $"'{k}'"))}") });
            }

            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var limit = q.NumberInRange("limit", 1, 500, 50) ?? 50;
            var channelId = q.Text("channelId", 64, ChannelIdPattern());
            var beforeId = q.Number64InRange("beforeId", 1, long.MaxValue);
            q.ThrowIfInvalid();
            return await svc.ListDeliveriesAsync(limit, channelId, beforeId, http.RequestAborted);
        });
    }

    private static JsonObject ToObject(System.Text.Json.JsonElement element) => (JsonObject)JsonNode.Parse(element.GetRawText())!;

    private static string ChannelId(string raw) => ChannelIdPattern().IsMatch(raw) ? raw : throw AppException.BadRequest("Invalid channel id");

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ChannelIdPattern();
}
