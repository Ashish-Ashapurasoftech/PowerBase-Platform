using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using PowerBase.Application.Pipelines.Commands.CreatePipeline;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using PowerBase.Domain.Entities;
using PowerBase.UnitTests.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>
/// The pipeline import end to end below the database: the real-export pipeline is resolved against the tables and fields the
/// app import created, then created and saved through the real <see cref="CreatePipelineCommandHandler"/> and
/// <see cref="SavePipelineStepsCommandHandler"/> — so every step also passes the validation a PowerFlow saved in the editor gets.
/// </summary>
public class PblPipelinesImporterTests
{
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private readonly Guid _appId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _pipelineId = Guid.NewGuid();
    private readonly AppTable _employee = new() { Id = 1, AppId = 10, PublicId = Guid.NewGuid(), Name = "Employee" };
    private readonly AppTable _employee1 = new() { Id = 2, AppId = 10, PublicId = Guid.NewGuid(), Name = "Employee1" };
    private readonly List<List<PipelineStep>> _saves = [];
    private Pipeline? _created;

    public PblPipelinesImporterTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        var tenant = new Tenant { Id = 1, PublicId = _tenantId, Name = "Realm" };
        _tenantRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(tenant);
        _tenantRepo.GetTenantForUserAsync(_tenantId, 1, Arg.Any<CancellationToken>()).Returns(tenant);

