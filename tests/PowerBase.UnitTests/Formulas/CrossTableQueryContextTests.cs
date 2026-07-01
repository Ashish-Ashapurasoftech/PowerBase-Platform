using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Querying;

namespace PowerBase.UnitTests.Formulas;

public class CrossTableQueryContextTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    private readonly Guid _tableGuid = Guid.NewGuid();

    private static AppField Field(int fid, string name, string typeCode) =>
        new() { Id = fid, Fid = fid, Name = name, TypeCode = typeCode };

    private CrossTableQueryContext NewContext()
    {
        var table = new AppTable { Id = 9, PublicId = _tableGuid, Name = "Items" };
        _tableRepo.GetByPublicIdAsync(_tableGuid, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(9, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(7, "Qty", "Number"), Field(8, "Name", "Text") });
        return new CrossTableQueryContext(_tableRepo, _fieldRepo, _recordRepo, currentTable: null);
    }

    [Fact]
    public void QueryRecords_translates_query_and_returns_ids()
    {
        FilterGroup? captured = null;
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
                Arg.Do<FilterGroup?>(f => captured = f), Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["Id"] = 1L },
                new Dictionary<string, object?> { ["Id"] = 2L },
            });

        RecordQueryParser.TryParse("{8.EX.'Open'}AND{7.GT.5}", out var query).Should().BeTrue();
        var ids = NewContext().QueryRecords(_tableGuid.ToString(), query);

        ids.Should().Equal(1L, 2L);
        captured.Should().NotBeNull();
        captured!.Logic.Should().Be("and");
        captured.Nodes.Should().HaveCount(2);
        captured.Nodes[0].Condition!.FieldId.Should().Be(8);
        captured.Nodes[0].Condition!.Operator.Should().Be("eq");   // EX → eq
        captured.Nodes[1].Condition!.Operator.Should().Be("gt");   // GT → gt
    }

    [Fact]
    public void GetFieldValues_maps_values_in_id_order()
    {
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [1L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(7)] = 10m },
                [2L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(7)] = 30m },
            });

        var values = NewContext().GetFieldValues(_tableGuid.ToString(), new[] { 1L, 2L }, 7);

        values.Should().Equal(10m, 30m);
    }

    [Fact]
    public void Unresolvable_table_yields_empty()
    {
        var ctx = new CrossTableQueryContext(_tableRepo, _fieldRepo, _recordRepo, currentTable: null);
        ctx.QueryRecords("not-a-guid", RecordQuery.Empty).Should().BeEmpty();
    }
}
