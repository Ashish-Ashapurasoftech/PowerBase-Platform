using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Reports;

public class AiSearchRoleFilterTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly PowerBase.Application.Relationships.IRelationalProjector _relationalProjector = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();
    private readonly IAzureSearchService _searchService = Substitute.For<IAzureSearchService>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly ILogger<RunReportQueryHandler> _logger = Substitute.For<ILogger<RunReportQueryHandler>>();

    private static AppTable MakeTable(long id = 42) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = "Projects",
        PhysicalTableName = PhysicalNaming.TableName(id)
    };

    private static List<AppField> MakeStandardFields() =>
    [
        new() { Id = 1, Fid = 1, Name = "CreatedOn", TypeCode = "Date", IsSystem = true },
        new() { Id = 2, Fid = 2, Name = "ModifiedOn", TypeCode = "Date", IsSystem = true },
        new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
        new() { Id = 4, Fid = 4, Name = "CreatedBy", TypeCode = "Number", IsSystem = true },
        new() { Id = 103, Fid = 103, Name = "Department", TypeCode = "Text", IsReportable = true, IsSearchable = true, IsFilterable = true, SearchSlot = "str_01" },
        new() { Id = 104, Fid = 104, Name = "Budget", TypeCode = "Number", IsReportable = true, IsSearchable = true, IsFilterable = true, SearchSlot = "num_01" }
    ];

    [Fact]
    public void ExtractFieldValuesByFid_MapsNonNumericPhysicalKeys_Correctly()
    {
        var fields = MakeStandardFields();
        var row = new Dictionary<string, object?>
        {
            ["f_103"] = "Engineering",
            ["f_104"] = 50000m,
            ["Id"] = 999L,
            ["CreatedBy"] = 12L,
            ["CreatedOn"] = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };

        var extracted = RunReportQueryHandler.ExtractFieldValuesByFid(row, fields);

        extracted.Should().ContainKey(103).WhoseValue.Should().Be("Engineering");
        extracted.Should().ContainKey(104).WhoseValue.Should().Be(50000m);
        extracted.Should().ContainKey(3).WhoseValue.Should().Be(999L);
        extracted.Should().ContainKey(4).WhoseValue.Should().Be(12L);
        extracted[1].Should().Be("2026-01-15T10:00:00");
    }

    [Fact]
    public void ExtractFieldValuesByFid_HandlesNumericKeys_FidPrefix_AndNullsSafely()
    {
        var fields = MakeStandardFields();
        var row = new Dictionary<string, object?>
        {
            ["103"] = "Sales",
            ["fid_104"] = 12000,
            ["Id"] = DBNull.Value,
            ["f_999"] = null
        };

        var extracted = RunReportQueryHandler.ExtractFieldValuesByFid(row, fields);

        extracted.Should().ContainKey(103).WhoseValue.Should().Be("Sales");
        extracted.Should().ContainKey(104).WhoseValue.Should().Be(12000);
        extracted.Should().ContainKey(999).WhoseValue.Should().BeNull();
    }

    [Fact]
    public void ExtractFieldValuesByFid_MalformedOrEmptyRow_ReturnsEmptyDictionaryWithoutThrowing()
    {
        var fields = MakeStandardFields();
        var emptyResult = RunReportQueryHandler.ExtractFieldValuesByFid(new Dictionary<string, object?>(), fields);
        emptyResult.Should().BeEmpty();

        var malformedRow = new Dictionary<string, object?>
        {
            ["invalid_key"] = "test",
            [""] = null
        };
        var result = RunReportQueryHandler.ExtractFieldValuesByFid(malformedRow, fields);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task RunReport_AiSearch_EnforcesRoleFilter_AndCalculatesTotalCountCorrectly()
    {
        var table = MakeTable();
        var fields = MakeStandardFields();
        var report = new Report
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            AppTableId = table.Id,
            ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [103L, 104L] })
        };

        _reportRepo.GetVisibleReportAsync(report.PublicId).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(fields);

        // Role filter: only Department == "Engineering"
        var roleFilter = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 103, Operator = "eq", Value = "Engineering" } }]
        };

        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.AllRecords,
                ViewFilter = roleFilter,
                VisibleFields = fields,
                EditableFieldIds = new HashSet<long> { 103, 104 }
            }));

        var row1Id = Guid.NewGuid();
        _searchService.IsGridSearchEnabled.Returns(true);
        _searchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns([new Dictionary<long, object?>()]);
        _recordRepo.ListRecordsChangedSinceAsync(Arg.Any<AppTable>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, DateTime ChangedOn)>()); // No recent writes

        _searchService.SearchRecordsPagedAsync(
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns((new List<Guid> { row1Id }, 1L));

        _recordRepo.ListByPublicIdsAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([new Dictionary<string, object?>
            {
                ["PublicId"] = row1Id,
                ["Id"] = 101L,
                ["f_103"] = "Engineering",
                ["f_104"] = 80000m,
                ["CreatedBy"] = 5L,
                ["CreatedOn"] = DateTime.UtcNow
            }]);

        var sut = new RunReportQueryHandler(
            _reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer,
            _userRepo, _formulaProjector, _relationalProjector, _searchService,
            _appUserRepo, _queryContext, _logger);

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));

        result.Items.Should().ContainSingle();
        result.TotalCount.Should().Be(1, "TotalCount must match authorized records, never leaking unauthorized counts");
    }

    [Fact]
    public async Task RunReport_ChildReportWithGridSearch_EnforcesRoleFilter()
    {
        var table = MakeTable();
        var fields = MakeStandardFields();
        var report = new Report
        {
            Id = 2,
            PublicId = Guid.NewGuid(),
            AppTableId = table.Id,
            ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [103L, 104L] })
        };

        _reportRepo.GetVisibleReportAsync(report.PublicId).Returns(report);
        _tableRepo.GetByIdAsync(table.Id).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(fields);

        // Role filter: only CreatedBy == 7
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.OwnRecords,
                RestrictToCreatedBy = 7L,
                VisibleFields = fields,
                EditableFieldIds = new HashSet<long> { 103, 104 }
            }));

        var sut = new RunReportQueryHandler(
            _reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer,
            _userRepo, _formulaProjector, _relationalProjector, _searchService,
            _appUserRepo, _queryContext, _logger);

        _recordRepo.ListAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            1, 20,
            Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(),
            Arg.Is<long?>(c => c == 7L),
            Arg.Any<CancellationToken>())
            .Returns([new Dictionary<string, object?>
            {
                ["PublicId"] = Guid.NewGuid(),
                ["Id"] = 201L,
                [PhysicalNaming.ColumnName(103)] = "Engineering",
                ["CreatedBy"] = 7L,
                ["CreatedOn"] = DateTime.UtcNow
            }]);

        _recordRepo.CountAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<FilterGroup?>(),
            Arg.Is<long?>(c => c == 7L),
            Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20, QuickSearch: "Eng"));

        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
        await _recordRepo.Received(1).ListAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            1, 20,
            Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(),
            7L,
            Arg.Any<CancellationToken>());
    }
}
