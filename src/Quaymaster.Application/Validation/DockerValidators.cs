using System.Linq.Expressions;
using FluentValidation;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;

namespace Quaymaster.Application.Validation;

/// <summary><c>POST /containers/:ref/actions/:action</c> body (strict).</summary>
public sealed class ActionRequestValidator : AbstractValidator<ActionRequest>
{
    public ActionRequestValidator()
    {
        RuleFor(x => x.Confirm).Must(s => s is null || s.Length <= 300).WithMessage("String must contain at most 300 character(s)").OverridePropertyName("confirm");
        RuleFor(x => x.Signal).Must(s => s is null || s.Length <= 12).WithMessage("String must contain at most 12 character(s)").OverridePropertyName("signal");
        RuleFor(x => x.TimeoutSeconds).Must(n => n is null or >= 0).WithMessage("Number must be greater than or equal to 0").OverridePropertyName("timeoutSeconds");
        RuleFor(x => x.TimeoutSeconds).Must(n => n is null or <= 600).WithMessage("Number must be less than or equal to 600").OverridePropertyName("timeoutSeconds");
    }
}

/// <summary><c>POST /compose/projects/:name/actions/:action</c> body (strict).</summary>
public sealed class ComposeActionRequestValidator : AbstractValidator<ComposeActionRequest>
{
    public ComposeActionRequestValidator()
    {
        RuleFor(x => x.Service).Must(s => s is null || s.Length <= 100).WithMessage("String must contain at most 100 character(s)").OverridePropertyName("service");
        RuleFor(x => x.Confirm).Must(s => s is null || s.Length <= 300).WithMessage("String must contain at most 300 character(s)").OverridePropertyName("confirm");
    }
}

/// <summary><c>PUT /monitors/settings</c> body: every field is required (strict).</summary>
public sealed record MonitorSettingsRequest(
    bool? Enabled,
    double? DiskFreePercentBelow,
    double? DiskFreeBytesBelow,
    double? CpuPercentAbove,
    double? MemoryPercentAbove,
    double? SustainSeconds,
    double? RestartLoopCount,
    double? RestartLoopWindowSeconds,
    double? UpcomingMaintenanceMinutes)
{
    public MonitorSettingsDto ToDto() => new(
        Enabled!.Value,
        DiskFreePercentBelow!.Value,
        DiskFreeBytesBelow!.Value,
        CpuPercentAbove!.Value,
        MemoryPercentAbove!.Value,
        (int)SustainSeconds!.Value,
        (int)RestartLoopCount!.Value,
        (int)RestartLoopWindowSeconds!.Value,
        (int)UpcomingMaintenanceMinutes!.Value);
}

public sealed class MonitorSettingsRequestValidator : AbstractValidator<MonitorSettingsRequest>
{
    public MonitorSettingsRequestValidator()
    {
        RuleFor(x => x.Enabled).NotNull().WithMessage("Required").OverridePropertyName("enabled");
        Number(x => x.DiskFreePercentBelow, "diskFreePercentBelow", 0, 100, integer: false);
        Number(x => x.DiskFreeBytesBelow, "diskFreeBytesBelow", 0, null, integer: false);
        Number(x => x.CpuPercentAbove, "cpuPercentAbove", 1, 1000, integer: false);
        Number(x => x.MemoryPercentAbove, "memoryPercentAbove", 1, 100, integer: false);
        Number(x => x.SustainSeconds, "sustainSeconds", 0, 86_400, integer: true);
        Number(x => x.RestartLoopCount, "restartLoopCount", 2, 1000, integer: true);
        Number(x => x.RestartLoopWindowSeconds, "restartLoopWindowSeconds", 10, 86_400, integer: true);
        Number(x => x.UpcomingMaintenanceMinutes, "upcomingMaintenanceMinutes", 1, 7 * 24 * 60, integer: true);
    }

    private void Number(Expression<Func<MonitorSettingsRequest, double?>> property, string path, double min, double? max, bool integer) =>
        RuleFor(property)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Required")
            .Must(n => !integer || n!.Value == Math.Floor(n.Value)).WithMessage("Expected integer, received float")
            .Must(n => n >= min).WithMessage($"Number must be greater than or equal to {min}")
            .Must(n => max is null || n <= max).WithMessage($"Number must be less than or equal to {max}")
            .OverridePropertyName(path);
}
