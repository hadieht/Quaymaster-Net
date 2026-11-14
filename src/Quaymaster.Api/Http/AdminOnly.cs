using Quaymaster.Application.Errors;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Api.Http;

/// <summary>Endpoint filter: requires an authenticated caller holding <paramref name="capability"/>.</summary>
public static class AuthorizationFilters
{
    public static RouteGroupBuilder RequireCapability(this RouteGroupBuilder group, Capability capability)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var auth = invocation.HttpContext.GetAuth() ?? throw AppException.Unauthorized();
            if (!auth.User.Can(capability))
            {
                throw AppException.Forbidden(
                    capability == Capability.Logs ? "Your account may not read container logs" : "Administrator role required");
            }

            return await next(invocation);
        });
        return group;
    }
}
