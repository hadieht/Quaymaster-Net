using FluentAssertions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Validation;

namespace Quaymaster.UnitTests.Validation;

public sealed class RequestValidatorTests
{
    private static IReadOnlyList<ValidationIssue> Issues<T>(FluentValidation.IValidator<T> validator, T model)
    {
        var error = FluentActions.Invoking(() => validator.EnsureValid(model)).Should().Throw<AppException>().Which;
        error.Status.Should().Be(400);
        error.Code.Should().Be(ErrorCode.Validation);
        error.Message.Should().Be("Invalid request");
        return (IReadOnlyList<ValidationIssue>)error.Details!;
    }

    [Fact]
    public void Missing_Fields_Are_Reported_Per_Field_With_Zod_Style_Messages()
    {
        var issues = Issues(new LoginRequestValidator(), new LoginRequest(null, null));

        issues.Should().Equal(new ValidationIssue("username", "Required"), new ValidationIssue("password", "Required"));
    }

    [Fact]
    public void Length_Limits_Match_The_Original_Schemas()
    {
        Issues(new LoginRequestValidator(), new LoginRequest("", "x")).Should().Equal(new ValidationIssue("username", "String must contain at least 1 character(s)"));
        Issues(new LoginRequestValidator(), new LoginRequest(new string('a', 65), "x")).Should().Equal(new ValidationIssue("username", "String must contain at most 64 character(s)"));
        Issues(new LoginRequestValidator(), new LoginRequest("u", new string('a', 1025))).Should().Equal(new ValidationIssue("password", "String must contain at most 1024 character(s)"));
        FluentActions.Invoking(() => new LoginRequestValidator().EnsureValid(new LoginRequest(new string('a', 64), new string('a', 1024)))).Should().NotThrow();
    }

    [Fact]
    public void Setup_Token_Is_Limited_To_200_Characters()
    {
        Issues(new SetupRequestValidator(), new SetupRequest(new string('t', 201), "u", "p")).Should().Equal(new ValidationIssue("token", "String must contain at most 200 character(s)"));
    }

    [Fact]
    public void Change_Password_Uses_The_Original_Field_Names()
    {
        Issues(new ChangePasswordRequestValidator(), new ChangePasswordRequest(null, "")).Select(i => i.Path).Should().Equal("current", "next");
    }

    [Theory]
    [InlineData(null, "Required")]
    [InlineData("root", "Invalid enum value. Expected 'admin' | 'viewer', received 'root'")]
    [InlineData("Admin", "Invalid enum value. Expected 'admin' | 'viewer', received 'Admin'")]
    public void Role_Must_Be_An_Exact_Enum_Value(string? role, string message) =>
        Issues(new CreateUserRequestValidator(), new CreateUserRequest("bob", "pw", role, null)).Should().Equal(new ValidationIssue("role", message));

    [Fact]
    public void Patch_Fields_Are_All_Optional_But_Role_Is_Checked_When_Present()
    {
        FluentActions.Invoking(() => new PatchUserRequestValidator().EnsureValid(new PatchUserRequest(null, null, null))).Should().NotThrow();
        FluentActions.Invoking(() => new PatchUserRequestValidator().EnsureValid(new PatchUserRequest("viewer", true, false))).Should().NotThrow();
        Issues(new PatchUserRequestValidator(), new PatchUserRequest("nope", null, null)).Should().ContainSingle().Which.Path.Should().Be("role");
    }
}
