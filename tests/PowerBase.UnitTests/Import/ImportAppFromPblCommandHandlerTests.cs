using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Commands.CreateAppRole;
using PowerBase.Application.Apps.Commands.UpdateTablePermissions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.BulkCreateFields;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Formulas;
using PowerBase.Application.Forms.Commands.CreateForm;
using PowerBase.Application.Forms.Commands.CreateFormRule;
using PowerBase.Application.Forms.Commands.SaveFormLayout;
using PowerBase.Application.Forms.Commands.SaveFormRule;
using PowerBase.Application.Forms.Queries.GetFormLayout;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Services;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Import;

public class ImportAppFromPblCommandHandlerTests
{
    // --- CreateAppCommandHandler's own dependencies ---
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IAppSeeder _appSeeder = Substitute.For<IAppSeeder>();

    // --- BulkCreateFieldsCommandHandler's dependencies (a real instance is used, since its
    // HandleAsync is invoked for real for both scalar fields (pass 1) and formula fields (pass 2)) ---
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IFieldNameResolver _fieldNameResolver = Substitute.For<IFieldNameResolver>();

    // --- CreateReportCommandHandler's own dependency not shared with the above ---
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();

    // --- CreateRelationshipCommandHandler's own dependency not shared with the above ---
    // (no existing test in this file exercises a PblDocument with Relationships, so this stays
    // a bare substitute — relationship-orchestration correctness is covered by integration
    // tests instead, per the "don't grow the fake-repo unit harness indefinitely" note.)
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();

    // --- Form Rules handlers' own dependencies not shared with the above ---
    // (no existing test in this file exercises a PblDocument with form Rules either, same
    // reasoning as relationships above.)
    private readonly IFormRuleRepository _formRuleRepo = Substitute.For<IFormRuleRepository>();
    private readonly IFormulaExpressionValidator _formulaExpressionValidator = Substitute.For<IFormulaExpressionValidator>();

    // --- Roles handlers' own dependency not shared with the above ---
    // (no existing test in this file exercises a PblDocument with Roles either, same reasoning
    // as relationships/form rules above.)
    private readonly IAppRolePermissionRepository _appRolePermissionRepo = Substitute.For<IAppRolePermissionRepository>();

    private readonly PblValidator _validator = new();

    // Fake in-memory persistence so pass 2 (formula fields + reports) sees what pass 1 actually
    // created, exactly like a real database would.
    private readonly List<AppTable> _capturedTables = [];
    private readonly Dictionary<long, List<AppField>> _fieldsByTable = new();
    private int _nextFid = 6;
    private int _nextFieldId = 100;

