namespace Quaymaster.Domain.Identity;

/// <summary>
/// The system must always keep at least one active administrator, otherwise nobody could ever
/// manage users again. Pure functions so the rule is testable without a database.
/// </summary>
public static class LastAdminRule
{
    /// <summary>Would this patch demote or disable <paramref name="target"/> (an active admin)?</summary>
    public static bool PatchRemovesAdmin(User target, UserPatch patch)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(patch);
        return target.IsActiveAdmin && (patch.Role == Role.Viewer || patch.Disabled == true);
    }

    public static bool PatchViolates(User target, UserPatch patch, int activeAdminCount) =>
        PatchRemovesAdmin(target, patch) && activeAdminCount <= 1;

    public static bool DeleteViolates(User target, int activeAdminCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.IsActiveAdmin && activeAdminCount <= 1;
    }
}
