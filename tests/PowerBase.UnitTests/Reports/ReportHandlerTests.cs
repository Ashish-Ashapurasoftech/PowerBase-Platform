using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

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
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly PowerBase.Application.Relationships.IRelationalProjector _relationalProjector = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();

    private static AppTable MakeTable(long id = 5) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = "T",
        PhysicalTableName = PhysicalNaming.TableName(id),
    };

    private static AppField MakeField(long id = 1, bool reportable = true) => new()
    {
        Id = id, Name = $"Field{id}", TypeCode = "Text", IsReportable = reportable,
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
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo);

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
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(table.PublicId, columns: [999L])))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateReport_InvalidVisibility_ThrowsValidationException()
    {
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(Guid.NewGuid(), visibility: "Invalid")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateReport_EmptyName_ThrowsValidationException()
    {
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(Guid.NewGuid(), name: "")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateReport_InvalidReportType_ThrowsValidationException()
    {
        var sut = new CreateReportCommandHandler(_tableRepo, _fieldRepo, _reportRepo, Substitute.For<IAppUserRepository>(), Substitute.For<IAppRoleRepository>(), _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(MakeCreateCommand(Guid.NewGuid(), reportType: "Chart")))
            .Should().ThrowAsync<ValidationException>();
    }

    // --- GetReportQueryHandler ---

    [Fact]
    public async Task GetReport_ReturnsReportDetailWithDeserializedDefinition()
    {
        var table = MakeTable();
        var report = MakeReport(table.Id, [1L, 2L]);
        _reportRepo.GetByPublicIdAsync(report.PublicId).Returns(report);
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
        _appRepo.GetByPublicIdAsync(app.PublicId).Returns(app);
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
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

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
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));

        result.Columns.Should().ContainSingle(c => c.FieldId == 1L);
        result.Columns.Should().NotContain(c => c.FieldId == 2L);
    }

    [Fact]
    public async Task RunReport_QuickSearchFieldIds_RestrictsSearchToThoseFields()
    {
        var table = MakeTable();
        var field1 = new AppField { Id = 1, Name = "Field1", TypeCode = "Text", IsReportable = true, IsSearchable = true };
        var field2 = new AppField { Id = 2, Name = "Field2", TypeCode = "Text", IsReportable = true, IsSearchable = true };
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
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

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
        var field1 = new AppField { Id = 1, Name = "Field1", TypeCode = "Text", IsReportable = true, IsSearchable = true };
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
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

        await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20, QuickSearch: "abc", QuickSearchExact: true));

        await _recordRepo.Received(1).ListAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
            Arg.Is<FilterGroup?>(f => f != null && f.Nodes[0].Condition!.Operator == "eq"),
            Arg.Any<IReadOnlyList<SortSpec>?>());
    }
}
