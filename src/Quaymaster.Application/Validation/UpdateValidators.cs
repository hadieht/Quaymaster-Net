using System.Text.RegularExpressions;
using FluentValidation;
using Quaymaster.Application.Updates;

namespace Quaymaster.Application.Validation;

internal static class UpdateRules
{
    internal static readonly Regex Uuid = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void Optional<T>(this AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, string?>> property, string path, int min, int max)
    {
        v.RuleFor(property)
            .Must(s => s is null || s.Length >= min).WithMessage($"String must contain at least {min} character(s)")
            .Must(s => s is null || s.Length <= max).WithMessage($"String must contain at most {max} character(s)")
            .OverridePropertyName(path);
    }

    internal static void Minutes<T>(this AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, double?>> property, string path)
    {
        v.RuleFor(property)
            .Must(n => n is null || n >= 0).WithMessage("Number must be greater than or equal to 0")
            .Must(n => n is null || n <= 24 * 365).WithMessage($"Number must be less than or equal to {24 * 365}")
            .OverridePropertyName(path);
    }

    internal static void Categories<T>(this AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, IReadOnlyList<string>?>> property, string path, bool required, int min, int max, params string[] allowed)
    {
        v.RuleFor(property).Custom((list, context) =>
        {
            if (list is null)
            {
                if (required)
                {
                    context.AddFailure(path, "Required");
                }

                return;
            }

            if (list.Count < min)
            {
                context.AddFailure(path, $"Array must contain at least {min} element(s)");
            }

            if (list.Count > max)
            {
                context.AddFailure(path, $"Array must contain at most {max} element(s)");
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (!allowed.Contains(list[i]))
                {
                    context.AddFailure($"{path}.{i}", $"Invalid enum value. Expected {string.Join(" | ", allowed.Select(a => $"'{a}'"))}, received '{list[i]}'");
                }
            }
        });
    }

    internal static void PreviewId<T>(this AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, string?>> property)
    {
        v.RuleFor(property).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Required")
            .Must(s => Uuid.IsMatch(s!)).WithMessage("Invalid uuid")
            .OverridePropertyName("previewId");
    }
}

public sealed class ImagePullRequestValidator : AbstractValidator<ImagePullRequestBody>
{
    public ImagePullRequestValidator() => this.RequireString(x => x.Ref, "ref", 1, 400);
}

public sealed class QuarantineClearValidator : AbstractValidator<QuarantineClearBody>
{
    public QuarantineClearValidator()
    {
        this.RequireString(x => x.Target, "target", 1, 400);
        RuleFor(x => x.Digest).Must(d => d is null || Regex.IsMatch(d, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            .WithMessage("Invalid").OverridePropertyName("digest");
    }
}

public sealed class CredentialBodyValidator : AbstractValidator<CredentialBody>
{
    public CredentialBodyValidator()
    {
        RuleFor(x => x.Host).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Required")
            .Must(s => s!.Length >= 1).WithMessage("String must contain at least 1 character(s)")
            .Must(s => s!.Length <= 255).WithMessage("String must contain at most 255 character(s)")
            .Must(s => Regex.IsMatch(s!, @"^[A-Za-z0-9.-]+(:\d{1,5})?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)).WithMessage("Invalid")
            .OverridePropertyName("host");
        this.RequireString(x => x.Username, "username", 1, 200);
        this.RequireString(x => x.Password, "password", 1, 4000);
    }
}

public sealed class CleanupPreviewBodyValidator : AbstractValidator<CleanupPreviewBody>
{
    public CleanupPreviewBodyValidator()
    {
        this.Categories(x => x.Categories, "categories", required: false, 0, 4, "dangling_images", "unused_images", "stopped_containers", "expired_rollback");
        this.Minutes(x => x.MinAgeHours, "minAgeHours");
        this.Optional(x => x.LabelFilter, "labelFilter", 0, 200);
    }
}

public sealed class ExecuteBodyValidator : AbstractValidator<ExecuteBody>
{
    public ExecuteBodyValidator()
    {
        this.PreviewId(x => x.PreviewId);
        this.RequireString(x => x.Confirm, "confirm", 0, 300);
    }
}

public sealed class StoragePreviewBodyValidator : AbstractValidator<StoragePreviewBody>
{
    public StoragePreviewBodyValidator()
    {
        this.Categories(x => x.Categories, "categories", required: true, 1, 3, "unused_networks", "anonymous_volumes", "named_volumes");
        this.Minutes(x => x.MinAgeHours, "minAgeHours");
    }
}

public sealed class VolumeRemoveBodyValidator : AbstractValidator<VolumeRemoveBody>
{
    public VolumeRemoveBodyValidator() => this.RequireString(x => x.Confirm, "confirm", 0, 300);
}

public sealed class ConfirmBodyValidator : AbstractValidator<ConfirmBody>
{
    public ConfirmBodyValidator() => this.RequireString(x => x.Confirm, "confirm", 0, 300);
}

public sealed class ImageRemoveBodyValidator : AbstractValidator<ImageRemoveBody>
{
    public ImageRemoveBodyValidator()
    {
        this.RequireString(x => x.Confirm, "confirm", 0, 400);
        this.Optional(x => x.Tag, "tag", 1, 400);
    }
}
