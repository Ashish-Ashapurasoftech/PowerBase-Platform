using FluentAssertions;
using PowerBase.Application.Common.Formatting;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.ValueObjects;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

public class QblMappingTests
{
    private static QblResourceNode Node(string type, params (string Key, object? Value)[] props) => new()
    {
        Type = type,
        Properties = props.ToDictionary(p => p.Key, p => p.Value),
    };

    private static string? Field(int id) => id switch { 8 => "Name", 5 => "Age", 6 => "Status", 7 => "Joined", _ => null };

    // ---------------- icons ----------------

    [Theory]
    [InlineData("Folder", "folder")]
    [InlineData("Personal Record", "id-card")]
    [InlineData("Application", "database")]
    public void IconMapper_KnownNames_MapToCatalogIds(string qb, string expected) =>
        QblIconMapper.MapTable(qb).Should().Be(expected);

    [Fact]
    public void IconMapper_UnknownOrBlank_UsesDefaults()
    {
        QblIconMapper.MapApp(null).Should().Be("database");
        QblIconMapper.MapApp("zzz-not-an-icon").Should().Be("database");
        QblIconMapper.MapTable("  ").Should().Be("table");
        QblIconMapper.MapTable("zzz-not-an-icon").Should().Be("table");
    }

    // ---------------- field mapper ----------------

    [Fact]
    public void FieldMapper_Text_MapsMaxLengthRequiredAndHelp()
    {
        var m = QblFieldMapper.Map(Node("QB::Field::Text", ("MaxLength", 50), ("IsRequired", true), ("HelpText", "Hint")), "Text");
        m.SettingsJson.Should().Contain("50");
        m.IsRequired.Should().BeTrue();
        m.HelpText.Should().Be("Hint");
    }

    [Fact]
    public void FieldMapper_Boolean_InvertsShowAsCheckmark()
    {
        var m = QblFieldMapper.Map(Node("QB::Field::Checkbox", ("ShowAsCheckmark", false)), "Boolean");
        m.SettingsJson.Should().NotBeNull();
        m.SettingsJson!.ToLowerInvariant().Should().Contain("showyesno").And.Contain("true");
    }

    [Fact]
    public void FieldMapper_SearchableAndReportable_OnlyEverNarrow()
    {
        var on = QblFieldMapper.Map(Node("QB::Field::Text", ("IsSearchable", true)), "Text");
        on.IsSearchable.Should().BeNull();
        var off = QblFieldMapper.Map(Node("QB::Field::Text", ("IsSearchable", false)), "Text");
        off.IsSearchable.Should().BeFalse();
    }

    // ---------------- app / table mapper ----------------

    [Fact]
    public void AppFormatting_BetweenPosition_MapsToBetweenSignAndNumber()
    {
        var issues = new List<PblIssue>();
        var f = QblAppTableMapper.Formatting(Node("QB::Application", ("CurrencySymbol", "$"), ("CurrencySymbolPosition", "Between")), issues, "$App");
        f.CurrencyPosition.Should().Be("BetweenSignAndNumber");
        f.CurrencySymbol.Should().Be("$");
    }

    [Fact]
    public void AppFormatting_UnknownSymbol_WarnsAndKeepsDefault()
    {
        var issues = new List<PblIssue>();
        var f = QblAppTableMapper.Formatting(Node("QB::Application", ("CurrencySymbol", "Fr")), issues, "$App");
        f.CurrencySymbol.Should().NotBe("Fr");
        issues.Should().Contain(i => i.Code == "QBL_APP_CURRENCY_SYMBOL_UNSUPPORTED");
    }

    [Theory]
    [InlineData("DD-MM-YYYY", "DD-MM-YYYY")]
    [InlineData("YYYY-MM-DD", "YYYY-MM-DD")]
    [InlineData(null, "MM-DD-YYYY")]
    public void DateFormat_KeepsTokenOrder(string? input, string expected) =>
        QblAppTableMapper.DateFormat(input).Should().Be(expected);

    [Fact]
    public void AppSecurity_Everyone_MapsToTrue_AndNonAdminSyncIsInverted()
    {
        var issues = new List<PblIssue>();
        var s = QblAppTableMapper.Security(Node("QB::Application",
            ("CanCopyApp", "Admin"), ("CanExportData", "Admin"), ("DisableNonAdminSyncConnections", true)), issues, "$App");
        s.AllowNonAdminsToCopy.Should().BeFalse();
        s.AllowNonAdminsToExport.Should().BeFalse();
        s.AllowNonAdminsToConnect.Should().BeFalse();
    }

