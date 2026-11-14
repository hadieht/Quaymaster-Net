using System.Text.RegularExpressions;
using FluentValidation;
using Quaymaster.Application.Validation;

namespace Quaymaster.Application.Maintenance;

internal static partial class MaintenanceValidation
{
    [GeneratedRegex(@"^\d{2}:\d{2}$", RegexOptions.CultureInvariant)]
    internal static partial Regex HhmmRe();

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    internal static partial Regex UuidRe();

    internal static void Hhmm<T>(this AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, string?>> property, string path)
    {
        v.RuleFor(property).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Required")
            .Must(s => HhmmRe().IsMatch(s!)).WithMessage("Invalid")
            .OverridePropertyName(path);
    }

    internal static void StringList<T>(this AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, IReadOnlyList<string>?>> property, string path, int maxItems, int maxLen)
    {
        v.RuleFor(property).Custom((list, ctx) => CheckList(list, path, maxItems, maxLen, ctx.AddFailure));
    }

    internal static void CheckList(IReadOnlyList<string>? list, string path, int maxItems, int maxLen, Action<string, string> fail)
    {
        if (list is null)
        {
            return;
        }

        if (list.Count > maxItems)
        {
            fail(path, $"Array must contain at most {maxItems} element(s)");
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is null || list[i].Length > maxLen)
            {
                fail($"{path}.{i}", $"String must contain at most {maxLen} character(s)");
            }
        }
    }
}

public sealed class WindowBodyValidator : AbstractValidator<WindowBody>
{
    public WindowBodyValidator()
    {
        this.RequireString(x => x.Name, "name", 1, 100);
        RuleFor(x => x.Days).Custom((days, ctx) =>
        {
            if (days is null)
            {
                ctx.AddFailure("days", "Required");
                return;
            }

            if (days.Count < 1)
            {
                ctx.AddFailure("days", "Array must contain at least 1 element(s)");
            }

            if (days.Count > 7)
            {
                ctx.AddFailure("days", "Array must contain at most 7 element(s)");
            }

            for (var i = 0; i < days.Count; i++)
            {
                if (days[i] is < 0 or > 6)
                {
                    ctx.AddFailure($"days.{i}", "Number must be between 0 and 6");
                }
            }
        });
        this.Hhmm(x => x.Start, "start");
        this.Hhmm(x => x.End, "end");
        this.RequireString(x => x.Timezone, "timezone", 1, 100);
    }
}

public sealed class ScheduleBodyValidator : AbstractValidator<ScheduleBody>
{
    public ScheduleBodyValidator()
    {
        this.RequireString(x => x.Name, "name", 1, 100);
        this.RequireString(x => x.Cron, "cron", 1, 100);
        this.RequireString(x => x.Timezone, "timezone", 1, 100);
        this.RequireEnum(x => x.JobType, "jobType", "image_check", "image_pull", "update", "cleanup_report");
        this.RequireEnum(x => x.Mode, "mode", "manual", "auto");
        RuleFor(x => x.WindowId).Must(s => s is null || MaintenanceValidation.UuidRe().IsMatch(s)).WithMessage("Invalid uuid").OverridePropertyName("windowId");
        RuleFor(x => x.Selector).Custom((sel, ctx) =>
        {
            if (sel is null)
            {
                return;
            }

            if (sel.Project is { Length: > 100 })
            {
                ctx.AddFailure("selector.project", "String must contain at most 100 character(s)");
            }

            if (sel.Container is { Length: > 200 })
            {
                ctx.AddFailure("selector.container", "String must contain at most 200 character(s)");
            }

            MaintenanceValidation.CheckList(sel.Services, "selector.services", 100, 100, ctx.AddFailure);
        });
    }
}

public sealed class PlanBodyValidator : AbstractValidator<PlanBody>
{
    public PlanBodyValidator()
    {
        this.RequireString(x => x.Project, "project", 1, 100);
        this.StringList(x => x.Services, "services", 100, 100);
    }
}

public sealed class RunBodyValidator : AbstractValidator<RunBody>
{
    public RunBodyValidator()
    {
        this.RequireString(x => x.Project, "project", 1, 100);
        this.StringList(x => x.Services, "services", 100, 100);
        RuleFor(x => x.Confirm).MaximumLength(200).WithMessage("String must contain at most 200 character(s)").OverridePropertyName("confirm");
        RuleFor(x => x.WindowId).Must(s => s is null || MaintenanceValidation.UuidRe().IsMatch(s)).WithMessage("Invalid uuid").OverridePropertyName("windowId");
    }
}

public sealed partial class ApproveBodyValidator : AbstractValidator<ApproveBody>
{
    public ApproveBodyValidator()
    {
        RuleFor(x => x.PlanHash).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Required")
            .Must(s => PlanHashRe().IsMatch(s!)).WithMessage("Invalid")
            .OverridePropertyName("planHash");
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlanHashRe();
}

public sealed class RestoreBodyValidator : AbstractValidator<RestoreBody>
{
    public RestoreBodyValidator()
    {
        RuleFor(x => x.Confirm).MaximumLength(200).WithMessage("String must contain at most 200 character(s)").OverridePropertyName("confirm");
        this.StringList(x => x.Services, "services", 100, 100);
    }
}
