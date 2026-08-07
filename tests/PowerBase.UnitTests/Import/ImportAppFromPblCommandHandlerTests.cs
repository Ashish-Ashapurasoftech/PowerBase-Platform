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
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
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

    private ImportAppFromPblCommandHandler CreateSut()
    {
        var bulkCreateHandler = new BulkCreateFieldsCommandHandler(
            _tableRepo, _fieldRepo, _fieldTypeRepo, _schemaEngine, _queryContext, _auditRepo, _formRepo,
            new FieldSettingsValidatorRegistry(Array.Empty<IFieldSettingsValidator>()), _fieldNameResolver);

        var createAppHandler = new CreateAppCommandHandler(
            _appRepo, _appRoleRepo, _appUserRepo, _uow, _queryContext, _tableRepo, _auditRepo, _userRepo,
            _appSeeder, bulkCreateHandler);

        var formulaTranslator = new FormulaTranslator(new FormulaEngine());

        var createReportHandler = new CreateReportCommandHandler(
            _tableRepo, _fieldRepo, _reportRepo, _appUserRepo, _appRoleRepo, _queryContext, _auditRepo);

        var relationshipFieldFactory = new PowerBase.Application.Relationships.RelationshipFieldFactory(
            _fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _fieldNameResolver);
        var createRelationshipHandler = new CreateRelationshipCommandHandler(
            _tableRepo, _fieldRepo, _fieldTypeRepo, _relRepo, relationshipFieldFactory, _auditRepo, _appRepo);

        var createFormHandler = new CreateFormCommandHandler(_tableRepo, _formRepo, _queryContext, _auditRepo);
        var saveFormLayoutHandler = new SaveFormLayoutCommandHandler(_formRepo, _fieldRepo, _queryContext, _auditRepo);
        var getFormLayoutHandler = new GetFormLayoutQueryHandler(_formRepo);
        var createFormRuleHandler = new CreateFormRuleCommandHandler(_formRepo, _formRuleRepo, _queryContext, _auditRepo);
        var saveFormRuleHandler = new SaveFormRuleCommandHandler(_formRuleRepo, _queryContext, _auditRepo, _formRepo, _fieldRepo, _formulaExpressionValidator);

        var createAppRoleHandler = new CreateAppRoleCommandHandler(_appRepo, _appRoleRepo, _queryContext, _auditRepo, _appRolePermissionRepo);
        var updateTablePermissionsHandler = new UpdateTablePermissionsCommandHandler(_appRoleRepo, _appRolePermissionRepo, _tableRepo, _auditRepo);

        var deleteAppHandler = new DeleteAppCommandHandler(_appRepo, _auditRepo);

        return new ImportAppFromPblCommandHandler(
            _validator, createAppHandler, deleteAppHandler, _appRepo, _tableRepo, _fieldRepo,
            _formRepo, _reportRepo, _appRoleRepo, bulkCreateHandler,
            formulaTranslator, createReportHandler, createRelationshipHandler, createFormHandler, saveFormLayoutHandler,
            getFormLayoutHandler, createFormRuleHandler, saveFormRuleHandler,
            createAppRoleHandler, updateTablePermissionsHandler);
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

        _appSeeder.CreateTableWithDefaultsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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
        report.FieldsCreated.Should().Be(2);
        report.Skipped.Should().ContainSingle(s => s.Name == "Legacy Score" && s.LogicalRef == "$Field_Clients_Legacy");
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
        await _fieldRepo.Received(2).CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>());
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
            Arg.Is<AppField>(f => f.Name == "Total" && f.TypeCode == "Formula"),
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

        report.FieldsCreated.Should().Be(1); // only Qty; Total was never created
        report.Skipped.Should().ContainSingle(s => s.Name == "Total" && s.Reason.Contains("manual review"));
        report.FormulaTranslations.Should().ContainSingle(f => f.Name == "Total" && f.Status == "NeedsManualReview");

        await _fieldRepo.DidNotReceive().CreateAsync(
            Arg.Is<AppField>(f => f.TypeCode == "Formula"),
            Arg.Any<CancellationToken>());
    }
}
