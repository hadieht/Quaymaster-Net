using System.Linq.Expressions;
using FluentValidation;
using Quaymaster.Application.Errors;

namespace Quaymaster.Application.Validation;

/// <summary>
/// Request validation helpers. Messages deliberately mirror the zod issues of the original server
/// (<c>Required</c>, <c>String must contain at least 1 character(s)</c>, ...), and failures become a
/// <c>400 validation</c> error with <c>details: [{path, message}]</c>.
/// </summary>
public static class ValidationExtensions
{
    public static void EnsureValid<T>(this IValidator<T> validator, T instance)
    {
        ArgumentNullException.ThrowIfNull(validator);
        var result = validator.Validate(instance);
        if (result.IsValid)
        {
            return;
        }

        throw AppException.BadRequest(
            "Invalid request",
            result.Errors.Select(e => new ValidationIssue(e.PropertyName, e.ErrorMessage)).ToList());
    }

    /// <summary>A required string with zod-style length messages; <paramref name="path"/> is the JSON property name.</summary>
    public static void RequireString<T>(this AbstractValidator<T> validator, Expression<Func<T, string?>> property, string path, int min, int max)
    {
        ArgumentNullException.ThrowIfNull(validator);
        validator.RuleFor(property)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Required")
            .Must(s => s!.Length >= min).WithMessage($"String must contain at least {min} character(s)")
            .Must(s => s!.Length <= max).WithMessage($"String must contain at most {max} character(s)")
            .OverridePropertyName(path);
    }

    /// <summary>A required string restricted to <paramref name="values"/> (zod enum message).</summary>
    public static void RequireEnum<T>(this AbstractValidator<T> validator, Expression<Func<T, string?>> property, string path, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(validator);
        validator.RuleFor(property)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Required")
            .Must(values.Contains!)
            .WithMessage((_, v) => $"Invalid enum value. Expected {string.Join(" | ", values.Select(x => $"'{x}'"))}, received '{v}'")
            .OverridePropertyName(path);
    }
}
