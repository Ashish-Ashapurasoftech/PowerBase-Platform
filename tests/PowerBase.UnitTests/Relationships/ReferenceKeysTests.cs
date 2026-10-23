using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

public class ReferenceKeysTests
{
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();

    // Sessions (77): Name (Text, 7), "Related Client" (Reference created as one, 12) to Clients (9), "Related Trainer"
    // (Reference converted from a Text field, 13) to Trainers (8). Clients' key is Client Name (fid 6); Trainers set none.
    private static readonly List<AppField> ChildFields =
    [
        new() { Id = 7, Fid = 7, AppTableId = 77, Name = "Name", TypeCode = "Text" },
        new() { Id = 12, Fid = 12, AppTableId = 77, Name = "Related Client", TypeCode = "Reference",
            Settings = """{"relationshipId":181,"parentTableId":9,"originalTypeCode":null}""" },
        new() { Id = 13, Fid = 13, AppTableId = 77, Name = "Related Trainer", TypeCode = "Reference",
            Settings = """{"relationshipId":182,"parentTableId":8,"originalTypeCode":"Text"}""" },
    ];

    private static readonly Dictionary<long, AppField> ChildFieldsByFid = ChildFields.ToDictionary(f => (long)f.Fid!.Value);

    private static readonly AppField ClientName = new() { Id = 906, Fid = 6, AppTableId = 9, Name = "Client Name", TypeCode = "Text" };

