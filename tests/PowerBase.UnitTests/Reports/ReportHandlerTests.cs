using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Reports.Validation;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

using Microsoft.Extensions.Logging;
namespace PowerBase.UnitTests.Reports;

public class ReportHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly PowerBase.Application.Relationships.IRelationalProjector _relationalProjector = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();
    private readonly IAzureSearchService _searchService = Substitute.For<IAzureSearchService>();

    private static AppTable MakeTable(long id = 5) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = "T",
        PhysicalTableName = PhysicalNaming.TableName(id),
    };

    private static AppField MakeField(long id = 1, bool reportable = true) => new()
    {
        Id = id, Fid = (int)id, Name = $"Field{id}", TypeCode = "Text", IsReportable = reportable,
    };

    private static Report MakeReport(long tableId, List<long>? columns = null, string reportType = "Table") => new()
    {
        Id = 10,
        PublicId = Guid.NewGuid(),
        AppTableId = tableId,
        Name = "My Report",
        ReportType = reportType,
        Visibility = "Personal",
        Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = columns ?? [] }),
        CreatedOn = DateTime.UtcNow,
    };

    // Real (non-mocked) validator registry — exercises the actual per-type validation rules
    // instead of stubbing them out, same as production DI wiring in Program.cs.
    private static ReportConfigValidatorRegistry MakeConfigValidatorRegistry() =>
        new([new TableReportConfigValidator(), new SummaryReportConfigValidator(), new ChartReportConfigValidator()]);

    // Helper to build a CreateReportCommand with all required new fields defaulted
    private static CreateReportCommand MakeCreateCommand(
        Guid tableId, string name = "My Report", string visibility = "Personal",
        string reportType = "Table", List<long>? columns = null)
        => new(tableId, name, null, visibility, reportType,
            columns ?? [], [], null,
            null, "EqualValues", false, false, false, [], "Default", [], null, true, []);

    public ReportHandlerTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _appUserRepo.GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.ReportsRead, PermissionCodes.ReportsCreate, PermissionCodes.ReportsUpdate });

        // Default: no relationship fields — an empty computed-value map per row.
        _relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<IReadOnlyDictionary<long, object?>>)ci.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
    }

    // --- CreateReportCommandHandler ---

    [Fact]
    public async Task CreateReport_ValidCommand_ReturnsReportDetail()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _reportRepo.CreateAsync(Arg.Any<Report>()).Returns((1L, Guid.NewGuid()));
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo, MakeConfigValidatorRegistry());

        var result = await sut.HandleAsync(MakeCreateCommand(table.PublicId));

        result.Name.Should().Be("My Report");
        result.Visibility.Should().Be("Personal");
        result.ReportType.Should().Be("Table");
    }

    [Fact]
    public async Task CreateReport_UnknownColumnFieldId_ThrowsValidationException()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { MakeField(1) });
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo, MakeConfigValidatorRegistry());

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(table.PublicId, columns: [999L])))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateReport_InvalidVisibility_ThrowsValidationException()
    {
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo, MakeConfigValidatorRegistry());

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(Guid.NewGuid(), visibility: "Invalid")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateReport_EmptyName_ThrowsValidationException()
    {
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo, MakeConfigValidatorRegistry());

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(Guid.NewGuid(), name: "")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateReport_InvalidReportType_ThrowsValidationException()
    {
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo, MakeConfigValidatorRegistry());

        // "GridEdit" was removed as a report type (now a session-only client-side toggle on Table
        // reports, not a persisted type) — no validator is registered for it, so it's rejected.
        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(Guid.NewGuid(), reportType: "GridEdit")))
            .Should().ThrowAsync<ValidationException>();

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(Guid.NewGuid(), reportType: "InvalidType")))
            .Should().ThrowAsync<ValidationException>();
    }

    // --- GetReportQueryHandler ---

    [Fact]
    public async Task GetReport_ReturnsReportDetailWithDeserializedDefinition()
    {
        var table = MakeTable();
        var report = MakeReport(table.Id, [1L, 2L]);
        _reportRepo.GetVisibleReportAsync(report.PublicId, Arg.Any<CancellationToken>()).Returns(report);
        var sut = new GetReportQueryHandler(_reportRepo);

        var result = await sut.HandleAsync(new GetReportQuery(report.PublicId));

        result.Id.Should().Be(report.PublicId);
        result.Name.Should().Be("My Report");
        result.Definition.Columns.Should().BeEquivalentTo(new[] { 1L, 2L });
    }

    // --- ListReportsQueryHandler ---

    [Fact]
    public async Task ListReports_ReturnsAllReportsForApp()
    {
        var app = new App { Id = 1, PublicId = Guid.NewGuid(), Name = "App" };
        var table = MakeTable();
        var reports = new List<Report> { MakeReport(table.Id), MakeReport(table.Id) };
        _appRepo.GetIdByPublicIdAsync(app.PublicId).Returns(app.Id);
        _reportRepo.ListByAppAsync(app.Id).Returns(reports);
        var sut = new ListReportsQueryHandler(_reportRepo, _appRepo, _tableRepo);

        var result = await sut.HandleAsync(new ListReportsQuery(app.PublicId));

        result.Should().HaveCount(2);
        result.All(r => r.Name == "My Report").Should().BeTrue();
    }

    // --- RunReportQueryHandler ---

    [Fact]
    public async Task RunReport_ExplicitColumns_ReturnsOnlySelectedFields()
    {
        var table = MakeTable();
        var field1 = MakeField(1);
        var field2 = MakeField(2);
        var report = MakeReport(table.Id, [1L]);
        var row = new Dictionary<string, object?>
        {
            ["PublicId"] = Guid.NewGuid(),
            ["CreatedOn"] = DateTime.UtcNow,
            [PhysicalNaming.ColumnName(1)] = "Alice",
        };
        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field1, field2 });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { row });
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(1);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = ci.Arg<IReadOnlyList<AppField>>().Where(f => !f.IsSystem).Select(f => f.Id).ToHashSet(),
            }));
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));

        result.TotalCount.Should().Be(1);
        result.Columns.Should().ContainSingle(c => c.FieldId == 1L);
        result.Columns.Should().NotContain(c => c.FieldId == 2L);
    }

    [Fact]
    public async Task RunReport_EmptyColumns_ReturnsAllReportableFields()
    {
        var table = MakeTable();
        var reportableField = MakeField(1, reportable: true);
        var nonReportableField = MakeField(2, reportable: false);
        var report = MakeReport(table.Id, []);
        var row = new Dictionary<string, object?>
        {
            ["PublicId"] = Guid.NewGuid(),
            ["CreatedOn"] = DateTime.UtcNow,
            [PhysicalNaming.ColumnName(1)] = "Data",
        };
        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { reportableField, nonReportableField });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { row });
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(1);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = ci.Arg<IReadOnlyList<AppField>>().Where(f => !f.IsSystem).Select(f => f.Id).ToHashSet(),
            }));
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));

        result.Columns.Should().ContainSingle(c => c.FieldId == 1L);
        result.Columns.Should().NotContain(c => c.FieldId == 2L);
    }

    [Fact]
    public async Task RunReport_QuickSearchFieldIds_RestrictsSearchToThoseFields()
    {
        var table = MakeTable();
        var field1 = new AppField { Id = 1, Fid = 1, Name = "Field1", TypeCode = "Text", IsReportable = true, IsSearchable = true };
        var field2 = new AppField { Id = 2, Fid = 2, Name = "Field2", TypeCode = "Text", IsReportable = true, IsSearchable = true };
        var report = MakeReport(table.Id, []);
        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field1, field2 });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(0);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = new HashSet<long>(),
            }));
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20, QuickSearch: "abc", QuickSearchFieldIds: [1]));

        await _recordRepo.Received(1).ListAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Is<FilterGroup?>(f => f != null && f.Nodes.Count == 1 && f.Nodes[0].Condition!.FieldId == 1),
            Arg.Any<IReadOnlyList<SortSpec>?>());
    }

    [Fact]
    public async Task RunReport_QuickSearchExact_UsesEqOperator()
    {
        var table = MakeTable();
        var field1 = new AppField { Id = 1, Fid = 1, Name = "Field1", TypeCode = "Text", IsReportable = true, IsSearchable = true };
        var report = MakeReport(table.Id, []);
        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field1 });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(0);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = new HashSet<long>(),
            }));
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20, QuickSearch: "abc", QuickSearchExact: true));

        await _recordRepo.Received(1).ListAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Is<FilterGroup?>(f => f != null && f.Nodes[0].Condition!.Operator == "eq"),
            Arg.Any<IReadOnlyList<SortSpec>?>());
    }

    [Fact]
    public async Task RunReport_QuickSearch_FallbackToSql_IncludesNonComputedPhysicalFields()
    {
        var table = MakeTable();
        var textfield = new AppField { Id = 1, Fid = 1, Name = "Name", TypeCode = "Text", IsReportable = true, IsSearchable = false };
        var dateField = new AppField { Id = 2, Fid = 2, Name = "CreatedOn", TypeCode = "DateTime", IsSystem = true, IsReportable = true, IsSearchable = false };
        var report = MakeReport(table.Id, []);
        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { textfield, dateField });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(0);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = new HashSet<long>(),
            }));
        _searchService.IsGridSearchEnabled.Returns(false);

        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20, QuickSearch: "nira"));

        await _recordRepo.Received(1).ListAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Is<FilterGroup?>(f => f != null && f.Logic == "or" && f.Nodes.Count == 2),
            Arg.Any<IReadOnlyList<SortSpec>?>());
    }

    [Fact]
    public async Task RunReport_ReportBuilderWithoutTableAccess_MasksDataAndReturnsCountAndColumns()
    {
        var table = MakeTable();
        var field1 = MakeField(1);
        var report = MakeReport(table.Id, [1L]);
        var row = new Dictionary<string, object?>
        {
            ["PublicId"] = Guid.NewGuid(),
            ["CreatedOn"] = DateTime.UtcNow,
            [PhysicalNaming.ColumnName(1)] = "ConfidentialSalary$100k",
        };

        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field1 });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(), restrictToCreatedBy: Arg.Any<long?>(), ct: Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { row });
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(1);

        // Access enforcer returns CanView = false (ViewScope = None)
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.None,
                ModifyScope = RecordScopes.None,
                VisibleFields = [],
                EditableFieldIds = new HashSet<long>(),
            }));

        // User has report builder capability
        _appUserRepo.GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.ReportsCreate });

        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));

        result.IsDataMasked.Should().BeTrue();
        result.TotalCount.Should().Be(1);
        result.Columns.Should().ContainSingle(c => c.FieldId == 1L);
        // After fix: Table-type masked preview must return EMPTY Items — requirement says
        // "row counts and aggregate outputs only — never raw record data".
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RunReport_ReportsReadOnlyUser_WithoutTableAccess_ReturnsEmptyResult()
    {
        // Arrange: user has ONLY reports:read (viewer-level), NOT reports:create/update.
        // With no table access, they are NOT a report builder — should get empty result,
        // NOT masked preview.
        var table = MakeTable();
        var field1 = MakeField(1);
        var report = MakeReport(table.Id, [1L]);

        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field1 });

        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.None,
                ModifyScope = RecordScopes.None,
                VisibleFields = [],
                EditableFieldIds = new HashSet<long>(),
            }));

        // Only ReportsRead — NOT a builder
        _appUserRepo.GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.ReportsRead });

        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));

        // Must be completely empty — no masked preview, no data
        result.IsDataMasked.Should().BeFalse();
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.Columns.Should().BeEmpty();
    }

    [Fact]
    public async Task RunReport_TableType_MaskedPreview_ReturnsOnlyCountAndColumns_NoRows()
    {
        // Arrange: genuine report builder (has reports:create) with NO table data access.
        // Must receive count + column structure ONLY — zero individual rows.
        var table = MakeTable();
        var field1 = MakeField(1);
        var report = MakeReport(table.Id, [1L], reportType: "Table");
        var row = new Dictionary<string, object?>
        {
            ["PublicId"] = Guid.NewGuid(),
            ["CreatedOn"] = DateTime.UtcNow,
            [PhysicalNaming.ColumnName(1)] = "SensitivePayrollData",
        };

        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field1 });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(), restrictToCreatedBy: Arg.Any<long?>(), ct: Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { row });
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(42);

        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.None,
                ModifyScope = RecordScopes.None,
                VisibleFields = [],
                EditableFieldIds = new HashSet<long>(),
            }));

        _appUserRepo.GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.ReportsCreate });

        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));

        result.IsDataMasked.Should().BeTrue();
        result.TotalCount.Should().Be(42);            // count is exposed so builder can see scale
        result.Columns.Should().ContainSingle(c => c.FieldId == 1L); // structure visible
        result.Items.Should().BeEmpty();              // ZERO individual rows — no raw data at all
    }

    // --- What a table report reads and projects ---

    private static AppField Typed(int fid, string typeCode) => new()
    {
        Id = fid, Fid = fid, Name = $"F{fid}", TypeCode = typeCode, IsReportable = true,
    };

    /// <summary>Runs a table report over <paramref name="fields"/> showing <paramref name="columns"/>; returns the field lists
    /// the relational projector was given.</summary>
    private async Task<List<IReadOnlyList<AppField>>> RunAndCaptureProjection(
        List<AppField> fields, List<long> columns, RunReportQuery? query = null)
    {
        var table = MakeTable();
        var report = MakeReport(table.Id, columns);
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1L, ["PublicId"] = Guid.NewGuid(), ["CreatedOn"] = DateTime.UtcNow },
        };
        _reportRepo.GetVisibleReportAsync(Arg.Any<Guid>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(fields);
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20, Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(rows);
        _recordRepo.ListAllMatchingAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
                Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(rows);
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = new HashSet<long>(),
            }));
        var projected = new List<IReadOnlyList<AppField>>();
        _relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Do<IReadOnlyList<AppField>>(projected.Add),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(2)
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());

        await sut.HandleAsync(query ?? new RunReportQuery(report.PublicId, 1, 20));
        return projected;
    }

    [Fact]
    public async Task RunReport_ProjectsOnlyTheSummariesItShowsOrSortsBy()
    {
        // Summaries 2 and 3 are each a query per page; only 3 is a column, so 2 is never worked out.
        var projected = await RunAndCaptureProjection([Typed(1, "Text"), Typed(2, "Summary"), Typed(3, "Summary"), Typed(4, "Lookup")], [1L, 3L]);

        projected.Should().ContainSingle();
        projected[0].Select(f => f.Fid).Should().Contain([1, 3]).And.NotContain([2, 4]);
    }

    [Fact]
    public async Task RunReport_ProjectsTheSummaryAFormulaColumnReads()
    {
        _formulaProjector.GetFormulaDependencies(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IEnumerable<long>>())
            .Returns(new HashSet<long> { 5, 2 });

        var projected = await RunAndCaptureProjection([Typed(1, "Text"), Typed(2, "Summary"), Typed(3, "Summary"), Typed(5, "Formula_Number")], [1L, 5L]);

        projected[0].Select(f => f.Fid).Should().Contain(2).And.NotContain(3);
    }

    [Fact]
    public async Task RunReport_ProjectsEverythingWhenAFormulasReadsCannotBeListed()
    {
        _formulaProjector.GetFormulaDependencies(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IEnumerable<long>>())
            .Returns((IReadOnlySet<long>?)null);

        var projected = await RunAndCaptureProjection([Typed(1, "Text"), Typed(2, "Summary"), Typed(5, "Formula_Number")], [1L, 5L]);

        projected[0].Select(f => f.Fid).Should().Contain([1, 2, 5]);
    }

    [Fact]
    public async Task RunReport_ReadsOnlyTheColumnsItUses()
    {
        // Shown: Name (1), Customer (7, a Reference converted from text: falls back to its original, 8) and a lookup (9)
        // through Order (10). Notes (6) isn't used anywhere, so its column isn't read or decrypted.
        var customer = Typed(7, "Reference");
        customer.Settings = """{"parentTableId":99,"originalFid":8}""";
        var lookup = Typed(9, "Lookup");
        lookup.Settings = """{"referenceFid":10,"sourceTableId":98,"sourceFid":5}""";
        await RunAndCaptureProjection([Typed(1, "Text"), Typed(6, "Text"), customer, Typed(8, "Text"), lookup, Typed(10, "Reference")], [1L, 7L, 9L]);

        await _recordRepo.Received(1).ListAsync(Arg.Any<AppTable>(),
            Arg.Is<IReadOnlyList<AppField>>(fs => fs.Select(f => f.Fid!.Value).OrderBy(fid => fid).SequenceEqual(new[] { 1, 7, 8, 9, 10 })),
            1, 20, Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>());
    }

    [Fact]
    public async Task RunReport_WithAColumnWhoseReadsAreUnknown_ReadsEveryColumn()
    {
        // A Report Link isn't a formula: what it reads from the row can't be listed, so every column is read.
        await RunAndCaptureProjection([Typed(1, "Text"), Typed(6, "Text"), Typed(11, "ReportLink")], [1L, 11L]);

        await _recordRepo.Received(1).ListAsync(Arg.Any<AppTable>(),
            Arg.Is<IReadOnlyList<AppField>>(fs => fs.Any(f => f.Fid == 6)), 1, 20, Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>());
    }

    [Fact]
    public async Task RunReport_SortByASummary_ReadsEveryRecordAndProjectsIt()
    {
        var projected = await RunAndCaptureProjection([Typed(1, "Text"), Typed(2, "Summary")], [1L, 2L],
            query: new RunReportQuery(Guid.NewGuid(), 1, 20, RuntimeSortFieldId: 2));

        await _recordRepo.Received(1).ListAllMatchingAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        projected[0].Select(f => f.Fid).Should().Contain(2);
    }

    /// <summary>The formula projector as the real one treats the relational values: merged into what it returns.</summary>
    private void FormulaProjectorReturnsRelational() =>
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(2)
                ?? ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1).Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());

    [Fact]
    public async Task RunReport_SortByASummary_WorksOutTheSummariesItOnlyShowsForThePageAlone()
    {
        // Sorting by Total (2) needs it for every record read; Names (3, a Combined Text say) is only shown, so it is
        // worked out for the page's rows — not for every record, which could be 50,000 of them.
        FormulaProjectorReturnsRelational();

        var projected = await RunAndCaptureProjection([Typed(1, "Text"), Typed(2, "Summary"), Typed(3, "Summary")], [1L, 2L, 3L],
            query: new RunReportQuery(Guid.NewGuid(), 1, 20, RuntimeSortFieldId: 2));

        projected.Should().HaveCount(2);
        projected[0].Select(f => f.Fid).Should().Contain(2).And.NotContain(3);
        projected[1].Select(f => f.Fid).Should().Equal(3);
    }

    [Fact]
    public async Task RunReport_FilterOnASummary_WorksOutOnlyThatSummaryForEveryRecord()
    {
        FormulaProjectorReturnsRelational();
        var filter = new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 2, Operator = "gt", Value = "0" } }] };

        var projected = await RunAndCaptureProjection([Typed(1, "Text"), Typed(2, "Summary"), Typed(3, "Summary")], [1L, 2L, 3L],
            query: new RunReportQuery(Guid.NewGuid(), 1, 20, RuntimeFilterTree: filter));

        projected[0].Select(f => f.Fid).Should().Contain(2).And.NotContain(3);
    }

    [Fact]
    public async Task RunReport_SortByASummary_WithNothingElseToShow_ProjectsOnce()
    {
        FormulaProjectorReturnsRelational();

        var projected = await RunAndCaptureProjection([Typed(1, "Text"), Typed(2, "Summary")], [1L, 2L],
            query: new RunReportQuery(Guid.NewGuid(), 1, 20, RuntimeSortFieldId: 2));

        projected.Should().ContainSingle();
    }
}
