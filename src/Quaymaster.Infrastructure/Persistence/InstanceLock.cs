using Quaymaster.Infrastructure.Files;

namespace Quaymaster.Infrastructure.Persistence;

/// <summary>
/// Single-instance guard: two instances on one data directory would run two schedulers. Holds an exclusive
/// OS-level lock (flock on Linux) on <c>&lt;data&gt;/quaymaster.lock</c> for the life of the process; the OS releases it
/// on any exit, so there is no stale-lock handling to get wrong.
/// </summary>
internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock Acquire(string dataDir)
    {
        FilePermissions.EnsureDirectory(dataDir);
        var path = Path.Combine(dataDir, "quaymaster.lock");
        try
        {
            return new InstanceLock(new FileStream(path, FilePermissions.Options(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)));
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"Another Quaymaster instance is using {dataDir}. Refusing to start.", e);
        }
    }

    public void Dispose() => _stream.Dispose();
}
