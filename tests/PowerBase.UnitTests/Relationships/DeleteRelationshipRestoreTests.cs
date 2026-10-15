using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Commands.DeleteRelationship;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

/// <summary>Deleting a relationship whose reference field was created with it: a reference holding data is kept as a real
/// field with the parent's key text on every row (as a converted field is), not deleted; an empty one is deleted.</summary>
public class DeleteRelationshipRestoreTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();

    private const long AppId = 10, ClientTableId = 170, SessionTableId = 171, RelId = 159;
    private const int TextTypeId = 1;

    private readonly AppTable _clients = new() { Id = ClientTableId, PublicId = Guid.NewGuid(), AppId = AppId, Name = "Clients", KeyFieldId = 2476 };
    private readonly AppTable _sessions = new() { Id = SessionTableId, PublicId = Guid.NewGuid(), AppId = AppId, Name = "Sessions" };
    private readonly AppField _clientName = new() { Id = 2476, Fid = 6, AppTableId = ClientTableId, Name = "Client Name", TypeCode = "Text", IsUnique = true };
    private readonly AppField _reference = new()
    {
        Id = 2484, PublicId = Guid.NewGuid(), Fid = 7, AppTableId = SessionTableId, Name = "Related Client", TypeCode = "Reference",
        Settings = $$"""{"relationshipId":{{RelId}},"parentTableId":{{ClientTableId}},"originalFid":9}""",
    };
    private readonly AppField _original = new() { Id = 2505, PublicId = Guid.NewGuid(), Fid = 9, AppTableId = SessionTableId, Name = "Related Client (original)", TypeCode = "Text" };
    private readonly Relationship _rel;

    public DeleteRelationshipRestoreTests()
    {
        _rel = new Relationship
        {
            Id = RelId, PublicId = Guid.NewGuid(), AppId = AppId, ParentTableId = ClientTableId, ChildTableId = SessionTableId,
            ReferenceFieldId = _reference.Id, ReferenceFid = 7, ReferenceFieldIsExisting = false,
        };
        _relRepo.GetByPublicIdAsync(_rel.PublicId, Arg.Any<CancellationToken>()).Returns(_rel);
        _tableRepo.GetByIdAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(_clients);
        _tableRepo.GetByIdAsync(SessionTableId, Arg.Any<CancellationToken>()).Returns(_sessions);
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField> { _clientName });
        _fieldRepo.ListByTableAsync(SessionTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField> { _reference, _original });
        _fieldTypeRepo.GetByCodeAsync("Text", Arg.Any<CancellationToken>()).Returns(new FieldType { Id = TextTypeId, Code = "Text" });
        _recordRepo.EncryptColumnTextsAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(ci => ci.ArgAt<IReadOnlyDictionary<long, string>>(2));
        _schemaEngine.RestoreColumnFromReferenceAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
    }

    private DeleteRelationshipCommandHandler Handler() => new(
        _tableRepo, _fieldRepo, _relRepo, _recordRepo, Substitute.For<IAuditRepository>(),
        fieldTypeRepo: _fieldTypeRepo, schemaEngine: _schemaEngine);

    private void GivenReferenceHoldsData() =>
        _recordRepo.HasAnyDataAsync(_sessions, _reference, Arg.Any<CancellationToken>()).Returns(true);

    [Fact]
    public async Task Delete_NewReferenceWithData_RewritesEveryRowAsKeyText_AndKeepsTheField()
    {
        GivenReferenceHoldsData();
        _recordRepo.ListAllRowsDecryptedAsync(_sessions, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["Id"] = 1L, ["f_7"] = 1L, ["f_9"] = null },
                new Dictionary<string, object?> { ["Id"] = 2L, ["f_7"] = 3L, ["f_9"] = null },
                new Dictionary<string, object?> { ["Id"] = 3L, ["f_7"] = null, ["f_9"] = "Ronak" },   // matched no client on import
                new Dictionary<string, object?> { ["Id"] = 4L, ["f_7"] = null, ["f_9"] = null },
            });
        _recordRepo.GetRowsByIdsAsync(_clients, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [1] = new Dictionary<string, object?> { ["f_6"] = "Johnss" },
                [3] = new Dictionary<string, object?> { ["f_6"] = "Roman" },
            });

        await Handler().HandleAsync(new DeleteRelationshipCommand(_rel.PublicId, Force: true));

        await _schemaEngine.Received(1).RestoreColumnFromReferenceAsync(
            _sessions,
            Arg.Is<AppField>(f => f.Id == _reference.Id && f.TypeCode == "Text"),
            Arg.Is<IReadOnlyDictionary<long, string>>(d =>
                d.Count == 3 && d[1] == "Johnss" && d[2] == "Roman" && d[3] == "Ronak"),
            Arg.Any<CancellationToken>());
        await _fieldRepo.Received(1).UpdateFieldTypeAsync(_reference.Id, TextTypeId, Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _fieldRepo.DidNotReceive().DeleteAsync(_reference.PublicId, Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _fieldRepo.Received(1).DeleteAsync(_original.PublicId, SessionTableId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NewReferenceWithRecordIdKeyAndNoOriginalText_RevertsToNumber()
    {
        _clients.KeyFieldId = null;
        _reference.Settings = $$"""{"relationshipId":{{RelId}},"parentTableId":{{ClientTableId}}}""";
        GivenReferenceHoldsData();

        await Handler().HandleAsync(new DeleteRelationshipCommand(_rel.PublicId, Force: true));

        await _fieldRepo.Received(1).RevertToNumberFieldAsync(_reference.Id, Arg.Any<CancellationToken>());
        await _fieldRepo.DidNotReceive().DeleteAsync(_reference.PublicId, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NewReferenceWithoutData_DeletesItAndItsOriginalField()
    {
        await Handler().HandleAsync(new DeleteRelationshipCommand(_rel.PublicId, Force: false));

        await _fieldRepo.Received(1).DeleteAsync(_reference.PublicId, SessionTableId, Arg.Any<CancellationToken>());
        await _fieldRepo.Received(1).DeleteAsync(_original.PublicId, SessionTableId, Arg.Any<CancellationToken>());
        await _schemaEngine.DidNotReceiveWithAnyArgs().RestoreColumnFromReferenceAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Delete_NewReferenceWhileItsTableIsDeleted_DeletesItWithoutRestoring()
    {
        GivenReferenceHoldsData();

        await Handler().HandleAsync(new DeleteRelationshipCommand(_rel.PublicId, Force: true, Restore: OriginalFieldRestore.None));

        await _fieldRepo.Received(1).DeleteAsync(_reference.PublicId, SessionTableId, Arg.Any<CancellationToken>());
        await _schemaEngine.DidNotReceiveWithAnyArgs().RestoreColumnFromReferenceAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Delete_NewReferenceThatCannotBeRebuilt_IsDeletedAsBefore()
    {
        GivenReferenceHoldsData();
        _recordRepo.ListAllRowsDecryptedAsync(_sessions, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _schemaEngine.RestoreColumnFromReferenceAsync(default!, default!, default!, default).ReturnsForAnyArgs(false);

        var act = () => Handler().HandleAsync(new DeleteRelationshipCommand(_rel.PublicId, Force: true));

        await act.Should().NotThrowAsync();
        await _fieldRepo.Received(1).DeleteAsync(_reference.PublicId, SessionTableId, Arg.Any<CancellationToken>());
    }
}
