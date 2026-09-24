using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.UpdateDisplayKey;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// Covers UpdateDisplayKeyCommandHandler's auto-chain behavior: when a relationship's display
/// key override changes away from a field (back to Standard, or to a different override), the
/// field that was the old key should be left behind as a real Lookup field on the child — unless
/// one already exists, or its label collides with something else on the child.
/// </summary>
public class UpdateDisplayKeyCommandHandlerTests
{
    private const long ParentTableId = 10;
    private const long ChildTableId = 20;
    private const long RelId = 1;
    private const int ReferenceFid = 8;
    private static readonly Guid RelPublicId = Guid.NewGuid();

    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private readonly UpdateDisplayKeyCommandHandler _handler;

    private static AppTable Table(long id, string name) => new() { Id = id, PublicId = Guid.NewGuid(), Name = name };

    private static AppField Field(long id, int fid, string name, string typeCode, bool isUnique = false, string? settings = null) =>
        new() { Id = id, Fid = fid, Name = name, Label = name, TypeCode = typeCode, IsUnique = isUnique, Settings = settings, PublicId = Guid.NewGuid() };

    /// <summary>A Lookup field on the child shaped like one the carry-over auto-chain would have left
    /// behind for <paramref name="source"/> during an earlier switch-away from it as the alternate key.</summary>
    private static AppField StaleLookup(long id, int fid, AppField source) =>
        Field(id, fid, source.Label ?? source.Name, "Lookup",
            settings: $"{{\"relationshipId\":{RelId},\"referenceFid\":{ReferenceFid},\"sourceTableId\":{ParentTableId},\"sourceFid\":{source.Fid},\"sourceTypeCode\":\"{source.TypeCode}\"}}");

    /// <summary>DepartmentCode: the field commonly used as an override display key in these tests.</summary>
    private static readonly AppField DeptCode = Field(40, 7, "Department Code", "Text", isUnique: true);
    private static readonly AppField DeptName = Field(39, 6, "Department Name", "Text");

    /// <summary>The seeded system Record ID# field (Fid 3) — present on every real parent table;
    /// only fixtures that include it exercise the Standard-key carry-over/removal paths.</summary>
    private static readonly AppField RecordId = Field(3, 3, "Record ID#", "Number", isUnique: true);

    public UpdateDisplayKeyCommandHandlerTests()
    {
        var fieldFactory = new RelationshipFieldFactory(_fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _nameResolver);
        var carryOver = new RelationshipKeyCarryOverService(_fieldRepo, _relRepo, fieldFactory);
        var queries = new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo);
        _handler = new UpdateDisplayKeyCommandHandler(_relRepo, _tableRepo, _fieldRepo, carryOver, queries, _auditRepo);

        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(Table(ParentTableId, "Department"));
        _tableRepo.GetByIdAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(Table(ChildTableId, "Employee"));