    private ImportAppFromPblCommandHandler CreateSut(PblPipelinesImporter? pipelinesImporter = null, ImportFieldNoteRecorder? fieldNoteRecorder = null)
    {
        var bulkCreateHandler = new BulkCreateFieldsCommandHandler(
            _tableRepo, _fieldRepo, _fieldTypeRepo, _schemaEngine, _queryContext, _auditRepo, _formRepo,
            new FieldSettingsValidatorRegistry(Array.Empty<IFieldSettingsValidator>()), _fieldNameResolver);

        var createAppHandler = new CreateAppCommandHandler(
            _appRepo, _appRoleRepo, _appUserRepo, _uow, _queryContext, _tableRepo, _auditRepo, _userRepo,
            _appSeeder, bulkCreateHandler);

        var formulaTranslator = new FormulaTranslator(new FormulaEngine());

        var createReportHandler = new CreateReportCommandHandler(
            _tableRepo, _fieldRepo, _reportRepo, _appUserRepo, _appRoleRepo, _queryContext, _auditRepo,
            new ReportConfigValidatorRegistry([new TableReportConfigValidator(), new SummaryReportConfigValidator(), new ChartReportConfigValidator()]));

        var relationshipFieldFactory = new PowerBase.Application.Relationships.RelationshipFieldFactory(
            _fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _fieldNameResolver);
        var createRelationshipHandler = new CreateRelationshipCommandHandler(
            _tableRepo, _fieldRepo, _fieldTypeRepo, _relRepo, relationshipFieldFactory, _auditRepo, _appRepo, NSubstitute.Substitute.For<PowerBase.Application.Common.Interfaces.ISchemaEngineService>(), NSubstitute.Substitute.For<PowerBase.Application.Common.Interfaces.IRecordRepository>(), NSubstitute.Substitute.For<PowerBase.Application.Formulas.IFormulaProjector>(), NSubstitute.Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>());

        var createFormHandler = new CreateFormCommandHandler(_tableRepo, _formRepo, _queryContext, _auditRepo);
        var saveFormLayoutHandler = new SaveFormLayoutCommandHandler(_formRepo, _fieldRepo, _queryContext, _auditRepo);
        var getFormLayoutHandler = new GetFormLayoutQueryHandler(_formRepo);
        var createFormRuleHandler = new CreateFormRuleCommandHandler(_formRepo, _formRuleRepo, _queryContext, _auditRepo);
        var saveFormRuleHandler = new SaveFormRuleCommandHandler(_formRuleRepo, _queryContext, _auditRepo, _formRepo, _fieldRepo, _formulaExpressionValidator);

        var createAppRoleHandler = new CreateAppRoleCommandHandler(_appRepo, _appRoleRepo, _queryContext, _auditRepo, _appRolePermissionRepo, _appUserRepo);
        var updateTablePermissionsHandler = new UpdateTablePermissionsCommandHandler(_appRoleRepo, _appRolePermissionRepo, _tableRepo, _auditRepo, _queryContext, _appUserRepo);

        var deleteAppHandler = new DeleteAppCommandHandler(_appRepo, _auditRepo);

        var variableRepo = Substitute.For<IAppVariableRepository>();
        var accessService = Substitute.For<IAppAccessService>();
        var extrasImporter = new PblExtrasImporter(
            new PowerBase.Application.Apps.Commands.CreateAppVariable.CreateAppVariableCommandHandler(_appRepo, variableRepo, accessService),
            new PowerBase.Application.Apps.Commands.UpdateAppVariable.UpdateAppVariableCommandHandler(_appRepo, variableRepo, accessService),
            new PowerBase.Application.Pages.Commands.CreatePage.CreatePageCommandHandler(
                _appRepo, _appRoleRepo, _appUserRepo, Substitute.For<IPageRepository>(), _queryContext, _auditRepo, Substitute.For<ITenantRepository>()),
            _appRepo, variableRepo);
        var sourceRefRecorder = new ImportSourceRefRecorder(
            _appRepo, _tableRepo, _fieldRepo, _reportRepo, _formRepo, _appRoleRepo, variableRepo,
            Substitute.For<IPageRepository>(), Substitute.For<IImportSourceRefRepository>());

        return new ImportAppFromPblCommandHandler(
            _validator, createAppHandler, deleteAppHandler, _appRepo, _tableRepo, _fieldRepo,
            _formRepo, _reportRepo, _appRoleRepo, bulkCreateHandler,
            formulaTranslator, createReportHandler, createRelationshipHandler, createFormHandler, saveFormLayoutHandler,
            getFormLayoutHandler, createFormRuleHandler, saveFormRuleHandler,
            createAppRoleHandler, updateTablePermissionsHandler, extrasImporter, sourceRefRecorder,
            new PblDeferredResolver(null!, null!, _fieldRepo),
            new ImportFieldSanitizer(new FieldSettingsValidatorRegistry(Array.Empty<IFieldSettingsValidator>())),
            // The settings applier drives other handlers (update app/table/field, set key); it has its own tests — here it is a no-op.
            Substitute.For<PblSettingsApplier>(new object?[] { null, null, null, null, null, null, null, null, null, null }),
            Substitute.For<PblPermissionsApplier>(new object?[] { null, null, null, null, null, null, null }),
            connectedTablesApplier: null,
            pipelinesImporter: pipelinesImporter,
            fieldNoteRecorder: fieldNoteRecorder);
    }

    private const string QblWithPipeline = """
        Version: 0.12
        Resources:
          $App_Ops:
            Type: QB::Application
            Properties:
              Name: Ops
            Tables:
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
          $Pipeline_Wait:
            Type: QB::Pipeline
            Properties:
              Name: Wait
            Steps:
              - Type: QB::PipelineStep::Clock
                Properties:
                  Id: aa
                  Type: pipe
                  Name: Pause
                  Channel: { Resource: timer, Id: clock, Selector: pause }
                InputsMeta:
                  $PipelineStep_InputsMeta_aa:
                    Type: QB::PipelineStep::InputsMeta
                    Properties:
                      Duration: 1 s
        """;

