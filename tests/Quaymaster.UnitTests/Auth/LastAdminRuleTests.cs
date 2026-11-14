using FluentAssertions;
using Quaymaster.Domain.Identity;

namespace Quaymaster.UnitTests.Auth;

public sealed class LastAdminRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static User Make(Role role, bool disabled = false)
    {
        var user = new User("u", "h", role, false, Now);
        if (disabled)
        {
            user.Apply(new UserPatch(Disabled: true));
        }

        return user;
    }

    [Theory]
    [InlineData(Role.Admin, false, null, null, false)] // empty patch
    [InlineData(Role.Admin, false, Role.Viewer, null, true)] // demote
    [InlineData(Role.Admin, false, null, true, true)] // disable
    [InlineData(Role.Admin, false, Role.Admin, false, false)] // no-op
    [InlineData(Role.Admin, true, Role.Viewer, null, false)] // already disabled: not an active admin
    [InlineData(Role.Viewer, false, null, true, false)] // viewers are irrelevant
    [InlineData(Role.Viewer, false, Role.Viewer, null, false)]
    public void Patch_Removes_An_Admin_Only_When_It_Demotes_Or_Disables_An_Active_Admin(
        Role role, bool alreadyDisabled, Role? newRole, bool? disable, bool expected) =>
        LastAdminRule.PatchRemovesAdmin(Make(role, alreadyDisabled), new UserPatch(newRole, null, disable)).Should().Be(expected);

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void Demoting_Is_Blocked_Only_When_No_Other_Active_Admin_Remains(int activeAdmins, bool blocked) =>
        LastAdminRule.PatchViolates(Make(Role.Admin), new UserPatch(Role.Viewer), activeAdmins).Should().Be(blocked);

    [Theory]
    [InlineData(Role.Admin, false, 1, true)]
    [InlineData(Role.Admin, false, 2, false)]
    [InlineData(Role.Admin, true, 1, false)] // deleting a disabled admin never leaves the system admin-less
    [InlineData(Role.Viewer, false, 1, false)]
    public void Deleting_Is_Blocked_Only_For_The_Last_Active_Admin(Role role, bool disabled, int activeAdmins, bool blocked) =>
        LastAdminRule.DeleteViolates(Make(role, disabled), activeAdmins).Should().Be(blocked);

    [Fact]
    public void Admins_Always_Read_Logs_And_Viewers_Only_When_Granted()
    {
        var admin = new User("a", "h", Role.Admin, false, Now);
        var viewer = new User("v", "h", Role.Viewer, false, Now);

        admin.EffectiveLogsRead.Should().BeTrue();
        viewer.EffectiveLogsRead.Should().BeFalse();
        viewer.Can(Capability.Logs).Should().BeFalse();
        viewer.Can(Capability.Read).Should().BeTrue();
        viewer.Can(Capability.Write).Should().BeFalse();

        viewer.Apply(new UserPatch(LogsRead: true));
        viewer.Can(Capability.Logs).Should().BeTrue();
        viewer.Can(Capability.Write).Should().BeFalse();
        admin.Can(Capability.Write).Should().BeTrue();
    }
}
