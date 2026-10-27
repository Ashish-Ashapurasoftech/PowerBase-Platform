using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Queries.GetDistinctFieldValues;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// Filter dropdowns (distinct values) and reference pickers (parent options) read values across many records, so
/// they must be limited to the rows the role may view — ViewableRowIds resolves those rows.
/// </summary>
public class ViewableRowIdsTests
{
    private const int DeptFid = 6;
    private const int RegionFid = 10; // a Formula field

    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRelationalProjector _relational = Substitute.For<IRelationalProjector>();
    private readonly IFormulaProjector _formula = Substitute.For<IFormulaProjector>();
    private readonly AppTable _table = new() { Id = 5, AppId = 3, PublicId = Guid.NewGuid() };
    private readonly IReadOnlyList<AppField> _fields =
    [
        new AppField { Id = 100, PublicId = Guid.NewGuid(), Fid = DeptFid, TypeCode = "Text", PhysicalColumnName = "f_6" },
        new AppField { Id = 101, PublicId = Guid.NewGuid(), Fid = RegionFid, TypeCode = "Formula" },
    ];

    // Rows 1 and 2 are in IT; Region (computed) is West for row 1 and East for row 2.
    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows =
    [
        new Dictionary<string, object?> { ["Id"] = 1L, ["f_6"] = "IT" },
        new Dictionary<string, object?> { ["Id"] = 2L, ["f_6"] = "IT" },
    ];

    public ViewableRowIdsTests()
    {
        _recordRepo.ListMatchingRowIdsAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
                Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1L, 2L });
        _recordRepo.ListAllMatchingAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
                Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Rows);
        _relational.ProjectAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(Rows.Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
        _formula.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(new List<IReadOnlyDictionary<long, object?>>
            {
                new Dictionary<long, object?> { [RegionFid] = "West" },
                new Dictionary<long, object?> { [RegionFid] = "East" },
            });
    }

    [Fact]
    public async Task Unrestricted_role_reads_every_row()
    {
        (await Resolve(new TableAccessContext { Unrestricted = true })).Should().BeNull();
        (await Resolve(Access(viewFilter: null))).Should().BeNull();
    }

    [Fact]
    public async Task Denied_table_reads_no_row()
    {
        (await Resolve(Access(viewFilter: null, scope: RecordScopes.None))).Should().BeEmpty();
    }

    [Fact]
    public async Task Physical_rule_is_applied_in_SQL()
    {
        var rule = Rule(DeptFid, "IT");

        (await Resolve(Access(rule))).Should().BeEquivalentTo(new[] { 1L, 2L });
        await _recordRepo.Received().ListMatchingRowIdsAsync(_table, _fields,
            Arg.Is<FilterGroup?>(g => g != null && g.Nodes.Single().Condition!.FieldId == DeptFid),
            Arg.Any<long?>(), Arg.Any<int>(), 30, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Formula_rule_is_applied_in_memory()
    {
        (await Resolve(Access(Rule(RegionFid, "West")))).Should().BeEquivalentTo(new[] { 1L });
    }

    [Fact]
    public async Task Own_records_scope_is_passed_to_SQL()
    {
        var access = new TableAccessContext { Unrestricted = false, ViewScope = RecordScopes.OwnRecords, RestrictToCreatedBy = 77 };

        await Resolve(access);

        await _recordRepo.Received().ListMatchingRowIdsAsync(_table, _fields, Arg.Any<FilterGroup?>(),
            77, Arg.Any<int>(), 30, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Distinct_values_read_only_viewable_rows()
    {
        var enforcer = Substitute.For<IRolePermissionEnforcer>();
        var access = Access(Rule(RegionFid, "West"), visible: _fields);
        enforcer.GetTableAccessAsync(_table, _fields, Arg.Any<CancellationToken>()).Returns(access);
        var handler = Distinct(enforcer);

        await handler.HandleAsync(new GetDistinctFieldValuesQuery(_table.PublicId, DeptFid));

        await _recordRepo.Received().GetDistinctFieldValuesAsync(_table, _fields[0], Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Is<IReadOnlyCollection<long>?>(ids => ids != null && ids.SequenceEqual(new[] { 1L })));
    }

    [Fact]
    public async Task Distinct_values_of_a_hidden_field_are_empty()
    {
        var enforcer = Substitute.For<IRolePermissionEnforcer>();
        enforcer.GetTableAccessAsync(_table, _fields, Arg.Any<CancellationToken>())
            .Returns(Access(viewFilter: null, visible: [_fields[1]])); // Dept is hidden from the role

        var result = await Distinct(enforcer).HandleAsync(new GetDistinctFieldValuesQuery(_table.PublicId, DeptFid));

        result.Values.Should().BeEmpty();
        await _recordRepo.DidNotReceiveWithAnyArgs().GetDistinctFieldValuesAsync(default!, default!, default);
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

    private Task<IReadOnlyCollection<long>?> Resolve(TableAccessContext access) =>
        ViewableRowIds.ResolveAsync(access, _table, _fields, _recordRepo, _relational, _formula);

    private TableAccessContext Access(FilterGroup? viewFilter, string scope = RecordScopes.AllRecords,
        IReadOnlyList<AppField>? visible = null) => new()
    {
        Unrestricted = false,
        ViewScope = scope,
        ViewFilter = viewFilter,
        VisibleFields = visible ?? _fields,
    };

    private static FilterGroup Rule(long fieldId, string value) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = "eq", Value = value } }],
    };
}
