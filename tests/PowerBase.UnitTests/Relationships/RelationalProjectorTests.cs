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
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();

    private RelationalProjector NewProjector() => new(_tableRepo, _fieldRepo, _recordRepo, _relRepo);

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
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = true });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 5 }, parentFields, parentRows);

        result[0][21].Should().Be(true);    // parent 1 has children
        result[1][21].Should().Be(false);   // parent 2 has none → Exists defaults to false
    }

    [Fact]
    public async Task Reference_projects_relationship_display_key_value_over_table_key()
    {
        // Child fid 10 → parent 99 via relationship 1. Relationship overrides the display key to
        // parent fid 6 ("Code"); the parent table's own KeyFieldId points at fid 5 ("Name").
        var childFields = new List<AppField> { Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}") };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L },
            new Dictionary<string, object?> { ["Id"] = 2L, [PhysicalNaming.ColumnName(10)] = null });

        var parentTable = new AppTable { Id = 99, Name = "Department", KeyFieldId = 5 };
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Field(5, "Name", "Text"), Field(6, "Code", "Text") });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 1, ReferenceFid = 10, ParentTableId = 99, ChildTableId = 7, DisplayKeyFieldId = 6 } });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Engineering", [PhysicalNaming.ColumnName(6)] = "ENG" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][10].Should().Be("ENG");     // linked → relationship display key ("Code"), not "Name"
        result[1].Should().NotContainKey(10); // unlinked → nothing projected (stored NULL shows as null)
    }

    [Fact]
    public async Task Reference_falls_back_to_table_key_then_record_id()
    {
        // No per-relationship display key. Parent table key = fid 5 → projects "Name".
        var childFields = new List<AppField> { Field(10, "Department", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}") };
        var childRows = Rows(new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L });

        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99, Name = "Department", KeyFieldId = 5 });
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(5, "Name", "Text") });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 1, ReferenceFid = 10, ParentTableId = 99, ChildTableId = 7, DisplayKeyFieldId = null } });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Engineering" } });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][10].Should().Be("Engineering");

        // And with no table key either: Record ID# ⇒ nothing projected, the stored row Id shows as-is.
        _tableRepo.GetByIdAsync(98, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 98, Name = "Team" });
        _fieldRepo.ListByTableAsync(98, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        var refToPlainParent = new List<AppField> { Field(12, "Team", "Reference", "{\"relationshipId\":2,\"parentTableId\":98}") };
        var rows2 = Rows(new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(12)] = 7L });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 2, ReferenceFid = 12, ParentTableId = 98, ChildTableId = 7 } });

        var result2 = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, refToPlainParent, rows2);
        result2[0].Should().NotContainKey(12);
    }

    [Fact]
    public async Task No_relationship_fields_yields_empty_maps()
    {
        var fields = new List<AppField> { Field(1, "Qty", "Number") };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 5m });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 1 }, fields, rows);

        result[0].Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_with_encrypted_parent_field_maps_decrypted_value_by_fid_or_physical_name()
    {
        // Child table: lookup (fid 11) points to parent field (fid 5, IsEncrypted = true)
        var childFields = new List<AppField>
        {
            Field(10, "Employee", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
            Field(11, "Employee - SSN", "Lookup", "{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":99,\"sourceFid\":5,\"sourceTypeCode\":\"Text\"}"),
        };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L });

        var parentTable = new AppTable { Id = 99, AppId = 1, Name = "Employee" };
        var ssnField = Field(5, "SSN", "Text");
        ssnField.IsEncrypted = true;
        ssnField.PhysicalColumnName = "SSN";

        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { ssnField });

        // RecordRepository decrypts and returns the row with f_5
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "123-45-6789" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][11].Should().Be("123-45-6789");
    }

    [Fact]
    public async Task Reference_display_key_with_encrypted_parent_field_projects_plaintext()
    {
        var childFields = new List<AppField>
        {
            Field(10, "Customer", "Reference", "{\"relationshipId\":1,\"parentTableId\":99}"),
        };
        var childRows = Rows(
            new Dictionary<string, object?> { ["Id"] = 1L, [PhysicalNaming.ColumnName(10)] = 42L });

        var parentTable = new AppTable { Id = 99, AppId = 1, Name = "Customer" };
        var nameField = Field(5, "Name", "Text");
        nameField.IsEncrypted = true;
        nameField.PhysicalColumnName = "CustName";

        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(parentTable);
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { nameField });
        _relRepo.ListByChildTableAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship>
            {
                new() { Id = 1, ReferenceFid = 10, ParentTableId = 99, ChildTableId = 7, DisplayKeyFieldId = 5 }
            });

        // RecordRepository returns decrypted row
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [42L] = new Dictionary<string, object?> { [PhysicalNaming.ColumnName(5)] = "Acme Corp" },
            });

        var result = await NewProjector().ProjectAsync(new AppTable { Id = 7 }, childFields, childRows);

        result[0][10].Should().Be("Acme Corp");
    }
}
