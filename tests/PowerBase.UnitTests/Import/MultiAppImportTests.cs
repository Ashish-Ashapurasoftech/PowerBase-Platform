using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Jobs;
using PowerBase.Application.Import.Mapping;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Queries.ImportPreview;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Import;

public class MultiAppImportTests
{
    private const string TwoApps = """
    Version: '0.12'
    Resources:
      $App_Sales:
        Type: QB::Application
        Properties:
          Name: Sales
        Tables:
          $Table_Contacts:
            Type: QB::Table
            Properties:
              Name: Contacts
            Fields:
              $Field_Name:
                Type: QB::Field::Text
                Properties:
                  Label: Name
      $App_Hr:
        Type: QB::Application
        Properties:
          Name: HR
        Tables:
          $Table_Contacts:
            Type: QB::Table
            Properties:
              Name: Staff
            Fields:
              $Field_Name:
                Type: QB::Field::Text
                Properties:
                  Label: Name
    """;

    private const string OneApp = """
    Version: '0.12'
    Resources:
      $App_Sales:
        Type: QB::Application
        Properties:
          Name: Sales
    """;

    [Fact]
    public void ConvertAll_TwoApplications_ReturnsEachWithOnlyItsOwnTables()
    {
        var results = QblToPblConverter.ConvertAll(QblSerializer.Deserialize(TwoApps));

        results.Select(r => r.Document.App.Name).Should().Equal("Sales", "HR");
        results.Select(r => r.AppRef).Should().Equal("$App_Sales", "$App_Hr");
        results[0].Document.Tables.Should().ContainSingle(t => t.Name == "Contacts");
        results[1].Document.Tables.Should().ContainSingle(t => t.Name == "Staff");
    }