        _fieldTypeRepo.GetByCodeAsync("Lookup", Arg.Any<CancellationToken>()).Returns(new FieldType { Id = 99, Code = "Lookup" });
        _nameResolver.GenerateUniqueNameAsync(ChildTableId, Arg.Any<string>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => "C_" + ci.ArgAt<string>(1).Replace(" ", ""));
        _fieldRepo.GetNextFidAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(100);
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>()).Returns((501L, Guid.NewGuid()));
        _fieldRepo.LabelExistsInTableAsync(ChildTableId, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Form>());
    }

    private void SetParentFields(params AppField[] fields) =>
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)fields.ToList());

    private void SetChildFields(params AppField[] fields) =>
        _fieldRepo.ListByTableAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)fields.ToList());

    private void SetRelationship(long? displayKeyFieldId, long? proxyFieldId = null) =>
        _relRepo.GetByPublicIdAsync(RelPublicId, Arg.Any<CancellationToken>()).Returns(new Relationship
        {
            Id = RelId,
            PublicId = RelPublicId,
            ParentTableId = ParentTableId,
            ChildTableId = ChildTableId,
            ReferenceFieldId = 41,
            ReferenceFid = ReferenceFid,
            ProxyFieldId = proxyFieldId,
            DisplayKeyFieldId = displayKeyFieldId,
        });

    [Fact]
    public async Task Handle_ResetOverrideToStandard_CreatesLookupForOldKeyField()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id, proxyFieldId: null);
        SetParentFields(DeptName, DeptCode);
        SetChildFields(); // no existing lookups yet

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await _fieldRepo.Received(1).CreateAsync(
            Arg.Is<AppField>(f => f.TypeCode == "Lookup" && f.Label == "Department Code"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ResetOverrideToStandard_NewLookupCarriesCorrectSettings()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();

        AppField? created = null;
        _fieldRepo.WhenForAnyArgs(r => r.CreateAsync(default!, default)).Do(ci => created = ci.Arg<AppField>());

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        created.Should().NotBeNull();
        var settings = JsonSerializer.Deserialize<LookupSettings>(created!.Settings!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        settings.Should().NotBeNull();
        settings!.RelationshipId.Should().Be(RelId);
        settings.ReferenceFid.Should().Be(ReferenceFid);
        settings.SourceTableId.Should().Be(ParentTableId);
        settings.SourceFid.Should().Be(DeptCode.Fid);
        settings.SourceTypeCode.Should().Be("Text");
    }

    [Fact]
    public async Task Handle_SwitchToDifferentOverride_CreatesLookupForPreviousKeyField()
    {
        var region = Field(50, 9, "Region", "Text", isUnique: true);
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode, region);
        SetChildFields();

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: region.Fid));

        await _fieldRepo.Received(1).CreateAsync(
            Arg.Is<AppField>(f => f.TypeCode == "Lookup" && f.Label == "Department Code"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StandardToOverride_CarriesOverRecordIdAsLookup()
    {
        // On a realistic parent table (Record ID# field present), switching away from Standard
        // is exactly like switching away from any other key field — it must leave a lookup behind.
        // Label is qualified with the parent's name ("Department Record ID#") because every table —
        // including the child — already has its own native field literally labeled "Record ID#";
        // a bare label would always collide and silently skip creation.
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode, RecordId);
        SetChildFields();

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _fieldRepo.Received(1).CreateAsync(
            Arg.Is<AppField>(f => f.TypeCode == "Lookup" && f.Label == "Department Record ID#"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StandardToOverride_RecordIdLookupCarriesCorrectSettings()
    {
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode, RecordId);
        SetChildFields();

        AppField? created = null;
        _fieldRepo.WhenForAnyArgs(r => r.CreateAsync(default!, default)).Do(ci => created = ci.Arg<AppField>());

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        created.Should().NotBeNull();
        var settings = JsonSerializer.Deserialize<LookupSettings>(created!.Settings!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        settings.Should().NotBeNull();
        settings!.RelationshipId.Should().Be(RelId);
        settings.ReferenceFid.Should().Be(ReferenceFid);
        settings.SourceTableId.Should().Be(ParentTableId);
        settings.SourceFid.Should().Be(3);
        settings.SourceTypeCode.Should().Be("Number");
    }

    [Fact]
    public async Task Handle_StandardToOverride_RecordIdLabelAvoidsCollisionWithChildsOwnRecordId()
    {
        // Every child table has its own native field labeled "Record ID#" — simulate that collision
        // explicitly and confirm the parent-qualified label is used instead of the bare one, so the
        // carry-over isn't silently skipped by the label-collision guard every single time.
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode, RecordId);
        SetChildFields();
        _fieldRepo.LabelExistsInTableAsync(ChildTableId, "Record ID#", Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(true);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _fieldRepo.Received(1).CreateAsync(
            Arg.Is<AppField>(f => f.TypeCode == "Lookup" && f.Label == "Department Record ID#"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StandardToOverride_RecordIdLookupAlreadyExists_DoesNotDuplicate()
    {
        var existingLookup = StaleLookup(60, 13, RecordId);
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode, RecordId);
        SetChildFields(existingLookup);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _fieldRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Handle_SwitchBackToStandard_RemovesStaleRecordIdLookup()
    {
        // Symmetric case: DepartmentCode was the override (an earlier switch-away from Standard
        // left a "Record ID#" lookup behind); switching back to Standard makes that lookup redundant.
        var staleLookup = StaleLookup(60, 13, RecordId);
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode, RecordId);
        SetChildFields(staleLookup);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await _fieldRepo.Received(1).DeleteAsync(staleLookup.PublicId, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SameOverrideSavedAgain_DoesNotCreateLookup()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _fieldRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Handle_LookupForOldKeyAlreadyExists_DoesNotDuplicate()
    {
        var existingLookup = Field(60, 13, "Department - Department Code", "Lookup",
            settings: $"{{\"relationshipId\":{RelId},\"referenceFid\":{ReferenceFid},\"sourceTableId\":{ParentTableId},\"sourceFid\":{DeptCode.Fid},\"sourceTypeCode\":\"Text\"}}");
        SetRelationship(displayKeyFieldId: DeptCode.Id, proxyFieldId: existingLookup.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields(existingLookup);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await _fieldRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Handle_LabelCollisionOnChild_SkipsCreationWithoutThrowing()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();
        _fieldRepo.LabelExistsInTableAsync(ChildTableId, "Department Code", Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(true);

        var act = () => _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await act.Should().NotThrowAsync();
        await _fieldRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Handle_NoExistingProxy_NewCarriedOverLookupBecomesProxy()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id, proxyFieldId: null);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, 501L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ProxyAlreadyExists_DoesNotReassignProxy()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id, proxyFieldId: 999L);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await _relRepo.DidNotReceiveWithAnyArgs().UpdateProxyFieldAsync(default, default, default);
    }

    [Fact]
    public async Task Handle_UnknownDisplayKeyFid_ThrowsValidationException()
    {
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();

        var act = () => _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: 999));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Handle_NonUniqueFieldAsOverride_ThrowsValidationException()
    {
        var nonUnique = Field(70, 11, "Notes", "Text", isUnique: false);
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode, nonUnique);
        SetChildFields();

        var act = () => _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: nonUnique.Fid));

        await act.Should().ThrowAsync<ValidationException>();
        await _relRepo.DidNotReceiveWithAnyArgs().UpdateDisplayKeyFieldAsync(default, default, default);
    }

    [Fact]
    public async Task Handle_RelationshipNotFound_ThrowsNotFoundException()
    {
        _relRepo.GetByPublicIdAsync(RelPublicId, Arg.Any<CancellationToken>()).Returns((Relationship?)null);

        var act = () => _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Handle_CarriedOverLookup_AppendedToAutoAddForms()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();
        var childPublicId = Guid.NewGuid();
        _tableRepo.GetByIdAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ChildTableId, PublicId = childPublicId, Name = "Employee" });

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await _formRepo.Received(1).ListByTableAsync(childPublicId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CarriedOverLookup_AuditMessageNotesIt()
    {
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: null));

        await _auditRepo.Received(1).LogActivityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string>(title => title != null && title.Contains("Department Code") && title.Contains("lookup")),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoCarryOver_AuditMessageOmitsLookupNote()
    {
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode);
        SetChildFields();

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _auditRepo.Received(1).LogActivityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string>(title => title != null && !title.Contains("lookup")),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Switching back to a previously-carried-over key removes the now-redundant lookup ───────

    [Fact]
    public async Task Handle_SwitchBackToFieldWithStaleLookup_RemovesIt()
    {
        var staleLookup = StaleLookup(60, 13, DeptCode);
        SetRelationship(displayKeyFieldId: null); // Standard — DeptCode was carried over earlier
        SetParentFields(DeptName, DeptCode);
        SetChildFields(staleLookup);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _fieldRepo.Received(1).DeleteAsync(staleLookup.PublicId, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StaleLookupWasProxy_ClearsProxyBeforeDeleting()
    {
        var staleLookup = StaleLookup(60, 13, DeptCode);
        SetRelationship(displayKeyFieldId: null, proxyFieldId: staleLookup.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields(staleLookup); // no other lookup to hand the proxy role to

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, (long?)null, Arg.Any<CancellationToken>());
        await _fieldRepo.Received(1).DeleteAsync(staleLookup.PublicId, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StaleLookupWasProxy_OtherLookupExists_ReassignsProxyToIt()
    {
        var staleLookup = StaleLookup(60, 13, DeptCode);
        var otherLookup = StaleLookup(61, 14, DeptName);
        SetRelationship(displayKeyFieldId: null, proxyFieldId: staleLookup.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields(staleLookup, otherLookup);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, otherLookup.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SwitchToOverride_NoStaleLookup_DoesNotDelete()
    {
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode);
        SetChildFields(); // nothing to clean up

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _fieldRepo.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task Handle_SameOverrideSavedAgain_DoesNotDeleteItsOwnLookup()
    {
        // A Lookup field for DeptCode existing while DeptCode is ALSO the current override is an
        // unrelated (e.g. manually added) field — re-saving the same override must not touch it.
        var unrelatedLookup = StaleLookup(60, 13, DeptCode);
        SetRelationship(displayKeyFieldId: DeptCode.Id);
        SetParentFields(DeptName, DeptCode);
        SetChildFields(unrelatedLookup);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _fieldRepo.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task Handle_RemovedStaleLookup_AuditMessageNotesIt()
    {
        var staleLookup = StaleLookup(60, 13, DeptCode);
        SetRelationship(displayKeyFieldId: null);
        SetParentFields(DeptName, DeptCode);
        SetChildFields(staleLookup);

        await _handler.HandleAsync(new UpdateDisplayKeyCommand(RelPublicId, DisplayKeyFieldFid: DeptCode.Fid));

        await _auditRepo.Received(1).LogActivityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string>(title => title != null && title.Contains("removed") && title.Contains("Department Code")),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