    public ReferenceKeysTests()
    {
        _relRepo.ListByChildTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 181, ParentTableId = 9, ChildTableId = 77, ReferenceFid = 12 },
            new() { Id = 182, ParentTableId = 8, ChildTableId = 77, ReferenceFid = 13 },
        });
        _tableRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 9, KeyFieldId = 906 });
        _tableRepo.GetByIdAsync(8, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 8 });
        _fieldRepo.ListByTableAsync(9, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 903, Fid = 3, AppTableId = 9, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
            ClientName,
            new() { Id = 907, Fid = 7, AppTableId = 9, Name = "Client Code", TypeCode = "Formula_Text" },
            new() { Id = 908, Fid = 8, AppTableId = 9, Name = "Started", TypeCode = "Date" },
        });
        _fieldRepo.ListByTableAsync(8, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 803, Fid = 3, AppTableId = 8, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
            new() { Id = 806, Fid = 6, AppTableId = 8, Name = "Trainer Name", TypeCode = "Text" },
        });
    }

    private Task<IReadOnlyDictionary<long, AppField>> Load() =>
        ReferenceKeys.LoadAsync(77, ChildFields, _relRepo, _tableRepo, _fieldRepo, CancellationToken.None);

    private static FilterNode Cond(long fieldId, string op, string? value, string? valueMode = null, long? valueFieldId = null) =>
        new() { Condition = new FilterCondition { FieldId = fieldId, Operator = op, Value = value, ValueMode = valueMode, ValueFieldId = valueFieldId } };

    [Fact]
    public async Task A_reference_created_as_one_shows_its_parents_Set_Key()
    {
        var keys = await Load();

        keys[12].Should().BeSameAs(ClientName);
        ReferenceKeys.KeyTypeCode(ChildFieldsByFid[12], keys).Should().Be("Text");
    }

    [Fact]
    public async Task A_reference_whose_parent_sets_no_key_stays_its_Record_ID_even_one_converted_from_text()
    {
        var keys = await Load();

        keys.Should().NotContainKey(13);
        ReferenceKeys.KeyTypeCode(ChildFieldsByFid[13], keys).Should().BeNull();
        ReferenceKeys.ComparesAsKey(ChildFieldsByFid[13], keys).Should().BeFalse();
    }

    [Fact]
    public async Task The_relationships_own_display_key_wins_over_the_parents_Set_Key()
    {
        _relRepo.ListByChildTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 181, ParentTableId = 9, ChildTableId = 77, ReferenceFid = 12, DisplayKeyFieldId = 908 },
        });

        ReferenceKeys.KeyTypeCode(ChildFieldsByFid[12], await Load()).Should().Be("Date");
    }

    [Theory]
    [InlineData(907L)]   // a calculated key has no column to read
    [InlineData(903L)]   // Record ID# itself
    public async Task A_key_with_no_column_of_its_own_leaves_the_reference_its_Record_ID(long keyFieldId)
    {
        _relRepo.ListByChildTableAsync(77, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 181, ParentTableId = 9, ChildTableId = 77, ReferenceFid = 12, DisplayKeyFieldId = keyFieldId },
        });

        (await Load()).Should().NotContainKey(12);
    }

    [Fact]
    public async Task A_table_without_references_loads_nothing()
    {
        var keys = await ReferenceKeys.LoadAsync(77, [ChildFields[0]], _relRepo, _tableRepo, _fieldRepo, CancellationToken.None);

        keys.Should().BeEmpty();
        await _relRepo.DidNotReceiveWithAnyArgs().ListByChildTableAsync(default, default);
    }

    [Fact]
    public void Only_a_reference_among_the_read_fields_needs_the_keys()
    {
        ReferenceKeys.AnyReference(ChildFields, [7, 12]).Should().BeTrue();
        ReferenceKeys.AnyReference(ChildFields, [7]).Should().BeFalse();
    }

    [Fact]
    public async Task A_keyed_reference_is_compared_through_a_lookup_of_its_parents_key()
    {
        var filter = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                Cond(12, "contains", "Acme"),
                new FilterNode { Group = new FilterGroup { Logic = "or", Nodes = [Cond(7, "eq", null, "field", 12)] } },
                Cond(13, "eq", "5"),
            ],
        };

        var keys = ReferenceKeys.StandIn(filter, null, ChildFieldsByFid, await Load());

        var standIn = ReferenceKeys.StandInFid(12);
        keys.Should().NotBeNull();
        keys!.Value.Filter!.Nodes[0].Condition!.FieldId.Should().Be(standIn);
        keys.Value.Filter.Nodes[1].Group!.Nodes[0].Condition!.ValueFieldId.Should().Be(standIn);
        keys.Value.Filter.Nodes[2].Condition!.FieldId.Should().Be(13);   // no key set: its Record ID#
        filter.Nodes[0].Condition!.FieldId.Should().Be(12);              // the saved criteria are left alone

        var lookup = keys.Value.Lookups.Single();
        lookup.Fid.Should().Be(standIn);
        SummaryLookupSources.Settings(lookup).Should().BeEquivalentTo(new LookupSettings
        {
            RelationshipId = 181, ReferenceFid = 12, SourceTableId = 9, SourceFid = 6, SourceTypeCode = "Text",
        });
        keys.Value.Sources[standIn].Should().BeSameAs(ClientName);
    }

    [Fact]
    public async Task Criteria_without_a_keyed_reference_are_left_as_they_are() =>
        ReferenceKeys.StandIn(new FilterGroup { Nodes = [Cond(13, "eq", "5"), Cond(7, "eq", "x")] }, null, ChildFieldsByFid, await Load())
            .Should().BeNull();

    [Fact]
    public async Task A_keyed_reference_summarized_is_read_as_its_parents_key()
    {
        var keys = ReferenceKeys.StandIn(null, 12, ChildFieldsByFid, await Load());

        keys!.Value.Filter.Should().BeNull();
        keys.Value.Moved[12].Should().Be(ReferenceKeys.StandInFid(12));
        SummaryLookupSources.Settings(keys.Value.Lookups.Single())!.SourceFid.Should().Be(6);
    }

    [Fact]
    public async Task A_reference_with_no_key_summarized_stays_its_Record_ID() =>
        ReferenceKeys.StandIn(null, 13, ChildFieldsByFid, await Load()).Should().BeNull();
}
