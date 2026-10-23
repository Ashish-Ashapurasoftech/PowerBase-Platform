using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
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
/// Writes by record id (update, delete, mass update, bulk delete, buttons) must respect the View Custom Rules
/// as well as the Modify ones: a record the user cannot see cannot be changed.
/// </summary>
public class RecordModifyGuardViewRulesTests
{
    private const int RegionFid = 10;

    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly RecordModifyGuard _guard;
    private readonly AppTable _table = new() { Id = 5, AppId = 3 };
    private readonly IReadOnlyList<AppField> _fields =
        [new AppField { Id = 101, PublicId = Guid.NewGuid(), Fid = RegionFid, TypeCode = "Text", PhysicalColumnName = "f_10" }];
    private readonly Guid _westRecord = Guid.NewGuid();

    public RecordModifyGuardViewRulesTests()
    {
        // The repository evaluates the filter against one record whose Region is West.
        _recordRepo.ExistsWithViewFilterAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), _westRecord,
                Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Matches(ci.ArgAt<FilterGroup?>(3), "West"));
        _guard = new RecordModifyGuard(_recordRepo, Substitute.For<IRelationalProjector>(), Substitute.For<IFormulaProjector>());
    }

    [Fact]
    public async Task Update_of_a_record_outside_the_View_rules_is_refused()
    {
        var access = Access(view: RegionIs("East"), modify: null);

        var act = () => _guard.EnsureAsync(access, _table, _fields, [_westRecord]);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task Update_of_a_record_inside_the_View_rules_is_allowed()
    {
        var access = Access(view: RegionIs("West"), modify: null);

        await _guard.EnsureAsync(access, _table, _fields, [_westRecord]);
    }

    [Fact]
    public async Task Record_must_match_View_and_Modify_rules()
    {
        var access = Access(view: RegionIs("West"), modify: RegionIs("East"));

        var act = () => _guard.EnsureAsync(access, _table, _fields, [_westRecord]);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task Bulk_filter_drops_records_outside_the_View_rules()
    {
        var access = Access(view: RegionIs("East"), modify: null);

        var allowed = await _guard.FilterAllowedAsync(access, _table, _fields, [_westRecord]);

        allowed.Should().BeEmpty();
    }

    [Fact]
    public async Task Button_check_uses_View_rules_only()
    {
        await _guard.EnsureVisibleAsync(Access(view: RegionIs("West"), modify: RegionIs("East")), _table, _fields, _westRecord);

        var act = () => _guard.EnsureVisibleAsync(Access(view: RegionIs("East"), modify: null), _table, _fields, _westRecord);
        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task Unrestricted_user_is_never_checked()
    {
        var access = new TableAccessContext { Unrestricted = true, ViewFilter = RegionIs("East") };

        await _guard.EnsureAsync(access, _table, _fields, [_westRecord]);
    }

    private static TableAccessContext Access(FilterGroup? view, FilterGroup? modify) => new()
    {
        Unrestricted = false,
        ViewScope = RecordScopes.AllRecords,
        ModifyScope = RecordScopes.AllRecords,
        ViewFilter = view,
        ModifyFilter = modify,
    };

    private static FilterGroup RegionIs(string value) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = RegionFid, Operator = "eq", Value = value } }],
    };

    private static bool Matches(FilterGroup? group, string region)
    {
        if (group is null) return true;
        var results = group.Nodes.Select(n => n.Condition is { } c ? c.Value == region : Matches(n.Group, region));
        return group.Logic == "or" ? results.Any(r => r) : results.All(r => r);
    }
}
