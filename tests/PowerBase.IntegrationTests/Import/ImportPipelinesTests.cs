using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Import;

/// <summary>A QBL file with pipelines through the real import endpoint: the app imports as before and each pipeline becomes an
/// inactive PowerFlow, with the HTTP account name kept and writes to calculated fields reported.</summary>
[Collection("PowerBase")]
public class ImportPipelinesTests : IntegrationTestBase
{
    public ImportPipelinesTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    private const string QblWithPipeline = """
        Version: 0.12
        Resources:
          $App_Docs:
            Type: QB::Application
            Properties:
              Name: Docs With PowerFlow
            Tables:
              $Table_Employee:
                Type: QB::Table
                Properties:
                  Name: Employee
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                    Properties: { Label: Record ID# }
                  $Field_Name:
                    Type: QB::Field::Text
                    Id: 20
                    Properties: { Label: Name }
                  $Field_Name_Formula:
                    Type: QB::Field::Text::Formula
                    Id: 21
                    Properties: { Label: Name Formula, Formula: '[Name]' }
          $Pipeline_P:
            Type: QB::Pipeline
            Properties: { Name: Imported Flow }
            Steps:
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: aa
                  Name: On New Event
                  PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                  Channel: { Resource: record, Id: quickbase, Selector: on_new_event }
                InputsMeta:
                  $Meta:
                    Type: QB::PipelineStep::InputsMeta
                    Properties: { On_Add_Record: true }
                    PipelineStep_DataSources:
                      $Ds:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                          Name: table
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ab
                  Name: Update Record
                  Channel: { Resource: record, Id: quickbase, Selector: update }
                  Target: { StepId: aa, Type: step-reference }
                  Inputs: { Name: '{{aa.name}}', Name_Formula: '{{aa.name}}' }
              - Type: QB::PipelineStep::Http
                Properties:
                  Id: ac
                  Name: Make Request
                  PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Http }
                  Channel: { Resource: request, Id: http, Selector: make_request }
                InputsMeta:
                  $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Method: GET, Path: /get } }
          $PipelineChannelAccount_Quickbase:
            Type: QB::PipelineChannelAccount
            Properties: { Channel: quickbase, AccountType: tokenless }
          $PipelineChannelAccount_Http:
            Type: QB::PipelineChannelAccount
            Properties: { Channel: http, Alias: ysl-dev }
        """;

    [Fact]
    public async Task Import_QblWithPipeline_CreatesTheAppAndAnInactivePowerFlow()
    {
        var (token, _) = await SignupAsync();

        var response = await PostFileAsync("/apps/import", QblWithPipeline, fileName: "flow.qbl", token: token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var batch = await ReadData<ImportBatchDto>(response);
        batch.Failed.Should().BeEmpty();
        var report = batch.Apps.Should().ContainSingle().Subject;
        report.TablesCreated.Should().Be(1);
        var pipeline = report.Pipelines.Should().ContainSingle().Subject;
        pipeline.Status.Should().Be("Created");
        pipeline.StepsImported.Should().Be(3);
        pipeline.StepsSkipped.Should().Be(0);
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_COMPUTED_FIELD_WRITE");
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_HTTP_CONNECTION_MISSING" && i.Message.Contains("ysl-dev"));

        var editor = await ReadData<EditorDto>(await GetAsync($"/pipelines/{pipeline.PipelinePublicId}/editor", token));
        editor.IsActive.Should().BeFalse();
        editor.Steps.Select(s => s.Subtype).Should().Equal("new-event", "update-record", "make-request");
        editor.Steps[2].ConfigJson.Should().Contain("\"accountName\":\"ysl-dev\"");
        editor.Steps[1].ConfigJson.Should().Contain("fid_20").And.NotContain("fid_21");
    }

    private record IssueDto(string Code, string Message, string? StepId, string? StepName);
    private record PipelineDto(string Name, string Status, Guid? PipelinePublicId, int StepsImported, int StepsSkipped, List<IssueDto> Issues);
    private record ImportReportDto(Guid AppPublicId, string AppName, int TablesCreated, List<PipelineDto> Pipelines);
    private record ImportFailureDto(string AppRef, string AppName, string Code, string Message);
    private record ImportBatchDto(List<ImportReportDto> Apps, List<ImportFailureDto> Failed);
    private record EditorStepDto(string RefId, string Subtype, string? ConfigJson);
    private record EditorDto(bool IsActive, List<EditorStepDto> Steps);
}
