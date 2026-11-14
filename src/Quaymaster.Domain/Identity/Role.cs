namespace Quaymaster.Domain.Identity;

/// <summary>Account role. Wire/DB form is the lowercase name (<see cref="RoleNames"/>).</summary>
public enum Role
{
    Admin,
    Viewer,
}

public static class RoleNames
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";

    public static string ToWire(this Role role) => role == Role.Admin ? Admin : Viewer;

    public static bool TryParse(string? value, out Role role)
    {
        switch (value)
        {
            case Admin: role = Role.Admin; return true;
            case Viewer: role = Role.Viewer; return true;
            default: role = default; return false;
        }
    }
}

/// <summary>What a caller is asking to do. Viewers: <see cref="Read"/>; admins: everything.</summary>
public enum Capability
{
    Read,
    Write,
    Logs,
}
