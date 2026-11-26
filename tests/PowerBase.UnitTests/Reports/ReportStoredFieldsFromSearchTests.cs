using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Search;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Reports;

/// <summary>
/// Plan "computed fields from AI Search", D3 / work items 7–8: a stored Summary whose values are fully in the index ("in AI") is
/// filtered and sorted in the index; one that is only Ready (still being indexed) stays on SQL c_{fid}, as before.
/// </summary>
public class ReportStoredFieldsFromSearchTests
{
    private const long TableId = 42;

    private static AppField CountSummary(string? slot = "num_02") => new()
    {
        Id = 105, Fid = 105, AppTableId = TableId, Name = "Open tasks", TypeCode = "Summary", IsReportable = true,
        IsSearchable = true, IsFilterable = true, SearchSlot = slot,
        Settings = JsonSerializer.Serialize(new { ChildTableId = 43, ReferenceFid = 6, Function = "Count" }),
    };

    private static List<AppField> Fields(string? slot = "num_02") =>
    [
        new() { Id = 1, Fid = 1, Name = "CreatedOn", TypeCode = "Date", IsSystem = true },
        new() { Id = 2, Fid = 2, Name = "ModifiedOn", TypeCode = "Date", IsSystem = true },
        new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
        new() { Id = 4, Fid = 4, Name = "CreatedBy", TypeCode = "Number", IsSystem = true },
        new() { Id = 103, Fid = 103, Name = "Department", TypeCode = "Text", IsReportable = true, IsSearchable = true, IsFilterable = true, SearchSlot = "str_01" },
        CountSummary(slot),
    ];

    // ── SearchIndexedFields ───────────────────────────────────────────────────

    [Fact]
    public void An_in_AI_summary_becomes_a_number_in_its_slot()
    {
        var fields = Fields();
        var swapped = SearchIndexedFields.For(fields, new Dictionary<long, bool> { [105] = true });
        var s = swapped.Single(f => f.Fid == 105);
        s.TypeCode.Should().Be("Number");
        s.SearchSlot.Should().Be("num_02");
        fields.Single(f => f.Fid == 105).TypeCode.Should().Be("Summary", "the original field is left alone");
        SearchIndexedFields.Fids(fields, new Dictionary<long, bool> { [105] = true }).Should().BeEquivalentTo([105L]);
    }

    [Fact]
    public void A_field_not_in_AI_or_without_a_typed_slot_is_left_as_it_is()
    {
        SearchIndexedFields.For(Fields(), new Dictionary<long, bool>()).Single(f => f.Fid == 105).TypeCode.Should().Be("Summary");
        SearchIndexedFields.For(Fields(slot: null), new Dictionary<long, bool> { [105] = true }).Single(f => f.Fid == 105).TypeCode.Should().Be("Summary");
        SearchIndexedFields.For(Fields(slot: "str_04"), new Dictionary<long, bool> { [105] = true }).Single(f => f.Fid == 105).TypeCode.Should().Be("Summary");
    }

    [Fact]
    public void A_field_with_values_the_index_rounds_is_not_sorted_in_the_index()
    {
        var inSearch = new Dictionary<long, bool> { [105] = false };
        SearchIndexedFields.For(Fields(), inSearch).Single(f => f.Fid == 105).TypeCode.Should().Be("Number", "filters may still use the index");
        SearchIndexedFields.For(Fields(), inSearch, forSort: true).Single(f => f.Fid == 105).TypeCode.Should().Be("Summary");
    }

    // ── RunReport ─────────────────────────────────────────────────────────────

    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly PowerBase.Application.Relationships.IRelationalProjector _relationalProjector = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();
    private readonly IAzureSearchService _search = Substitute.For<IAzureSearchService>();
    private readonly IMaterializedFieldState _materialized = Substitute.For<IMaterializedFieldState>();
    private readonly ILogger<RunReportQueryHandler> _logger = Substitute.For<ILogger<RunReportQueryHandler>>();

    private (RunReportQueryHandler Sut, Guid ReportId) Arrange(IReadOnlyDictionary<long, bool> inSearch)
    {
        var table = new AppTable { Id = TableId, PublicId = Guid.NewGuid(), Name = "Projects", PhysicalTableName = PhysicalNaming.TableName(TableId) };
        var fields = Fields();
        var report = new Report
        {
            Id = 1, PublicId = Guid.NewGuid(), AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition
            {
                Columns = [103L],
                FilterTree = new FilterGroup
                {
                    Logic = "and",
                    Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 105, Operator = "gt", Value = "5" } }],
                },
            }),
        };
        _reportRepo.GetVisibleReportAsync(report.PublicId).Returns(report);
        _tableRepo.GetByIdAsync(TableId).Returns(table);
        _fieldRepo.ListByTableAsync(TableId).Returns(fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TableAccessContext
            {
                Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>(),
            }));
        _materialized.ReadyFieldIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<long> { 105 });
        _materialized.SearchIndexedFieldsAsync(Arg.Any<CancellationToken>()).Returns(inSearch);

        _search.IsGridSearchEnabled.Returns(true);
        _search.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _recordRepo.ListRecordsChangedSinceAsync(Arg.Any<AppTable>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, DateTime ChangedOn)>());
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1).Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());

        var rowId = Guid.NewGuid();
        _search.SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((new List<Guid> { rowId }, 1L));
        _recordRepo.ListByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([new Dictionary<string, object?> { ["PublicId"] = rowId, ["Id"] = 7L, ["f_103"] = "Engineering", ["CreatedBy"] = 1L, ["CreatedOn"] = DateTime.UtcNow, ["ModifiedOn"] = DateTime.UtcNow }]);

        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, Substitute.For<IUserRepository>(),
            _formulaProjector, _relationalProjector, _search, Substitute.For<IAppUserRepository>(), Substitute.For<IQueryContext>(),
            _logger, materialized: _materialized);
        return (sut, report.PublicId);
    }

    [Fact]
    public async Task A_filter_on_an_in_AI_summary_is_answered_by_the_index()
    {
        var (sut, reportId) = Arrange(new Dictionary<long, bool> { [105] = true });

        var result = await sut.HandleAsync(new RunReportQuery(reportId, 1, 20));

        await _search.Received().SearchRecordsPagedAsync(Arg.Any<long>(), TableId, Arg.Any<string?>(),
            Arg.Is<string?>(o => o != null && o.Contains("num_02")), 1, 20, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        result.Items.Should().ContainSingle(string.Join(" | ", _logger.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<Exception>().Select(e => e.ToString())));
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task A_summary_that_is_only_ready_stays_on_SQL()
    {
        var (sut, reportId) = Arrange(new Dictionary<long, bool>());

        await sut.HandleAsync(new RunReportQuery(reportId, 1, 20));

        await _search.DidNotReceiveWithAnyArgs().SearchRecordsPagedAsync(default, default, default, default, default, default, default, default, default);
        await _search.DidNotReceiveWithAnyArgs().SearchRecordsByFilterPagedAsync(default, default, default!, default, default, default, default);
    }
}
