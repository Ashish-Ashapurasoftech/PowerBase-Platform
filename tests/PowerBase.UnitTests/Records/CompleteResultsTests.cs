using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Queries.GetDistinctFieldValues;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// No read answers from the first records only (Phase 1 of the performance plan, doc 22): a role's View rule runs in SQL when it
/// can (no id list, no limit), an in-memory evaluation is complete or refused with <see cref="ResultTooLargeException"/>.
/// </summary>
public class CompleteResultsTests
{
    private const int DeptFid = 6;
    private const int RegionFid = 10;   // a Lookup (calculated) field
    private const long RegionFieldId = 101;

    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRelationalProjector _relational = Substitute.For<IRelationalProjector>();
    private readonly IFormulaProjector _formula = Substitute.For<IFormulaProjector>();
    private readonly AppTable _table = new() { Id = 5, AppId = 3, PublicId = Guid.NewGuid() };
    private readonly IReadOnlyList<AppField> _fields =
    [
        new AppField { Id = 100, PublicId = Guid.NewGuid(), Fid = DeptFid, TypeCode = "Text", PhysicalColumnName = "f_6" },
        new AppField { Id = RegionFieldId, PublicId = Guid.NewGuid(), Fid = RegionFid, TypeCode = "Lookup",
            Settings = """{"resultTypeCode":"Text","sourceTypeCode":"Text"}""" },
    ];

    [Fact]
    public void Budget_tells_a_complete_read_from_a_cut_one()
    {
        InMemoryReadLimit.ReadRows.Should().Be(InMemoryReadLimit.Rows + 1);
        InMemoryReadLimit.IsComplete(InMemoryReadLimit.Rows).Should().BeTrue();
        InMemoryReadLimit.IsComplete(InMemoryReadLimit.ReadRows).Should().BeFalse();

        var act = () => InMemoryReadLimit.EnsureComplete(InMemoryReadLimit.ReadRows, "This export");
        act.Should().Throw<ResultTooLargeException>()
            .Where(e => e.ErrorCode == ResultTooLargeException.Code && e.Limit == InMemoryReadLimit.Rows);
        InMemoryReadLimit.EnsureComplete(InMemoryReadLimit.Rows, "This export");   // no throw
    }

    [Fact]
    public async Task Physical_rule_becomes_a_SQL_scope_without_reading_ids()
    {
        var scope = await Scope(Access(Rule(DeptFid, "IT")));

        scope.Kind.Should().Be(ViewRowScopeKind.Sql);
        scope.Filter!.Nodes.Single().Condition!.FieldId.Should().Be(DeptFid);
        scope.Fields.Should().BeSameAs(_fields);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListMatchingRowIdsAsync(default!, default!);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllMatchingAsync(default!, default!);
    }

    [Fact]
    public async Task Own_records_scope_stays_SQL()
    {
        var scope = await Scope(new TableAccessContext { Unrestricted = false, ViewScope = RecordScopes.OwnRecords, RestrictToCreatedBy = 77 });

        scope.Kind.Should().Be(ViewRowScopeKind.Sql);
        scope.RestrictToCreatedBy.Should().Be(77);
    }

    [Fact]
    public async Task Rule_on_a_Ready_stored_formula_runs_in_SQL_through_its_stored_column()
    {
        var scope = await Scope(Access(Rule(RegionFid, "West")), ready: new HashSet<long> { RegionFieldId });

        scope.Kind.Should().Be(ViewRowScopeKind.Sql);
        scope.Fields!.Single(f => f.Fid == RegionFid).PhysicalColumnName.Should().Be(PhysicalNaming.MaterializedColumnName(RegionFid));
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllMatchingAsync(default!, default!);
    }

    [Fact]
    public async Task Rule_on_a_not_stored_formula_is_worked_out_in_memory_and_complete()
    {
        StreamRows(2, westWhen: id => id == 1);

        var scope = await Scope(Access(Rule(RegionFid, "West")));

        scope.Kind.Should().Be(ViewRowScopeKind.Ids);
        scope.Ids.Should().Equal(1L);
    }

