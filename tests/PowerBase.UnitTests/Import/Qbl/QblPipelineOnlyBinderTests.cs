using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Pipeline-only QBL files (no QB::Application): Quickbase app / table ids are matched to the ids stored on imported
/// apps and tables (QBAppID / QBTableID) and the file is rewritten so the normal converter reads it.</summary>
public class QblPipelineOnlyBinderTests
{
    private const string File = """
Version: 0.12
ParameterDefinitions:
  Pipeline_Name_1:
    Type: String
    Value: Copy of All Step
Resources:
  $Pipeline_A:
    Type: QB::Pipeline
    Properties:
      Name: !Var
        Name: Pipeline_Name_1
      Tags: []
    Steps:
      - Type: QB::PipelineStep::Quickbase
        Properties:
          Id: aa
          Type: pipe
          Name: On New Event
          SubType: trigger
          PipelineChannelAccount: !Ref
            PipelineChannelAccount: $PipelineChannelAccount_Quickbase
          Channel:
            Resource: record
            Id: quickbase
            Selector: on_new_event
        InputsMeta:
          $PipelineStep_InputsMeta_aa:
            Type: QB::PipelineStep::InputsMeta
            Properties:
              On_Add_Record: true
            PipelineStep_DataSources:
              $PipelineStep_DataSource_1:
                Type: QB::PipelineStep::DataSource::App
                Properties:
                  App: bt2kqxkqk
                  Name: app
              $PipelineStep_DataSource_2:
                Type: QB::PipelineStep::DataSource::Fields
                Properties:
                  Fields:
                    - 7
                    - 20
                    - 27
                    - 40
                  Name: export_fields
                  Source: !Ref
                    PipelineStep_DataSource: $PipelineStep_DataSource_3
              $PipelineStep_DataSource_3:
                Type: QB::PipelineStep::DataSource::Table
                Properties:
                  Table: bwcrtu9k5
                  Name: table
                  Source: !Ref
                    PipelineStep_DataSource: $PipelineStep_DataSource_1
      - Type: QB::PipelineStep::Quickbase
        Properties:
          Id: ab
          Type: pipe
          Name: Create Record
          SubType: action
          PipelineChannelAccount: !Ref
            PipelineChannelAccount: $PipelineChannelAccount_Quickbase
          Channel:
            Resource: record
            Id: quickbase
            Selector: create
          Inputs:
            Employee_Name: |-
              {{aa.name}} {{aa.t_rating}}
        InputsMeta:
          $PipelineStep_InputsMeta_ab:
            Type: QB::PipelineStep::InputsMeta
            PipelineStep_DataSources:
              $PipelineStep_DataSource_1:
                Type: QB::PipelineStep::DataSource::App
                Properties:
                  App: bxxxxxxx2
                  Name: app
              $PipelineStep_DataSource_2:
                Type: QB::PipelineStep::DataSource::Fields
                Properties:
                  Fields:
                    - 6
                  Name: export_fields
                  Source: !Ref
                    PipelineStep_DataSource: $PipelineStep_DataSource_3
              $PipelineStep_DataSource_3:
                Type: QB::PipelineStep::DataSource::Table
                Properties:
                  Table: bwepvdwy6
                  Name: table
                  Source: !Ref
                    PipelineStep_DataSource: $PipelineStep_DataSource_1
      - Type: QB::PipelineStep::Quickbase
        Properties:
          Id: ac
          Type: pipe
          Name: Make Request
          SubType: action
          PipelineChannelAccount: !Ref
            PipelineChannelAccount: $PipelineChannelAccount_Quickbase
          Channel:
            Resource: quickbase_api
            Id: quickbase
            Selector: make
          Inputs:
            Content_Type: application/json
            Method: POST
            Url: https://api.quickbase.com/v1/records/query
            Body: '{"from":"bwcrtu9k5"}'
  $PipelineChannelAccount_Quickbase:
    Type: QB::PipelineChannelAccount
    Properties:
      Channel: quickbase
      AccountType: tokenless
      Alias: realm-default-account
""";

