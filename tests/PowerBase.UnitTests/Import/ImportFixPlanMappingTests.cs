using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>B1 / B4 of docs/QBL_REIMPORT_FIX_PLAN.md: settings PowerBase already has, now produced by the converter.</summary>
public class ImportFixPlanMappingTests
{
    private const string App = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Roles:
                Type: QB::Table
                Properties:
                  Name: File Type Roles
                Fields:
                  $Field_Related_File_Type:
                    Type: QB::Field::Text
                    Properties:
                      Label: Related File Type
                Relationships:
                  $Relationship_Parent_Anchor:
                    Type: QB::Relationship::Parent
                    Properties: {}
              $Table_Files:
                Type: QB::Table
                Properties:
                  Name: Files
                Fields:
                  $Field_Related_File_Type:
                    Type: QB::Field::Text
                    Properties:
                      Label: File Type
                  $Field_Related_Role:
                    Type: QB::Field::Reference
                    Properties:
                      Label: Related Role
                      Comments: Pick the role
                      Reference:
                        Relationship: !Ref
                          Relationship: $Relationship_To_Roles
                        ConditionalValues:
                          FilterField: !Ref
                            Field: $Field_Related_File_Type
                          MatchField: !Ref
                            Table: $Table_Roles
                            Field: $Field_Related_File_Type
                  $Field_Blank_Text:
                    Type: QB::Field::Text::Formula
                    Properties:
                      Label: Blank Text
                      Formula:
                  $Field_Blank_Number:
                    Type: QB::Field::Numeric::Formula
                    Properties:
                      Label: Blank Number
                      Formula:
                  $Field_Blank_Duration:
                    Type: QB::Field::Duration::Formula
                    Properties:
                      Label: Blank Duration
                      Formula:
                Relationships:
                  $Relationship_To_Roles:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Roles
                        Relationship: $Relationship_Parent_Anchor
        """;

    [Fact]
    public void ReferenceConditionalValues_BecomeADependentDropdown()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(App));

        var rel = result.Document.Relationships.Should().ContainSingle().Subject;
        rel.ReferenceFilterFormFieldName.Should().Be("File Type");
        rel.ReferenceFilterParentFieldName.Should().Be("Related File Type");
        rel.ReferenceFieldDescription.Should().Be("Pick the role");
    }

    [Fact]
    public void EmptyFormulas_AreKeptWithATypedBlank_WhenOneExists()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(App));
        var files = result.Document.Tables.Single(t => t.Name == "Files");

        files.Fields.Single(f => f.Label == "Blank Text").FormulaExpression.Should().Be("\"\"");
        files.Fields.Single(f => f.Label == "Blank Number").FormulaExpression.Should().Be("ToNumber(\"\")");
        files.Fields.Should().NotContain(f => f.Label == "Blank Duration"); // no blank constant for a duration: reported, as before
        result.Issues.Should().Contain(i => i.Code == "QBL_EMPTY_FORMULA" && i.Message.Contains("blank result"));
        result.Issues.Should().Contain(i => i.Code == "QBL_EMPTY_FORMULA" && i.Message.Contains("skipped"));
    }

    [Theory]
    [InlineData("//only a comment\n//and another")]
    [InlineData("//[16] old\n\n")]
    [InlineData("var text a = \"x\";\nvar text b = $a & \"y\";")]
    public void FormulaWithoutAResultLine_IsAFormulaWithoutAnExpression(string formula) =>
        QblToPblConverter.HasNoResultLine(formula).Should().BeTrue();

    [Theory]
    [InlineData("[Qty] * 2")]
    [InlineData("//old\n[Qty]")]
    [InlineData("var number a = 1;\n$a + 1")]
    public void FormulaWithAResultLine_IsKept(string formula) =>
        QblToPblConverter.HasNoResultLine(formula).Should().BeFalse();

    private const string TwoFormsApp = """
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
              $Table_Files:
                Type: QB::Table
                Properties:
                  Name: Files
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Name
                Forms:
                  Properties:
                    RoleDefaults:
                      Everyone:
                        ViewForm: Default
                        EditForm: !Ref
                          FormV2: $FormV2_Main
                        AddForm: !Ref
                          FormV2: $FormV2_Main
                      $Role_Clerk:
                        ViewForm: UseRoleSettings
                        EditForm: !Ref
                          FormV2: $FormV2_Short
                        AddForm: Default
                  Resources:
                    $FormV2_Main:
                      Type: QB::FormV2
                      Properties:
                        Name: Main
                    $FormV2_Short:
                      Type: QB::FormV2
                      Properties:
                        Name: Short
        """;

    [Fact]
    public void FormRoleDefaults_ExplicitChoices_AreCarriedAndPlaceholdersAreNot()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(TwoFormsApp));

        var defaults = result.Document.FormRoleDefaults;
        defaults.Should().HaveCount(2);
        defaults.Single(d => d.RoleRef is null).EditFormRef.Should().Be("$Table_Files::$FormV2_Main");
        var clerk = defaults.Single(d => d.RoleRef == "$Role_Clerk");
        clerk.EditFormRef.Should().Be("$Table_Files::$FormV2_Short");
        clerk.AddFormRef.Should().BeNull();
    }

    private const string CommentedIdApp = """
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
                  $Field_Qty:
                    Type: QB::Field::Number
                    Id: 7
                    Properties:
                      Label: Qty
                  $Field_Total:
                    Type: QB::Field::Numeric::Formula
                    Id: 8
                    Properties:
                      Label: Total
                      Formula: |-
                        //[16] old test, now unused
                        [7] * 2
        """;

    [Fact]
    public void FieldIdInsideAComment_IsNotTreatedAsAReference()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(CommentedIdApp));

        result.Issues.Should().NotContain(i => i.Code == "QBL_FORMULA_UNKNOWN_FIELD_ID");
        var total = result.Document.Tables.Single().Fields.Single(f => f.Label == "Total");
        total.FormulaExpression.Should().Contain("//[16] old test").And.Contain("[Qty] * 2");
    }
}