    [Fact]
    public async Task Rule_on_a_not_stored_formula_over_more_rows_than_one_read_is_complete_not_cut()
    {
        const int rows = InMemoryReadLimit.Rows + 10_000;   // 60,000: before, only the first 50,000 were looked at
        StreamRows(rows, westWhen: id => id % 2 == 0);

        var scope = await Scope(Access(Rule(RegionFid, "West")));

        scope.Kind.Should().Be(ViewRowScopeKind.Ids);
        scope.Ids!.Count.Should().Be(rows / 2);
        scope.Ids.Max().Should().Be(rows);   // rows past the old 50,000 cut are there
        await _recordRepo.DidNotReceiveWithAnyArgs().ListAllMatchingAsync(default!, default!);
    }

    [Fact]
    public async Task Id_list_of_a_SQL_scope_is_complete_past_10000_and_50000()
    {
        const long rows = 120_000;
        _recordRepo.ListIdsAfterIdAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<FilterGroup?>(),
                Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var after = ci.ArgAt<long>(2);
                var size = ci.ArgAt<int>(3);
                return (IReadOnlyList<(long Id, Guid PublicId)>)Enumerable.Range(0, (int)Math.Max(0, Math.Min(size, rows - after)))
                    .Select(i => (after + i + 1, Guid.Empty)).ToList();
            });

        var ids = await ViewableRowIds.ResolveAsync(Access(Rule(DeptFid, "IT")), _table, _fields, _recordRepo, _relational, _formula);

        ids!.Count.Should().Be((int)rows);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Distinct_values_apply_a_SQL_rule_in_the_distinct_query_with_no_id_list()
    {
        var enforcer = Substitute.For<IRolePermissionEnforcer>();
        enforcer.GetTableAccessAsync(_table, _fields, Arg.Any<CancellationToken>())
            .Returns(Access(Rule(DeptFid, "IT"), visible: _fields));

        await Distinct(enforcer).HandleAsync(new GetDistinctFieldValuesQuery(_table.PublicId, DeptFid));

        await _recordRepo.Received(1).GetDistinctFieldValuesAsync(_table, _fields[0], Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), null, false,
            Arg.Is<FilterGroup?>(g => g != null && g.Nodes.Single().Condition!.FieldId == DeptFid),
            Arg.Is<IReadOnlyList<AppField>?>(f => f != null && f.Count == _fields.Count), null);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListMatchingRowIdsAsync(default!, default!);
    }

    /// <summary>Serves rows 1..count through the keyset batch reader, with the Lookup worked out as West where <paramref name="westWhen"/> holds.</summary>
    private void StreamRows(int count, Func<long, bool> westWhen)
    {
        _recordRepo.ListBatchAfterIdAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<FilterGroup?>(),
                Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(ci =>
            {
                var after = ci.ArgAt<long>(2);
                var size = ci.ArgAt<int>(3);
                return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)Enumerable.Range(0, (int)Math.Max(0, Math.Min(size, count - after)))
                    .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = after + i + 1, ["f_6"] = "IT" })
                    .ToList();
            });
        _relational.ProjectAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(2)
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
        _formula.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(1)
                .Select(r => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>
                    { [RegionFid] = westWhen(Convert.ToInt64(r["Id"])) ? "West" : "East" })
                .ToList());
    }

    private GetDistinctFieldValuesQueryHandler Distinct(IRolePermissionEnforcer enforcer)
    {
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(_fields);
        return new GetDistinctFieldValuesQueryHandler(tableRepo, fieldRepo, _recordRepo, Substitute.For<IUserRepository>(),
            enforcer, _relational, _formula);
    }

    private Task<ViewRowScope> Scope(TableAccessContext access, IReadOnlySet<long>? ready = null) =>
        ViewableRowIds.ResolveScopeAsync(access, _table, _fields, _recordRepo, _relational, _formula, default, ready);

    private TableAccessContext Access(FilterGroup? viewFilter, IReadOnlyList<AppField>? visible = null) => new()
    {
        Unrestricted = false,
        ViewScope = RecordScopes.AllRecords,
        ViewFilter = viewFilter,
        VisibleFields = visible ?? _fields,
    };

    private static FilterGroup Rule(int fid, string value) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = "eq", Value = value } }]
    };
}
