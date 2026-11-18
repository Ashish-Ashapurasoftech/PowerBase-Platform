using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.QbMigration;
using PowerBase.Infrastructure.QbMigration;

namespace PowerBase.QbMigration.Tests;

public class QbUserPlannerTests
{
    private static readonly QbRole Viewer = new(10, "Viewer", 1), VdbAdmin = new(11, "VDB Administrator", 2), Admin = new(12, "Administrator", 1),
        Test = new(20, "Test", 3), None = new(9, "none", 0), Super = new(0, "", 0);
    private static readonly QbRole[] Order = [Viewer, VdbAdmin, Admin, Test];
    private static readonly string[] PbRoles = ["Administrator", "Participant", "Viewer", "VDB Administrator", "Test"];

    private static QbAppUser U(string email, bool? registered, params QbRole[] roles) => new("id-" + email, email, email.Split('@')[0], null, null, registered, roles);
    private static QbUserPlanResult Plan(params QbAppUser[] users) => QbUserPlanner.Plan(new QbUserDirectory(Order, users, []), PbRoles);

    [Fact]
    public void Registered_users_are_active_and_others_inactive()
    {
        var r = Plan(U("a@x.com", true, Test), U("b@x.com", false, Test), U("c@x.com", null, Test));
        r.Users.Select(u => (u.Email, u.IsActive)).Should().Equal(("a@x.com", true), ("b@x.com", false), ("c@x.com", false));
        r.Warnings.Should().ContainSingle(w => w.Contains("c@x.com"));
    }

    [Fact]
    public void Each_user_gets_the_exact_role_and_several_roles_take_quickbases_first()
    {
        var r = Plan(U("a@x.com", true, Test), U("b@x.com", false, VdbAdmin, Viewer), U("c@x.com", true, Test, None));
        r.Users.Select(u => u.AppRoleName).Should().Equal("Test", "Viewer", "Test");
        r.Users[1].Note.Should().Contain("2 QuickBase roles");
    }

    [Fact]
    public void Users_without_a_role_get_the_none_role_and_keep_their_status()
    {
        var r = Plan(U("a@x.com", true, None), U("b@x.com", false));
        r.Users.Select(u => (u.AppRoleName, u.IsActive)).Should().Equal(("None", true), ("None", false));
        r.CanApply.Should().BeTrue("PowerBase's 'None' role is created by the migration");
    }

    [Fact]
    public void A_super_user_is_an_administrator_but_never_more()
    {
        var r = Plan(U("chaim@x.com", true, Super), U("dev@x.com", true, Test, Super));
        r.Users.Should().OnlyContain(u => u.IsSuperUser && u.AppRoleName == "Administrator");
    }

    [Fact]
    public void A_role_powerbase_lacks_stops_the_migration_and_users_without_email_are_reported()
    {
        var r = QbUserPlanner.Plan(new QbUserDirectory([.. Order, new QbRole(30, "CC Rep", 0)],
            [U("a@x.com", true, new QbRole(30, "CC Rep", 0)), new QbAppUser("x", null, "Nobody", null, null, null, [None])], []), PbRoles);
        r.MissingRoles.Should().Equal("CC Rep");
        r.CanApply.Should().BeFalse();
        r.Warnings.Should().ContainSingle(w => w.Contains("Nobody") && w.Contains("no email"));
    }

    [Fact]
    public void A_user_listed_directly_and_through_a_group_counts_once_with_all_roles()
    {
        var r = Plan(U("a@x.com", true, Test), U("A@X.com", true, Admin));
        r.Users.Should().ContainSingle().Which.AppRoleName.Should().Be("Administrator");
    }

    [Fact]
    public void Built_in_roles_get_quickbases_switches_and_keep_everything_else()
    {
        var current = new[] { "records:read", "tables:read", "fields:read", "reports:run" };
        var target = QbmBuiltInRoleSync.Target(current, new PblRole { Name = "Viewer", ManageUsers = true, EditApp = true });
        target.Should().Contain(current).And.Contain(QbmBuiltInRoleSync.ManageUsersCodes).And.Contain(QbmBuiltInRoleSync.EditAppCodes);

        var withAdminCodes = target.Append("audit:read").ToList();
        var basic = QbmBuiltInRoleSync.Target(withAdminCodes, new PblRole { Name = "Viewer" });
        basic.Should().Contain(current).And.Contain("audit:read").And.NotContain(QbmBuiltInRoleSync.ManageUsersCodes).And.NotContain(QbmBuiltInRoleSync.EditAppCodes);
    }

    [Fact]
    public void A_viewer_without_edit_app_keeps_powerbases_default_page_access()
    {
        QbmBuiltInRoleSync.EditAppCodes.Should().NotContain("pages:read");
        QbmBuiltInRoleSync.Target(["records:read", "pages:read"], new PblRole { Name = "Viewer" }).Should().Contain("pages:read");
    }
}
