using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>No Quickbase field is lost: one PowerBase can create only as a stand-in keeps its place (same name, label and number), typed after
/// its result type where that is certain, with the complete original definition and everything the source said about it.</summary>
public class QblPlaceholderFieldTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    /// <summary>Projects (parent) and Tasks (child) with one field of every kind the importer cannot create as itself, next to ordinary ones.</summary>
    private const string MixedApp = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Projects:
                Type: QB::Table
                Properties:
                  Name: Projects
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                  $Field_Project_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Project Name
                  $Field_Task_Spread:
                    Type: QB::Field::Summary
                    Id: 20
                    Properties:
                      Label: Task Spread
                      Summary:
                        ReferenceField: !Ref
                          Table: $Table_Tasks
                          Field: $Field_Project
                        FieldToSummarize: !Ref
                          Table: $Table_Tasks
                          Field: $Field_Hours
                        Function: StdDeviation
                  $Field_Task_Count:
                    Type: QB::Field::Summary
                    Id: 21
                    Properties:
                      Label: Task Count
                      Summary:
                        ReferenceField: !Ref
                          Table: $Table_Tasks
                          Field: $Field_Project
                        Function: Count
                Relationships:
                  $Relationship_to_Projects_1:
                    Type: QB::Relationship::Parent
              $Table_Tasks:
                Type: QB::Table
                Properties:
                  Name: Tasks
                Fields:
                  $Field_Title:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Title
                      Comments: The task title
                  $Field_Hours:
                    Type: QB::Field::Number
                    Id: 7
                    Properties:
                      Label: Hours
                  $Field_Project:
                    Type: QB::Field::Reference
                    Id: 8
                    Properties:
                      Label: Project
                      Reference:
                        Relationship: !Ref
                          Relationship: $Relationship_to_Projects
                  $Field_Legacy:
                    Type: QB::Field::Unsupported
                    Id: 30
                    Properties:
                      Label: Legacy Link
                      Explanation: Refers to a deleted field.
                  $Field_Choice_Calc:
                    Type: QB::Field::TextMultipleChoice::Formula
                    Id: 31
                    Properties:
                      Label: Choice Calc
                      Formula: If([Hours] > 8, "Long", "Short")
                  $Field_Broken_Total:
                    Type: QB::Field::Currency::Formula
                    Id: 32
                    Properties:
                      Label: Broken Total
                      Formula: "[Hours] * [Rate That Was Deleted]"
                  $Field_Blank_Duration:
                    Type: QB::Field::Duration::Formula
                    Id: 33
                    Properties:
                      Label: Blank Duration
                      Formula:
                  $Field_Orphan_Reference:
                    Type: QB::Field::Reference
                    Id: 34
                    Properties:
                      Label: Orphan Reference
                  $Field_Orphan_Lookup:
                    Type: QB::Field::Lookup
                    Id: 35
                    Properties:
                      Label: Orphan Lookup
                  $Field_Dead_Link:
                    Type: QB::Field::ReportLink
                    Id: 36
                    Properties:
                      Label: Dead Link
                  $Field_Proxy:
                    Type: QB::Field::ReferenceProxy
                    Id: 37
                    Properties:
                      Label: Project Proxy
                Relationships:
                  $Relationship_to_Projects:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Projects
                        Relationship: $Relationship_to_Projects_1
        """;

    private static PblTable Table(QblConversionResult result, string name) => result.Document.Tables.Single(t => t.Name == name);

    [Fact]
    public void Convert_MixedDocument_KeepsEveryFieldAndIsValid()
    {
        var result = Convert(MixedApp);
        var tasks = Table(result, "Tasks");
        var projects = Table(result, "Projects");

        // Supported fields are untouched…
        tasks.Fields.Single(f => f.Name == "Title").Placeholder.Should().BeNull();
        tasks.Fields.Single(f => f.Name == "Hours").TypeCode.Should().Be("Number");
        // …and every field PowerBase cannot create as itself is still there, under its own name and number.
        tasks.Fields.Where(f => f.Placeholder is not null).Select(f => f.Name).Should().BeEquivalentTo(
            "Legacy Link", "Choice Calc", "Blank Duration", "Orphan Reference", "Orphan Lookup", "Dead Link");
        tasks.Fields.Single(f => f.Name == "Legacy Link").SourceId.Should().Be(30);

        // The stand-ins pass the same validation the real fields do — one of them can never block the import.
        var validation = new PblValidator().Validate(result.Document);
        validation.Errors.Should().BeEmpty();
        projects.Fields.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("Legacy Link", "Text")]            // Quickbase's own "unsupported": nothing certain about its type
    [InlineData("Choice Calc", "Text")]            // a multiple-choice formula result has no PowerBase result type
    [InlineData("Blank Duration", "Duration")]     // no blank constant exists, but the result type is certain
    [InlineData("Orphan Reference", "Number")]     // a reference holds a record number
    [InlineData("Orphan Lookup", "Text")]          // the looked-up field is unknown
    [InlineData("Dead Link", "Text")]
    public void Convert_Placeholders_AreTypedOnlyWhereTheSourceTypeIsCertain(string name, string expectedType)
    {
        var field = Table(Convert(MixedApp), "Tasks").Fields.Single(f => f.Name == name);

        field.TypeCode.Should().Be(expectedType);
        field.IsRequired.Should().BeFalse(); // there is no data to satisfy a required stand-in
    }

    [Fact]
    public void Convert_SummaryWithAFunctionPowerBaseLacks_KeepsItsPlaceAsANumber()
    {
        var result = Convert(MixedApp);

        var spread = Table(result, "Projects").Fields.Single(f => f.Name == "Task Spread");
        spread.TypeCode.Should().Be("Number");
        spread.SourceId.Should().Be(20);
        spread.Placeholder!.Code.Should().Be("QBL_UNSUPPORTED_SUMMARY_FUNCTION");
        spread.Placeholder.OriginalDefinition.Should().Contain("StdDeviation").And.Contain("$Field_Hours");

        // A summary that is fine still becomes part of the relationship, not a stand-in.
        result.Document.Relationships.Single().Summaries.Should().ContainSingle(s => s.Name == "Task Count");
        Table(result, "Projects").Fields.Should().NotContain(f => f.Name == "Task Count");
    }

    [Fact]
    public void Convert_UnsupportedField_KeepsDescriptionHelpAndTheCompleteSourceSnapshot()
    {
        var legacy = Table(Convert(MixedApp), "Tasks").Fields.Single(f => f.Name == "Legacy Link");

        legacy.Placeholder!.Reason.Should().Be("Refers to a deleted field");
        legacy.Placeholder.OriginalMetadataJson.Should().Contain("QB::Field::Unsupported").And.Contain("Refers to a deleted field.");
        // The warning is the note's business; the field's own description and help stay the end user's.
        legacy.Description.Should().BeNull();
        legacy.HelpText.Should().BeNull();
    }

    [Fact]
    public void Convert_Formula_KeepsTheCompleteOriginalAndTheSourceType()
    {
        var tasks = Table(Convert(MixedApp), "Tasks");

        var broken = tasks.Fields.Single(f => f.Name == "Broken Total");
        broken.TypeCode.Should().Be("Formula");
        broken.OriginalFormula.Should().Be("[Hours] * [Rate That Was Deleted]");
        broken.SourceType.Should().Be("QB::Field::Currency::Formula");

        // A multiple-choice formula cannot be a PowerBase formula: its complete formula travels with the stand-in.
        tasks.Fields.Single(f => f.Name == "Choice Calc").Placeholder!.OriginalDefinition.Should().Be("If([Hours] > 8, \"Long\", \"Short\")");
    }

    [Fact]
    public void Convert_LongSourceDescription_IsCutForTheFieldButKeptWhole()
    {
        var longText = new string('x', 800);
        var yaml = MixedApp.Replace("Explanation: Refers to a deleted field.", $"Explanation: Refers to a deleted field.\n              Comments: {longText}");

        var legacy = Table(Convert(yaml), "Tasks").Fields.Single(f => f.Name == "Legacy Link");

        legacy.Description!.Length.Should().BeLessThanOrEqualTo(500);
        legacy.Placeholder!.FullDescription.Should().Be(longText);
    }

    [Fact]
    public void Convert_CrossAppRelationshipReference_KeepsItsPlaceAndSaysWhy()
    {
        var yaml = MixedApp.Replace("Type: QB::Relationship::Child", "Type: QB::Relationship::CrossAppChild");

        var result = Convert(yaml);

        var project = Table(result, "Tasks").Fields.Single(f => f.Name == "Project");
        project.Placeholder.Should().NotBeNull();
        project.Placeholder!.Code.Should().Be("QBL_UNSUPPORTED_CROSS_APP_RELATIONSHIP");
        project.TypeCode.Should().Be("Number");
        new PblValidator().Validate(result.Document).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Convert_ReferenceProxy_IsReportedNotSilentlyDropped()
    {
        var result = Convert(MixedApp);

        result.Issues.Should().Contain(i => i.Code == "QBL_REFERENCE_PROXY_NOT_SEPARATE" && i.Message.Contains("Project Proxy"));
    }

    [Fact]
    public void Convert_EveryStandInHasAWarningIssue()
    {
        var result = Convert(MixedApp);

        foreach (var fieldRef in new[] { "$Field_Legacy", "$Field_Choice_Calc", "$Field_Blank_Duration", "$Field_Orphan_Reference", "$Field_Orphan_Lookup", "$Field_Dead_Link" })
            result.Issues.Should().Contain(i => i.ElementRef != null && i.ElementRef.EndsWith(fieldRef) && i.Message.Contains("placeholder"), $"'{fieldRef}' must not be placed silently");
    }

    [Fact]
    public void Convert_TwoUnsupportedFieldsWithTheSameLabel_BothSurviveUnderNumberedNames()
    {
        var yaml = MixedApp.Replace("Label: Orphan Lookup", "Label: Orphan Reference");

        var tasks = Table(Convert(yaml), "Tasks");

        tasks.Fields.Count(f => f.Label!.StartsWith("Orphan Reference")).Should().Be(2);
        tasks.Fields.Select(f => f.Name).Should().OnlyHaveUniqueItems();
        new PblValidator().Validate(Convert(yaml).Document).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_IsLosslessForNestedReferences()
    {
        var node = QblSerializer.Deserialize(MixedApp).Resources["$App_Test"].ChildMap("Tables")["$Table_Projects"].ChildMap("Fields")["$Field_Task_Spread"];

        var json = QblPlaceholders.SnapshotJson(node);

        json.Should().Contain("\"StdDeviation\"").And.Contain("$Table_Tasks").And.Contain("$Field_Hours").And.Contain("\"id\":20");
    }
}
