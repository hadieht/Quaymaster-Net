using System.Text.Json;
using FluentValidation;
using Quaymaster.Domain.Notifications;

namespace Quaymaster.Application.Notifications;

/// <summary><c>POST /notifications/channels</c> body (strict). <c>config</c> is validated per channel kind by the service.</summary>
public sealed record ChannelCreateBody(string? Type, string? Name, bool? Enabled, string? MinSeverity, List<string>? Events, JsonElement? Config);

/// <summary><c>PATCH /notifications/channels/:id</c> body (strict).</summary>
public sealed record ChannelPatchBody(string? Name, bool? Enabled, string? MinSeverity, List<string>? Events, JsonElement? Config);

internal static class ChannelBodyRules
{
    public static string EnumMessage(IEnumerable<string> values, string? received) =>
        $"Invalid enum value. Expected {string.Join(" | ", values.Select(x => $"'{x}'"))}, received '{received}'";

    public static void Name(Action<string, string> fail, string? name, bool required)
    {
        if (name is null)
        {
            if (required)
            {
                fail("name", "Required");
            }

            return;
        }

        if (name.Length < 1)
        {
            fail("name", "String must contain at least 1 character(s)");
        }

        if (name.Length > 80)
        {
            fail("name", "String must contain at most 80 character(s)");
        }
    }

    public static void Severity(Action<string, string> fail, string? s)
    {
        if (s is not null && !NotificationCatalog.Severities.Contains(s))
        {
            fail("minSeverity", EnumMessage(NotificationCatalog.Severities, s));
        }
    }

    public static void Events(Action<string, string> fail, List<string>? events)
    {
        if (events is null)
        {
            return;
        }

        if (events.Count < 1)
        {
            fail("events", "Array must contain at least 1 element(s)");
        }

        if (events.Count > 50)
        {
            fail("events", "Array must contain at most 50 element(s)");
        }

        var allowed = new[] { "*" }.Concat(NotificationCatalog.EventTypes).ToList();
        for (var i = 0; i < events.Count; i++)
        {
            if (!allowed.Contains(events[i]))
            {
                fail($"events.{i}", EnumMessage(allowed, events[i]));
            }
        }
    }

    public static void Config(Action<string, string> fail, JsonElement? config, bool required)
    {
        if (config is null)
        {
            if (required)
            {
                fail("config", "Required");
            }

            return;
        }

        if (config.Value.ValueKind != JsonValueKind.Object)
        {
            var received = config.Value.ValueKind switch
            {
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                _ => "null",
            };
            fail("config", $"Expected object, received {received}");
        }
    }
}


public sealed class ChannelCreateBodyValidator : AbstractValidator<ChannelCreateBody>
{
    public ChannelCreateBodyValidator() =>
        RuleFor(x => x).Custom((b, c) =>
        {
            if (b.Type is null)
            {
                c.AddFailure("type", "Required");
            }
            else if (!NotificationCatalog.ChannelKinds.Contains(b.Type))
            {
                c.AddFailure("type", ChannelBodyRules.EnumMessage(NotificationCatalog.ChannelKinds, b.Type));
            }

            ChannelBodyRules.Name(c.AddFailure, b.Name, true);
            ChannelBodyRules.Severity(c.AddFailure, b.MinSeverity);
            ChannelBodyRules.Events(c.AddFailure, b.Events);
            ChannelBodyRules.Config(c.AddFailure, b.Config, true);
        });
}

public sealed class ChannelPatchBodyValidator : AbstractValidator<ChannelPatchBody>
{
    public ChannelPatchBodyValidator() =>
        RuleFor(x => x).Custom((b, c) =>
        {
            ChannelBodyRules.Name(c.AddFailure, b.Name, false);
            ChannelBodyRules.Severity(c.AddFailure, b.MinSeverity);
            ChannelBodyRules.Events(c.AddFailure, b.Events);
            ChannelBodyRules.Config(c.AddFailure, b.Config, false);
        });
}
