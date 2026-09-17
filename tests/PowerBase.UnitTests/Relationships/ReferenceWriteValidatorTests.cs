using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

public class ReferenceWriteValidatorTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();

    private const long ParentTableId = 99;
    private const int RefFid = 10;

    private static AppField RefField() =>
        new() { Id = RefFid, Fid = RefFid, Name = "Department", TypeCode = "Reference", Settings = "{\"relationshipId\":1,\"parentTableId\":99}" };

    private static AppField ScalarField(int fid, string name) =>
        new() { Id = fid, Fid = fid, Name = name, TypeCode = "Text" };

    private Task<Dictionary<long, object?>> Run(IReadOnlyDictionary<long, object?> values) =>
        ReferenceWriteValidator.ValidateAsync([RefField()], values, _tableRepo, _fieldRepo, _recordRepo, _relRepo, CancellationToken.None);

    public ReferenceWriteValidatorTests()
    {
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>())
            .Returns(new AppTable { Id = ParentTableId, Name = "Department" });
        _relRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Relationship { Id = 1, ParentTableId = ParentTableId, ChildTableId = 7, ReferenceFid = RefFid });
    }

    [Fact]
    public async Task Live_row_id_passes_through_untranslated()
    {
        _recordRepo.ExistsAsync(Arg.Any<AppTable>(), 42L, Arg.Any<CancellationToken>()).Returns(true);

        var overrides = await Run(new Dictionary<long, object?> { [RefFid] = "42" });

        overrides.Should().BeEmpty();
    }

    [Fact]
    public async Task Human_key_resolves_via_relationship_display_key()
    {
        // Relationship overrides the display key to parent fid 6 ("Code").
        _relRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Relationship { Id = 1, ParentTableId = ParentTableId, ChildTableId = 7, ReferenceFid = RefFid, DisplayKeyFieldId = 6 });
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { ScalarField(5, "Name"), ScalarField(6, "Code") });
        _recordRepo.ExistsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(false);
        _recordRepo.GetIdsByColumnValuesAsync(Arg.Any<AppTable>(), PowerBase.Domain.Constants.PhysicalNaming.ColumnName(6),
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, long> { ["ENG"] = 42L });

        var overrides = await Run(new Dictionary<long, object?> { [RefFid] = "ENG" });

        overrides[RefFid].Should().Be(42L);
    }

    [Fact]
    public async Task Human_key_resolves_via_table_key_when_no_relationship_override()
    {
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>())
            .Returns(new AppTable { Id = ParentTableId, Name = "Department", KeyFieldId = 5 });
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { ScalarField(5, "Name") });
        _recordRepo.ExistsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(false);
        _recordRepo.GetIdsByColumnValuesAsync(Arg.Any<AppTable>(), PowerBase.Domain.Constants.PhysicalNaming.ColumnName(5),
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, long> { ["Engineering"] = 7L });

        var overrides = await Run(new Dictionary<long, object?> { [RefFid] = "Engineering" });

        overrides[RefFid].Should().Be(7L);
    }

    [Fact]
    public async Task Unresolvable_value_throws()
    {
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>())
            .Returns(new AppTable { Id = ParentTableId, Name = "Department", KeyFieldId = 5 });
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { ScalarField(5, "Name") });
        _recordRepo.ExistsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(false);
        _recordRepo.GetIdsByColumnValuesAsync(Arg.Any<AppTable>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, long>());

        var act = () => Run(new Dictionary<long, object?> { [RefFid] = "Nope" });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Numeric_value_matching_a_real_row_id_wins_over_a_key_collision()
    {
        // "100" could be a Dept code, but a parent row with Id 100 exists → treated as the row Id.
        _recordRepo.ExistsAsync(Arg.Any<AppTable>(), 100L, Arg.Any<CancellationToken>()).Returns(true);

        var overrides = await Run(new Dictionary<long, object?> { [RefFid] = "100" });

        overrides.Should().BeEmpty();
        await _recordRepo.DidNotReceive().GetIdsByColumnValuesAsync(
            Arg.Any<AppTable>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blank_value_is_skipped()
    {
        var overrides = await Run(new Dictionary<long, object?> { [RefFid] = "" });

        overrides.Should().BeEmpty();
    }
}
