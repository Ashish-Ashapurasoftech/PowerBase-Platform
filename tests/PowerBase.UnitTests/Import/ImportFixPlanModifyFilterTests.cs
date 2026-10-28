using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>A role's "modify" rule that differs from its "view" rule is carried as PowerBase's separate Modify record filter.</summary>
public class ImportFixPlanModifyFilterTests
{
    private static string App(string view, string modify) => $$"""
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Roles:
              $Role_Clerk:
                Type: QB::Application::Role
                Properties:
                  Name: Clerk
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                  RolePermissions:
                    $Role_Clerk:
                      CanAddRecords: true
                      CanDeleteRecords: false
                      CanViewRecords:
        {{view}}
                      CanModifyRecords:
        {{modify}}
                Fields:
                  $Field_Status:
                    Type: QB::Field::Text
                    Id: 8
                    Properties:
                      Label: Status
        """;

    private const string Always = "                        When: Always";
    private const string Never = "                        When: Never";

    private static string Custom(string criteria) =>
        "                      When: CustomRule\n                      CustomAccessCriteria: |-\n                        " + criteria;

    private static PowerBase.Application.Import.Pbl.PblTablePermission Permission(string view, string modify, out List<PowerBase.Application.Import.Pbl.PblIssue> issues)
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(App(view, modify)));
        issues = result.Issues;
        return result.Document.Roles.Single().TablePermissions.Single();
    }

    [Fact]
    public void ModifyRuleDifferentFromViewRule_IsCarriedAsASecondFilter()
    {
        var permission = Permission(Always, Custom("({'8'.CT.'Open'})"), out var issues);

        permission.ViewScope.Should().Be("AllRecords");
        permission.ModifyScope.Should().Be("AllRecords");
        permission.RecordFilter.Should().BeNull();
        permission.ModifyRecordFilter.Should().NotBeNull();
        issues.Should().NotContain(i => i.Code == "QBL_ROLE_MODIFY_CRITERIA_DIFFERS");
    }

    [Fact]
    public void ModifyRuleSameAsViewRule_NeedsNoSecondFilter()
    {
        var permission = Permission(Custom("({'8'.CT.'Open'})"), Custom("({'8'.CT.'Open'})"), out _);

        permission.ModifyScope.Should().Be("AllRecords");
        permission.ModifyRecordFilter.Should().BeNull();
        permission.RecordFilter.Should().NotBeNull();
    }

    [Fact]
    public void ModifyAlways_IsBoundedByTheViewRuleAndNeedsNoSecondFilter()
    {
        var permission = Permission(Custom("({'8'.CT.'Open'})"), Always, out _);

        permission.ModifyScope.Should().Be("AllRecords");
        permission.ModifyRecordFilter.Should().BeNull();
    }

    [Fact]
    public void ModifyRuleOnATableTheRoleCannotView_StaysNone()
    {
        var permission = Permission(Never, Custom("({'8'.CT.'Open'})"), out var issues);

        permission.ModifyScope.Should().Be("None");
        permission.ModifyRecordFilter.Should().BeNull();
        issues.Should().Contain(i => i.Code == "QBL_ROLE_MODIFY_CRITERIA_DIFFERS");
    }
}