    private static PblPipelinesImporter PipelinesImporter() =>
        Substitute.For<PblPipelinesImporter>(new object?[] { null, null, null, null, null, null, null, null, null });

    [Fact]
    public async Task HandleBatchAsync_QblWithPipeline_ImportsThePipelineAfterTheApp()
    {
        var importer = PipelinesImporter();
        var sut = CreateSut(importer);

        var batch = await sut.HandleBatchAsync(new ImportAppFromPblCommand(QblWithPipeline));

        batch.Apps.Should().ContainSingle(a => a.AppName == "Ops");
        await importer.Received(1).ImportAsync(
            Arg.Is<IReadOnlyList<PowerBase.Application.Import.Qbl.ParsedApp>>(apps => apps.Single().Pipelines!.Single().Name == "Wait"),
            Arg.Is<IReadOnlyList<(PowerBase.Application.Import.Qbl.ParsedApp App, ImportReport Report)>>(imported => imported.Single().Report == batch.Apps[0]),
            Arg.Any<CancellationToken>(), false);
    }

    [Fact]
    public async Task HandleBatchAsync_ActivatePipelinesInTheMapping_IsPassedToThePipelineImporter()
    {
        var importer = PipelinesImporter();
        var sut = CreateSut(importer);

        await sut.HandleBatchAsync(new ImportAppFromPblCommand(QblWithPipeline, Mapping: new PowerBase.Application.Import.Mapping.ImportMapping { ActivatePipelines = true }));

        await importer.Received(1).ImportAsync(Arg.Any<IReadOnlyList<PowerBase.Application.Import.Qbl.ParsedApp>>(),
            Arg.Any<IReadOnlyList<(PowerBase.Application.Import.Qbl.ParsedApp App, ImportReport Report)>>(), Arg.Any<CancellationToken>(), true);
    }

    [Fact]
    public async Task HandleBatchAsync_QblWithoutPipelines_NeverCallsThePipelineImporter()
    {
        var importer = PipelinesImporter();
        var sut = CreateSut(importer);

        var batch = await sut.HandleBatchAsync(new ImportAppFromPblCommand(QblWithPipeline[..QblWithPipeline.IndexOf("  $Pipeline_Wait:", StringComparison.Ordinal)]));

        batch.Apps.Should().ContainSingle();
        batch.Apps[0].Pipelines.Should().BeEmpty();
        await importer.DidNotReceiveWithAnyArgs().ImportAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleBatchAsync_PipelineImportFails_TheAppIsStillImported()
    {
        var importer = PipelinesImporter();
        importer.ImportAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("boom")));
        var sut = CreateSut(importer);

        var batch = await sut.HandleBatchAsync(new ImportAppFromPblCommand(QblWithPipeline));

