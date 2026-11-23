using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Read paths added after Phase 5: Summary/Chart reports, role rules (distinct values, reference pickers).</summary>
public class ReadPathFollowUpTests
{
    private const long Dept = 10, Emp = 11;

    private static AppField Sum(int fid, long id) => new()
    {
        Id = id, AppTableId = Dept, Fid = fid, TypeCode = "Summary", Name = $"s{fid}",
        Settings = System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = Emp, ReferenceFid = 6, Function = "Sum", TargetFid = 7, TargetTypeCode = "Number" })
    };

    private static AppField Plain(int fid, string type = "Text") => new() { Id = Dept * 100 + fid, AppTableId = Dept, Fid = fid, TypeCode = type, Name = $"p{fid}" };

    private static FilterGroup Rule(int fid, string op, string value) => new()
    {
        Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value } }]
    };

    [Fact]
    public void Summary_report_hands_sql_stand_ins_for_ready_fields_rows_and_series()
    {
        var total = Sum(8, 5008);
        var other = Sum(9, 5009);
        var name = Plain(6);
        var ready = new HashSet<long> { total.Id };

        var s = SummarySqlFields.For(ready, [name, total, other], [(total, "Equal"), (name, "Equal")], total);

        s.Fields[1].PhysicalColumnName.Should().Be("c_8");
        s.Fields[2].Should().BeSameAs(other, "not Ready");
        s.RowGroups[0].Field.PhysicalColumnName.Should().Be("c_8");
        s.RowGroups[0].Mode.Should().Be("Equal");
        s.RowGroups[1].Field.Should().BeSameAs(name);
        s.Series!.PhysicalColumnName.Should().Be("c_8");
        total.TypeCode.Should().Be("Summary", "the original is never changed");
    }

    [Fact]
    public void Summary_report_with_nothing_ready_keeps_every_field()
    {
        var total = Sum(8, 5008);
        var s = SummarySqlFields.For(new HashSet<long>(), [total], [(total, "Equal")], null);
        s.Fields[0].Should().BeSameAs(total);
        s.RowGroups[0].Field.Should().BeSameAs(total);
        s.Series.Should().BeNull();
    }

    private static (IRecordRepository Repo, IRelationalProjector Rel, IFormulaProjector Formula) Mocks()
    {
        var repo = Substitute.For<IRecordRepository>();
        repo.ListMatchingRowIdsAsync(default!, default!, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<long>>(new List<long> { 1, 2 }));
        repo.ListAllMatchingAsync(default!, default!, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>()));
        return (repo, Substitute.For<IRelationalProjector>(), Substitute.For<IFormulaProjector>());
    }

    [Fact]
    public async Task Role_rule_on_a_ready_stored_field_runs_wholly_in_sql_through_the_stand_in()
    {
        var total = Sum(8, 5008);
        var fields = new List<AppField> { Plain(6), total };
        var access = new TableAccessContext { ViewScope = RecordScopes.AllRecords, ViewFilter = Rule(8, "GreaterThan", "100") };
        var (repo, rel, formula) = Mocks();

        var ids = await ViewableRowIds.ResolveAsync(access, new AppTable { Id = Dept }, fields, repo, rel, formula,
            CancellationToken.None, new HashSet<long> { total.Id });

        ids.Should().BeEquivalentTo(new[] { 1L, 2L });
        await repo.Received(1).ListMatchingRowIdsAsync(Arg.Any<AppTable>(),
            Arg.Is<IReadOnlyList<AppField>>(f => f.Any(x => x.PhysicalColumnName == "c_8")),
            access.ViewFilter, Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceiveWithAnyArgs().ListAllMatchingAsync(default!, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task Role_rule_on_a_stored_field_that_is_not_ready_keeps_the_in_memory_path()
    {
        var total = Sum(8, 5008);
        var fields = new List<AppField> { Plain(6), total };
        var access = new TableAccessContext { ViewScope = RecordScopes.AllRecords, ViewFilter = Rule(8, "GreaterThan", "100") };
        var (repo, rel, formula) = Mocks();

        await ViewableRowIds.ResolveAsync(access, new AppTable { Id = Dept }, fields, repo, rel, formula,
            CancellationToken.None, new HashSet<long>());

        await repo.DidNotReceiveWithAnyArgs().ListMatchingRowIdsAsync(default!, default!, default, default, default, default, default);
        await repo.ReceivedWithAnyArgs(1).ListAllMatchingAsync(default!, default!, default, default, default, default, default);
    }
}
