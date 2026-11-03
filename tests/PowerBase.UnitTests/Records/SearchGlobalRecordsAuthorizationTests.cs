using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Queries.SearchGlobalRecords;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// Global search shows only what the user may see: the index proposes candidates, and each one is checked against the database (the
/// record exists, its table and the record itself are visible, the search matches in visible fields). The display text and the count
/// come from that check, never from the index, and records the index has not caught up with are found in SQL.
/// </summary>
public class SearchGlobalRecordsAuthorizationTests
{
    private readonly IAzureSearchService _searchService = Substitute.For<IAzureSearchService>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly ISearchOutbox _outbox = Substitute.For<ISearchOutbox>();

    private readonly App _app = new() { Id = 10L, PublicId = Guid.NewGuid(), Name = "HealthApp" };
    private readonly AppTable _table = new() { Id = 20L, AppId = 10L, PublicId = Guid.NewGuid(), Name = "Patients", SingularLabel = "Patient" };
    private static readonly AppField Name = new() { Id = 2L, Fid = 7, Name = "PatientName", AppTableId = 20L, TypeCode = "Text", IsSearchable = true };
    private static readonly AppField Diagnosis = new() { Id = 1L, Fid = 99, Name = "Diagnosis", AppTableId = 20L, TypeCode = "Text", IsSearchable = true };
    private readonly List<AppField> _fields = [Diagnosis, Name];

