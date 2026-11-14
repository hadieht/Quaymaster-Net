using FluentValidation;
using Quaymaster.Api.Http;
using Quaymaster.Application.Auth;

namespace Quaymaster.Api.Endpoints;

/// <summary>
/// <c>/api/v1/auth/*</c>. Public: status, setup, login (see <see cref="ApiGuardMiddleware.PublicPaths"/>); the rest
/// require a session, which the guard middleware has already verified.
/// </summary>
public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/auth/status", async (HttpContext http, ISetupService setup) =>
            new AuthStatusDto(await setup.IsRequiredAsync(http.RequestAborted), http.GetAuth() is not null));

        api.MapPost("/auth/setup", async (HttpContext http, IValidator<SetupRequest> validator, ISetupService setup, ISessionService sessions, SessionCookie cookie) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            var (token, context) = await setup.CompleteAsync(
                body.Token!, body.Username!, body.Password!, http.ClientIp(), http.UserAgent(), http.RequestAborted);
            cookie.Set(http.Response, token);
            return sessions.ToInfo(context);
        });

        api.MapPost("/auth/login", async (HttpContext http, IValidator<LoginRequest> validator, IAuthService auth, ISessionService sessions, SessionCookie cookie) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            var (token, context) = await auth.LoginAsync(
                body.Username!, body.Password!, http.ClientIp(), http.UserAgent(), http.RequestAborted);
            cookie.Set(http.Response, token);
            return sessions.ToInfo(context);
        });

        api.MapPost("/auth/logout", async (HttpContext http, IAuthService auth, SessionCookie cookie) =>
        {
            await auth.LogoutAsync(http.RequireAuth(), http.ActorOf(), http.RequestAborted);
            cookie.Clear(http.Response);
            return new OkDto();
        });

        api.MapPost("/auth/logout-all", async (HttpContext http, IAuthService auth, SessionCookie cookie) =>
        {
            await auth.LogoutAllAsync(http.RequireAuth(), http.ActorOf(), http.RequestAborted);
            cookie.Clear(http.Response);
            return new OkDto();
        });

        api.MapGet("/auth/me", (HttpContext http, ISessionService sessions) => sessions.ToInfo(http.RequireAuth()));

        api.MapPost("/auth/password", async (HttpContext http, IValidator<ChangePasswordRequest> validator, IAuthService auth) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            await auth.ChangePasswordAsync(http.RequireAuth(), body.Current!, body.Next!, http.ActorOf(), http.RequestAborted);
            return new OkNoteDto(true, "All other sessions were signed out");
        });
    }
}
