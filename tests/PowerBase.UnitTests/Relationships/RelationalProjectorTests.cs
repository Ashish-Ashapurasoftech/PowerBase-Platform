using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class RelationalProjectorTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    private RelationalProjector NewProjector() => new(_tableRepo, _fieldRepo, _recordRepo);

    private static AppField Field(int fid, string name, string typeCode, string? settings = null) => new()
    {
        Id = fid, Fid = fid, Name = name, TypeCode = typeCode, Settings = settings,
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows) =>
        rows.Cast<IReadOnlyDictionary<string, object?>>().ToList();

    [Fact]
    public async Task Lookup_pulls_parent_field_value_onto_child_rows()
    {
        // Child table: reference (fid 10) → parent table 99; lookup (fid 11) pulls parent field fid 5.
        var childFields = new List<AppField>
        {
            Field(10, "Product", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Product - Name", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":5,\"sourceTypeCode\":\"Text\"}"),
        };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L },
            new Dictionary<string, object?> { ["Id"] = 2L, [PhysicalNaming.ColumnName(10)] = null });

        var parentTable = new AppTable { Id = 99, Name = "Product" };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(5, "Name", "Text") });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Widget" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][11].Should().Be("Widget");      // linked → parent's Name
        result[1][11].Should().BeNull();           // unlinked → null
    }

    [Fact]
    public async Task Summary_count_rolls_up_child_records_onto_parent_rows()
    {
        // Parent table has a Count summary (fid 20) over child table 77 via reference fid 10.
        var parentFields = new List<AppField>
        {
            Field(20, "# of Items", "Summary", "{\"relationshipId\":1,\"childTableId\":77,\"referenceFid\":10,\"function\":\"Count\"}"),
        };
        var parentRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 2L });

        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Invoice Item" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 3 });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5 }, parentFields, parentRows);

        result[0][20].Should().Be(3);   // parent 1 has 3 children
        result[1][20].Should().Be(0);   // parent 2 has none → Count defaults to 0
    }

    [Fact]
    public async Task Summary_exists_rolls_up_true_false_onto_parent_rows()
    {
        // Parent table has an Exists (True/False) summary (fid 21) over child table 77 via reference fid 10.
        var parentFields = new List<AppField>
        {
            Field(21, "Has Items", "Summary", "{\"relationshipId\":1,\"childTableId\":77,\"referenceFid\":10,\"function\":\"Exists\"}"),
        };
        var parentRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L },
            new Dictionary<string, object?> { ["Id"] = 2L });

        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Invoice Item" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Exists", null,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = true });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5 }, parentFields, parentRows);

        result[0][21].Should().Be(true);    // parent 1 has children
        result[1][21].Should().Be(false);   // parent 2 has none → Exists defaults to false
    }

    [Fact]
    public async Task No_relationship_fields_yields_empty_maps()
    {
        var fields = new List<AppField> { Field(1, "Qty", "Number") };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 5m });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 1 }, fields, rows);

        result[0].Should().BeEmpty();
    }
}