    public SearchGlobalRecordsAuthorizationTests()
    {
        _queryContext.TenantId.Returns(100L);
        _tableRepo.GetByIdAsync(20L, Arg.Any<CancellationToken>()).Returns(_table);
        _fieldRepo.ListByTableAsync(20L, Arg.Any<CancellationToken>()).Returns(_fields);
        _appRepo.GetByIdAsync(10L, Arg.Any<CancellationToken>()).Returns(_app);
        _outbox.ListPendingRecordsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchOutboxPendingRecord>?>(new List<SearchOutboxPendingRecord>()));
        IndexHas();
    }

    private SearchGlobalRecordsQueryHandler Sut(bool withOutbox = false) =>
        new(_searchService, _queryContext, _appRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, withOutbox ? _outbox : null);

    /// <summary>The index's candidates for the first round (best first); later rounds find nothing more.</summary>
    private void IndexHas(params Guid[] ids)
    {
        _searchService.SearchGlobalAsync(100L, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<GlobalSearchResult>(), 0L));
        _searchService.SearchGlobalAsync(100L, Arg.Any<string>(), Arg.Any<long?>(), 1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ids.Select(id => new GlobalSearchResult(id, 10L, 20L, new Dictionary<string, string> { ["search_content"] = "stale index text" })).ToList(), (long)ids.Length));
    }

    private void UserSees(TableAccessContext access) =>
        _enforcer.GetTableAccessAsync(_table, _fields, Arg.Any<CancellationToken>()).Returns(access);

    private static TableAccessContext Sees(params AppField[] visible) =>
        new() { Unrestricted = false, ViewScope = RecordScopes.AllRecords, VisibleFields = visible.ToList() };

    /// <summary>The records as the database holds them now (live ones only).</summary>
    private void DatabaseHas(params (Guid Id, string? Name, string? Diagnosis)[] rows) =>
        _recordRepo.ListByPublicIdsAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => rows.Where(r => ci.ArgAt<IReadOnlyList<Guid>>(2).Contains(r.Id))
                .Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["Id"] = 1L, ["PublicId"] = r.Id,
                    [PhysicalNaming.ColumnName(7)] = r.Name, [PhysicalNaming.ColumnName(99)] = r.Diagnosis,
                }).ToList());

    [Fact]
    public async Task HandleAsync_OmitRecordsFromTablesUserCannotView()
    {
        var id = Guid.NewGuid();
        IndexHas(id);
        DatabaseHas((id, "Alice", "Flu"));
        UserSees(new TableAccessContext { Unrestricted = false, ViewScope = RecordScopes.None });

        var result = await Sut().HandleAsync(new SearchGlobalRecordsQuery("Alice", null, 1, 50));

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0, "a record the user may not see is not counted either");
    }

    [Fact]
    public async Task HandleAsync_OmitRecordsFailingRowLevelViewFilterOrOwnership()
    {
        var id = Guid.NewGuid();
        IndexHas(id);
        DatabaseHas((id, "Alice", "Flu"));
        UserSees(new TableAccessContext { Unrestricted = false, ViewScope = RecordScopes.OwnRecords, RestrictToCreatedBy = 42L, VisibleFields = _fields });
        _recordRepo.ExistsWithViewFilterAsync(_table, _fields, id, null, 42L, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Sut().HandleAsync(new SearchGlobalRecordsQuery("Alice", null, 1, 50));

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_NeverDisplaysHiddenOrUnpermittedFields()
    {
        var id = Guid.NewGuid();
        IndexHas(id);
        DatabaseHas((id, null, "Sensitive Medical Diagnosis"));
        UserSees(Sees(Name)); // Diagnosis is hidden from this role

        var result = await Sut().HandleAsync(new SearchGlobalRecordsQuery("Sensitive", null, 1, 50));

        result.Items.Should().BeEmpty("typing a hidden field's content must not reveal the record");
    }

    [Fact]
    public async Task HandleAsync_DisplaysAuthorizedFieldWhenPermitted()
    {
        var id = Guid.NewGuid();
        _table.DisplayFieldId = 7L;
        IndexHas(id);
        DatabaseHas((id, "Alice Wonderland", "Flu"));
        UserSees(Sees(Name));

        var result = await Sut().HandleAsync(new SearchGlobalRecordsQuery("alice", null, 1, 50));

        result.Items.Should().ContainSingle().Which.PrimaryText.Should().Be("Alice Wonderland", "the display text is read from the database");
        result.Items[0].TableName.Should().Be("Patient");
    }

    [Fact]
    public async Task The_display_text_never_comes_from_a_hidden_field()
    {
        var id = Guid.NewGuid();
        _table.DisplayFieldId = 99L; // the display field is hidden from this role
        IndexHas(id);
        DatabaseHas((id, "Alice", "Sensitive Medical Diagnosis"));
        UserSees(Sees(Name));

        var result = await Sut().HandleAsync(new SearchGlobalRecordsQuery("alice", null, 1, 50));

        result.Items.Should().ContainSingle().Which.PrimaryText.Should().Be("Alice");
    }

    [Fact]
    public async Task A_record_whose_text_changed_is_not_found_by_its_old_text()
    {
        var id = Guid.NewGuid();
        IndexHas(id); // the index still has "Bob"
        DatabaseHas((id, "Robert", null));
        UserSees(Sees(Name));

        (await Sut().HandleAsync(new SearchGlobalRecordsQuery("Bob", null, 1, 50))).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_record_still_in_the_index_is_not_shown()
    {
        IndexHas(Guid.NewGuid());
        DatabaseHas();
        UserSees(Sees(Name));

        (await Sut().HandleAsync(new SearchGlobalRecordsQuery("Alice", null, 1, 50))).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_saved_a_moment_ago_is_found_before_the_index_has_it()
    {
        var id = Guid.NewGuid();
        IndexHas();
        DatabaseHas((id, "Alice", null));
        UserSees(Sees(Name));
        _outbox.ListPendingRecordsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchOutboxPendingRecord>?>(new List<SearchOutboxPendingRecord> { new(20L, 10L, id) }));

        var result = await Sut(withOutbox: true).HandleAsync(new SearchGlobalRecordsQuery("Alice", null, 1, 50));

        result.Items.Should().ContainSingle().Which.RecordId.Should().Be(id);
    }

    [Fact]
    public async Task A_page_is_filled_from_later_candidates_when_earlier_ones_are_hidden_and_only_visible_ones_are_counted()
    {
        var hidden = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        var visible = Guid.NewGuid();
        _searchService.SearchGlobalAsync(100L, Arg.Any<string>(), Arg.Any<long?>(), 1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((hidden.Select(id => new GlobalSearchResult(id, 10L, 20L, new Dictionary<string, string>())).ToList(), 101L));
        _searchService.SearchGlobalAsync(100L, Arg.Any<string>(), Arg.Any<long?>(), 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<GlobalSearchResult> { new(visible, 10L, 20L, new Dictionary<string, string>()) }, 101L));
        // the first 100 matched only in the hidden Diagnosis field; the last one in the visible name
        DatabaseHas(hidden.Select(id => (id, (string?)null, (string?)"Alice syndrome")).Append((visible, "Alice", null)).ToArray());
        UserSees(Sees(Name));

        var result = await Sut().HandleAsync(new SearchGlobalRecordsQuery("Alice", null, 1, 20));

        result.Items.Should().ContainSingle().Which.RecordId.Should().Be(visible);
        result.TotalCount.Should().Be(1, "the index's 101 counts records this user may not find");
    }

    [Fact]
    public async Task A_deleted_table_is_skipped_but_a_database_failure_is_not_hidden()
    {
        IndexHas(Guid.NewGuid());
        _tableRepo.GetByIdAsync(20L, Arg.Any<CancellationToken>()).ThrowsAsync(new NotFoundException("Table", 20L));

        (await Sut().HandleAsync(new SearchGlobalRecordsQuery("Alice", null, 1, 50))).Items.Should().BeEmpty();

        _tableRepo.GetByIdAsync(20L, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database down"));
        await Sut().Invoking(s => s.HandleAsync(new SearchGlobalRecordsQuery("Alice", null, 1, 50))).Should().ThrowAsync<InvalidOperationException>();
    }
}
