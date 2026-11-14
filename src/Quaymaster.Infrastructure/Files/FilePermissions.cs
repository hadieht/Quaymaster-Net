namespace Quaymaster.Infrastructure.Files;

/// <summary>
/// POSIX file modes for everything holding data or secrets: directories 0700, files 0600. On Windows (developer
/// machines only; deployment is Linux) the modes are skipped.
/// </summary>
internal static class FilePermissions
{
    /// <summary>rwx------ for the data directory.</summary>
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>rw------- for files holding secrets or data.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, DirectoryMode);
        }
    }

    public static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }

    /// <summary>Stream options that create new files as 0600.</summary>
    public static FileStreamOptions Options(System.IO.FileMode mode, FileAccess access, FileShare share = FileShare.Read)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        return options;
    }
}