        _appRepo.GetIdByPublicIdAsync(_appId, Arg.Any<CancellationToken>()).Returns(10L);
        _appRepo.GetByPublicIdAsync(_appId, Arg.Any<CancellationToken>()).Returns(new App { Id = 10, PublicId = _appId, Name = "Docs" });
        _tableRepo.ListByAppAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppTable> { _employee, _employee1 });
        _tableRepo.GetByPublicIdAsync(_employee.PublicId, Arg.Any<CancellationToken>()).Returns(_employee);
        _tableRepo.GetByPublicIdAsync(_employee1.PublicId, Arg.Any<CancellationToken>()).Returns(_employee1);
        _fieldRepo.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            RecordId(), Field(20, "Name"), Field(30, "TAddress", "Address"),
        });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { RecordId(), Field(6, "Employee Name") });

        _pipelineRepo.NameExistsForUserAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _pipelineRepo.CreateAsync(Arg.Do<Pipeline>(p => _created = p), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns((_pipelineId, 99L));
        _pipelineRepo.GetByPublicIdAsync(_pipelineId, Arg.Any<CancellationToken>())
            .Returns(_ => new Pipeline { Id = 99, PublicId = _pipelineId, AppId = 10, Name = _created!.Name, IsActive = false, RowVersion = [1] });
        _pipelineRepo.GetIdByPublicIdAsync(_pipelineId, Arg.Any<CancellationToken>()).Returns(99L);
        _pipelineRepo.GetStepsByPipelineIdAsync(99, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>());
        _pipelineRepo.SaveStepsAsync(99, Arg.Do<IEnumerable<PipelineStep>>(s => _saves.Add(s.ToList())), Arg.Any<byte[]>(),
            Arg.Any<bool>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private static AppField RecordId() => new()
    {
        Id = 103, Fid = 3, Name = "Record ID#", Label = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id",
    };

    private static AppField Field(int fid, string name, string typeCode = "Text") => new()
    {
        Id = 1000 + fid, Fid = fid, Name = name, Label = name, TypeCode = typeCode, PhysicalColumnName = $"f_{fid}",
    };

    private PblPipelinesImporter Importer()
    {
        var create = new CreatePipelineCommandHandler(_appRepo, _pipelineRepo, Substitute.For<IAuditRepository>(), _queryContext);
        var save = new SavePipelineStepsCommandHandler(_pipelineRepo, _appRepo, _tableRepo, _fieldRepo, Substitute.For<IAppAccessService>(),
            Substitute.For<ITenantUnitOfWork>(), _tenantRepo, _queryContext, Substitute.For<IMainPipelineQueueRepository>(), Substitute.For<IServiceProvider>());
        var update = new UpdatePipelineCommandHandler(_pipelineRepo, Substitute.For<IAuditRepository>(), _queryContext, _appRepo, _tableRepo, _fieldRepo,
            Substitute.For<IAppAccessService>(), _tenantRepo, Substitute.For<IMainPipelineQueueRepository>(), Substitute.For<IServiceProvider>());
        return new PblPipelinesImporter(create, save, _pipelineRepo, _appRepo, _tableRepo, _fieldRepo, _tenantRepo, _queryContext, update);
    }

    private async Task<ImportReport> ImportAsync(string pipelineYaml, bool activate = false)
    {
        var parsed = ImportDocumentParser.ParseAll(QblPipelineConverterTests.App + "\n" + pipelineYaml + "\n" + QblPipelineConverterTests.Account).Single();
        var report = new ImportReport { AppPublicId = _appId, AppName = "Docs" };
        await Importer().ImportAsync([parsed], [(parsed, report)], CancellationToken.None, activate);
        return report;
    }

    private PipelineStep Saved(string refId) => _saves.Last().Single(s => s.RefId == refId);

    private static JsonElement Config(PipelineStep step) => JsonDocument.Parse(step.ConfigJson!).RootElement;

    [Fact]
    public async Task RealExportPipeline_IsCreatedOffAndEveryStepPassesPowerFlowsValidation()
    {
        var report = await ImportAsync(QblPipelineConverterTests.AllSteps);

        var item = report.Pipelines.Should().ContainSingle().Subject;
        item.Status.Should().Be(PipelineImportStatuses.Created);
        item.PipelinePublicId.Should().Be(_pipelineId);
        item.StepsImported.Should().Be(18);
        item.StepsSkipped.Should().Be(0);
        report.PipelinesCreated.Should().Be(1);
        _created!.IsActive.Should().BeFalse();
        _created.Name.Should().Be("All Step");
        _saves.Should().ContainSingle("the steps pass validation on the first save");
        _saves[0].Should().HaveCount(18);
    }

    [Fact]
    public async Task Import_NumbersStepsAsTheEditorDoes()
    {
        // assignStepNumbers: a loop's steps continue the count.
        await ImportAsync(QblPipelineConverterTests.AllSteps);
        int Number(string refId) => Config(Saved(refId)).GetProperty("_displayNumber").GetInt32();

        Number("ref_aa").Should().Be(1);
        Number("ref_ab").Should().Be(2);
        var loop = _saves[0].Single(s => s.Type == "loop");
        Config(loop).GetProperty("_displayNumber").GetInt32().Should().Be(6);
        Number("ref_af").Should().Be(7);
    }

    [Fact]
    public async Task ActivateNotRequested_PowerFlowStaysOffWithoutAnActivationIssue()
    {
        var item = (await ImportAsync(QblPipelineConverterTests.AllSteps)).Pipelines.Single();

        item.IsActive.Should().BeFalse();
        item.Issues.Should().NotContain(i => i.Code == "PIPELINE_NOT_ACTIVATED");
    }

    [Fact]
    public async Task ActivateRequested_CompletePowerFlowIsTurnedOnThroughTheEditorsActivation()
    {
        _pipelineRepo.GetStepsByPipelineIdAsync(99, Arg.Any<CancellationToken>()).Returns(_ => _saves.LastOrDefault() ?? []);
        _pipelineRepo.UpdateAsync(Arg.Any<Pipeline>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>()).Returns(1);

        var item = (await ImportAsync(QblPipelineConverterTests.AllSteps, activate: true)).Pipelines.Single();

        if (item.NeedsReview)
            item.Issues.Should().Contain(i => i.Code == "PIPELINE_NOT_ACTIVATED");
        else
        {
            item.Issues.Should().NotContain(i => i.Code == "PIPELINE_NOT_ACTIVATED", string.Join(" | ", item.Issues.Select(i => i.Message)));
            item.IsActive.Should().BeTrue();
            await _pipelineRepo.Received().UpdateAsync(Arg.Is<Pipeline>(p => p.IsActive), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task ActivateRequested_DraftStepKeepsItOffAndSaysWhy()
    {
        var item = (await ImportAsync(QblPipelineConverterTests.AllSteps.Replace("Ronak", "{{zz.unknown}}"), activate: true)).Pipelines.Single();

        item.IsActive.Should().BeFalse();
        item.NeedsReview.Should().BeTrue();
        item.Issues.Should().Contain(i => i.Code == "PIPELINE_NOT_ACTIVATED");
    }

    [Fact]
    public async Task RealExportPipeline_ReferencesBecomeIdsFieldNumbersAndStepTokens()
    {
        await ImportAsync(QblPipelineConverterTests.AllSteps);
        var table = _employee.PublicId.ToString();
        var table1 = _employee1.PublicId.ToString();

        var trigger = Config(Saved("ref_aa"));
        trigger.GetProperty("tablePublicId").GetString().Should().Be(table);
        trigger.GetProperty("appPublicId").GetString().Should().Be(_appId.ToString());
        trigger.GetProperty("connectionPublicId").GetString().Should().Be(_tenantId.ToString());
        trigger.GetProperty("subsequentFields").EnumerateArray().Select(e => e.GetString()).Should().Equal("fid_20", "fid_30", "fid_30.city");
        trigger.GetProperty("triggerOnAdded").GetBoolean().Should().BeTrue();

        var create = Config(Saved("ref_ab")).GetProperty("fieldMappings")[0];
        create.GetProperty("field").GetString().Should().Be("fid_6");
        create.GetProperty("value").GetString().Should().Be("{{steps.ref_aa.fid_20}}");
        Config(Saved("ref_ab")).GetProperty("tablePublicId").GetString().Should().Be(table1);

        Config(Saved("ref_ac")).GetProperty("targetRecordId").GetString().Should().Be("{{steps.ref_ab.CreatedRecordPublicId}}");
        Config(Saved("ref_ad")).GetProperty("tableId").GetString().Should().Be(table1);
        Config(Saved("ref_ag")).GetProperty("recordIdValue").GetString().Should().Be("{{steps.ref_aa.fid_3}}");

        var rule = Config(Saved("ref_ae")).GetProperty("filterGroups")[0].GetProperty("rules")[0];
        rule.GetProperty("field").GetString().Should().Be("fid_20");
        rule.GetProperty("operator").GetString().Should().Be("starts-with");

        var copy = Config(Saved("ref_aj"));
        copy.GetProperty("sourceTable").GetString().Should().Be(table);
        copy.GetProperty("destinationTable").GetString().Should().Be(table1);
        copy.GetProperty("sourceFields")[0].GetString().Should().Be("fid_20");
        copy.GetProperty("destinationFields")[0].GetString().Should().Be("fid_6");
        copy.GetProperty("mergeField").GetString().Should().Be("fid_3");

        Config(Saved("ref_al")).GetProperty("mergeField").GetString().Should().Be("fid_3");
        Config(Saved("ref_an")).GetProperty("bulkRecordSetStepId").GetString().Should().Be("ref_al");
        Config(Saved("ref_ak")).GetProperty("durationText").GetString().Should().Be("1 s");
    }

    [Fact]
    public async Task HandleErrorsBranchesEmailAndCall_AreSavedThroughPowerFlowsValidation()
    {
        var report = await ImportAsync("""
              $Pipeline_P:
                Type: QB::Pipeline
                Properties: { Name: P }
                Steps:
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: aa
                      Name: On New Event
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
                  - Type: QB::PipelineStep::ErrorHandling
                    Properties: { Type: error_handling, Mode: Handle, Name: Handle Errors }
                    Try:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: an
                          Name: Create Record
                          Channel: { Resource: record, Id: quickbase, Selector: create }
                          Inputs: { Employee_Name: '{{aa.name}}' }
                        InputsMeta:
                          $Meta:
                            Type: QB::PipelineStep::InputsMeta
                            PipelineStep_DataSources:
                              $Ds:
                                Type: QB::PipelineStep::DataSource::Table
                                Properties:
                                  Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                                  Name: table
                    OnError:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ap
                          Name: Create Record
                          Channel: { Resource: record, Id: quickbase, Selector: create }
                          Inputs: { Employee_Name: '{{ERROR.error_message}}' }
                        InputsMeta:
                          $Meta:
                            Type: QB::PipelineStep::InputsMeta
                            PipelineStep_DataSources:
                              $Ds:
                                Type: QB::PipelineStep::DataSource::Table
                                Properties:
                                  Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                                  Name: table
                      - Type: QB::PipelineStep::Stop
                        Properties: { Type: stop, Name: Stop }
                    OnSuccess:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ao
                          Name: Update Record
                          Channel: { Resource: record, Id: quickbase, Selector: update }
                          Inputs: { Employee_Name: '{{an.employee_name}}' }
                          Target: { StepId: an, Type: step-reference }
                  - Type: QB::PipelineStep::MicrosoftOutlook
                    Properties:
                      Id: ar
                      Name: Send an Email
                      Channel: { Resource: email, Id: microsoft-outlook, Selector: send_email }
                      Inputs: { To_Addresses: '{{aa.name}}', Subject: Hi, Body: '{{aa.name}}', Save_To_Sent_Items: true }
                  - Type: QB::PipelineStep::CloudpipesMeta
                    Properties:
                      Id: at
                      Name: Call Another Pipeline
                      Channel: { Resource: pipeline, Id: cloudpipes-meta, Selector: call_pipeline }
                      Inputs: { Name: '{{aa.name}}' }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Call_Definition: 'Flow1(Name)' } }
            """);

        var item = report.Pipelines.Should().ContainSingle().Subject;
        item.StepsImported.Should().Be(8);
        _created!.IsActive.Should().BeFalse();
        _saves.Should().ContainSingle("the steps pass validation on the first save");
        _saves[0].Should().OnlyContain(s => s.IsValidated);

        var handle = _saves[0].Single(s => s.Subtype == "handle-errors");
        Config(handle).GetProperty("fallbackAction").GetString().Should().Be("handle");
        Saved("ref_an").ParentBranch.Should().Be("children");
        Saved("ref_ao").ParentBranch.Should().Be("successChildren");
        Saved("ref_ap").ParentBranch.Should().Be("errorChildren");
        _saves[0].Single(s => s.Subtype == "stop").ParentBranch.Should().Be("errorChildren");
        // Saved as the editor saves an error-detail chip, so opening the PowerFlow changes nothing.
        Config(Saved("ref_ap")).GetProperty("fieldMappings")[0].GetProperty("value").GetString().Should().Be("{{ERROR.error_message}}");
        Config(Saved("ref_ao")).GetProperty("fieldMappings")[0].GetProperty("value").GetString().Should().Be("{{steps.ref_an.fid_6}}");

        var email = Config(Saved("ref_ar"));
        email.GetProperty("toAddresses").GetString().Should().Be("{{steps.ref_aa.fid_20}}");
        email.GetProperty("saveToSentItems").GetString().Should().Be("Yes");
        var call = Config(Saved("ref_at"));
        call.GetProperty("callDefinition").GetString().Should().Be("Flow1(Name)");
        call.GetProperty("arguments").GetProperty("Name").GetString().Should().Be("{{steps.ref_aa.fid_20}}");
    }

    [Fact]
    public async Task IncomingRequestAndMakeRequest_AreSavedThroughPowerFlowsValidation()
    {
        var report = await ImportAsync("""
              $Pipeline_P:
                Type: QB::Pipeline
                Properties: { Name: P }
                Steps:
                  - Type: QB::PipelineStep::Webhooks
                    Properties:
                      Id: aa
                      Name: Incoming Request
                      Channel: { Resource: request, Id: webhooks, Selector: on_incoming }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Authentication_Schema: no-auth, Method_Type: POST } }
                  - Type: QB::PipelineStep::Http
                    Properties:
                      Id: ab
                      Name: Make Request
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Http }
                      Channel: { Resource: request, Id: http, Selector: make_request }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Method: POST, Path: /post, Body: '{{aa.body}}' } }
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ac
                      Name: Create Record
                      Channel: { Resource: record, Id: quickbase, Selector: create }
                      Inputs: { Employee_Name: '{{aa.body_params.name}}' }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                              Name: table
              $PipelineChannelAccount_Http:
                Type: QB::PipelineChannelAccount
                Properties: { Channel: http, Alias: ysl-dev }
            """);

        report.Pipelines.Should().ContainSingle().Which.StepsImported.Should().Be(3);
        Config(Saved("ref_ab")).GetProperty("accountName").GetString().Should().Be("ysl-dev");
        Config(Saved("ref_ab")).TryGetProperty("httpConnectionId", out _).Should().BeFalse("no connection is created on import");
        _created!.IsActive.Should().BeFalse();
        _saves.Should().ContainSingle("the steps pass validation on the first save");
        Saved("ref_aa").IsValidated.Should().BeTrue();
        Config(Saved("ref_aa")).GetProperty("methodType").GetString().Should().Be("POST");
        Saved("ref_ab").IsValidated.Should().BeFalse("the HTTP connection is not in the file");
        Config(Saved("ref_ab")).GetProperty("body").GetString().Should().Be("{{steps.ref_aa.body}}");
        Config(Saved("ref_ac")).GetProperty("fieldMappings")[0].GetProperty("value").GetString().Should().Be("{{steps.ref_aa.body_params.name}}");
    }

    [Fact]
    public async Task RealExportPipeline_KeepsBranchesAndLoopsAndMarksOnlyTheClearedStepAsADraft()
    {
        var report = await ImportAsync(QblPipelineConverterTests.AllSteps);

        Saved("ref_af").ParentBranch.Should().Be("children");
        Saved("ref_ah").ParentBranch.Should().Be("children");
        Saved("ref_ai").ParentBranch.Should().Be("elseChildren");
        _saves[0].Single(s => s.Subtype == "stop").ParentBranch.Should().Be("elseChildren");
        Config(_saves[0].Single(s => s.Subtype == "for-each")).GetProperty("loopOverStepId").GetString().Should().Be("ref_ae");

        _saves[0].Where(s => !s.IsValidated).Select(s => s.RefId).Should().Equal("ref_am");
        Config(Saved("ref_am")).GetProperty("rowValues").GetProperty("fid_20").GetString().Should().Be("");
        report.Pipelines[0].NeedsReview.Should().BeTrue();
        report.Pipelines[0].Issues.Should().Contain(i => i.StepId == "am" && i.Code == "PIPELINE_REFERENCE_CLEARED");
    }

    [Fact]
    public async Task UnsupportedStep_IsReportedWithPipelineStepNameIdAndReason()
    {
        var report = await ImportAsync(QblPipelineConverterTests.Unsupported);

        var item = report.Pipelines.Single();
        item.Status.Should().Be(PipelineImportStatuses.Created);
        item.StepsSkipped.Should().Be(2);
        item.NeedsReview.Should().BeTrue();
        item.Issues.Should().Contain(i => i.StepId == "ab" && i.StepName == "Send Slack Message" && i.IsSkippedStep
                                          && i.Message.StartsWith(QblPipelineConverter.UNSUPPORTED_STEP_MESSAGE));
        _saves.Last().Select(s => s.RefId).Should().Equal("ref_aa", "ref_ad");
    }

    [Fact]
    public async Task ExistingPipelineName_IsImportedUnderANumberedName()
    {
        _pipelineRepo.NameExistsForUserAsync(1, "All Step", Arg.Any<CancellationToken>()).Returns(true);

        var report = await ImportAsync(QblPipelineConverterTests.AllSteps);

        _created!.Name.Should().Be("All Step 2");
        report.Pipelines[0].Issues.Should().Contain(i => i.Code == "PIPELINE_RENAMED");
    }

    [Fact]
    public async Task FieldMissingFromTheImportedApp_IsLeftOutAndTheStepIsADraft()
    {
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { RecordId() });

        var report = await ImportAsync(QblPipelineConverterTests.AllSteps);

        Saved("ref_ab").IsValidated.Should().BeFalse();
        Config(Saved("ref_ab")).GetProperty("fieldMappings").GetArrayLength().Should().Be(0);
        report.Pipelines[0].Issues.Should().Contain(i => i.StepId == "ab" && i.Message.Contains("Employee Name"));
    }

    [Fact]
    public async Task StepsThatFailValidation_AreSavedAsDraftsRatherThanLost()
    {
        // The trigger's app check (only the save's validation reads the app this way) fails on the first save; the import
        // retries with every step as a draft.
        var calls = 0;
        _appRepo.GetByPublicIdAsync(_appId, Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? throw new InvalidOperationException("app lookup failed") : new App { Id = 10, PublicId = _appId, Name = "Docs" });

        var report = await ImportAsync(QblPipelineConverterTests.AllSteps);

        report.Pipelines[0].Status.Should().Be(PipelineImportStatuses.Created);
        _saves.Should().ContainSingle();
        _saves[0].Should().OnlyContain(s => !s.IsValidated);
        report.Pipelines[0].Issues.Should().Contain(i => i.Code == "PIPELINE_STEP_INCOMPLETE");
    }

    // A trigger on a table of an application that is not in the file (raw Quickbase DBIDs), followed by a step on an imported table.
    private const string TriggerOnForeignTable = """
          $Pipeline_Foreign_Trigger:
            Type: QB::Pipeline
            Properties:
              Name: Foreign Trigger
              Tags: []
              Owner: YslDevTeam
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
                          App: bu3wxsy83
                          Name: app
                      $PipelineStep_DataSource_2:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: bu3wxsy86
                          Name: table
              - Type: QB::PipelineStep::Clock
                Properties:
                  Id: ak
                  Type: pipe
                  Name: Pause
                  SubType: action
                  Channel:
                    Resource: timer
                    Id: clock
                    Selector: pause
                InputsMeta:
                  $PipelineStep_InputsMeta_ak:
                    Type: QB::PipelineStep::InputsMeta
                    Properties:
                      Duration: 1 s
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
                    Employee_Name: Fixed
                InputsMeta:
                  $PipelineStep_InputsMeta_ab:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $PipelineStep_DataSource_1:
                        Type: QB::PipelineStep::DataSource::App
                        Properties:
                          App: !Ref
                            App: $App_Docs
                          Name: app
                      $PipelineStep_DataSource_3:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee1
                          Name: table
        """;

    [Fact]
    public async Task TriggerOnTableNotInFile_IsLeftOutAndTheOtherStepsAreSavedAsDrafts()
    {
        var report = await ImportAsync(TriggerOnForeignTable);

        var item = report.Pipelines.Should().ContainSingle().Subject;
        item.Status.Should().Be(PipelineImportStatuses.Created);
        item.StepsImported.Should().Be(2);
        item.NeedsReview.Should().BeTrue();
        item.Issues.Should().Contain(i => i.Code == "PIPELINE_TRIGGER_NOT_SAVED" && i.StepName == "On New Event");
        var saved = _saves.Should().ContainSingle().Subject;
        saved.Select(s => s.RefId).Should().Equal("ref_ak", "ref_ab");
        saved.Select(s => s.DisplayOrder).Should().Equal(0, 1);
        saved.Should().OnlyContain(s => !s.IsValidated);
        saved.Select(s => Config(s).GetProperty("_displayNumber").GetInt32()).Should().Equal(1, 2);
    }

    [Fact]
    public async Task TriggerOnTableNotInFile_FollowedByCreateRecord_SavesTheCreateRecordAsTheFirstStep()
    {
        // Create Record can start a PowerFlow (as in Quickbase), so once the trigger is left out it is saved on its own.
        var pause = TriggerOnForeignTable.IndexOf("- Type: QB::PipelineStep::Clock", StringComparison.Ordinal);
        var create = TriggerOnForeignTable.IndexOf("- Type: QB::PipelineStep::Quickbase", pause, StringComparison.Ordinal);
        var withoutPause = TriggerOnForeignTable[..pause] + TriggerOnForeignTable[create..];

        var report = await ImportAsync(withoutPause);

        var item = report.Pipelines.Single();
        item.Status.Should().Be(PipelineImportStatuses.Created);
        item.Issues.Should().Contain(i => i.Code == "PIPELINE_TRIGGER_NOT_SAVED");
        _saves.Should().ContainSingle().Which.Should().ContainSingle().Which.Subtype.Should().Be("create-record");
    }

    [Fact]
    public async Task ConditionOnASearchFindingRecords_AndAnAndOrFilter_PassPowerFlowsValidation()
    {
        var report = await ImportAsync("""
          $Pipeline_Found:
            Type: QB::Pipeline
            Properties:
              Name: Found
            Steps:
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ab
                  Name: Search Records
                  PipelineChannelAccount: !Ref
                    PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                  Channel: { Resource: record, Id: quickbase, Selector: search }
                  Filters:
                    Type: composite-expression
                    Notation: postfix
                    Expression:
                      - { Type: relational-expression, Field: employee_name, Operator: starts, Value: Ron }
                      - { Type: relational-expression, Field: employee_name, Operator: '!contains', Value: x }
                      - { Type: logical-operator, Operator: AND }
                      - { Type: relational-expression, Field: employee_name, Operator: set }
                      - { Type: logical-operator, Operator: OR }
                InputsMeta:
                  $Meta:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $Ds:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                          Name: table
              - Type: QB::PipelineStep::Condition
                Properties:
                  Name: Condition
                  Filters:
                    Type: composite-expression
                    Notation: postfix
                    Expression:
                      - { Type: relational-expression, Operator: '!empty', Target: ab }
                Then:
                  - Type: QB::PipelineStep::Stop
                    Properties: { Type: stop, Name: Stop }
        """);

        var item = report.Pipelines.Should().ContainSingle().Subject;
        item.Status.Should().Be(PipelineImportStatuses.Created, string.Join(" | ", item.Issues.Select(i => i.Code + ": " + i.Message)));
        item.Issues.Should().NotContain(i => i.Code.StartsWith("PIPELINE_FILTER") || i.Code == "PIPELINE_STEP_INCOMPLETE");
        Saved("ref_ab").IsValidated.Should().BeTrue();
        Config(Saved("ref_ab")).GetProperty("filterGroups").GetArrayLength().Should().Be(2);
        var condition = _saves.Last().Single(s => s.Subtype == "if-else");
        condition.IsValidated.Should().BeTrue();
        var rule = Config(condition).GetProperty("ruleGroups")[0].GetProperty("rules")[0];
        rule.GetProperty("left").GetString().Should().Be("{{steps.ref_ab.count}}");
        rule.GetProperty("op").GetString().Should().Be("not-equals");
    }

    [Fact]
    public async Task TriggerOnImportedTable_IsKept()
    {
        await ImportAsync(QblPipelineConverterTests.AllSteps);

        _saves.Should().ContainSingle();
        _saves[0].Should().Contain(s => s.RefId == "ref_aa" && s.Subtype == "new-event");
    }

    [Fact]
    public async Task CreateFailure_IsReportedAndNeverThrown()
    {
        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database down"));

        var report = await ImportAsync(QblPipelineConverterTests.AllSteps);

        var item = report.Pipelines.Single();
        item.Status.Should().Be(PipelineImportStatuses.NotCreated);
        item.NeedsReview.Should().BeTrue();
        item.Issues.Should().Contain(i => i.Code == "PIPELINE_IMPORT_FAILED");
    }

    [Fact]
    public async Task FileWithoutPipelines_DoesNothing()
    {
        var parsed = ImportDocumentParser.ParseAll(QblPipelineConverterTests.App).Single();
        var report = new ImportReport { AppPublicId = _appId, AppName = "Docs" };

        await Importer().ImportAsync([parsed], [(parsed, report)], CancellationToken.None);

        report.Pipelines.Should().BeEmpty();
        await _pipelineRepo.DidNotReceive().CreateAsync(Arg.Any<Pipeline>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    // ---- Update Existing App ----

    private static ParsedApp Parsed(string pipelineYaml) =>
        ImportDocumentParser.ParseAll(QblPipelineConverterTests.App + "\n" + pipelineYaml + "\n" + QblPipelineConverterTests.Account).Single();

    private static PblPipelinesImporter.ExistingAppTargets NoTargets() => new(new Dictionary<string, long>(), new Dictionary<string, long>());

    [Fact]
    public void LoadApp_QblWithPipelines_KeepsThePipelinesForTheUpdate()
    {
        var content = QblPipelineConverterTests.App + "\n" + QblPipelineConverterTests.AllSteps + "\n" + QblPipelineConverterTests.Account;

        var (app, _) = PblDocumentTools.LoadApp(content, new PowerBase.Application.Import.Pbl.PblValidator());

        app.Pipelines.Should().ContainSingle().Which.Name.Should().Be("All Step");
        app.Pipelines![0].AppRef.Should().Be(app.AppRef);
    }

    [Fact]
    public async Task ExistingApp_ApprovedNewPipeline_IsCreatedOffWithAllItsSteps()
    {
        var parsed = Parsed(QblPipelineConverterTests.AllSteps);
        var pipelineRef = parsed.Pipelines!.Single().LogicalRef;

        var items = await Importer().ImportIntoExistingAppAsync(parsed, _appId, "Docs", [pipelineRef], new Dictionary<string, Guid>(), NoTargets(), default);

        var item = items.Should().ContainSingle().Subject;
        item.Status.Should().Be(PipelineImportStatuses.Created);
        item.PipelinePublicId.Should().Be(_pipelineId);
        item.StepsImported.Should().Be(18);
        _created!.IsActive.Should().BeFalse();
        _saves.Should().ContainSingle().Which.Should().HaveCount(18);
    }

    [Fact]
    public async Task ExistingApp_PipelineNotApproved_IsLeftAlone()
    {
        var parsed = Parsed(QblPipelineConverterTests.AllSteps);

        var items = await Importer().ImportIntoExistingAppAsync(parsed, _appId, "Docs", [], new Dictionary<string, Guid>(), NoTargets(), default);

        items.Should().BeEmpty();
        await _pipelineRepo.DidNotReceive().CreateAsync(Arg.Any<Pipeline>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        _saves.Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingApp_TableMatchedUnderAnotherName_StepsUseThatTable()
    {
        // The app's "Employee" table was renamed "Staff"; the diff matched it by source ref.
        _employee.Name = "Staff";
        var parsed = Parsed(QblPipelineConverterTests.AllSteps);
        var employeeRef = parsed.Document.Tables!.Single(t => t.Name == "Employee").LogicalRef;
        var targets = new PblPipelinesImporter.ExistingAppTargets(new Dictionary<string, long> { [employeeRef] = _employee.Id }, new Dictionary<string, long>());

        var items = await Importer().ImportIntoExistingAppAsync(parsed, _appId, "Docs", [parsed.Pipelines!.Single().LogicalRef],
            new Dictionary<string, Guid>(), targets, default);

        items.Single().Status.Should().Be(PipelineImportStatuses.Created);
        Config(Saved("ref_aa")).GetProperty("tablePublicId").GetString().Should().Be(_employee.PublicId.ToString());
        items.Single().Issues.Should().NotContain(i => i.Code == "PIPELINE_TABLE_MISSING");
    }

    [Fact]
    public async Task ExistingApp_FieldMatchedUnderAnotherName_MappingUsesThatField()
    {
        // "Name" on Employee is called "Full Name" in the app now; the diff matched it by source ref.
        var renamed = Field(20, "Full Name");
        _fieldRepo.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<AppField> { RecordId(), renamed, Field(30, "TAddress", "Address") });
        var parsed = Parsed(QblPipelineConverterTests.AllSteps);
        var nameRef = parsed.Document.Tables!.Single(t => t.Name == "Employee").Fields!.Single(f => f.Name == "Name").LogicalRef;
        var targets = new PblPipelinesImporter.ExistingAppTargets(new Dictionary<string, long>(), new Dictionary<string, long> { [nameRef] = renamed.Id });

        await Importer().ImportIntoExistingAppAsync(parsed, _appId, "Docs", [parsed.Pipelines!.Single().LogicalRef], new Dictionary<string, Guid>(), targets, default);

        Saved("ref_ab").ConfigJson.Should().Contain("fid_20");
    }

    private const string MetadataValue =
        "{{metadata.aa.context.app_name}}|{{metadata.aa.record.action}}|{{metadata.aa.struct.name.qb_field_id}}";

    [Fact]
    public async Task NewApp_QuickbaseMetadata_IsSavedAsStepMetadataTokensWithTheImportedFieldNumber()
    {
        await ImportAsync(QblPipelineConverterTests.AllStepsWith("{{aa.name}}", 0, MetadataValue));

        var create = Config(Saved("ref_ab")).GetProperty("fieldMappings")[0];
        create.GetProperty("value").GetString().Should().Be(
            "{{steps.ref_aa._metadata.context.app_name}}|{{steps.ref_aa._metadata.record.action}}|{{steps.ref_aa._metadata.struct.fid_20.qb_field_id}}");
    }

    // powerflowextraRd: Quickbase Jinja that our formula language has an equivalent for becomes a PowerFlow formula value (QblJinjaFormula),
    // checked with the formula engine; run with that engine it prints what Quickbase prints. Now = 2025-01-05 23:30 UTC, the trigger's Name = "Ronak".
    [Theory]
    [InlineData("{{aa.name}}/{{time.now|date_mdy}}", "Ronak/01-05-2025")]
    [InlineData("{{time.now | date_mdy('/')}} by {{aa.name}}", "01/05/2025 by Ronak")]
    [InlineData("{{time.today|date_ymd('')}}", "20250105")]
    [InlineData("D {{time.now|date_dmy('.')}}", "D 05.01.2025")]
    [InlineData("{{aa.name|upper}}", "RONAK")]
    [InlineData("{{aa.name|lower}}-{{aa.name|length}}", "ronak-5")]
    [InlineData("{{aa.name|trim|first}}{{aa.name|last}}", "Rk")]
    [InlineData("{{aa.name|replace('R', 'B')}}", "Bonak")]
    [InlineData("{{aa.name ~ ' ' ~ 'X'}}", "Ronak X")]
    [InlineData("{{aa.name + '!'}}", "Ronak!")]
    [InlineData("{{(time.today + time.delta(days=7))|date_ymd}}", "2025-01-12")]
    [InlineData("{{(time.now - time.delta(weeks=1))|date_mdy}}", "12-29-2024")]
    [InlineData("{{(time.now + time.delta(months=1))|date_mdy}}", "02-05-2025")]
    [InlineData("{{(time.now + time.delta(years=1))|date_ymd('/')}}", "2026/01/05")]
    [InlineData("{{time.now.strftime('%B %d, %Y %I:%M %p')}}", "January 05, 2025 11:30 PM")]
    [InlineData("{{time.now.strftime('%a %b %H%%')}}", "Sun Jan 23%")]
    [InlineData("{{(time.parse('2025-03-01') - time.today).days}}", "55")]
    [InlineData("{{(10 / 4) | round(1)}}", "2.5")]
    [InlineData("{{7 // 2}} {{7 % 2}} {{2 * 3 + 1}}", "3 1 7")]
    [InlineData("{{(-5)|abs}} {{ -5|abs}} {{'3.7'|float|int}}", "5 5 3")]   // Jinja: a filter applies to the negated value
    [InlineData("{{aa.name|default('none')}}", "Ronak")]
    [InlineData("{{'a&b c'|urlencode}}", "a%26b%20c")]
    [InlineData("{{CLEAR}}", "")]
    // comparisons, and / or / not, if-else, in, is-tests, ** (Jinja prints a boolean as True / False)
    [InlineData("v{{aa.name == 'Ronak'}} {{aa.name != 'Ronak'}}", "vTrue False")]
    [InlineData("v{{3 > 2}} {{2 >= 3}} {{2 < 3 and 1 <= 1}} {{1 > 2 or not 1 > 2}}", "vTrue False True True")]
    [InlineData("{{'Yes' if aa.name == 'Ronak' else 'No'}}", "Yes")]
    [InlineData("{{'big' if 5 > 10 else 'small'}}-{{'x' if 1 > 2}}-{{'on' if aa.name else 'off'}}", "small--on")]
    [InlineData("v{{'na' in aa.name}} {{'z' not in aa.name}}", "vTrue True")]
    [InlineData("v{{aa.name is none}}", "vFalse")]
    [InlineData("v{{aa.name is not none}} {{aa.name is defined}}", "vTrue True")]
    [InlineData("v{{7 is odd}} {{7 is even}} {{9 is divisibleby(3)}} {{5 is gt(3)}} {{5 is eq 5}}", "vTrue False True True True")]
    [InlineData("{{2 ** 3}} {{ -2 ** 2}} {{2 ** 3 ** 2}}", "8 4 64")]   // Jinja: ** binds looser than a minus, left to right
    // {{- and -}} drop the spaces before / after (whitespace control), they are no minus sign
    [InlineData("A  {{- aa.name -}}  B {{ aa.name }} {{-5|abs}}", "ARonakB Ronak5")]
    [InlineData("v{{true}} {{false or true}} {{(1 > 0) is true}} {{aa.name is true}} {{not aa.name}}", "vTrue True True False False")]
    [InlineData("{{'n' if aa.name == none else 'y'}}", "y")]
    // text methods, indexes and slices
    [InlineData("{{aa.name.upper()}} {{aa.name.lower()}} {{'  x  '.strip()}}", "RONAK ronak x")]
    [InlineData("v{{aa.name.startswith('Ro')}} {{aa.name.endswith('x')}}", "vTrue False")]
    [InlineData("{{aa.name.replace('nak', 'hit')}} {{aa.name.find('n')}} {{aa.name.find('z')}} {{'a,b,a'.count('a')}}", "Rohit 2 -1 2")]
    [InlineData("{{'ronak'.capitalize()}} {{'rONAK'|capitalize}}", "Ronak Ronak")]
    [InlineData("{{aa.name[0]}}{{aa.name[-1]}}", "Rk")]
    [InlineData("{{aa.name[1:3]}}|{{aa.name[:2]}}|{{aa.name[2:]}}|{{aa.name[-3:]}}|{{aa.name[:-2]}}|{{aa.name[1:-1]}}|{{aa.name[-4:-1]}}", "on|Ro|nak|nak|Ron|ona|ona")]
    [InlineData("{{'a,b,c'.split(',')[1]}} {{'a,b,c'.split(',')|join('-')}} {{'a,b,c'.split(',')|length}} {{'a,b,c'.split(',')|first}}", "b a-b-c 3 a")]
    [InlineData("v{{'b' in 'a,b,c'.split(',')}} {{'x' in 'a,b,c'.split(',')}}", "vTrue False")]
    // more filters
    [InlineData("{{aa.name|base64_encode}} {{'Um9uYWs='|base64_decode}}", "Um9uYWs= Ronak")]
    [InlineData("{{'<b>Hi</b>'|text}}", "Hi")]
    [InlineData("{{2.567|round(2)}} {{2.561|round(1, 'floor')}} {{2.521|round(1, 'ceil')}} {{2.5|round}}", "2.57 2.5 2.6 2.0")]
    // Python floats print with a point; round is Python's (a half goes to the even neighbour); // rounds down
    [InlineData("{{10 / 5}} {{10 // 4}} {{7.0 // 2}} {{ -7 // 2}} {{'3'|float}} {{1.5 * 2}} {{3.5|round}} {{0.125|round(2)}}", "2.0 2 3.0 -4 3.0 3.0 4.0 0.12")]
    [InlineData("{{'Hello, %s!'|format(aa.name)}} {{'%d items'|format(3.7)}}", "Hello, Ronak! 3 items")]
    // date parts and more strftime codes (2025-01-05 is a Sunday)
    [InlineData("{{time.now.year}}-{{time.now.month}}-{{time.now.day}} {{time.now.hour}}:{{time.now.minute}} {{time.today.year}}", "2025-1-5 23:30 2025")]
    [InlineData("{{time.now.weekday()}} {{time.now.isoweekday()}} {{time.now.strftime('%y %j %w')}}", "6 7 25 005 0")]
    [InlineData("{{(time.now + time.delta(days=1)).day}} {{time.parse('2025-03-01').month}}", "6 3")]
    [InlineData("v{{time.today > time.parse('2025-01-01')}}", "vTrue")]
    public async Task NewApp_JinjaWithAFormulaEquivalent_IsSavedAsAFormulaThatPrintsTheSameText(string qbValue, string expected)
    {
        var report = await ImportAsync(QblPipelineConverterTests.AllStepsWith("{{aa.name}}", 0, qbValue));

        var value = Config(Saved("ref_ab")).GetProperty("fieldMappings")[0].GetProperty("value").GetString()!;
        value.Should().StartWith(PowerBase.Application.Pipelines.PipelineFormulaValue.Prefix,
            string.Join(" | ", report.Pipelines.Single().Issues.Where(i => i.StepId == "ab").Select(i => i.Message)));
        report.Pipelines.Single().Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.StepId == "ab");

        var engine = new PowerBase.Formula.FormulaEngine();
        var result = PowerBase.Application.Pipelines.PipelineFormulaValue.Evaluate(value,
            token => token == "{{steps.ref_aa.fid_20}}" ? "Ronak" : throw new InvalidOperationException(token), engine,
            _ => new PowerBase.Formula.Evaluation.EvaluationOptions { UtcNow = new DateTime(2025, 1, 5, 23, 30, 0, DateTimeKind.Utc) });
        result.Should().Be(expected);
    }

    // powerflowextraRd: what a real Quickbase pipeline printed (YSL Dev Docs, "Test Pipeline Formula", 2026-11-13) for an empty field —
    // None there: it prints nothing, is none, default(v) leaves it, default(v, true) replaces it, it is not equal to '' — and for
    // whole dates: time.now / time.today are UTC datetimes ("2026-11-13 05:47:54.278606+00:00", "2026-11-13 00:00:00+00:00").
    // The trigger's Name is empty here; Now = 2025-01-05 23:30 UTC.
    [Theory]
    [InlineData("[{{aa.name}}] {{aa.name is none}} [{{aa.name|default('D')}}] {{aa.name|default('D', true)}}", "[] True [] D")]
    [InlineData("{{aa.name == ''}} {{aa.name != ''}} {{aa.name == none}} {{aa.name != none}} {{'' == ''}}", "False True True False True")]
    [InlineData("{{'x' if aa.name else 'empty'}} {{not aa.name}}", "empty True")]
    [InlineData("{{time.now}}|{{time.today}}|{{time.parse('2025-03-01')}}|{{time.now.date()}}", "2025-01-05 23:30:00+00:00|2025-01-05 00:00:00+00:00|2025-03-01 00:00:00|2025-01-05")]
    [InlineData("{{time.today + time.delta(days=1)}}", "2025-01-06 00:00:00+00:00")]
    public async Task NewApp_JinjaOnAnEmptyFieldAndWholeDates_PrintsWhatQuickbasePrints(string qbValue, string expected)
    {
        await ImportAsync(QblPipelineConverterTests.AllStepsWith("{{aa.name}}", 0, qbValue));

        var value = Config(Saved("ref_ab")).GetProperty("fieldMappings")[0].GetProperty("value").GetString()!;
        value.Should().StartWith(PowerBase.Application.Pipelines.PipelineFormulaValue.Prefix);
        var result = PowerBase.Application.Pipelines.PipelineFormulaValue.Evaluate(value,
            token => token == "{{steps.ref_aa.fid_20}}" ? "" : throw new InvalidOperationException(token), new PowerBase.Formula.FormulaEngine(),
            _ => new PowerBase.Formula.Evaluation.EvaluationOptions { UtcNow = new DateTime(2025, 1, 5, 23, 30, 0, DateTimeKind.Utc) });
        result.Should().Be(expected);
    }

    // powerflowextraRd: Jinja our formulas have no equivalent for is not guessed: the value is cleared, reported and the step is a draft.
    [Theory]
    [InlineData("{{aa.name|title}}")]
    [InlineData("{{time.now|timezone('America/New_York')}}")]
    [InlineData("{{runtime.pipeline_name}}")]
    [InlineData("{{time.delta(year=2016)}}")]
    [InlineData("{{time.now.strftime('%U')}}")]
    [InlineData("{{aa.name|truncate(3)}}")]
    [InlineData("{{aa.name|wordcount}}")]
    [InlineData("{{aa.name|md5}}")]
    [InlineData("{{aa.name|html2text}}")]
    [InlineData("{{aa.name is string}}")]
    [InlineData("{{aa.name.title()}}")]
    [InlineData("{{aa.name[aa.name]}}")]
    public async Task NewApp_JinjaWithoutAFormulaEquivalent_IsClearedAndReported(string qbValue)
    {
        var report = await ImportAsync(QblPipelineConverterTests.AllStepsWith("{{aa.name}}", 0, qbValue));

        Config(Saved("ref_ab")).GetProperty("fieldMappings")[0].GetProperty("value").GetString().Should().BeEmpty();
        report.Pipelines.Single().Issues.Should().Contain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.StepId == "ab" && i.Message.Contains(qbValue));
    }

    [Fact]
    public async Task ExistingApp_QuickbaseMetadata_SchemaEntryFollowsTheMatchedField()
    {
        // The app's "Name" is "Full Name" with field number 25 now; the diff matched it by source ref.
        var renamed = Field(25, "Full Name");
        _fieldRepo.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<AppField> { RecordId(), renamed, Field(30, "TAddress", "Address") });
        var parsed = Parsed(QblPipelineConverterTests.AllStepsWith("{{aa.name}}", 0, MetadataValue + " {{metadata.ae.batch.size}}"));
        var nameRef = parsed.Document.Tables!.Single(t => t.Name == "Employee").Fields!.Single(f => f.Name == "Name").LogicalRef;
        var targets = new PblPipelinesImporter.ExistingAppTargets(new Dictionary<string, long>(), new Dictionary<string, long> { [nameRef] = renamed.Id });

        var items = await Importer().ImportIntoExistingAppAsync(parsed, _appId, "Docs", [parsed.Pipelines!.Single().LogicalRef], new Dictionary<string, Guid>(), targets, default);

        items.Single().Status.Should().Be(PipelineImportStatuses.Created);
        // Search Records (ae) comes after this step, so its metadata cannot be read here: the value is cleared, the step a draft.
        Config(Saved("ref_ab")).GetProperty("fieldMappings")[0].GetProperty("value").GetString().Should().BeEmpty();
        items.Single().Issues.Should().Contain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.Message.Contains("{{metadata.ae.batch.size}}"));

        var reachable = Parsed(QblPipelineConverterTests.AllStepsWith("{{aa.name}}", 0, MetadataValue));
        _saves.Clear();
        await Importer().ImportIntoExistingAppAsync(reachable, _appId, "Docs", [reachable.Pipelines!.Single().LogicalRef], new Dictionary<string, Guid>(), targets, default);
        Config(Saved("ref_ab")).GetProperty("fieldMappings")[0].GetProperty("value").GetString().Should().Be(
            "{{steps.ref_aa._metadata.context.app_name}}|{{steps.ref_aa._metadata.record.action}}|{{steps.ref_aa._metadata.struct.fid_25.qb_field_id}}");
    }

    [Fact]
    public async Task ExistingApp_Overwrite_TurnsThePowerFlowOffAndReplacesStepsAndSchedule()
    {
        var existingId = Guid.NewGuid();
        var existing = new Pipeline { Id = 77, PublicId = existingId, AppId = 10, Name = "My flow", Description = "old", IsActive = true, RowVersion = [7] };
        var updates = new List<Pipeline>();
        _pipelineRepo.GetByPublicIdAsync(existingId, Arg.Any<CancellationToken>()).Returns(_ => existing);
        _pipelineRepo.GetIdByPublicIdAsync(existingId, Arg.Any<CancellationToken>()).Returns(77L);
        _pipelineRepo.GetStepsByPipelineIdAsync(77, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>());
        _pipelineRepo.UpdateAsync(Arg.Do<Pipeline>(p => updates.Add(new Pipeline { Name = p.Name, Description = p.Description, IsActive = p.IsActive })),
            Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>()).Returns(1);
        var oldSchedule = new PipelineSchedule { Id = 5, PublicId = Guid.NewGuid(), PipelineId = 77, ScheduleType = "daily" };
        _pipelineRepo.GetScheduleByPipelineIdAsync(77, Arg.Any<CancellationToken>()).Returns(oldSchedule);
        var overwriteSaves = new List<List<PipelineStep>>();
        _pipelineRepo.SaveStepsAsync(77, Arg.Do<IEnumerable<PipelineStep>>(s => overwriteSaves.Add(s.ToList())), Arg.Any<byte[]>(),
            Arg.Any<bool>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var parsed = Parsed(QblPipelineConverterTests.AllSteps);
        var pipelineRef = parsed.Pipelines!.Single().LogicalRef;

        var items = await Importer().ImportIntoExistingAppAsync(parsed, _appId, "Docs", [],
            new Dictionary<string, Guid> { [pipelineRef] = existingId }, NoTargets(), default);

        var item = items.Should().ContainSingle().Subject;
        item.Status.Should().Be(PipelineImportStatuses.Created);
        item.PipelinePublicId.Should().Be(existingId);
        item.Name.Should().Be("My flow", "an overwrite keeps the PowerFlow's own name");
        item.StepsImported.Should().Be(18);
        updates.Should().ContainSingle().Which.IsActive.Should().BeFalse();
        updates[0].Name.Should().Be("My flow");
        overwriteSaves.Should().ContainSingle().Which.Should().HaveCount(18);
        await _pipelineRepo.DidNotReceive().CreateAsync(Arg.Any<Pipeline>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).DeleteScheduleAsync(oldSchedule.PublicId, Arg.Any<CancellationToken>());
    }
}
