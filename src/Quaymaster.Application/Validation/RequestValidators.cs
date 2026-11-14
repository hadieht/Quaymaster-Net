using FluentValidation;
using Quaymaster.Application.Auth;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Application.Validation;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        this.RequireString(x => x.Username, "username", 1, 64);
        this.RequireString(x => x.Password, "password", 1, 1024);
    }
}

public sealed class SetupRequestValidator : AbstractValidator<SetupRequest>
{
    public SetupRequestValidator()
    {
        this.RequireString(x => x.Token, "token", 1, 200);
        this.RequireString(x => x.Username, "username", 1, 64);
        this.RequireString(x => x.Password, "password", 1, 1024);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        this.RequireString(x => x.Current, "current", 1, 1024);
        this.RequireString(x => x.Next, "next", 1, 1024);
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        this.RequireString(x => x.Username, "username", 1, 64);
        this.RequireString(x => x.Password, "password", 1, 1024);
        this.RequireEnum(x => x.Role, "role", RoleNames.Admin, RoleNames.Viewer);
    }
}

public sealed class PatchUserRequestValidator : AbstractValidator<PatchUserRequest>
{
    public PatchUserRequestValidator()
    {
        When(x => x.Role is not null, () => this.RequireEnum(x => x.Role, "role", RoleNames.Admin, RoleNames.Viewer));
    }
}

public sealed class SetPasswordRequestValidator : AbstractValidator<SetPasswordRequest>
{
    public SetPasswordRequestValidator()
    {
        this.RequireString(x => x.Password, "password", 1, 1024);
    }
}