    private static PipelineOnlyField F(int fid, string label, string type = "Text", bool system = false) => new(fid, label.Replace(" ", "_"), label, type, system);

    private static IReadOnlyList<PipelineOnlyField> Sys() =>
    [
        F(1, "Date Created", "DateTime", true), F(2, "Date Modified", "DateTime", true), F(3, "Record ID#", "Number", true),
        F(4, "Record Owner", "User", true), F(5, "Last Modified By", "User", true),
    ];

    private static PipelineOnlyTable Employee(long id, string? qbId = "bwcrtu9k5") => new(id, "Employee", null,
        [.. Sys(), F(7, "Email", "Email"), F(20, "Name"), F(22, "Status"), F(27, "TRating", "Rating"), F(37, "TAddress", "Address")], qbId);

    private static PipelineOnlyTable Employee1(long id, string? qbId = "bwepvdwy6") => new(id, "Employee1", null,
        [.. Sys(), F(6, "Employee Name"), F(7, "Employee Code")], qbId);

    /// <summary>Quickbase app bt2kqxkqk (Employee) and bxxxxxxx2 (Employee1) as the app import stored them, plus an unrelated app.</summary>
    private static IReadOnlyList<PipelineOnlyApp> Apps() =>
    [
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, "YSL Dev Docs", [Employee(10), new(12, "Orders", null, [.. Sys(), F(7, "Total", "Number")], "bwordersx")], "bt2kqxkqk"),
        new(Guid.Parse("00000000-0000-0000-0000-000000000003"), 3, "YSL Staff", [Employee1(11)], "bxxxxxxx2"),
        new(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, "Other", [new(20, "Customers", null, [.. Sys(), F(6, "Company"), F(7, "Email", "Email")], "bcustomer")], "bother001"),
    ];

    [Fact]
    public void IsPipelinesOnly_DetectsFileWithoutApplication()
    {
        QblPipelineOnlyBinder.IsPipelinesOnly(File).Should().BeTrue();
        QblPipelineOnlyBinder.IsPipelinesOnly(File + "\n  $App_X:\n    Type: QB::Application\n").Should().BeFalse();
    }

    [Fact]
    public void Bind_MatchesEachQuickbaseTableByItsStoredId()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, Apps());

        binding.SkippedPipelines.Should().BeEmpty();
        binding.Matches.Select(m => $"{m.QbTableId}={m.AppName}/{m.TableName}").Should()
            .BeEquivalentTo(["bwcrtu9k5=YSL Dev Docs/Employee", "bwepvdwy6=YSL Staff/Employee1"]);
        binding.Apps.Select(a => a.App.Name).Should().BeEquivalentTo(["YSL Dev Docs", "YSL Staff"]);
        binding.UnmatchedTables.Should().BeEmpty();
        binding.UnmatchedApps.Should().BeEmpty();
    }

    [Fact]
    public void Bind_RewrittenFileConvertsLikeAFullExport()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, Apps());
        var qbl = QblSerializer.Deserialize(binding.Content);
        var pipelines = QblPipelineConverter.ConvertAll(qbl, binding.Content);

        var pipeline = pipelines.Should().ContainSingle().Subject;
        pipeline.Name.Should().Be("Copy of All Step");
        pipeline.AppRef.Should().Be(binding.Apps.Single(a => a.App.Name == "YSL Dev Docs").AppRef);
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_TABLE_MISSING");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FIELD_NOT_FOUND");

        var trigger = pipeline.Steps[0];
        trigger.Config["tableId"].Should().BeOfType<PblPipelineTableValue>().Which.TableRef.Should().Be("$Table_PB_10");
        // Field id 40 is City of the TAddress field (37): Quickbase numbers address parts right after their address.
        ((IEnumerable<object?>)trigger.Config["subsequentFields"]).OfType<PblPipelineFieldValue>()
            .Should().Contain(f => f.SourceId == 37 && f.Part == "city");
        var create = pipeline.Steps[1];
        create.Config["tablePublicId"].Should().BeOfType<PblPipelineTableValue>().Which.TableRef.Should().Be("$Table_PB_11");
        // Each step keeps its own app.
        create.Config["tablePublicId"].Should().BeOfType<PblPipelineTableValue>().Which.AppRef
            .Should().Be(binding.Apps.Single(a => a.App.Name == "YSL Staff").AppRef);
    }

    [Fact]
    public void Bind_QuickbaseApiCallBecomesPowerBaseEndpointOfTheMatchedTable()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, Apps());
        var pipeline = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Single();

        var request = pipeline.Steps[2];
        request.Config["requestMode"].Should().Be("quickbase");
        request.Config["method"].Should().Be("POST");
        request.Config["url"].Should().Be("/v1/records/query");
        var body = request.Config["body"].Should().BeOfType<PblPipelineTextValue>().Subject;
        body.Parts.Should().HaveCount(3);
        body.Parts[0].Should().Be("{\"from\":\"");
        body.Parts[1].Should().BeOfType<PblPipelineTableValue>().Which.TableRef.Should().Be("$Table_PB_10");
        body.Parts[2].Should().Be("\"}");
        request.IsComplete.Should().BeTrue();
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REQUEST_URL_QUICKBASE");
    }

    [Fact]
    public void Convert_NumberedQuickbaseKey_IdZeroIsTheUserFieldLabelledId()
    {
        // Quickbase keys a field labelled "ID" as id_0 (id is the built-in Record ID#).
        var file = File.Replace("{{aa.name}} {{aa.t_rating}}", "{{aa.id_0}}");
        var apps = Apps().Select(a => a.Name == "YSL Dev Docs"
            ? a with { Tables = [Employee(10) with { Fields = [.. Employee(10).Fields, F(50, "ID")] }] } : a).ToList();
        var binding = QblPipelineOnlyBinder.Bind(file, apps);
        var pipeline = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Single();

        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
        System.Text.Json.JsonSerializer.Serialize(pipeline.Steps[1].Config).Should().Contain("50");
    }

    [Fact]
    public void Convert_NumberedAddressKey_IsThePartOfTheOnlyAddressWithThatName()
    {
        var file = File.Replace("{{aa.name}} {{aa.t_rating}}", "{{aa.t_address_0_city}}");
        var binding = QblPipelineOnlyBinder.Bind(file, Apps());
        var pipeline = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Single();

        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
    }

    [Fact]
    public void Bind_FieldsAndNamesNeverMatter_OnlyTheStoredIds()
    {
        // A table with exactly the right fields but WITHOUT the Quickbase id is not used; one with the id but other fields is.
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "Look-alike", [Employee(10, qbId: null), Employee1(11, qbId: null)], QbAppId: null),
            new(Guid.NewGuid(), 2, "Real", [new(20, "Staff", null, [.. Sys(), F(7, "Email", "Email")], "bwcrtu9k5")], "bt2kqxkqk"),
            new(Guid.NewGuid(), 3, "Real 2", [new(21, "People", null, [.. Sys(), F(6, "N")], "bwepvdwy6")], "bxxxxxxx2"),
        };
        var binding = QblPipelineOnlyBinder.Bind(File, apps);

        binding.Matches.Should().OnlyContain(m => m.AppName == "Real" || m.AppName == "Real 2");
        binding.Matches.Select(m => m.TableName).Should().BeEquivalentTo(["Staff", "People"]);
    }

    [Fact]
    public void Bind_QuickbaseIdsAreCaseInsensitiveAndTrimmed()
    {
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "A", [Employee(10, " BWCRTU9K5 ")], "BT2KQXKQK "),
            new(Guid.NewGuid(), 3, "B", [Employee1(11, "BWEPVDWY6")], " bxxxxxxx2"),
        };
        QblPipelineOnlyBinder.Bind(File, apps).SkippedPipelines.Should().BeEmpty();
    }

    [Fact]
    public void Bind_OneTableIdMissing_SkipsTheWholePipeline()
    {
        // Employee1 (bwepvdwy6) was not imported: the pipeline is not imported at all, not half of it.
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "YSL Dev Docs", [Employee(10)], "bt2kqxkqk"),
            new(Guid.NewGuid(), 3, "YSL Staff", [Employee1(11, qbId: null)], "bxxxxxxx2"),
        };
        var binding = QblPipelineOnlyBinder.Bind(File, apps);

        binding.Apps.Should().BeEmpty();
        binding.Matches.Should().BeEmpty();
        var skipped = binding.SkippedPipelines.Should().ContainSingle().Subject;
        skipped.Name.Should().Be("Copy of All Step");
        skipped.Reason.Should().Contain("bwepvdwy6");
        binding.UnmatchedTables.Should().Equal("bwepvdwy6");
        QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Should().BeEmpty();
    }

    [Fact]
    public void Bind_AppIdMissing_SkipsThePipeline()
    {
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "YSL Dev Docs", [Employee(10)], "bt2kqxkqk"),
            // bwepvdwy6 is stored, but app bxxxxxxx2 is not.
            new(Guid.NewGuid(), 3, "YSL Staff", [Employee1(11)], null),
        };
        var binding = QblPipelineOnlyBinder.Bind(File, apps);

        binding.SkippedPipelines.Should().ContainSingle().Which.Reason.Should().Contain("app id bxxxxxxx2");
        binding.UnmatchedApps.Should().Equal("bxxxxxxx2");
    }

    [Fact]
    public void Bind_TableImportedUnderAnotherApp_SkipsThePipeline()
    {
        // The step says table bwepvdwy6 is in app bxxxxxxx2, but it is stored in the app of bt2kqxkqk.
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "YSL Dev Docs", [Employee(10), Employee1(11)], "bt2kqxkqk"),
            new(Guid.NewGuid(), 3, "YSL Staff", [], "bxxxxxxx2"),
        };
        var binding = QblPipelineOnlyBinder.Bind(File, apps);

        binding.SkippedPipelines.Should().ContainSingle().Which.Reason.Should().Contain("different app");
    }

    [Fact]
    public void Bind_NoStoredIds_NothingIsImported()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, [new(Guid.NewGuid(), 9, "Empty", [new(90, "Notes", null, [.. Sys(), F(50, "Note")])])]);

        binding.Apps.Should().BeEmpty();
        binding.UnmatchedTables.Should().BeEquivalentTo(["bwcrtu9k5", "bwepvdwy6"]);
        binding.UnmatchedApps.Should().BeEquivalentTo(["bt2kqxkqk", "bxxxxxxx2"]);
    }

    [Fact]
    public void Bind_SameQuickbaseAppImportedTwice_UsesTheNewestAndSaysSo()
    {
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "YSL Dev Docs", [Employee(10)], "bt2kqxkqk"),
            new(Guid.NewGuid(), 5, "YSL Dev Docs (2)", [Employee(50)], "bt2kqxkqk"),
            new(Guid.NewGuid(), 3, "YSL Staff", [Employee1(11)], "bxxxxxxx2"),
        };
        var binding = QblPipelineOnlyBinder.Bind(File, apps);

        binding.Matches.Single(m => m.QbTableId == "bwcrtu9k5").AppName.Should().Be("YSL Dev Docs (2)");
        binding.Notes.Should().Contain(n => n.Contains("imported more than once"));
    }

    /// <summary>A pipeline of another Quickbase app with a table only.</summary>
    private const string ForeignFile = """
Version: 0.12
ParameterDefinitions:
  Pipeline_Name_1:
    Type: String
    Value: Foreign Flow
Resources:
  $Pipeline_F:
    Type: QB::Pipeline
    Properties:
      Name: !Var
        Name: Pipeline_Name_1
    Steps:
      - Type: QB::PipelineStep::Quickbase
        Properties:
          Id: a
          Type: pipe
          Name: Create Record
          SubType: action
          Channel:
            Resource: record
            Id: quickbase
            Selector: create
          Inputs:
            Ticket_Number: '42'
        InputsMeta:
          $PipelineStep_InputsMeta_a:
            Type: QB::PipelineStep::InputsMeta
            PipelineStep_DataSources:
              $PipelineStep_DataSource_1:
                Type: QB::PipelineStep::DataSource::Fields
                Properties:
                  Fields:
                    - 6
                    - 7
                    - 8
                  Name: export_fields
                  Source: !Ref
                    PipelineStep_DataSource: $PipelineStep_DataSource_2
              $PipelineStep_DataSource_2:
                Type: QB::PipelineStep::DataSource::Table
                Properties:
                  Table: bzzzzzzz1
                  Name: table
""";

    [Fact]
    public void Bind_TableWithoutAppDataSource_IsMatchedByTableIdAlone()
    {
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "Helpdesk", [new(10, "Tickets", null, [.. Sys(), F(6, "Ticket Number"), F(7, "Subject"), F(8, "Body")], "bzzzzzzz1")], "bhelpdesk"),
        };
        var binding = QblPipelineOnlyBinder.Bind(ForeignFile, apps);

        binding.Matches.Should().ContainSingle(m => m.QbTableId == "bzzzzzzz1" && m.AppName == "Helpdesk" && m.TableName == "Tickets");
        binding.SkippedPipelines.Should().BeEmpty();
    }

    [Fact]
    public void Bind_PerfectFieldMatchWithoutId_IsNotUsed()
    {
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "Helpdesk", [new(10, "Tickets", null, [.. Sys(), F(6, "Ticket Number"), F(7, "Subject"), F(8, "Body")])], "bhelpdesk"),
        };
        var binding = QblPipelineOnlyBinder.Bind(ForeignFile, apps);

        binding.Matches.Should().BeEmpty();
        binding.SkippedPipelines.Should().ContainSingle().Which.Name.Should().Be("Foreign Flow");
    }

    [Fact]
    public void Bind_MixedFile_KeepsMatchedPipelines_SkipsTheOthers()
    {
        var foreign = ForeignFile.Replace("\r\n", "\n").Split("Resources:\n")[1];
        var file = File.Replace("\r\n", "\n").Replace("  $PipelineChannelAccount_Quickbase:", foreign.TrimEnd('\n') + "\n  $PipelineChannelAccount_Quickbase:");
        var binding = QblPipelineOnlyBinder.Bind(file, Apps());

        binding.SkippedPipelines.Select(p => p.Reason).Should().ContainSingle().Which.Should().Contain("bzzzzzzz1");
        var pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content);
        pipelines.Select(p => p.Name).Should().Equal("Copy of All Step");
    }

    [Fact]
    public void SplitByApp_PipelineOfNoMatchedApp_IsNotImportedIntoTheFirstApp()
    {
        var pipelines = new List<PblPipeline>
        {
            new() { LogicalRef = "$P1", Name = "Ours", AppRef = "$App_PowerBase_1" },
            new() { LogicalRef = "$P2", Name = "Foreign", AppRef = null },
            new() { LogicalRef = "$P3", Name = "Raw", AppRef = "bu3wxsy83" },
        };

        var (kept, notImported) = PowerBase.Application.Import.Services.PipelineOnlyImportService.SplitByApp(pipelines, ["$App_PowerBase_1"]);

        kept.Select(p => p.Name).Should().Equal("Ours");
        notImported.Select(p => p.Name).Should().Equal("Foreign", "Raw");
    }
}
