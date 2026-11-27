using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Reports;

/// <summary>A Table report over more records than one in-memory read holds (<see cref="InMemoryReadLimit"/>): what it can
/// still work out over every record is (in SQL); what it can't is said (InMemoryRowLimit) or left out (TotalsIncomplete),
/// never shown as if it covered everything. And a Lookup of a plain parent field is filtered and sorted in SQL.</summary>
public class ReportLargeTableTests
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
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private const long TableId = 42, ParentTableId = 99, AppId = 7;
    private const int Budget = 104, Doubled = 105, Department = 103, ProjectRef = 110, ProjectName = 111, ProjectOwner = 112;
    private const string WithTotal = """{"showTotalInReports":true}""";

    private readonly AppTable _table = new() { Id = TableId, AppId = AppId, PublicId = Guid.NewGuid(), Name = "Tasks" };
    private App _app = new() { Id = AppId };
    private List<AppField> _parentFields = [new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text" }, new() { Id = 8, Fid = 8, Name = "Owner", TypeCode = "User" }];

    private static List<AppField> Fields() =>
    [
        new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
        new() { Id = Department, Fid = Department, Name = "Department", TypeCode = "Text", IsReportable = true, IsSearchable = true, IsFilterable = true, SearchSlot = "str_01" },
        new() { Id = Budget, Fid = Budget, Name = "Budget", TypeCode = "Number", Settings = WithTotal, IsReportable = true, IsFilterable = true, SearchSlot = "num_01" },
        new() { Id = Doubled, Fid = Doubled, Name = "Doubled", TypeCode = "Formula_Number", Settings = WithTotal, IsReportable = true },
        new() { Id = ProjectRef, Fid = ProjectRef, Name = "Project", TypeCode = "Reference", Settings = """{"relationshipId":1,"parentTableId":99}""" },
        new() { Id = ProjectName, Fid = ProjectName, Name = "Project - Name", TypeCode = "Lookup", IsReportable = true,
            Settings = """{"relationshipId":1,"referenceFid":110,"sourceTableId":99,"sourceFid":6,"sourceTypeCode":"Text"}""" },
        new() { Id = ProjectOwner, Fid = ProjectOwner, Name = "Project - Owner", TypeCode = "Lookup", IsReportable = true,
            Settings = """{"relationshipId":1,"referenceFid":110,"sourceTableId":99,"sourceFid":8,"sourceTypeCode":"User"}""" },
    ];

    private RunReportQueryHandler Handler(List<SortSpec>? sorts = null, FilterGroup? filter = null)
    {
        var report = new Report
        {
            Id = 1, PublicId = ReportId, AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [Department, Budget, Doubled, ProjectName], SortFields = sorts ?? [], FilterTree = filter }),
        };
        var fields = Fields();
        _reportRepo.GetVisibleReportAsync(ReportId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(_table);
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ParentTableId, AppId = AppId });
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(_ => _app);
        _fieldRepo.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(fields);
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(_ => _parentFields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>() });
        _relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(2).Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
        // Doubled = 2 × Budget, as the formula engine would work it out.
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1)
                .Select(r => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [Doubled] = (decimal)r[PhysicalNaming.ColumnName(Budget)]! * 2 }).ToList());
        return new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector,
            _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>(), _appRepo);
    }

    private static readonly Guid ReportId = Guid.NewGuid();

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(int count) =>
        Enumerable.Range(1, count).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Id"] = (long)i, ["PublicId"] = Guid.NewGuid(), ["CreatedOn"] = new DateTime(2026, 1, 1), ["CreatedBy"] = 1L,
            ["ModifiedOn"] = null, ["ModifiedBy"] = null, [PhysicalNaming.ColumnName(Budget)] = 1m,
        }).ToList();

    private void EveryMatchIs(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        _recordRepo.ListAllMatchingAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
                Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(rows);

    private void BudgetTotalInSqlIs(decimal sum, long rows) =>
        _recordRepo.AggregateColumnsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<AppField>>(),
                Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(new Dictionary<long, ColumnTotal> { [Budget] = new() { Sum = sum, NonBlankCount = rows, RowCount = rows } });

    /// <summary>The SQL page (up to 20 rows) and count of a report whose filter and sort are plain SQL.</summary>
    private void ListedPageAndCountAre(int count)
    {
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(Rows(Math.Min(count, 20)));
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(count);
    }

    // ── Sorting by a calculated field ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Sort_by_a_formula_that_reads_every_match_totals_it_in_memory_as_before()
    {
        var sut = Handler(sorts: [new() { FieldId = Doubled, Desc = true }]);
        EveryMatchIs(Rows(3));

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.InMemoryRowLimit.Should().BeNull();
        result.TotalsIncomplete.Should().BeFalse();
        result.TotalCount.Should().Be(3);
        result.Totals[Budget.ToString()].Sum.Should().Be(3m);
        result.Totals[Doubled.ToString()].Sum.Should().Be(6m);
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateColumnsAsync(default!, default!, default!, default, default, default, default);
    }

    /// <summary>More matches than one read holds: the single read comes back one row past the budget, then every match is
    /// streamed in Id batches (rows 1..count, Budget = the row number).</summary>
    private void MoreMatchThanOneRead(int count)
    {
        EveryMatchIs(Rows(InMemoryReadLimit.ReadRows));
        _recordRepo.ListBatchAfterIdAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<long>(), Arg.Any<int>(),
                Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(ci =>
            {
                var after = ci.ArgAt<long>(2);
                var size = ci.ArgAt<int>(3);
                return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)Enumerable.Range(0, (int)Math.Max(0, Math.Min(size, count - after)))
                    .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["Id"] = after + i + 1, ["PublicId"] = Guid.NewGuid(), ["CreatedOn"] = new DateTime(2026, 1, 1), ["CreatedBy"] = 1L,
                        ["ModifiedOn"] = null, ["ModifiedBy"] = null, [PhysicalNaming.ColumnName(Budget)] = (decimal)(after + i + 1),
                    }).ToList();
            });
    }

    [Fact]
    public async Task Sort_by_a_formula_over_more_records_than_one_read_is_complete_with_exact_totals()
    {
        const int count = InMemoryReadLimit.Rows + 10_000;
        var sut = Handler(sorts: [new() { FieldId = Doubled, Desc = true }]);
        MoreMatchThanOneRead(count);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.InMemoryRowLimit.Should().BeNull("every match was looked at");
        result.TotalCount.Should().Be(count);
        result.Items.Should().HaveCount(20);
        result.Items[0].Fields[Doubled.ToString()].Should().Be((decimal)count * 2, "the highest Doubled is on the last row, past the old 50,000 cut");
        result.Totals[Budget.ToString()].Sum.Should().Be((decimal)count * (count + 1) / 2);
        result.Totals[Doubled.ToString()].Sum.Should().Be((decimal)count * (count + 1), "a calculated total over every match");
        result.TotalsIncomplete.Should().BeFalse();
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateColumnsAsync(default!, default!, default!);
    }

    [Fact]
    public async Task Filter_on_a_formula_over_more_records_than_one_read_counts_and_totals_every_match()
    {
        const int count = InMemoryReadLimit.Rows + 10_000;
        // Doubled > 100,000 holds for Budget > 50,000: exactly the rows the old 50,000-row read never looked at.
        var sut = Handler(filter: new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = Doubled, Operator = "gt", Value = "100000" } }] });
        MoreMatchThanOneRead(count);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 2, 20));

        result.InMemoryRowLimit.Should().BeNull();
        result.TotalCount.Should().Be(count - InMemoryReadLimit.Rows);
        result.Items.Should().HaveCount(20);
        result.Items[0].Fields[Budget.ToString()].Should().Be((decimal)(InMemoryReadLimit.Rows + 21), "page 2 in Id order");
        result.Totals[Budget.ToString()].NonBlankCount.Should().Be(count - InMemoryReadLimit.Rows);
        result.TotalsIncomplete.Should().BeFalse();
    }

    // ── Totals on a plain report ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Calculated_totals_over_more_matches_than_one_read_are_left_out_without_reading_them()
    {
        var sut = Handler();
        ListedPageAndCountAre(10_000_000);
        BudgetTotalInSqlIs(sum: 10_000_000m, rows: 10_000_000);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.InMemoryRowLimit.Should().BeNull("the page, its order and the count are plain SQL");
        result.Totals[Budget.ToString()].Sum.Should().Be(10_000_000m);
        result.Totals.Should().NotContainKey(Doubled.ToString());
        result.TotalsIncomplete.Should().BeTrue();
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllMatchingAsync(default!, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task Calculated_totals_over_few_matches_are_worked_out_in_memory_as_before()
    {
        var sut = Handler();
        ListedPageAndCountAre(3);
        BudgetTotalInSqlIs(sum: 3m, rows: 3);
        EveryMatchIs(Rows(3));

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.Totals[Doubled.ToString()].Sum.Should().Be(6m);
        result.TotalsIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_lookup_or_summary_is_totalled_only_when_its_display_settings_ask_for_it()
    {
        const int OpenTasks = 120, Rate = 121, Closed = 122;
        var sut = Handler();
        // Count summary opted in on its Behavior Settings; a Currency lookup that wasn't; a True/False summary counting checked.
        var fields = Fields();
        fields.Add(new AppField { Id = OpenTasks, Fid = OpenTasks, Name = "Open Tasks", TypeCode = "Summary", IsReportable = true,
            Settings = """{"relationshipId":2,"childTableId":98,"referenceFid":5,"function":"Count","display":{"showTotalInReports":true}}""" });
        fields.Add(new AppField { Id = Rate, Fid = Rate, Name = "Project - Rate", TypeCode = "Lookup", IsReportable = true,
            Settings = """{"relationshipId":1,"referenceFid":110,"sourceTableId":99,"sourceFid":9,"sourceTypeCode":"Currency","showTotalInReports":true}""" });
        fields.Add(new AppField { Id = Closed, Fid = Closed, Name = "Any Closed", TypeCode = "Summary", IsReportable = true,
            Settings = """{"relationshipId":2,"childTableId":98,"referenceFid":5,"function":"Exists","display":{"showTotalsRow":true}}""" });
        _reportRepo.GetVisibleReportAsync(ReportId, Arg.Any<CancellationToken>()).Returns(new Report
        {
            Id = 1, PublicId = ReportId, AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [Budget, OpenTasks, Rate, Closed] }),
        });
        _fieldRepo.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>() });
        // Every Tasks record: 2 open tasks, a rate of 10, a closed task on odd records.
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1)
                .Select(r => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>
                {
                    [OpenTasks] = 2m, [Rate] = 10m, [Closed] = (long)r["Id"]! % 2 == 1,
                }).ToList());
        ListedPageAndCountAre(3);
        BudgetTotalInSqlIs(sum: 3m, rows: 3);
        EveryMatchIs(Rows(3));

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.Totals[OpenTasks.ToString()].Sum.Should().Be(6m);
        result.Totals[Closed.ToString()].Sum.Should().Be(2m, "a True/False summary counts its checked records");
        result.Totals.Should().NotContainKey(Rate.ToString(), "a lookup's opt-ins live in its display object, not beside its wiring");
        result.TotalsIncomplete.Should().BeFalse();
    }

    // ── The AI Search fast path ────────────────────────────────────────────────────────────────

    private FilterGroup DepartmentIsEngineering => new() { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = Department, Operator = "eq", Value = "Engineering" } }] };

    private void SearchIsOnAndMatches(long totalMatches)
    {
        _searchService.IsGridSearchEnabled.Returns(true);
        _searchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _recordRepo.ListRecordsChangedSinceAsync(Arg.Any<AppTable>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, DateTime ChangedOn)>());
        var page = Rows(2);
        _searchService.SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((page.Select(r => (Guid)r["PublicId"]!).ToList(), totalMatches));
        _recordRepo.ListByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(page);
    }

    [Fact]
    public async Task Fast_search_paging_totals_every_match_not_the_page()
    {
        var sut = Handler(filter: DepartmentIsEngineering);
        SearchIsOnAndMatches(totalMatches: 5_000);
        BudgetTotalInSqlIs(sum: 5_000m, rows: 5_000);
        EveryMatchIs(Rows(5_000));

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.Items.Should().HaveCount(2);
        result.Totals[Budget.ToString()].Sum.Should().Be(5_000m, "the page holds 2 rows; the total is over all 5,000 matches");
        result.Totals[Doubled.ToString()].Sum.Should().Be(10_000m);
        await _recordRepo.Received(1).AggregateColumnsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<FilterGroup?>(f => f != null), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>());
    }

    [Fact]
    public async Task Quick_search_with_totals_takes_the_standard_path_which_totals_what_it_matched()
    {
        var sut = Handler();
        SearchIsOnAndMatches(totalMatches: 5_000);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Eng"));

        // The fast path would ask the index for the report's page (20 rows); the standard path only asks it for the list of matches.
        await _searchService.DidNotReceive().SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), 20, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Lookups in SQL ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sort_by_a_lookup_of_a_plain_parent_field_runs_in_sql_over_every_record()
    {
        var sut = Handler(sorts: [new() { FieldId = ProjectName }]);
        ListedPageAndCountAre(10_000_000);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.InMemoryRowLimit.Should().BeNull();
        result.TotalCount.Should().Be(10_000_000);
        await _recordRepo.Received(1).ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20, Arg.Any<FilterGroup?>(),
            Arg.Is<IReadOnlyList<SortSpec>?>(s => s!.Single().FieldId == ProjectName), Arg.Any<long?>(), Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlySet<long>?>(l => l != null && l.SetEquals(new long[] { ProjectName })));
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllMatchingAsync(default!, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task Filter_on_a_lookup_of_a_plain_parent_field_is_counted_in_sql()
    {
        var sut = Handler(filter: new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = ProjectName, Operator = "eq", Value = "Apollo" } }] });
        ListedPageAndCountAre(1_234);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.TotalCount.Should().Be(1_234);
        await _recordRepo.Received(1).CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<FilterGroup?>(f => f != null), Arg.Any<long?>(),
            Arg.Any<CancellationToken>(), Arg.Is<IReadOnlySet<long>?>(l => l != null && l.Contains(ProjectName)));
    }

    [Fact]
    public async Task Sort_by_a_lookup_of_an_encrypted_parent_field_stays_in_memory()
    {
        _app = new App { Id = AppId, IsEncrypted = true };   // the parent's Name is ciphertext to SQL
        var sut = Handler(sorts: [new() { FieldId = ProjectName }]);
        EveryMatchIs(Rows(3));

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        await _recordRepo.Received(1).ListAllMatchingAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Is<IReadOnlySet<long>?>(l => l == null));
    }

    [Theory]
    [InlineData(ProjectOwner, "a looked-up user: its picked user is resolved only for the record's own User fields")]
    [InlineData(ProjectName, "a lookup saved as Text whose source has since become a Formula")]
    public async Task Sort_by_a_lookup_sql_cant_read_like_the_field_itself_stays_in_memory(int lookupFid, string because)
    {
        if (lookupFid == ProjectName) _parentFields = [new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Formula_Text" }];
        var sut = Handler(sorts: [new() { FieldId = lookupFid }]);
        EveryMatchIs(Rows(3));

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        await _recordRepo.Received(1).ListAllMatchingAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Is<IReadOnlySet<long>?>(l => l == null));
        because.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_lookup_whose_parent_table_is_gone_stays_in_memory()
    {
        var sut = Handler(sorts: [new() { FieldId = ProjectName }]);
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns<AppTable>(_ => throw new NotFoundException("AppTable", ParentTableId));
        EveryMatchIs(Rows(3));

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        await _recordRepo.Received(1).ListAllMatchingAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
            Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Is<IReadOnlySet<long>?>(l => l == null));
    }

    [Fact]
    public async Task Paging_with_SkipCount_and_SkipTotals_skips_both_count_and_totals_queries()
    {
        var fields = Fields().Where(f => f.Fid != Doubled).ToList();
        var report = new Report
        {
            Id = 1, PublicId = ReportId, AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [Department, Budget] }),
        };
        _reportRepo.GetVisibleReportAsync(ReportId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(_table);
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(_app);
        _fieldRepo.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>() });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 2, 20,
                Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(Rows(20));
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector,
            _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>(), _appRepo);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, Page: 2, PageSize: 20, SkipCount: true, SkipTotals: true));

        result.Items.Should().HaveCount(20);
        result.TotalCount.Should().Be(-1);
        result.TotalsSkipped.Should().BeTrue();
        await _recordRepo.DidNotReceiveWithAnyArgs().CountAsync(default!, default!, default, default, default, default);
        await _recordRepo.DidNotReceiveWithAnyArgs().AggregateColumnsAsync(default!, default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Sql_only_totals_derive_totalCount_without_running_duplicate_CountAsync()
    {
        var fields = Fields().Where(f => f.Fid != Doubled).ToList();
        var report = new Report
        {
            Id = 1, PublicId = ReportId, AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [Department, Budget] }),
        };
        _reportRepo.GetVisibleReportAsync(ReportId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(_table);
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(_app);
        _fieldRepo.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>() });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20,
                Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(Rows(20));
        BudgetTotalInSqlIs(sum: 87_713m, rows: 87_713);
        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector,
            _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>(), _appRepo);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, Page: 1, PageSize: 20));

        result.TotalCount.Should().Be(87_713);
        result.Totals[Budget.ToString()].Sum.Should().Be(87_713m);
        await _recordRepo.Received(1).AggregateColumnsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>());
        await _recordRepo.DidNotReceiveWithAnyArgs().CountAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Phase5_indexed_summary_report_hydrates_from_ai_search_with_zero_sql_row_reads()
    {
        const int TotalHours = 120;
        var summaryField = new AppField
        {
            Id = TotalHours, Fid = TotalHours, Name = "Total Hours", TypeCode = "Summary",
            Settings = """{"relationshipId":1,"function":"Sum","targetTypeCode":"Number"}""",
            IsReportable = true, IsFilterable = true, SearchExact = true, SearchSlot = "num_02",
            MaterializeMode = MaterializeModes.On, MaterializeState = MaterializeStates.Ready,
            SearchIndexState = SearchIndexStates.Indexed, MaterializeVersion = 1, SearchIndexVersion = 1
        };
        var deptField = new AppField
        {
            Id = Department, Fid = Department, Name = "Department", TypeCode = "Text",
            IsReportable = true, IsSearchable = true, IsFilterable = true, SearchSlot = "str_01"
        };
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Date Created", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn", IsFilterable = true, SearchSlot = "date_01" },
            new() { Id = 2, Fid = 2, Name = "Date Modified", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "ModifiedOn", IsFilterable = true, SearchSlot = "date_02" },
            new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id", IsFilterable = true, SearchExact = true, SearchSlot = "num_01" },
            new() { Id = 4, Fid = 4, Name = "Record Owner", TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy", IsFilterable = true, SearchExact = true, SearchSlot = "str_02" },
            deptField,
            summaryField
        };

        var report = new Report
        {
            Id = 1, PublicId = ReportId, AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition
            {
                Columns = [Department, TotalHours],
                SortFields = [new SortSpec { FieldId = TotalHours, Desc = true }]
            }),
        };

        _reportRepo.GetVisibleReportAsync(ReportId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(_table);
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(_app);
        _fieldRepo.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>() });
        _searchService.IsGridSearchEnabled.Returns(true);
        _searchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _recordRepo.ListRecordsChangedSinceAsync(Arg.Any<AppTable>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, DateTime ChangedOn)>());

        var pid1 = Guid.NewGuid();
        var pid2 = Guid.NewGuid();
        _searchService.SearchRecordsWithDocumentsPagedAsync(
                Arg.Any<long>(), TableId, Arg.Any<string?>(), Arg.Any<string?>(), 1, 20,
                Arg.Any<IReadOnlyList<string>>(), Arg.Is<IReadOnlyList<string>?>(o => o != null && o.Contains("num_02 desc")),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((
                (IReadOnlyList<SearchDocumentRow>)
                [
                    new SearchDocumentRow(pid1, new Dictionary<string, object?> { ["num_01"] = 101d, ["str_01"] = "Eng", ["num_02"] = 15d, ["date_01"] = DateTimeOffset.UtcNow, ["date_02"] = DateTimeOffset.UtcNow, ["str_02"] = "1" }),
                    new SearchDocumentRow(pid2, new Dictionary<string, object?> { ["num_01"] = 102d, ["str_01"] = "Ops", ["num_02"] = 8d, ["date_01"] = DateTimeOffset.UtcNow, ["date_02"] = DateTimeOffset.UtcNow, ["str_02"] = "1" })
                ],
                TotalCount: 2L));

        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector,
            _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>(), _appRepo);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, Page: 1, PageSize: 20, SkipTotals: true));

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListByPublicIdsAsync(default!, default!, default!, default);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListColumnsByPublicIdsAsync(default!, default!, default!, default, default, default);
        await _relationalProjector.DidNotReceiveWithAnyArgs().ProjectAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task AiSearch_document_hydration_fetches_only_missing_columns_when_one_field_is_not_indexed()
    {
        const int Notes = 121;
        var notesField = new AppField { Id = Notes, Fid = Notes, Name = "Notes", TypeCode = "RichText", IsReportable = true, IsSearchable = false, IsFilterable = false };
        var deptField = new AppField { Id = Department, Fid = Department, Name = "Department", TypeCode = "Text", IsReportable = true, IsSearchable = true, IsFilterable = true, SearchSlot = "str_01" };
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Date Created", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn", IsFilterable = true, SearchSlot = "date_01" },
            new() { Id = 2, Fid = 2, Name = "Date Modified", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "ModifiedOn", IsFilterable = true, SearchSlot = "date_02" },
            new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id", IsFilterable = true, SearchExact = true, SearchSlot = "num_01" },
            new() { Id = 4, Fid = 4, Name = "Record Owner", TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy", IsFilterable = true, SearchExact = true, SearchSlot = "str_02" },
            deptField,
            notesField
        };

        var report = new Report
        {
            Id = 1, PublicId = ReportId, AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [Department, Notes] }),
        };

        _reportRepo.GetVisibleReportAsync(ReportId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(_table);
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(_app);
        _fieldRepo.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>() });
        _searchService.IsGridSearchEnabled.Returns(true);
        _searchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _recordRepo.ListRecordsChangedSinceAsync(Arg.Any<AppTable>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, DateTime ChangedOn)>());

        var pid1 = Guid.NewGuid();
        _searchService.SearchRecordsWithDocumentsPagedAsync(
                Arg.Any<long>(), TableId, Arg.Any<string?>(), Arg.Any<string?>(), 1, 20,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((
                (IReadOnlyList<SearchDocumentRow>)[new SearchDocumentRow(pid1, new Dictionary<string, object?> { ["num_01"] = 101d, ["str_01"] = "Eng", ["date_01"] = DateTimeOffset.UtcNow, ["date_02"] = DateTimeOffset.UtcNow, ["str_02"] = "1" })],
                TotalCount: 1L));
        _recordRepo.ListColumnsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([new Dictionary<string, object?> { ["PublicId"] = pid1, [PhysicalNaming.ColumnName(Notes)] = "SQL note" }]);

        var sut = new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector,
            _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>(), _appRepo);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, Page: 1, PageSize: 20, SkipTotals: true));

        result.Items.Should().HaveCount(1);
        result.Items[0].Fields[Notes.ToString()].Should().Be("SQL note");
        await _recordRepo.Received(1).ListColumnsByPublicIdsAsync(
            _table,
            Arg.Is<IReadOnlyList<AppField>>(missing => missing.Count == 1 && missing[0].Fid == Notes),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == pid1),
            includeRecordId: false,
            includeAuditColumns: false,
            Arg.Any<CancellationToken>());
        await _recordRepo.DidNotReceiveWithAnyArgs().ListByPublicIdsAsync(default!, default!, default!, default);
    }
}
