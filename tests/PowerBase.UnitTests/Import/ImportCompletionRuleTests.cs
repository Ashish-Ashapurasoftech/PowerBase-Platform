using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>WP7 of the import completion work: form rules — case-insensitive names, nested groups, mixed AND/OR as a formula.</summary>
public class ImportCompletionRuleTests
{
    private const string Head = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
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
                  $Field_Qty:
                    Type: QB::Field::Number
                    Properties:
                      Label: Qty
                  $Field_Active:
                    Type: QB::Field::Checkbox
                    Properties:
                      Label: Active
                  $Field_Due:
                    Type: QB::Field::Date
                    Properties:
                      Label: Due
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
                                          Field: $Field_Name
                      Rules:
                        $FormV2Rule_R:
                          Type: QB::FormV2::Rule
                          Properties:
                            Name: R
                            RunOn: RecordSave
        """;

    private const string Action = """
        Actions:
          - Type: QB::FormV2::Rule::Action::MakeReadonly
            Properties:
              Target: !Ref
                Field: $Field_Name
        """;

    private const string NameIsX = """
        - Type: QB::FormV2::Rule::Condition::Field
          Properties:
            Field: !Ref
              Field: $Field_Name
            Comparison: IsEqualTo
            Value: x
        """;

    private const string QtyOver3 = """
        - Type: QB::FormV2::Rule::Condition::Field
          Properties:
            Field: !Ref
              Field: $Field_Qty
            Comparison: IsGreaterThan
            Value: '3'
        """;

    private const string ActiveIsTrue = """
        - Type: QB::FormV2::Rule::Condition::Field
          Properties:
            Field: !Ref
              Field: $Field_Active
            Comparison: HasTrueValue
        """;

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.Split('\n').Select(l => new string(' ', spaces) + l));

    private static string Group(string logic, params string[] children) =>
        "- Type: QB::FormV2::Rule::Condition::Group\n  Properties:\n    TrueWhen: " + logic + "\n  When:\n" +
        string.Join("\n", children.Select(c => Indent(c, 4)));

    private static PowerBase.Application.Import.Pbl.PblFormRule ConvertRule(string conditions, out List<PowerBase.Application.Import.Pbl.PblIssue> issues)
    {
        var yaml = Head + "\n" + Indent("When:\n" + Indent(conditions, 2) + "\n" + Action, 18);
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));
        issues = result.Issues;
        return result.Document.Forms[0].Rules.SingleOrDefault()!;
    }

    [Fact]
    public void MakeReadonlyActionSpelledWithLowerCaseO_IsRecognised()
    {
        var rule = ConvertRule(Group("All", NameIsX), out _);

        rule.Actions.Should().ContainSingle().Which.ActionType.Should().Be("Disable");
    }

    [Fact]
    public void NestedGroupWithSameLogic_IsFlattened()
    {
        var rule = ConvertRule(Group("All", NameIsX, Group("All", QtyOver3)), out _);

        rule.IsExpressionMode.Should().BeFalse();
        rule.Conditions.Select(c => c.FieldName).Should().Equal("Company Name", "Qty");
        rule.ConditionLogic.Should().Be("all");
    }

    [Fact]
    public void MixedAndOrTree_BecomesExpressionMode()
    {
        var rule = ConvertRule(Group("All", NameIsX, Group("Any", QtyOver3, ActiveIsTrue)), out _);

        rule.IsExpressionMode.Should().BeTrue();
        rule.Conditions.Should().BeEmpty();
        rule.ExpressionText.Should().Be("([Company Name] = \"x\") and (([Qty] > 3) or ([Active] = true))");
    }

    [Fact]
    public void MixedTreeWithARoleCondition_IsReportedNotGuessed()
    {
        const string role = """
            - Type: QB::FormV2::Rule::Condition::Role
              Properties:
                Role: !Ref
                  Role: $Role_X
                Comparison: IsInRole
            """;

        var rule = ConvertRule(Group("All", role, Group("Any", NameIsX, NameIsX)), out var issues);

        rule.Should().BeNull();
        issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_FORM_RULE_CONDITION_STRUCTURE");
    }

    private const string FormulaTrue = """
        - Type: QB::FormV2::Rule::Condition::FormulaIsTrue
          Properties:
            Formula: '[Qty] > 3 and Contains([Company Name], "x")'
        """;

    [Fact]
    public void ConditionListedDirectlyWithoutAGroup_FormulaCondition_IsKept()
    {
        var rule = ConvertRule(FormulaTrue, out var issues);

        rule.IsExpressionMode.Should().BeTrue();
        rule.ExpressionText.Should().Contain("Contains");
        issues.Should().NotContain(i => i.Code == "QBL_UNSUPPORTED_FORM_RULE_CONDITION_STRUCTURE");
    }

    [Fact]
    public void SeveralConditionsListedDirectly_MustAllHold()
    {
        var rule = ConvertRule(NameIsX + Environment.NewLine + QtyOver3, out _);

        rule.ConditionLogic.Should().Be("all");
        rule.Conditions.Select(c => c.FieldName).Should().Equal("Company Name", "Qty");
    }

    private const string DueBefore = """
        - Type: QB::FormV2::Rule::Condition::Field
          Properties:
            Field: !Ref
              Field: $Field_Due
            Comparison: Before
            Value: '2025-01-01'
        """;

    [Fact]
    public void ComparisonWithNoConditionOperator_BeforeADate_BecomesTheRuleFormula()
    {
        var rule = ConvertRule(Group("All", NameIsX, DueBefore), out var issues);

        rule.IsExpressionMode.Should().BeTrue();
        rule.ExpressionText.Should().Contain("[Due] < ToDate(\"2025-01-01\")").And.Contain("[Company Name]");
        issues.Should().NotContain(i => i.Code == "QBL_UNSUPPORTED_FORM_RULE_OPERATOR");
    }
}
