using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>Form rules the importer used to skip: page targets, role tests next to other conditions, relative-date and field-to-field comparisons.</summary>
public class ImportFormRuleGapsTests
{
    private static string Page(int n) => $"""
        $FormV2Page_{n}:
          Type: QB::FormV2::Page
          Properties:
            Name: Page {n}
          Sections:
            $FormV2Section_{n}:
              Type: QB::FormV2::Section
              Properties:
                Title: S{n}
              Columns:
                $FormV2Column_{n}:
                  Type: QB::FormV2::Column
                  Elements:
                    $FormV2Element_{n}:
                      Type: QB::FormV2::Element::Field
                      Properties:
                        LabelMode: Default
                        Field: !Ref
                          Field: $Field_Name
        """;

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.Split('\n').Select(l => new string(' ', spaces) + l));

    private static string App(int pages, string rule) => $"""
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
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Company Name
                  $Field_Start:
                    Type: QB::Field::Date
                    Properties:
                      Label: Start
                  $Field_End:
                    Type: QB::Field::Date
                    Properties:
                      Label: End
                  $Field_Active:
                    Type: QB::Field::Checkbox
                    Properties:
                      Label: Active
                Forms:
                  Resources:
                    $FormV2_Main_Form:
                      Type: QB::FormV2
                      Properties:
                        Name: Main Form
                      Pages:
        {Indent(string.Join("\n", Enumerable.Range(1, pages).Select(Page)), 18)}
                      Rules:
                        $FormV2Rule_R:
                          Type: QB::FormV2::Rule
                          Properties:
                            Name: R
                            RunOn: RecordSave
        {Indent(rule, 18)}
        """;

    private static string Rule(string when, string actions = "Hide FormV2Field") => $"""
        When:
        {Indent(when, 2)}
        Actions:
        {Indent(actions == "Hide FormV2Field" ? FieldAction : actions, 2)}
        """;

    private const string FieldAction = """
        - Type: QB::FormV2::Rule::Action::MakeReadonly
          Properties:
            Target: !Ref
              Field: $Field_Name
        """;

    private static string Field(string field, string comparison, string extra = "") => $"""
        - Type: QB::FormV2::Rule::Condition::Field
          Properties:
            Field: !Ref
              Field: ${field}
            Comparison: {comparison}
        {Indent(extra, 4)}
        """;

    private static string Typed(string type, string field, string comparison, string extra) => $"""
        - Type: QB::FormV2::Rule::Condition::{type}
          Properties:
            Field: !Ref
              Field: ${field}
            Comparison: {comparison}
        {Indent(extra, 4)}
        """;

    private static string Group(string logic, params string[] children) =>
        "- Type: QB::FormV2::Rule::Condition::Group\n  Properties:\n    TrueWhen: " + logic + "\n  When:\n" +
        string.Join("\n", children.Select(c => Indent(c, 4)));

    private static (PblFormRule Rule, PblDocument Document, List<PblIssue> Issues) Convert(int pages, string rule)
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(App(pages, rule)));
        return (result.Document.Forms[0].Rules.Single(), result.Document, result.Issues);
    }

    private const string RoleClerk = """
        - Type: QB::FormV2::Rule::Condition::Role
          Properties:
            Role: !Ref
              Role: $Role_Clerk
            Comparison: IsInRole
        """;

    [Fact]
    public void RelativeToToday_BecomesATodayComparison()
    {
        var (rule, _, _) = Convert(1, Rule(Group("All", Typed("Relative", "Field_Start", "Before", "RelativeTo: Today"))));

        var condition = rule.Conditions.Should().ContainSingle().Subject;
        (condition.Operator, condition.ValueType).Should().Be(("lt", "today"));
    }

    [Fact]
    public void DaysAgo_BecomesAPastDaysComparison()
    {
        var (rule, _, _) = Convert(1, Rule(Group("All", Typed("DaysAgo", "Field_Start", "Before", "DaysAgo: '365'"))));

        var condition = rule.Conditions.Should().ContainSingle().Subject;
        (condition.Operator, condition.ValueType, condition.Value).Should().Be(("lt", "pastDays", "365"));
    }

    [Fact]
    public void NegativeDaysAgo_IsDaysAhead()
    {
        var (rule, _, _) = Convert(1, Rule(Group("All", Typed("DaysAgo", "Field_Start", "After", "DaysAgo: '-30'"))));

        var condition = rule.Conditions.Should().ContainSingle().Subject;
        (condition.Operator, condition.ValueType, condition.Value).Should().Be(("gt", "futureDays", "30"));
    }

    [Fact]
    public void DuringThisWeek_BecomesADuringCondition()
    {
        var (rule, _, _) = Convert(1, Rule(Group("All", Typed("Range", "Field_Start", "IsDuring", "RelativeTo: This\nTimeFrame: Week"))));

        var condition = rule.Conditions.Should().ContainSingle().Subject;
        (condition.Operator, condition.ValueType, condition.Value).Should().Be(("during", "duringCurrent", "1:week"));
    }

    [Fact]
    public void ComparisonWithAnotherField_KeepsTheOtherField()
    {
        var (rule, _, _) = Convert(1, Rule(Group("All", Field("Field_Start", "Before", "Value: !Ref\n  Field: $Field_End"))));

        var condition = rule.Conditions.Should().ContainSingle().Subject;
        (condition.Operator, condition.ValueType, condition.ValueFieldName).Should().Be(("lt", "field", "End"));
    }

    [Fact]
    public void RoleNextToADateComparisonOnALiteral_BecomesAFormulaWithTheRoleTest()
    {
        var (rule, _, issues) = Convert(1, Rule(Group("All", RoleClerk, Field("Field_Start", "OnOrAfter", "Value: 09-01-2024"))));

        rule.IsExpressionMode.Should().BeTrue();
        rule.ExpressionText.Should().Contain("UserRoles(\"Name\")").And.Contain(";Clerk;").And.Contain("[Start] >= ToDate(\"2024-09-01\")");
        issues.Should().NotContain(i => i.Message.Contains("role condition cannot be part of a formula"));
    }

    [Fact]
    public void RoleInAMixedTree_IsPartOfTheFormula()
    {
        var (rule, _, _) = Convert(1, Rule(Group("Any", RoleClerk, Group("All", Field("Field_Name", "IsEqualTo", "Value: x"), Field("Field_Active", "HasTrueValue")))));

        rule.IsExpressionMode.Should().BeTrue();
        rule.ExpressionText.Should().Contain(" or ").And.Contain(";Clerk;");
    }

    [Fact]
    public void HasTrueValueOnANonCheckbox_MeansTheFieldHoldsSomething()
    {
        var (rule, _, _) = Convert(1, Rule(Group("Any", RoleClerk, Field("Field_Name", "HasTrueValue"))));

        rule.ExpressionText.Should().Contain("ToText([Company Name]) <> \"\"");
    }

    [Fact]
    public void RuleHidingAPageOfAMultiPageForm_TargetsThePage()
    {
        var (rule, document, _) = Convert(2, Rule(Group("All", Field("Field_Name", "IsEqualTo", "Value: x")), """
            - Type: QB::FormV2::Rule::Action::Hide
              Properties:
                Target: !Ref
                  FormV2Page: $FormV2Page_2
            """));

        var action = rule.Actions.Should().ContainSingle().Subject;
        action.TargetType.Should().Be("Page");
        document.Forms[0].Pages.Select(p => p.LogicalRef).Should().Contain(action.TargetPageRef);
        new PblValidator().Validate(document).Issues.Should().NotContain(i => i.Code == "UNKNOWN_FORM_RULE_ACTION_TARGET");
    }

    [Fact]
    public void RuleHidingTheOnlyPage_HidesItsSections()
    {
        var (rule, document, _) = Convert(1, Rule(Group("All", Field("Field_Name", "IsEqualTo", "Value: x")), """
            - Type: QB::FormV2::Rule::Action::Show
              Properties:
                Target: !Ref
                  FormV2Page: $FormV2Page_1
            """));

        var action = rule.Actions.Should().ContainSingle().Subject;
        (action.ActionType, action.TargetType).Should().Be(("Show", "Section"));
        document.Forms[0].Sections.Select(s => s.LogicalRef).Should().Contain(action.TargetSectionRef);
    }

    [Fact]
    public void EmptyDateTimeFormula_IsKeptWithABlankResult()
    {
        const string yaml = """
            Version: '0.12'
            Resources:
              $App_Test:
                Type: QB::Application
                Properties:
                  Name: Test App
                Tables:
                  $Table_Tasks:
                    Type: QB::Table
                    Properties:
                      Name: Tasks
                    Fields:
                      $Field_Reminder:
                        Type: QB::Field::DateTime::Formula
                        Properties:
                          Label: Reminder
                          Formula:
            """;

        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

        result.Document.Tables.Single().Fields.Single(f => f.Label == "Reminder").FormulaExpression.Should().Be("ToTimestamp(\"\")");
    }

    [Fact]
    public void FieldNameWithTrailingSpace_IsFoundByItsTrimmedLabel()
    {
        var fields = new List<PowerBase.Domain.Entities.AppField> { new() { Name = "C_x", Label = "Social Worker for # of Clients", Fid = 7 } };

        var map = PowerBase.Application.Import.Services.FieldFids.ByNameOrLabel(fields);

        map["Social Worker for # of Clients "].Should().Be(7);
    }

    [Fact]
    public void FormElementShowingAReportLink_IsTiedToTheChildTableAndItsReferenceField()
    {
        const string yaml = """
            Version: '0.12'
            Resources:
              $App_Test:
                Type: QB::Application
                Properties:
                  Name: Test App
                Tables:
                  $Table_Files:
                    Type: QB::Table
                    Properties:
                      Name: Files
                    Fields:
                      $Field_Related_Folder:
                        Type: QB::Field::Text
                        Properties:
                          Label: Related Folder
                  $Table_Folders:
                    Type: QB::Table
                    Properties:
                      Name: Folders
                    Fields:
                      $Field_File_records:
                        Type: QB::Field::ReportLink
                        Properties:
                          Label: File records
                          FieldRelationship:
                            Target: !Ref
                              Table: $Table_Files
                              Field: $Field_Related_Folder
                    Forms:
                      Resources:
                        $FormV2_Main_Form:
                          Type: QB::FormV2
                          Properties:
                            Name: Main Form
                          Pages:
                            $FormV2Page_1:
                              Type: QB::FormV2::Page
                              Sections:
                                $FormV2Section_1:
                                  Type: QB::FormV2::Section
                                  Properties:
                                    Title: S1
                                  Columns:
                                    $FormV2Column_1:
                                      Type: QB::FormV2::Column
                                      Elements:
                                        $FormV2Element_1:
                                          Type: QB::FormV2::Element::Field
                                          Properties:
                                            LabelMode: Default
                                            Field: !Ref
                                              Field: $Field_File_records
            """;

        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

        var element = result.Document.Forms.Single().Sections.SelectMany(s => s.Blocks).SelectMany(b => b.Elements).Should().ContainSingle().Subject;
        element.FieldName.Should().BeNull();
        element.ReportLinkChildTableRef.Should().Be("$Table_Files");
        element.ReportLinkReferenceFieldName.Should().Be("Related Folder");
    }

    [Fact]
    public void EmptyComparisonValueOnADate_MeansTheDateIsBlank()
    {
        // an IsEqualTo / IsNotEqualTo with no value, next to a role test so the rule is written as a formula
        var (rule, _, _) = Convert(1, Rule(Group("All", RoleClerk, Field("Field_Start", "IsNotEqualTo"), Field("Field_End", "OnOrAfter", "Value: 09-01-2024"))));

        rule.ExpressionText.Should().Contain("ToText([Start]) <> \"\"");
    }

    [Fact]
    public void DateFieldComparedWithAnotherDateField_IsComparedAsDates()
    {
        var (rule, _, _) = Convert(1, Rule(Group("All", RoleClerk, Field("Field_Start", "OnOrBefore", "Value: !Ref\n  Field: $Field_End"), Field("Field_End", "OnOrAfter", "Value: 09-01-2024"))));

        rule.ExpressionText.Should().Contain("[Start] <= [End]").And.NotContain("ToText([Start])");
    }
}
