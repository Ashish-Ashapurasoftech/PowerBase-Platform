using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

public class QblVariablesAndPagesConverterTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private const string AppWithVariablesAndPages = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables: {}
            Variables:
              $Variable_1:
                Type: QB::Application::Variable
                Properties:
                  Name: Tax Rate
                  Value: '0.2'
              $Variable_2:
                Type: QB::Application::Variable
                Properties:
                  Name: 'Bad[Name]'
                  Value: x
              $Variable_3:
                Type: QB::Application::Variable
                Properties:
                  Name: tax rate
                  Value: dup
            Pages:
              $Page_Home:
                Type: QB::CodePage
                Properties:
                  Name: Landing
                  RawCode: '<h1>Hello</h1>'
              $Page_Rich:
                Type: QB::RichTextPage
                Properties:
                  Name: Notes
        """;

    [Fact]
    public void Convert_Variables_MapsValidOnesAndFlagsInvalidAndDuplicates()
    {
        var result = Convert(AppWithVariablesAndPages);

        result.Document.Variables.Should().ContainSingle();
        result.Document.Variables[0].Name.Should().Be("Tax Rate");
        result.Document.Variables[0].Value.Should().Be("0.2");
        result.Document.Variables[0].LogicalRef.Should().Be("$Variable_1");

        result.Issues.Select(i => i.Code).Should().Contain(["QBL_VARIABLE_INVALID_NAME", "QBL_DUPLICATE_VARIABLE_NAME"]);
    }

    [Fact]
    public void Convert_CodePage_MapsToPblPageAndFlagsOtherPageTypes()
    {
        var result = Convert(AppWithVariablesAndPages);

        result.Document.Pages.Should().ContainSingle();
        result.Document.Pages[0].Name.Should().Be("Landing");
        result.Document.Pages[0].Code.Should().Be("<h1>Hello</h1>");

        result.Issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_PAGE_TYPE" && i.ElementRef == "$Page_Rich");
    }

    [Fact]
    public void Convert_VariableValueOverColumnLimit_IsFlaggedNotTruncated()
    {
        var yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables: {}
            Variables:
              $Variable_1:
                Type: QB::Application::Variable
                Properties:
                  Name: Long
                  Value: 'LONG'
        """;

        var result = Convert(yaml.Replace("LONG", new string('a', 501)));

        result.Document.Variables.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == "QBL_VARIABLE_VALUE_TOO_LONG");
    }

    [Fact]
    public void Convert_TwoFieldsSharingALabel_KeepsBothWithNumberedLabelAndWarns()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_A:
                Type: QB::Table
                Properties:
                  Name: A
                Fields:
                  $Field_One:
                    Type: QB::Field::Text
                    Properties:
                      Label: Add SPC
                  $Field_Two:
                    Type: QB::Field::URL::Formula
                    Properties:
                      Label: 'Add SPC '
                      Formula: '"x"'
        """;

        var result = Convert(yaml);

        var fields = result.Document.Tables[0].Fields;
        fields.Should().HaveCount(2);
        fields.Select(f => f.Label).Should().Equal("Add SPC", "Add SPC (2)");
        result.Issues.Should().Contain(i => i.Code == "QBL_DUPLICATE_FIELD_LABEL");
    }
}
