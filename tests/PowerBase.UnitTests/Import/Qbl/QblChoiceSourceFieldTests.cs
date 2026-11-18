using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>A Single/MultiSelect field whose choices come live from another field's values ("From another field" in
/// Quickbase, <c>InputOptions.InputType: FromAnotherField</c>) now maps to PowerBase's own ChoiceSourceSettings instead of
/// importing as a plain, choice-less select field. The exact InputOptions property Quickbase uses to name the source field
/// is not yet confirmed against a real export, so <c>QblFieldMapper.MapChoiceSource</c> looks for a cross-reference anywhere
/// under InputOptions rather than a specific key — these tests deliberately use a key name ("SourceField") chosen only for
/// readability, to prove the lookup does not depend on guessing it correctly.</summary>
public class QblChoiceSourceFieldTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private static string TableWithFields(string fieldsYaml) => $"""
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
        {Reindent(fieldsYaml, 10)}
        """;

    /// <summary>Re-bases a YAML fragment to start at <paramref name="targetIndent"/> spaces, preserving the fragment's own
    /// relative nesting (a plain per-line TrimStart would flatten nested keys onto the same level and break the YAML
    /// entirely). Copied from QblToPblConverterTests, which this file deliberately does not depend on.</summary>
    private static string Reindent(string text, int targetIndent)
    {
        var lines = text.Replace("\r\n", "\n").Trim('\n').Split('\n');
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        var minIndent = nonEmpty.Count == 0 ? 0 : nonEmpty.Min(l => l.Length - l.TrimStart(' ').Length);
        var prefix = new string(' ', targetIndent);
        return string.Join('\n', lines.Select(l => l.Trim().Length == 0 ? "" : prefix + l[Math.Min(minIndent, l.Length)..]));
    }

    [Fact]
    public void Convert_ChoiceFieldSourcedFromAnotherFieldOnTheSameTable_SetsChoiceSourceFieldRef()
    {
        var result = Convert(TableWithFields("""
            $Field_Status:
              Type: QB::Field::TextMultipleChoice
              Id: 10
              Properties:
                Label: Status
            $Field_Category:
              Type: QB::Field::MultiselectText
              Id: 11
              Properties:
                Label: Category
                InputOptions:
                  InputType: FromAnotherField
                  SourceField: !Ref
                    Field: $Field_Status
            """));

        var table = result.Document.Tables[0];
        var category = table.Fields.Single(f => f.Name == "Category");
        var status = table.Fields.Single(f => f.Name == "Status");

        category.ChoiceSourceFieldRef.Should().Be(status.LogicalRef);
        result.Issues.Should().NotContain(i => i.Message.Contains("choices from another field") || i.Message.Contains("could not be determined"));
    }

    [Fact]
    public void Convert_ChoiceFieldSourcedFromAnotherField_NeverCarriesAStaticOptionsListToo()
    {
        var result = Convert(TableWithFields("""
            $Field_Status:
              Type: QB::Field::TextMultipleChoice
              Id: 10
              Properties:
                Label: Status
            $Field_Category:
              Type: QB::Field::MultiselectText
              Id: 11
              Properties:
                Label: Category
                InputOptions:
                  ListChoices: [A, B]
                  InputType: FromAnotherField
                  SourceField: !Ref
                    Field: $Field_Status
            """));

        var category = result.Document.Tables[0].Fields.Single(f => f.Name == "Category");
        category.ChoiceSourceFieldRef.Should().NotBeNull();
        category.Settings.Should().NotContain("\"options\"");
    }

    [Fact]
    public void Convert_ChoiceFieldSourcedFromAnotherField_WithNoResolvableReference_FallsBackToPlainSelectWithANote()
    {
        var result = Convert(TableWithFields("""
            $Field_Category:
              Type: QB::Field::MultiselectText
              Id: 11
              Properties:
                Label: Category
                InputOptions:
                  InputType: FromAnotherField
            """));

        var category = result.Document.Tables[0].Fields.Single(f => f.Name == "Category");
        category.ChoiceSourceFieldRef.Should().BeNull();
        result.Issues.Should().Contain(i => i.Code == "QBL_FIELD_SETTING_NOT_CARRIED" && i.Message.Contains("could not be determined"));
    }
}