    // ---------------- criteria ----------------

    [Fact]
    public void Criteria_SimpleContains_Translates()
    {
        var r = QblCriteriaTranslator.Translate("({'8'.CT.'Test'})", Field);
        r.Succeeded.Should().BeTrue();
        var c = r.Filter!.Conditions.Should().ContainSingle().Subject;
        c.FieldName.Should().Be("Name");
        c.Operator.Should().Be("contains");
        c.Value.Should().Be("Test");
    }

    [Fact]
    public void Criteria_AndOrWithParentheses_KeepsGrouping()
    {
        var r = QblCriteriaTranslator.Translate("{'8'.EX.'a'} AND ({'5'.GT.'3'} OR {'6'.XEX.'x'})", Field);
        r.Succeeded.Should().BeTrue();
        r.Filter!.Logic.Should().Be("and");
        r.Filter.Count.Should().Be(3);
        r.Filter.Groups.Should().ContainSingle().Which.Logic.Should().Be("or");
    }

    [Fact]
    public void Criteria_CurrentUser_BecomesCurrentUserCondition()
    {
        var r = QblCriteriaTranslator.Translate("{'8'.EX.'_curuser_'}", Field);
        r.Succeeded.Should().BeTrue();
        r.Filter!.Conditions[0].UseCurrentUser.Should().BeTrue();
    }

    [Fact]
    public void Criteria_EmptyValue_BecomesIsEmptyIsNotEmpty()
    {
        QblCriteriaTranslator.Translate("{'8'.EX.''}", Field).Filter!.Conditions[0].Operator.Should().Be("isEmpty");
        QblCriteriaTranslator.Translate("{'8'.XEX.''}", Field).Filter!.Conditions[0].Operator.Should().Be("isNotEmpty");
    }

