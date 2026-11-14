using Quaymaster.Application.Errors;

namespace Quaymaster.Application.Docker;

/// <summary>API version negotiation (RT-31): use min(daemon, tested maximum); refuse daemons that are too old or too new.</summary>
public static class ApiVersioning
{
    /// <summary>Highest Engine API version Quaymaster has been tested against.</summary>
    public const string TestedMax = "1.55";

    /// <summary>Lowest API version the code relies on.</summary>
    public const string RequiredMin = "1.40";

    public static int Compare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var pa = a.Split('.').Select(ParsePart).ToArray();
        var pb = b.Split('.').Select(ParsePart).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var d = (i < pa.Length ? pa[i] : 0) - (i < pb.Length ? pb[i] : 0);
            if (d != 0)
            {
                return d < 0 ? -1 : 1;
            }
        }

        return 0;
    }

    public static string Negotiate(string daemonApi, string? daemonMin, string testedMax = TestedMax, string required = RequiredMin)
    {
        ArgumentNullException.ThrowIfNull(daemonApi);
        if (Compare(daemonApi, required) < 0)
        {
            throw new AppException(
                503,
                ErrorCode.DockerUnavailable,
                $"Docker Engine API {daemonApi} is older than the minimum Quaymaster supports ({required})");
        }

        if (!string.IsNullOrEmpty(daemonMin) && Compare(daemonMin, testedMax) > 0)
        {
            throw new AppException(
                503,
                ErrorCode.DockerUnavailable,
                $"Docker daemon requires API >= {daemonMin}, newer than the newest version Quaymaster is tested with ({testedMax})");
        }

        return Compare(daemonApi, testedMax) < 0 ? daemonApi : testedMax;
    }

    private static int ParsePart(string s) => int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
}
