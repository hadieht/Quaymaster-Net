namespace Quaymaster.Api.Http;

public static class RequestLimits
{
    /// <summary>Largest accepted request body (the original server's bodyLimit).</summary>
    public const int MaxBodyBytes = 64 * 1024;
}
