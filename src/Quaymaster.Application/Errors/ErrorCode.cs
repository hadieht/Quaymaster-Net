namespace Quaymaster.Application.Errors;

/// <summary>
/// Every error code of the HTTP API (docs/API.md; mirrors <c>ErrorCode</c> in shared/src/api.ts).
/// The wire form is the snake_case string from <see cref="ErrorCodes.ToWire"/>.
/// </summary>
public enum ErrorCode
{
    Validation,
    Unauthorized,
    Forbidden,
    Csrf,
    Origin,
    NotFound,
    Conflict,
    RateLimited,
    ReadOnly,
    OutOfScope,
    SelfProtected,
    ConfirmationRequired,
    BlockedDrift,
    LimitedCapability,
    Ignored,
    Unsupported,
    DockerUnavailable,
    ComposeFailed,
    ApprovalStale,
    WindowClosed,
    InsufficientDisk,
    RegistryError,
    SetupRequired,
    Internal,
}

public static class ErrorCodes
{
    public static string ToWire(this ErrorCode code) => code switch
    {
        ErrorCode.Validation => "validation",
        ErrorCode.Unauthorized => "unauthorized",
        ErrorCode.Forbidden => "forbidden",
        ErrorCode.Csrf => "csrf",
        ErrorCode.Origin => "origin",
        ErrorCode.NotFound => "not_found",
        ErrorCode.Conflict => "conflict",
        ErrorCode.RateLimited => "rate_limited",
        ErrorCode.ReadOnly => "read_only",
        ErrorCode.OutOfScope => "out_of_scope",
        ErrorCode.SelfProtected => "self_protected",
        ErrorCode.ConfirmationRequired => "confirmation_required",
        ErrorCode.BlockedDrift => "blocked_drift",
        ErrorCode.LimitedCapability => "limited_capability",
        ErrorCode.Ignored => "ignored",
        ErrorCode.Unsupported => "unsupported",
        ErrorCode.DockerUnavailable => "docker_unavailable",
        ErrorCode.ComposeFailed => "compose_failed",
        ErrorCode.ApprovalStale => "approval_stale",
        ErrorCode.WindowClosed => "window_closed",
        ErrorCode.InsufficientDisk => "insufficient_disk",
        ErrorCode.RegistryError => "registry_error",
        ErrorCode.SetupRequired => "setup_required",
        ErrorCode.Internal => "internal",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