    [Fact]
    public void Convert_ReferenceFieldNamedLikeSystemField_IsRenamedInsteadOfBlockingTheImport()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test
            Tables:
              $Table_Users:
                Type: QB::Table
                Properties:
                  Name: Users
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Name
              $Table_Sheets:
                Type: QB::Table
                Properties:
                  Name: Sheets
                Fields:
                  $Field_Last_Modified_By:
                    Type: QB::Field::Reference
                    Properties:
                      Label: Last Modified By
                      Reference:
                        Relationship: !Ref
                          Relationship: $Relationship_to_Users
                Relationships:
                  $Relationship_to_Users:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Users
                        Relationship: $Relationship_to_Users_1
        """;

        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

        var rel = result.Document.Relationships.Should().ContainSingle().Subject;
        rel.ReferenceFieldName.Should().Be("Last Modified By (2)");
        rel.ReferenceFieldLabel.Should().Be("Last Modified By (2)");
        result.Issues.Should().Contain(i => i.Code == "QBL_DUPLICATE_FIELD_NAME");
        new PblValidator().Validate(result.Document).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ConvertAll_NoApplication_ReturnsMissingApplicationError()
    {
        var results = QblToPblConverter.ConvertAll(QblSerializer.Deserialize("Version: '0.12'\nResources: {}\n"));

        results.Should().ContainSingle().Which.Issues.Should().Contain(i => i.Code == "QBL_MISSING_APPLICATION");
    }

    [Fact]
    public void Parse_TwoApplications_ThrowsMultipleAppsNotAllowedWithNames()
    {
        var act = () => ImportDocumentParser.Parse(TwoApps);

        var ex = act.Should().Throw<BadRequestException>().Which;
        ex.ErrorCode.Should().Be(ImportDocumentParser.MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE);
        ex.Message.Should().Contain("2 applications").And.Contain("Sales").And.Contain("HR");
    }

    [Fact]
    public void Parse_SingleApplication_ReturnsIt()
    {
        var (document, _) = ImportDocumentParser.Parse(OneApp);

        document.App.Name.Should().Be("Sales");
    }

    [Fact]
    public void Load_TwoApplications_IsRejectedBeforeValidation()
    {
        var act = () => PblDocumentTools.Load(TwoApps, new PblValidator());

        act.Should().Throw<BadRequestException>().Which.ErrorCode.Should().Be(ImportDocumentParser.MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE);
    }

    [Fact]
    public void MultipleAppsMessage_ManyApplications_ListsFirstFiveAndCountsTheRest()
    {
        var apps = Enumerable.Range(1, 7)
            .Select(i => new ParsedApp($"$App_{i}", new PblDocument { App = new PblApp { Name = $"App{i}" } }, []))
            .ToList();

        var message = ImportDocumentParser.MultipleAppsMessage(apps);

        message.Should().Contain("7 applications").And.Contain("App5").And.NotContain("App6").And.Contain("and 2 more");
    }

    [Fact]
    public async Task Preview_NewMode_ReturnsOneItemPerApplicationAndFlagsExistingName()
    {
        var appRepo = Substitute.For<IAppRepository>();
        appRepo.NameExistsAsync("HR", Arg.Any<CancellationToken>()).Returns(true);
        var handler = new ImportPreviewQueryHandler(new PblValidator(), appRepo);

        var result = await handler.HandleAsync(new ImportPreviewQuery(TwoApps));

        result.AppCount.Should().Be(2);
        result.IsValid.Should().BeTrue();
        result.Apps[0].NameExists.Should().BeFalse();
        result.Apps[1].NameExists.Should().BeTrue();
        result.Apps[1].Warnings.Should().Contain(w => w.Code == "APP_NAME_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Preview_UpdateMode_TwoApplications_IsInvalidWithNoApps()
    {
        var handler = new ImportPreviewQueryHandler(new PblValidator(), Substitute.For<IAppRepository>());

        var result = await handler.HandleAsync(new ImportPreviewQuery(TwoApps, ImportMode.UpdateExistingApp));

        result.IsValid.Should().BeFalse();
        result.Apps.Should().BeEmpty();
        result.Errors.Should().ContainSingle(e => e.Code == ImportDocumentParser.MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE);
    }

    [Fact]
    public async Task Preview_UpdateMode_SingleApplication_IsReviewable()
    {
        var handler = new ImportPreviewQueryHandler(new PblValidator(), Substitute.For<IAppRepository>());

        var result = await handler.HandleAsync(new ImportPreviewQuery(OneApp, ImportMode.UpdateExistingApp));

        result.Errors.Should().BeEmpty();
        result.Apps.Should().ContainSingle(a => a.AppName == "Sales");
    }

    [Fact]
    public void ImportMapping_Parse_ReadsPerApplicationChoices()
    {
        var mapping = ImportMapping.Parse("""{ "apps": [ { "appRef": "$App_Hr", "include": false }, { "appRef": "$App_Sales", "nameOverride": "Sales 2", "excludedTableRefs": ["$Table_X"] } ] }""");

        mapping.ForApp("$App_Hr")!.Include.Should().BeFalse();
        mapping.ForApp("$App_Sales")!.NameOverride.Should().Be("Sales 2");
        mapping.ForApp("$App_Sales")!.ExcludedTableRefs.Should().Equal("$Table_X");
        mapping.ForApp("$App_Missing").Should().BeNull();
    }

    [Theory]
    [InlineData(1024, 1, false)]
    [InlineData(1024, 2, true)]
    [InlineData(4L * 1024 * 1024, 1, true)]
    public void RunsInBackground_LargeOrMultiApp_IsTrue(long size, int apps, bool expected)
    {
        AppImportJobPolicy.RunsInBackground(size, apps).Should().Be(expected);
    }

    [Fact]
    public void CountApplications_CountsOnlyApplicationNodes()
    {
        AppImportJobPolicy.CountApplications(TwoApps).Should().Be(2);
        AppImportJobPolicy.CountApplications("  Type: QB::Application::Variable\n").Should().Be(1);
        AppImportJobPolicy.CountApplications("{ \"app\": {} }").Should().Be(1);
    }

    [Fact]
    public void ImportProgress_WithSeveralApps_MapsIntoEachAppsShareAndNamesTheApp()
    {
        var seen = new List<(string Stage, int Percent)>();
        ImportProgress.Current = (stage, percent) => seen.Add((stage, percent));
        try
        {
            ImportProgress.BeginApp(1, 4, "HR");
            ImportProgress.Report("Creating tables", 40);
            ImportProgress.EndApp();
            ImportProgress.Report("Done", 50);
        }
        finally
        {
            ImportProgress.Current = null;
        }

        seen[0].Percent.Should().Be((100 + 40) / 4);
        seen[0].Stage.Should().Contain("App 2 of 4").And.Contain("HR").And.Contain("Creating tables");
        seen[1].Should().Be(("Done", 50));
    }
}
