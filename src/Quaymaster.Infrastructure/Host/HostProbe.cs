using System.Globalization;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Safety;

namespace Quaymaster.Infrastructure.Host;

/// <summary>Linux host metrics from /proc plus statvfs (via <see cref="DriveInfo"/>).</summary>
internal sealed class HostProbe(string procRoot = "/proc") : IHostProbe
{
    public (double Idle, double Total) CpuTimes()
    {
        var line = ReadFirstLine(Path.Combine(procRoot, "stat"));
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return (0, 0);
        }

        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        if (f.Length < 5)
        {
            return (0, 0);
        }

        // user nice system idle iowait irq: as os.cpus() totals them (idle excludes iowait)
        var idle = f[3];
        var total = f[0] + f[1] + f[2] + f[3] + (f.Length > 5 ? f[5] : 0);
        return (idle, total);
    }

    public long MemTotalBytes() => MemInfo("MemTotal") ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public long? MemAvailableBytes() => MemInfo("MemAvailable");

    public double[] LoadAverage()
    {
        var line = ReadFirstLine(Path.Combine(procRoot, "loadavg"));
        var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is { Length: >= 3 })
        {
            return [.. parts.Take(3).Select(p => double.Parse(p, CultureInfo.InvariantCulture))];
        }

        return [0, 0, 0];
    }

    public double UptimeSeconds()
    {
        var line = ReadFirstLine(Path.Combine(procRoot, "uptime"));
        var first = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : Environment.TickCount64 / 1000.0;
    }

    public (long Total, long Free) Statfs(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("ENOENT");
        }

        var drive = new DriveInfo(path);
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }

    private long? MemInfo(string key)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(procRoot, "meminfo")))
            {
                if (line.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    var digits = new string([.. line[(key.Length + 1)..].Where(char.IsAsciiDigit)]);
                    return long.Parse(digits, CultureInfo.InvariantCulture) * 1024;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            // not Linux, or restricted
        }

        return null;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class SelfProbe : ISelfProbe
{
    public string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string Hostname() => Environment.MachineName;

    public bool IsDirectory(string path) => Directory.Exists(path);
}
