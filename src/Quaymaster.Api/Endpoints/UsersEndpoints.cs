using FluentValidation;
using Quaymaster.Api.Http;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Users;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Api.Endpoints;

/// <summary><c>/api/v1/users</c> - administrator only.</summary>
public static class UsersEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var users = api.MapGroup("/users").RequireCapability(Capability.Write);

        users.MapGet(string.Empty, (HttpContext http, IUserService service) => service.ListAsync(http.RequestAborted));

        users.MapPost(string.Empty, async (HttpContext http, IValidator<CreateUserRequest> validator, IUserService service) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            _ = RoleNames.TryParse(body.Role, out var role);
            var created = await service.CreateAsync(
                body.Username!, body.Password!, role, body.LogsRead ?? false, http.ActorOf(), http.RequestAborted);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        });

        users.MapPatch("/{id}", async (HttpContext http, string id, IValidator<PatchUserRequest> validator, IUserService service) =>
        {
            var userId = ParseId(id);
            var body = await StrictJsonBody.ReadAsync(http, validator);
            var patch = new UserPatch(
                RoleNames.TryParse(body.Role, out var role) ? role : null,
                body.LogsRead,
                body.Disabled);
            return await service.UpdateAsync(userId, patch, http.ActorOf(), http.RequestAborted);
        });

        users.MapPost("/{id}/password", async (HttpContext http, string id, IValidator<SetPasswordRequest> validator, IUserService service) =>
        {
            var userId = ParseId(id);
            var body = await StrictJsonBody.ReadAsync(http, validator);
            await service.SetPasswordAsync(userId, body.Password!, http.ActorOf(), http.RequestAborted);
            return new OkDto();
        });

        users.MapDelete("/{id}", async (HttpContext http, string id, IUserService service) =>
        {
            await service.DeleteAsync(ParseId(id), http.ActorOf(), http.RequestAborted);
            return new OkDto();
        });
    }

    private static long ParseId(string raw) =>
        long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw AppException.BadRequest("Invalid request", new[] { new ValidationIssue("id", "Expected a positive integer") });
}
