using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Pipeline-only QBL files (no QB::Application): Quickbase table ids are matched to existing tables and the file is
/// rewritten so the normal converter reads it.</summary>
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

    private static PipelineOnlyTable Employee(long id) => new(id, "Employee", null,
        [.. Sys(), F(7, "Email", "Email"), F(20, "Name"), F(22, "Status"), F(27, "TRating", "Rating"), F(37, "TAddress", "Address")]);

    private static PipelineOnlyTable Employee1(long id) => new(id, "Employee1", null,
        [.. Sys(), F(6, "Employee Name"), F(7, "Employee Code")]);

    /// <summary>App 1 has both tables; app 2 has an unrelated table that shares some field ids.</summary>
    private static IReadOnlyList<PipelineOnlyApp> Apps() =>
    [
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, "YSL Dev Docs", [Employee(10), Employee1(11), new(12, "Orders", null, [.. Sys(), F(7, "Total", "Number")])]),
        new(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, "Other", [new(20, "Customers", null, [.. Sys(), F(6, "Company"), F(7, "Email", "Email")])]),
    ];

    [Fact]
    public void IsPipelinesOnly_DetectsFileWithoutApplication()
    {
        QblPipelineOnlyBinder.IsPipelinesOnly(File).Should().BeTrue();
        QblPipelineOnlyBinder.IsPipelinesOnly(File + "\n  $App_X:\n    Type: QB::Application\n").Should().BeFalse();
    }

    [Fact]
    public void Bind_MatchesEachQuickbaseTableByFieldIdsAndKeys()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, Apps());

        binding.UnmatchedTables.Should().BeEmpty();
        binding.Matches.Should().Contain(m => m.QbTableId == "bwcrtu9k5" && m.TableName == "Employee" && m.AppName == "YSL Dev Docs");
        binding.Matches.Should().Contain(m => m.QbTableId == "bwepvdwy6" && m.TableName == "Employee1" && m.AppName == "YSL Dev Docs");
        binding.Apps.Should().ContainSingle(a => a.App.Name == "YSL Dev Docs");
    }

    [Fact]
    public void Bind_RewrittenFileConvertsLikeAFullExport()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, Apps());
        var qbl = QblSerializer.Deserialize(binding.Content);
        var pipelines = QblPipelineConverter.ConvertAll(qbl, binding.Content);

        var pipeline = pipelines.Should().ContainSingle().Subject;
        pipeline.Name.Should().Be("Copy of All Step");
        pipeline.AppRef.Should().Be(binding.Apps[0].AppRef);
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_TABLE_MISSING");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FIELD_NOT_FOUND");

        var trigger = pipeline.Steps[0];
        trigger.Config["tableId"].Should().BeOfType<PblPipelineTableValue>().Which.TableRef.Should().Be("$Table_PB_10");
        // Field id 40 is City of the TAddress field (37): Quickbase numbers address parts right after their address.
        ((IEnumerable<object?>)trigger.Config["subsequentFields"]).OfType<PblPipelineFieldValue>()
            .Should().Contain(f => f.SourceId == 37 && f.Part == "city");
        var create = pipeline.Steps[1];
        create.Config["tablePublicId"].Should().BeOfType<PblPipelineTableValue>().Which.TableRef.Should().Be("$Table_PB_11");
    }

    [Fact]
    public void Bind_QuickbaseApiCallBecomesPowerBaseEndpointOfTheMatchedTable()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, Apps());
        var pipeline = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Single();

        var request = pipeline.Steps[2];
        request.Config["requestMode"].Should().Be("quickbase");
        request.Config["method"].Should().Be("GET");
        var url = request.Config["url"].Should().BeOfType<PblPipelineTextValue>().Subject;
        url.Parts.Should().HaveCount(3);
        url.Parts[0].Should().Be("/tables/");
        url.Parts[1].Should().BeOfType<PblPipelineTableValue>().Which.TableRef.Should().Be("$Table_PB_10");
        url.Parts[2].Should().Be("/records");
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_REQUEST_URL_MAPPED");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REQUEST_URL_QUICKBASE");
    }

    [Fact]
    public void Bind_NoMatchingTable_LeavesTablesUnmatched()
    {
        var binding = QblPipelineOnlyBinder.Bind(File, [new(Guid.NewGuid(), 9, "Empty", [new(90, "Notes", null, [.. Sys(), F(50, "Note")])])]);

        binding.Apps.Should().BeEmpty();
        binding.UnmatchedTables.Should().Contain(["bwcrtu9k5", "bwepvdwy6"]);
    }

    [Fact]
    public void Bind_TwoIdenticalAppCopies_UsesTheNewestAndSaysSo()
    {
        var older = new PipelineOnlyApp(Guid.NewGuid(), 1, "YSL Dev Docs", [Employee(10), Employee1(11)]);
        var newer = new PipelineOnlyApp(Guid.NewGuid(), 5, "YSL Dev Docs (2)", [Employee(50), Employee1(51)]);

        var binding = QblPipelineOnlyBinder.Bind(File, [older, newer]);

        binding.Matches.Should().OnlyContain(m => m.AppName == "YSL Dev Docs (2)");
    }

    [Fact]
    public void Bind_TablesOfDifferentAppsInOnePipeline_EachStepKeepsItsApp()
    {
        // Employee in app 1, Employee1 in app 2 (and its Quickbase app id differs in the file).
        var apps = new List<PipelineOnlyApp>
        {
            new(Guid.NewGuid(), 1, "HR", [Employee(10)]),
            new(Guid.NewGuid(), 2, "Payroll", [Employee1(20)]),
        };
        var binding = QblPipelineOnlyBinder.Bind(File, apps);
        binding.Apps.Should().HaveCount(2);

        var pipeline = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content).Single();
        var hr = binding.Apps.Single(a => a.App.Name == "HR").AppRef;
        var payroll = binding.Apps.Single(a => a.App.Name == "Payroll").AppRef;
        pipeline.Steps[0].Config["tableId"].Should().BeOfType<PblPipelineTableValue>().Which.AppRef.Should().Be(hr);
        pipeline.Steps[1].Config["tablePublicId"].Should().BeOfType<PblPipelineTableValue>().Which.AppRef.Should().Be(payroll);
    }

    [Fact]
    public void RealFile_PipelineOnly_FromDesktop()
    {
        var path = @"C:\Users\Administrator\Desktop\PipelineOnly.qbl";
        if (!System.IO.File.Exists(path)) return;
        // The YSL Dev Docs tables as the import of onlyApp.qbl created them (Fid = Quickbase field id).
        var employee = new PipelineOnlyTable(10, "Employee", null,
        [
            .. Sys(), F(7, "Email", "Email"), F(20, "Name"), F(22, "Status"), F(23, "TestUser", "User"), F(24, "TestListUser", "MultiUser"),
            F(25, "TestMutiline", "TextMultiLine"), F(26, "TPercent", "Percent"), F(27, "TRating", "Rating"), F(28, "TCurrency", "Currency"),
            F(29, "TRichText", "RichText"), F(30, "TMutipleChoice", "SingleSelect"), F(31, "TMultiSelecttext", "MultiSelect"),
            F(32, "TCheckbox", "Boolean"), F(34, "Turl", "Url"), F(35, "TDuration", "Duration"), F(37, "TAddress", "Address"),
            F(45, "TFormulaCheckbox", "Formula_Bool"), F(46, "TFormulaphone", "Formula_Phone"), F(47, "TformulaEmail", "Formula_Email"),
        ]);
        var employee1 = new PipelineOnlyTable(11, "Employee1", null,
        [
            .. Sys(), F(6, "Employee Name"), F(7, "Employee Code"), F(8, "Salary", "Number"), F(9, "Experience(Year)", "Number"),
            F(10, "Joining Date", "Date"), F(11, "Related Departmnent1", "Number"), F(13, "Related Departmnent12", "Number"), F(16, "Related Departmnent13", "Number"),
        ]);
        var apps = new List<PipelineOnlyApp> { new(Guid.NewGuid(), 104, "YSL Dev Docs", [employee, employee1]), Apps()[1] };
        var binding = QblPipelineOnlyBinder.Bind(System.IO.File.ReadAllText(path), apps);
        binding.Matches.Select(m => $"{m.QbTableId}={m.TableName}").Should().BeEquivalentTo(["bwcrtu9k5=Employee", "bwepvdwy6=Employee1"]);
        System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "pipelineonly_bound.qbl"), binding.Content);
        var pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content);
        pipelines.Should().HaveCount(8);
        pipelines.Should().NotContain(p => p.Name == "New pipeline");
        pipelines.SelectMany(p => p.Issues).Where(i => i.Code == "PIPELINE_TABLE_MISSING").Select(i => i.Message).Should().BeEmpty();
    }
}