    [Fact]
    public void Criteria_UnknownField_FailsWholeQuery()
    {
        var r = QblCriteriaTranslator.Translate("{'999'.EX.'a'}", Field);
        r.Succeeded.Should().BeFalse();
        r.Problem.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Criteria_UnsupportedOperator_Fails()
    {
        QblCriteriaTranslator.Translate("{'7'.IR.'today'}", Field).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Criteria_Malformed_Fails()
    {
        QblCriteriaTranslator.Translate("{'8'.EX.'a'", Field).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Criteria_AskPlaceholder_OnlyWhenPromptsAllowed()
    {
        QblCriteriaTranslator.Translate("{'8'.EX.'_ask1_'}", Field).Succeeded.Should().BeFalse();
        var r = QblCriteriaTranslator.Translate("{'8'.EX.'_ask1_'}", Field, allowPrompts: true);
        r.Succeeded.Should().BeTrue();
        r.Filter!.Conditions[0].ValueMode.Should().Be("ask");
    }

    [Fact]
    public void Criteria_RelativeDate_OnlyWhenAllowed()
    {
        QblCriteriaTranslator.Translate("{'7'.OBF.'today'}", Field).Succeeded.Should().BeFalse();
        var r = QblCriteriaTranslator.Translate("{'7'.OBF.'today'}", Field, allowRelativeDates: true);
        r.Succeeded.Should().BeTrue();
        r.Filter!.Conditions[0].ValueMode.Should().Be("today");
    }

    [Fact]
    public void Criteria_FieldReference_BecomesFieldValueMode()
    {
        var r = QblCriteriaTranslator.Translate("{'8'.EX.'_fid_5'}", Field);
        r.Succeeded.Should().BeTrue();
        r.Filter!.Conditions[0].ValueMode.Should().Be("field");
        r.Filter.Conditions[0].ValueFieldName.Should().Be("Age");
    }

    // ---------------- permissions (via converter) ----------------

    private static string RoleYaml(string viewBlock, string modifyBlock, string roleProps = "") => $"""
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Roles:
              $Role_Rep:
                Type: QB::Application::Role
                Properties:
                  Name: Rep
                  {roleProps}
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                  RolePermissions:
                    $Role_Rep:
                      CanAddRecords: true
                      CanDeleteRecords: false
                      CanSaveCommonReports: false
                      CanEditFieldProperties: false
                      CanViewRecords:
        {viewBlock}
                      CanModifyRecords:
        {modifyBlock}
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Id: 8
                    Properties:
                      Label: Name
        """;

    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private const string ViewAlways = "                        When: Always";
    private const string ViewNever = "                        When: Never";
    private const string ViewCustom = "                        When: CustomRule\n                        CustomAccessCriteria: \"({'8'.CT.'Test'})\"";
    private const string ViewCustomBad = "                        When: CustomRule\n                        CustomAccessCriteria: \"({'999'.EX.'1'})\"";

    [Fact]
    public void Permission_CustomRule_TranslatesToRecordFilter()
    {
        var perm = Convert(RoleYaml(ViewCustom, ViewCustom)).Document.Roles[0].TablePermissions.Should().ContainSingle().Subject;
        perm.ViewScope.Should().NotBe("None");
        perm.RecordFilter.Should().NotBeNull();
        perm.RecordFilter!.Conditions[0].FieldName.Should().Be("Name");
    }

    [Fact]
    public void Permission_UntranslatableCustomRule_NeverWidensToAllRecords()
    {
        var result = Convert(RoleYaml(ViewCustomBad, ViewCustomBad));
        var perm = result.Document.Roles[0].TablePermissions.Should().ContainSingle().Subject;
        perm.ViewScope.Should().Be("None");
        perm.RecordFilter.Should().BeNull();
        result.Issues.Should().Contain(i => i.Severity == PblIssueSeverity.Warning && i.Code == "QBL_ROLE_CUSTOM_RULE_UNTRANSLATABLE");
    }

    [Fact]
    public void Permission_ViewNever_ForcesModifyToNone()
    {
        var perm = Convert(RoleYaml(ViewNever, ViewAlways)).Document.Roles[0].TablePermissions.Should().ContainSingle().Subject;
        perm.ViewScope.Should().Be("None");
        perm.ModifyScope.Should().Be("None");
    }

    [Fact]
    public void Permission_ModifyRuleDifferentFromView_IsNotWidened()
    {
        const string otherRule = "                        When: CustomRule\n                        CustomAccessCriteria: \"({'5'.GT.'3'})\"";
        var perm = Convert(RoleYaml(ViewCustom, otherRule)).Document.Roles[0].TablePermissions.Should().ContainSingle().Subject;
        perm.ModifyScope.Should().Be("None");
    }

    [Fact]
    public void Permission_ModifyAlways_IsBoundedByViewFilter()
    {
        var perm = Convert(RoleYaml(ViewCustom, ViewAlways)).Document.Roles[0].TablePermissions.Should().ContainSingle().Subject;
        perm.RecordFilter.Should().NotBeNull();
        perm.ModifyScope.Should().Be("AllRecords");
    }

    [Fact]
    public void Role_DisableAccess_ForcesEveryTablePermissionToNone()
    {
        var role = Convert(RoleYaml(ViewAlways, ViewAlways, "DisableAccess: true")).Document.Roles[0];
        role.DisableAccess.Should().BeTrue();
        role.TablePermissions.Should().OnlyContain(p => p.ViewScope == "None" && p.ModifyScope == "None");
    }

    [Fact]
    public void PermissionCodes_DisableAccess_IsEmpty()
    {
        PblPermissionsApplier.PermissionCodesFor(new PblRole { DisableAccess = true, ManageUsers = true, EditApp = true }).Should().BeEmpty();
    }

    [Fact]
    public void PermissionCodes_ManageUsersAndEditApp_AreAdditiveOverBase()
    {
        var baseCodes = PblPermissionsApplier.PermissionCodesFor(new PblRole());
        var withUsers = PblPermissionsApplier.PermissionCodesFor(new PblRole { ManageUsers = true });
        var withApp = PblPermissionsApplier.PermissionCodesFor(new PblRole { EditApp = true });
        withUsers.Should().Contain(baseCodes).And.HaveCountGreaterThan(baseCodes.Count);
        withApp.Should().Contain(baseCodes).And.HaveCountGreaterThan(baseCodes.Count);
        baseCodes.Should().NotContain("users:manage");
    }

    // ---------------- display formatting ----------------

    [Fact]
    public void FormatCurrency_BetweenSignAndNumber_PutsSymbolAfterMinus()
    {
        var app = new AppFormattingSettings();
        app.Currency.Symbol = "$";
        app.Currency.Position = "BetweenSignAndNumber";
        DisplayValueFormatter.FormatCurrency(-123.45m, null, app).Should().Be("-$123.45");
        DisplayValueFormatter.FormatCurrency(123.45m, null, app).Should().Be("$123.45");
    }
}
