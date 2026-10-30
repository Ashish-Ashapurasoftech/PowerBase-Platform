using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Reports;

/// <summary>
/// A report only takes the search index's word when the index can answer exactly what SQL would: not while a record changed in the
/// last seconds (the index may not hold it yet), not when there are more matches than the index hands over (the report would be cut
/// short), and not for an exact or field-limited quick search. When SQL answers instead it searches the same fields the index does.
/// </summary>
public class ReportQuickSearchIndexTests
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

    private const long TableId = 42, AppId = 7;
    private const int Department = 103, Notes = 106, Diagnosis = 108;
    private static readonly Guid ReportId = Guid.NewGuid();
    private readonly AppTable _table = new() { Id = TableId, AppId = AppId, PublicId = Guid.NewGuid(), Name = "Tasks" };

    private static List<AppField> Fields(bool departmentSearchable) =>
    [
        new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
        new() { Id = Department, Fid = Department, Name = "Department", TypeCode = "Text", IsReportable = true,
            IsSearchable = departmentSearchable, IsFilterable = true, SearchSlot = "str_01" },
        new() { Id = Notes, Fid = Notes, Name = "Notes", TypeCode = "Text", IsReportable = true },
    ];

    private readonly ISearchOutbox _outbox = Substitute.For<ISearchOutbox>();
    private static readonly Guid PendingRecord = Guid.NewGuid();

    /// <param name="hidden">Fields this role may not see. Diagnosis, a Searchable text field, exists only when it is hidden.</param>
    private RunReportQueryHandler Handler(FilterGroup? filter = null, bool departmentSearchable = true, bool withOutbox = false, ISearchFieldJobs? jobs = null,
        params int[] hidden)
    {
        _outbox.ListPendingRecordsAsync(TableId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>?>(new List<Guid>()));
        var report = new Report
        {
            Id = 1, PublicId = ReportId, AppTableId = TableId, ReportType = "Table",
            Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [Department, Notes], FilterTree = filter }),
        };
        var fields = Fields(departmentSearchable);
        if (hidden.Contains(Diagnosis))
            fields.Add(new() { Id = Diagnosis, Fid = Diagnosis, Name = "Diagnosis", TypeCode = "Text", IsReportable = true, IsSearchable = true, IsFilterable = true, SearchSlot = "str_02" });
        _reportRepo.GetVisibleReportAsync(ReportId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(_table);
        _appRepo.GetByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(new App { Id = AppId });
        _fieldRepo.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(hidden.Length == 0
                ? new TableAccessContext { Unrestricted = true, ViewScope = RecordScopes.AllRecords, VisibleFields = fields, EditableFieldIds = new HashSet<long>() }
                : new TableAccessContext
                {
                    Unrestricted = false, ViewScope = RecordScopes.AllRecords, ModifyScope = RecordScopes.AllRecords,
                    VisibleFields = fields.Where(f => !hidden.Contains((int)f.Id)).ToList(), EditableFieldIds = new HashSet<long>()
                });
        _relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(2).Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1).Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(Rows(2));
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(2);
        _recordRepo.GetIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<long> { 1, 2 });

        _searchService.IsGridSearchEnabled.Returns(true);
        _searchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        NoRecentChanges();
        // The fast path (the index pages the report itself) is not what these tests are about: make it give way.
        _searchService.SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Is(20),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("fast path not under test"));

        return new RunReportQueryHandler(_reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector,
            _relationalProjector, _searchService, _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>(), _appRepo,
            withOutbox ? _outbox : null, jobs);
    }

    /// <summary>Changes of these records are written but the indexer has not confirmed them (their outbox rows are still there).</summary>
    private void OutboxHasPending(int count = 1)
    {
        var pending = new List<Guid> { PendingRecord };
        pending.AddRange(Enumerable.Range(1, count - 1).Select(_ => Guid.NewGuid()));
        _outbox.ListPendingRecordsAsync(TableId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>?>(pending));
        _recordRepo.GetIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(PendingRecord)), Arg.Any<CancellationToken>())
            .Returns(new List<long> { 9 });
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(int count) =>
        Enumerable.Range(1, count).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Id"] = (long)i, ["PublicId"] = Guid.NewGuid(), ["CreatedOn"] = new DateTime(2026, 1, 1), ["CreatedBy"] = 1L,
            ["ModifiedOn"] = null, ["ModifiedBy"] = null,
        }).ToList();

    private void NoRecentChanges() =>
        _recordRepo.ListRecordsChangedSinceAsync(Arg.Any<AppTable>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, DateTime ChangedOn)>());

    private void ARecordChangedJustNow() =>
        _recordRepo.ListRecordsChangedSinceAsync(Arg.Any<AppTable>(), Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, DateTime ChangedOn)> { (9, DateTime.UtcNow) });

    /// <summary>What the index answers when asked for the list of matches (the standard path asks for up to 2,000).</summary>
    private void IndexQuickSearchFinds(int handedOver, long total) =>
        _searchService.SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Is<int>(n => n != 20),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((Enumerable.Range(0, handedOver).Select(_ => Guid.NewGuid()).ToList(), total));

    private void IndexFilterFinds(int handedOver, long total) =>
        _searchService.SearchRecordsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((Enumerable.Range(0, handedOver).Select(_ => Guid.NewGuid()).ToList(), total));

    private static IEnumerable<FilterCondition> Conditions(FilterGroup? group) =>
        group == null ? [] : group.Nodes.SelectMany(n => n.Condition != null ? [n.Condition] : Conditions(n.Group));

    /// <summary>The filter the SQL page was read with.</summary>
    private List<FilterCondition> SqlFilter()
    {
        var call = _recordRepo.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IRecordRepository.ListAsync));
        return Conditions((FilterGroup?)call.GetArguments()[4]).ToList();
    }

    private static FilterGroup DepartmentIs(string value) =>
        new() { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = Department, Operator = "eq", Value = value } }] };

    // ---- bug 16: the recent-change check covers every place that asks the index

    [Fact]
    public async Task Quick_search_is_answered_in_sql_while_a_record_changed_in_the_last_seconds()
    {
        var sut = Handler();
        ARecordChangedJustNow();
        IndexQuickSearchFinds(0, 0);   // the index does not hold the new record yet

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Acme"));

        result.Items.Should().HaveCount(2, "the just-saved record is found in SQL instead of 'no results'");
        await _searchService.DidNotReceiveWithAnyArgs().SearchRecordsPagedAsync(default, default, default, default, default, default, default, default, default);
        SqlFilter().Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains" && c.Value == "Acme");
    }

    [Fact]
    public async Task A_filter_is_not_pre_searched_in_the_index_while_a_record_changed_in_the_last_seconds()
    {
        var sut = Handler(filter: DepartmentIs("Engineering"));
        ARecordChangedJustNow();
        IndexFilterFinds(0, 0);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        result.Items.Should().HaveCount(2);
        await _searchService.DidNotReceiveWithAnyArgs().SearchRecordsByFilterPagedAsync(default, default, default!, default, default, default(string?), default);
        SqlFilter().Should().ContainSingle(c => c.FieldId == Department && c.Operator == "eq");
    }

    [Fact]
    public async Task No_match_in_a_caught_up_index_is_still_answered_without_sql()
    {
        var sut = Handler();
        IndexQuickSearchFinds(0, 0);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "nothing"));

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task Matches_found_by_a_caught_up_index_are_read_by_id()
    {
        var sut = Handler();
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Eng"));

        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in");
        filter.Should().NotContain(c => c.Operator == "contains");
    }

    // ---- bug 17: more matches than the index hands over

    [Fact]
    public async Task Quick_search_with_more_matches_than_the_index_hands_over_is_answered_in_sql_over_every_record()
    {
        var sut = Handler();
        IndexQuickSearchFinds(2000, 5000);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Eng"));

        var filter = SqlFilter();
        filter.Should().NotContain(c => c.FieldId == 3 && c.Operator == "in", "only the first 2,000 of 5,000 matches would be shown");
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains");
    }

    [Fact]
    public async Task A_filter_with_more_matches_than_the_index_hands_over_runs_in_sql_over_every_record()
    {
        var sut = Handler(filter: DepartmentIs("Engineering"));
        IndexFilterFinds(2000, 5000);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        var filter = SqlFilter();
        filter.Should().NotContain(c => c.FieldId == 3 && c.Operator == "in");
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "eq" && c.Value == "Engineering");
    }

    // ---- bug 18: SQL searches the same fields the index does

    [Fact]
    public async Task The_sql_quick_search_covers_only_searchable_fields_like_the_index()
    {
        var sut = Handler();
        _searchService.IsGridSearchEnabled.Returns(false);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "urgent"));

        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == Department);
        filter.Should().NotContain(c => c.FieldId == Notes, "Notes is not Searchable, and the index does not search it either");
    }

    [Fact]
    public async Task A_table_with_no_searchable_field_still_searches_every_visible_text_field_in_sql()
    {
        var sut = Handler(departmentSearchable: false);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "urgent"));

        SqlFilter().Select(c => c.FieldId).Should().Contain(new long[] { Department, Notes }, "with nothing marked Searchable, SQL keeps searching every visible field as before");
    }

    [Fact]
    public async Task A_quick_search_limited_to_chosen_fields_searches_those_fields_in_sql()
    {
        var sut = Handler();
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "urgent", QuickSearchFieldIds: [Notes]));

        await _searchService.DidNotReceiveWithAnyArgs().SearchRecordsPagedAsync(default, default, default, default, default, default, default, default, default);
        SqlFilter().Should().ContainSingle(c => c.FieldId == Notes && c.Operator == "contains");
    }

    [Fact]
    public async Task An_exact_quick_search_is_answered_in_sql()
    {
        var sut = Handler();
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Engineering", QuickSearchExact: true));

        await _searchService.DidNotReceiveWithAnyArgs().SearchRecordsPagedAsync(default, default, default, default, default, default, default, default, default);
        SqlFilter().Should().ContainSingle(c => c.FieldId == Department && c.Operator == "eq");
    }

    // ---- the index lag, from the outbox instead of a 15-second guess

    [Fact]
    public async Task A_record_the_index_has_not_caught_up_with_is_decided_by_sql_while_the_rest_comes_from_the_index()
    {
        var sut = Handler(withOutbox: true);
        OutboxHasPending();
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Acme"));

        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[1,2]", "the index's matches it is up to date on");
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[9]", "the record it is behind on");
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains" && c.Value == "Acme", "which SQL checks for that record");
        await _searchService.Received(1).SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), "Acme", Arg.Any<string?>(), 1, 1800,
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_match_in_the_index_still_lets_sql_check_the_records_it_is_behind_on()
    {
        var sut = Handler(withOutbox: true);
        OutboxHasPending();
        IndexQuickSearchFinds(0, 0);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Acme"));

        result.Items.Should().HaveCount(2, "the just-saved record can match although the index does not hold it yet");
        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[9]");
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains");
    }

    [Fact]
    public async Task An_index_far_behind_this_table_sends_the_whole_quick_search_to_sql_however_long_ago_the_changes_were()
    {
        var sut = Handler(withOutbox: true);
        OutboxHasPending(count: 101);   // more than can be re-checked; nothing changed in the last seconds

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Acme"));

        await _searchService.DidNotReceiveWithAnyArgs().SearchRecordsPagedAsync(default, default, default, default, default, default, default, default, default);
        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains");
        filter.Should().NotContain(c => c.FieldId == 3);
    }

    [Fact]
    public async Task A_caught_up_index_is_used_even_right_after_older_changes()
    {
        var sut = Handler(withOutbox: true);   // the outbox is empty: every change is indexed
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Eng"));

        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[1,2]");
        filter.Should().NotContain(c => c.Operator == "contains");
    }

    [Fact]
    public async Task A_record_indexed_a_moment_ago_is_rechecked_until_the_index_shows_it()
    {
        var sut = Handler(withOutbox: true);
        ARecordChangedJustNow();   // already confirmed by the indexer, but within the index's refresh delay
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Acme"));

        SqlFilter().Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[9]");
    }

    [Fact]
    public async Task The_fast_path_is_not_taken_while_the_index_is_behind_on_any_record()
    {
        var sut = Handler(withOutbox: true);
        OutboxHasPending();
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Acme"));

        await _searchService.DidNotReceive().SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), 20, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_filter_pre_searched_in_the_index_lets_sql_decide_the_records_it_is_behind_on()
    {
        var sut = Handler(filter: DepartmentIs("Engineering"), withOutbox: true);
        OutboxHasPending();
        IndexFilterFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[1,2]");
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[9]");
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "eq" && c.Value == "Engineering");
    }

    // ---- a field job still running on the table

    private static ISearchFieldJobs JobRunningOn(long fid)
    {
        var jobs = Substitute.For<ISearchFieldJobs>();
        jobs.ListForTableAsync(TableId, Arg.Any<CancellationToken>())
            .Returns(new List<SearchFieldJobEntry> { new(1, TableId, AppId, PowerBase.Application.Common.Models.IndexAction.BackfillField, fid, null, null, 500) });
        return jobs;
    }

    [Fact]
    public async Task Quick_search_waits_in_sql_while_a_field_job_is_still_rewriting_the_tables_records()
    {
        var sut = Handler(jobs: JobRunningOn(Notes));
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Acme"));

        await _searchService.DidNotReceiveWithAnyArgs().SearchRecordsPagedAsync(default, default, default, default, default, default, default, default, default);
        SqlFilter().Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains");
    }

    [Fact]
    public async Task A_filter_on_a_field_whose_job_is_still_running_is_answered_in_sql()
    {
        var sut = Handler(filter: DepartmentIs("Engineering"), jobs: JobRunningOn(Department));
        IndexFilterFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        await _searchService.DidNotReceiveWithAnyArgs().SearchRecordsByFilterPagedAsync(default, default, default!, default, default, default(string?), default);
        SqlFilter().Should().ContainSingle(c => c.FieldId == Department && c.Operator == "eq");
    }

    [Fact]
    public async Task A_filter_on_another_field_still_uses_the_index_while_a_job_runs()
    {
        var sut = Handler(filter: DepartmentIs("Engineering"), jobs: JobRunningOn(Notes));
        IndexFilterFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20));

        SqlFilter().Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[1,2]");
    }

    // ---- quick search matches only fields the role can see

    [Fact]
    public async Task The_sql_quick_search_never_looks_in_a_field_hidden_from_the_role()
    {
        var sut = Handler(hidden: Diagnosis);
        _searchService.IsGridSearchEnabled.Returns(false);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "diabetes"));

        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains");
        filter.Should().NotContain(c => c.FieldId == Diagnosis, "a match there would tell the role what the hidden field holds");
    }

    [Fact]
    public async Task Index_matches_are_rechecked_against_the_visible_fields_for_a_role_that_cannot_see_every_searchable_field()
    {
        var sut = Handler(hidden: Diagnosis);
        IndexQuickSearchFinds(2, 2);   // the index's free-text holds Diagnosis too, so it may have matched there

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "diabetes"));

        var filter = SqlFilter();
        filter.Should().ContainSingle(c => c.FieldId == 3 && c.Operator == "in" && c.Value == "[1,2]", "the index still narrows the search");
        filter.Should().ContainSingle(c => c.FieldId == Department && c.Operator == "contains" && c.Value == "diabetes",
            "and SQL keeps only the records that match in a field the role can see");
        filter.Should().NotContain(c => c.FieldId == Diagnosis);
        await _searchService.DidNotReceive().SearchRecordsPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), 20, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_role_that_sees_every_field_still_takes_the_index_at_its_word()
    {
        var sut = Handler();
        IndexQuickSearchFinds(2, 2);

        await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "Eng"));

        SqlFilter().Should().NotContain(c => c.Operator == "contains");
    }

    [Fact]
    public async Task A_role_that_sees_no_searchable_field_finds_nothing_rather_than_every_record()
    {
        var sut = Handler(hidden: [Diagnosis, Department]);
        IndexQuickSearchFinds(2, 2);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "diabetes"));

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task A_quick_search_limited_to_a_hidden_field_finds_nothing()
    {
        var sut = Handler(hidden: Diagnosis);

        var result = await sut.HandleAsync(new RunReportQuery(ReportId, 1, 20, QuickSearch: "diabetes", QuickSearchFieldIds: [Diagnosis]));

        result.Items.Should().BeEmpty();
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default, default, default, default, default, default, default);
    }
}