        batch.Apps.Should().ContainSingle(a => a.AppName == "Ops");
        batch.Failed.Should().BeEmpty();
        batch.Apps[0].Skipped.Should().Contain(s => s.Name == "PowerFlows" && s.Reason.Contains("boom"));
        await _appRepo.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    public ImportAppFromPblCommandHandlerTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _userRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, PublicId = Guid.NewGuid(), Name = "Test User", Email = "test@example.com" });

        _appRepo.NameExistsAsync(Arg.Any<string>()).Returns(false);
        _appRepo.CreateAsync(Arg.Any<App>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 10L));
        _appRepo.GetIdByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(10L);
        _appRoleRepo.CreateAsync(Arg.Any<AppRole>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns((1L, Guid.NewGuid()));

        _tableRepo.NameExistsInAppAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _appSeeder.CreateTableWithDefaultsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(ci =>
            {
                var t = ci.Arg<AppTable>();
                t.Id = 20 + _capturedTables.Count;
                t.PublicId = Guid.NewGuid();
                t.PhysicalTableName = $"t_{t.Id}";
                _capturedTables.Add(t);
                return t;
            });

        _tableRepo.ListByAppAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<AppTable>)_capturedTables);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => _capturedTables.First(t => t.PublicId == ci.Arg<Guid>()));

        // BulkCreateFieldsCommandHandler's own dependencies
        _fieldRepo.NameExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new FieldType { Id = 1, Code = ci.Arg<string>() });
        _fieldRepo.GetNextFidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(_ => _nextFid++);
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var f = ci.Arg<AppField>();
                f.Id = _nextFieldId++;
                f.PublicId = Guid.NewGuid();
                if (!_fieldsByTable.TryGetValue(f.AppTableId, out var list))
                    _fieldsByTable[f.AppTableId] = list = [];
                list.Add(f);
                return (f.Id, f.PublicId);
            });
        _fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<AppField>)(_fieldsByTable.TryGetValue(ci.Arg<long>(), out var list) ? list : []));
        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Form>());

        // CreateReportCommandHandler's own dependency
        _reportRepo.CreateAsync(Arg.Any<Report>(), Arg.Any<CancellationToken>())
            .Returns((1L, Guid.NewGuid()));
    }

    private const string ValidPbl = """
    {
      "version": "1.0",
      "app": { "logicalRef": "$App_Test", "name": "Imported App" },
      "tables": [
        {
          "logicalRef": "$Table_Clients",
          "name": "Clients",
          "fields": [
            { "logicalRef": "$Field_Clients_Name", "name": "Company Name", "typeCode": "Text" },
            { "logicalRef": "$Field_Clients_Balance", "name": "Balance", "typeCode": "Currency" },
            { "logicalRef": "$Field_Clients_Legacy", "name": "Legacy Score", "typeCode": "SomeUnsupportedType" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task HandleAsync_ValidPbl_CreatesAppAndReportsCountsAndSkipped()
    {
        var sut = CreateSut();

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(ValidPbl));

        report.AppName.Should().Be("Imported App");
        report.TablesCreated.Should().Be(1);
        report.FieldsCreated.Should().Be(3); // the unsupported type is kept as a plain Text field, never dropped
        report.Skipped.Should().ContainSingle(s => s.Name == "Legacy Score" && s.LogicalRef == "$Field_Clients_Legacy" && s.Reason.Contains("placeholder Text field"));
    }

    [Fact]
    public async Task HandleAsync_ValidPbl_DelegatesToCreateAppWithTranslatedTablesAndFields()
    {
        var sut = CreateSut();

        await sut.HandleAsync(new ImportAppFromPblCommand(ValidPbl));

        await _appRepo.Received(1).CreateAsync(
            Arg.Is<App>(a => a.Name == "Imported App"),
            Arg.Any<System.Data.IDbTransaction?>(),
            Arg.Any<CancellationToken>());
        await _fieldRepo.Received(3).CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>());
    }

    private const string LabelClashPbl = """
    {
      "version": "1.0",
      "app": { "logicalRef": "$App_Test", "name": "Imported App" },
      "tables": [
        {
          "logicalRef": "$Table_Logs",
          "name": "Logs",
          "fields": [
            { "logicalRef": "$Field_Logs_Created", "name": "Date Created", "typeCode": "DateTime" },
            { "logicalRef": "$Field_Logs_Title", "name": "Title", "typeCode": "Text" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task HandleAsync_FieldLabelClashingWithSystemField_IsNumberedNotRejected()
    {
        var sut = CreateSut();

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(LabelClashPbl));

        report.FieldsCreated.Should().Be(2);
        report.Skipped.Should().Contain(s => s.LogicalRef == "$Field_Logs_Created" && s.Reason.Contains("Date Created (2)"));
    }

    [Fact]
    public async Task HandleAsync_MalformedJson_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new ImportAppFromPblCommand("{ not valid json")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_InvalidDocument_ThrowsValidationException()
    {
        var sut = CreateSut();
        const string missingAppName = """{ "version": "1.0", "app": { "logicalRef": "$App_X", "name": "" }, "tables": [] }""";

        await sut.Invoking(s => s.HandleAsync(new ImportAppFromPblCommand(missingAppName)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_UnsupportedMode_ThrowsBadRequestException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new ImportAppFromPblCommand(ValidPbl, (ImportMode)999)))
            .Should().ThrowAsync<BadRequestException>();
    }

    private const string PblWithFormulaAndReport = """
    {
      "version": "1.0",
      "app": { "logicalRef": "$App_Test", "name": "Imported App" },
      "tables": [
        {
          "logicalRef": "$Table_Clients",
          "name": "Clients",
          "fields": [
            { "logicalRef": "$Field_Qty", "name": "Qty", "typeCode": "Number" },
            { "logicalRef": "$Field_Price", "name": "Price", "typeCode": "Currency" },
            { "logicalRef": "$Field_Total", "name": "Total", "typeCode": "Formula", "resultType": "Number", "formulaExpression": "[Qty] * [Price]" }
          ],
          "reports": [
            { "logicalRef": "$Report_ByTotal", "name": "By Total", "columns": ["Qty", "Price", "Total"], "sortFields": [{ "fieldName": "Total", "desc": true }] }
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task HandleAsync_CleanFormula_CreatesFormulaFieldAfterScalarFields()
    {
        var sut = CreateSut();

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(PblWithFormulaAndReport));

        report.FieldsCreated.Should().Be(3); // Qty, Price, Total
        report.Skipped.Should().BeEmpty();
        report.FormulaTranslations.Should().ContainSingle(f => f.Name == "Total" && f.Status == "Clean");

        await _fieldRepo.Received(1).CreateAsync(
            Arg.Is<AppField>(f => f.Name == "Total" && f.TypeCode == "Formula_Number"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ReportWithValidColumns_ResolvesFieldNamesToFids()
    {
        var sut = CreateSut();
        Report? capturedReport = null;
        _reportRepo.CreateAsync(Arg.Any<Report>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedReport = ci.Arg<Report>();
                return (1L, Guid.NewGuid());
            });

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(PblWithFormulaAndReport));

        report.ReportsCreated.Should().Be(1);
        capturedReport.Should().NotBeNull();

        var definition = JsonSerializer.Deserialize<ReportDefinition>(capturedReport!.Definition, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        definition.Columns.Should().HaveCount(3);
        definition.SortFields.Should().ContainSingle(s => s.Desc);
    }

    private const string PblWithDefaultReportSettings = """
    {
      "version": "1.0",
      "app": { "logicalRef": "$App_Test", "name": "Imported App" },
      "tables": [
        {
          "logicalRef": "$Table_Clients",
          "name": "Clients",
          "fields": [
            { "logicalRef": "$Field_Qty", "name": "Qty", "typeCode": "Number" },
            { "logicalRef": "$Field_Price", "name": "Price", "typeCode": "Currency" },
            { "logicalRef": "$Field_Total", "name": "Total", "typeCode": "Formula", "resultType": "Number", "formulaExpression": "[Qty] * [Price]" }
          ],
          "defaultReportSettings": {
            "columnFieldNames": ["Qty", "Total", "Gone"],
            "dynamicFilterType": "Custom",
            "dynamicFilterFieldNames": ["Price"],
            "allowQuickSearch": false,
            "sortFieldName": "Total",
            "sortDescending": true
          }
        }
      ]
    }
    """;

    [Fact]
    public async Task HandleAsync_DefaultReportSettings_AreWrittenToTheTablesDefaultSettingsRecord()
    {
        var sut = CreateSut();
        var record = new Report { PublicId = Guid.NewGuid(), Name = "Default Report Settings", Visibility = "Shared", Definition = "{}", IsDefaultSettingsRecord = true };
        _reportRepo.GetDefaultSettingsRecordAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(record);
        string? savedDefinition = null;
        _reportRepo.UpdateAsync(record.PublicId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Do<string>(d => savedDefinition = d), Arg.Any<CancellationToken>())
            .Returns(1);

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(PblWithDefaultReportSettings));

        savedDefinition.Should().NotBeNull();
        var definition = JsonSerializer.Deserialize<ReportDefinition>(savedDefinition!)!;
        var fidOf = (string name) => _fieldsByTable.Values.SelectMany(f => f).Single(f => f.Name == name).Fid!.Value;
        definition.Columns.Should().Equal(fidOf("Qty"), fidOf("Total"));
        definition.ColumnsMode.Should().Be("Custom");
        definition.DynamicFilterType.Should().Be("Custom");
        definition.CustomDynamicFilterFields.Should().Equal(fidOf("Price"));
        definition.AllowQuickSearch.Should().BeFalse();
        definition.SortFields.Should().ContainSingle(s => s.FieldId == fidOf("Total") && s.Desc);
        report.Skipped.Should().ContainSingle(s => s.Reason.Contains("'Gone'"));
    }

    [Fact]
    public async Task HandleAsync_NoDefaultReportSettingsInTheFile_LeavesTheDefaultRecordAlone()
    {
        var sut = CreateSut();

        await sut.HandleAsync(new ImportAppFromPblCommand(PblWithFormulaAndReport));

        await _reportRepo.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private const string PblWithFormulaReferencingUnknownField = """
    {
      "version": "1.0",
      "app": { "logicalRef": "$App_Test", "name": "Imported App" },
      "tables": [
        {
          "logicalRef": "$Table_Clients",
          "name": "Clients",
          "fields": [
            { "logicalRef": "$Field_Qty", "name": "Qty", "typeCode": "Number" },
            { "logicalRef": "$Field_Total", "name": "Total", "typeCode": "Formula", "resultType": "Number", "formulaExpression": "[Qty] * [Missing]" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task HandleAsync_FormulaReferencingUnknownField_IsSkippedAsNeedsManualReview()
    {
        var sut = CreateSut();

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(PblWithFormulaReferencingUnknownField));

        // The formula does not compile, but it is still a formula field (as written, like Quickbase) - never a Text stand-in.
        report.FieldsCreated.Should().Be(2);
        var total = AllFields.Single(f => f.Name == "Total");
        total.TypeCode.Should().Be("Formula_Number");
        total.Settings.Should().Contain("[Missing]");
        report.Skipped.Should().ContainSingle(s => s.Name == "Total" && s.Reason.Contains("could not be compiled") && s.Reason.Contains("'Missing' does not exist on this table") && !s.Reason.Contains("placeholder"));
        report.FormulaTranslations.Should().ContainSingle(f => f.Name == "Total" && f.Status == "NeedsManualReview");
    }

    // ── Import notes: no field is lost, the original is kept whole, one failure never fails the app ──

    private (ImportFieldNoteRecorder Recorder, List<AppFieldImportNote> Stored) NoteRecorder()
    {
        var noteRepo = Substitute.For<IAppFieldImportNoteRepository>();
        var stored = new List<AppFieldImportNote>();
        noteRepo.AddManyAsync(Arg.Do<IReadOnlyList<AppFieldImportNote>>(n => stored.AddRange(n)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var versionRepo = Substitute.For<IFieldVersionRepository>();
        versionRepo.InsertStandaloneVersionAsync(Arg.Any<AppFieldVersion>(), Arg.Any<IReadOnlyList<PowerBase.Application.Fields.Versioning.FieldChangeEntry>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _versions.Add((ci.Arg<AppFieldVersion>(), ci.Arg<IReadOnlyList<PowerBase.Application.Fields.Versioning.FieldChangeEntry>>()));
                return Task.FromResult(_versions.Count);
            });
        var versionService = new PowerBase.Application.Fields.Versioning.FieldVersionService(versionRepo, _queryContext);
        return (new ImportFieldNoteRecorder(_appRepo, _tableRepo, _fieldRepo, noteRepo, versionService), stored);
    }

    /// <summary>The Import entries written to the fields' audit history.</summary>
    private readonly List<(AppFieldVersion Version, IReadOnlyList<PowerBase.Application.Fields.Versioning.FieldChangeEntry> Changes)> _versions = [];

    private IEnumerable<AppField> AllFields => _fieldsByTable.Values.SelectMany(f => f);

    // A formula far longer than a field description may hold (500), referencing a field that does not exist.
    private static readonly string LongBrokenFormula = "[Qty] * [Missing]" + string.Concat(Enumerable.Repeat(" + [Qty]", 120));

    private static string MixedPbl(string extraFields = "") => $$"""
    {
      "version": "1.0",
      "app": { "logicalRef": "$App_Test", "name": "Imported App" },
      "tables": [
        {
          "logicalRef": "$Table_Clients",
          "name": "Clients",
          "fields": [
            { "logicalRef": "$Field_Qty", "name": "Qty", "typeCode": "Number" },
            { "logicalRef": "$Field_Legacy", "name": "Legacy Score", "typeCode": "SomeUnsupportedType", "description": "Old score" },
            { "logicalRef": "$Field_Double", "name": "Double", "typeCode": "Formula", "resultType": "Number", "formulaExpression": "[Qty] * 2" },
            { "logicalRef": "$Field_Total", "name": "Total", "typeCode": "Formula", "resultType": "Number", "formulaExpression": "{{LongBrokenFormula}}", "description": "Grand total" }
            {{extraFields}}
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task HandleAsync_MixedSupportedAndUnsupportedFields_EveryFieldExistsAndTheOriginalIsKeptWhole()
    {
        var (recorder, stored) = NoteRecorder();
        var sut = CreateSut(fieldNoteRecorder: recorder);

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(MixedPbl()));

        // Every source field is in the app: Qty, the unsupported one (as Text), the good formula, and the broken formula (typed after its result).
        report.FieldsCreated.Should().Be(4);
        AllFields.Single(f => f.Name == "Qty").TypeCode.Should().Be("Number");
        AllFields.Single(f => f.Name == "Legacy Score").TypeCode.Should().Be("Text");
        AllFields.Single(f => f.Name == "Double").TypeCode.Should().StartWith("Formula");
        var total = AllFields.Single(f => f.Name == "Total");
        total.TypeCode.Should().Be("Formula_Number", "a formula that does not compile is still saved as a formula field, as written");
        total.Settings.Should().Contain("[Missing]");

        // The field's own description stays the user's; the warning is in the audit history.
        total.Description.Should().Be("Grand total");
        AllFields.Single(f => f.Name == "Legacy Score").Description.Should().Be("Old score");

        var totalNote = stored.Should().ContainSingle(n => n.AppFieldId == total.Id).Subject;
        totalNote.IsPlaceholder.Should().BeFalse("it is a real formula field, not a stand-in");
        totalNote.Code.Should().Be("FORMULA_SAVED_BROKEN");
        totalNote.OriginalDefinition.Should().Be(LongBrokenFormula, "the complete formula is kept, never truncated");
        totalNote.OriginalDefinition!.Length.Should().BeGreaterThan(500);
        totalNote.Severity.Should().Be(ImportNoteSeverities.Warning);
        totalNote.Message.Should().Contain("could not be compiled").And.Contain("'Missing' does not exist on this table");
        totalNote.RecommendedAction.Should().NotBeNullOrWhiteSpace();

        // The audit history of the field says it in plain words, with the complete formula in the change details.
        var history = _versions.Should().ContainSingle(v => v.Version.AppFieldId == total.Id).Subject;
        history.Version.ChangeType.Should().Be(FieldVersionChangeType.Import);
        history.Version.CommitMessage.Should().StartWith("Imported from Quickbase.").And.Contain("'Missing' does not exist on this table");
        history.Version.CommitMessage.Length.Should().BeLessThanOrEqualTo(500);
        history.Changes.Should().Contain(c => c.PropertyName == "Original Quickbase definition" && c.NewValue == LongBrokenFormula);

        var legacyNote = stored.Should().ContainSingle(n => n.AppFieldId == AllFields.Single(f => f.Name == "Legacy Score").Id && n.IsPlaceholder).Subject;
        legacyNote.SourceType.Should().Be("SomeUnsupportedType");

        // The good formula needs no stand-in and no note.
        stored.Should().NotContain(n => n.AppFieldId == AllFields.Single(f => f.Name == "Double").Id);
    }

    [Fact]
    public async Task HandleAsync_OneFormulaFieldCannotBeSaved_TheOtherFieldsAndTheAppSurvive()
    {
        var (recorder, stored) = NoteRecorder();
        var sut = CreateSut(fieldNoteRecorder: recorder);
        // "Bad" compiles, but PowerBase refuses to save it as a formula field.
        _fieldRepo.CreateAsync(Arg.Is<AppField>(f => f.Name == "Bad" && f.TypeCode.StartsWith("Formula")), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<(long, Guid)>(new InvalidOperationException("boom")));

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(MixedPbl(
            ", { \"logicalRef\": \"$Field_Bad\", \"name\": \"Bad\", \"typeCode\": \"Formula\", \"resultType\": \"Number\", \"formulaExpression\": \"[Qty] + 1\" }")));

        await _appRepo.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        AllFields.Should().Contain(f => f.Name == "Double" && f.TypeCode.StartsWith("Formula")); // the other formula is untouched
        var bad = AllFields.Single(f => f.Name == "Bad");
        bad.TypeCode.Should().Be("Number"); // kept as a stand-in
        stored.Should().Contain(n => n.AppFieldId == bad.Id && n.IsPlaceholder && n.OriginalDefinition == "[Qty] + 1");
        report.Skipped.Should().Contain(s => s.Name == "Bad" && s.Reason.Contains("boom"));
    }

    private const string QblWithEverythingUnsupported = """
        Version: '0.12'
        Resources:
          $App_Ops:
            Type: QB::Application
            Properties:
              Name: Ops
            Tables:
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
                      Formula: If([Title] = "a", "Long", "Short")
                  $Field_Orphan_Reference:
                    Type: QB::Field::Reference
                    Id: 34
                    Properties:
                      Label: Orphan Reference
                  $Field_Dead_Link:
                    Type: QB::Field::ReportLink
                    Id: 36
                    Properties:
                      Label: Dead Link
        """;

    [Fact]
    public async Task HandleBatchAsync_QblWithUnsupportedFields_ImportsEveryFieldAndStoresANoteForEachStandIn()
    {
        var (recorder, stored) = NoteRecorder();
        var sut = CreateSut(fieldNoteRecorder: recorder);

        var batch = await sut.HandleBatchAsync(new ImportAppFromPblCommand(QblWithEverythingUnsupported));

        batch.Failed.Should().BeEmpty();
        var names = AllFields.Select(f => f.Name).ToList();
        names.Should().Contain(["Title", "Legacy Link", "Choice Calc", "Orphan Reference", "Dead Link"]);

        foreach (var (name, fieldRef) in new[] { ("Legacy Link", "$Field_Legacy"), ("Choice Calc", "$Field_Choice_Calc"), ("Orphan Reference", "$Field_Orphan_Reference"), ("Dead Link", "$Field_Dead_Link") })
        {
            var field = AllFields.Single(f => f.Name == name);
            stored.Should().Contain(n => n.AppFieldId == field.Id && n.IsPlaceholder, $"'{name}' must carry an import note");
            batch.Apps[0].Skipped.Should().Contain(s => s.LogicalRef.EndsWith(fieldRef), $"'{name}' must be in the report");
        }

        var choice = stored.Single(n => n.AppFieldId == AllFields.Single(f => f.Name == "Choice Calc").Id);
        choice.OriginalDefinition.Should().Be("If([Title] = \"a\", \"Long\", \"Short\")");
        choice.OriginalMetadata.Should().Contain("QB::Field::TextMultipleChoice::Formula");
        batch.Apps[0].Issues.Summary.AffectedFieldCount.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task HandleAsync_DescriptionLongerThanAFieldHolds_IsCutKeptWholeInOneInformationNote()
    {
        var (recorder, stored) = NoteRecorder();
        var sut = CreateSut(fieldNoteRecorder: recorder);
        var longDescription = new string('d', 700);
        var pbl = $$"""
        { "version": "1.0", "app": { "logicalRef": "$App_Test", "name": "Imported App" },
          "tables": [ { "logicalRef": "$Table_T", "name": "T", "fields": [
            { "logicalRef": "$Field_A", "name": "Alpha", "typeCode": "Text", "description": "{{longDescription}}" } ] } ] }
        """;

        await sut.HandleAsync(new ImportAppFromPblCommand(pbl));

        var alpha = AllFields.Single(f => f.Name == "Alpha");
        alpha.Description!.Length.Should().BeLessThanOrEqualTo(500);
        var note = stored.Should().ContainSingle(n => n.AppFieldId == alpha.Id, "the cut text is one note, not the note plus a duplicate warning").Subject;
        note.Severity.Should().Be(ImportNoteSeverities.Information);
        note.OriginalDefinition.Should().Contain(longDescription);
    }

    [Fact]
    public async Task HandleAsync_NoteStorageFails_TheImportStillSucceedsAndSaysSo()
    {
        var noteRepo = Substitute.For<IAppFieldImportNoteRepository>();
        noteRepo.AddManyAsync(default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("no table")));
        var sut = CreateSut(fieldNoteRecorder: new ImportFieldNoteRecorder(_appRepo, _tableRepo, _fieldRepo, noteRepo));

        var report = await sut.HandleAsync(new ImportAppFromPblCommand(MixedPbl()));

        await _appRepo.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        report.FieldsCreated.Should().Be(4);
        report.Skipped.Should().Contain(s => s.Code == "FIELD_NOTES_NOT_STORED" && s.Reason.Contains("no table"));
    }
}
