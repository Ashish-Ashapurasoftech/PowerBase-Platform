using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.RemoveRelationshipField;
using PowerBase.Application.Relationships.Commands.UpdateProxyField;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// Reference proxy (Quickbase "Proxy field"): which child fields may be the proxy, how the role moves when the proxy
/// field goes away, and the command that sets it. Employee (child) → Department (parent).
/// </summary>
public class ReferenceProxyTests
{
    private const long ParentTableId = 10;
    private const long ChildTableId = 20;
    private const long RelId = 1;
    private const long OtherRelId = 2;
    private const int ReferenceFid = 8;
    private static readonly Guid RelPublicId = Guid.NewGuid();

    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private static readonly AppTable Parent = new() { Id = ParentTableId, PublicId = Guid.NewGuid(), Name = "Department" };
    private static readonly AppTable Child = new() { Id = ChildTableId, PublicId = Guid.NewGuid(), Name = "Employee" };

    private static AppField Field(long id, int fid, string name, string typeCode, long tableId = ChildTableId, string? settings = null) =>
        new() { Id = id, Fid = fid, Name = name, Label = name, TypeCode = typeCode, AppTableId = tableId, Settings = settings, PublicId = Guid.NewGuid() };

    private static AppField Lookup(long id, int fid, string name, long relationshipId = RelId) =>
        Field(id, fid, name, "Lookup",
            settings: $"{{\"relationshipId\":{relationshipId},\"referenceFid\":{ReferenceFid},\"sourceTableId\":{ParentTableId},\"sourceFid\":6,\"sourceTypeCode\":\"Text\"}}");

    private static readonly AppField DeptName = Lookup(501, 101, "Department Name");
    private static readonly AppField DeptCode = Lookup(502, 102, "Department Code");
    private static readonly AppField ManagerName = Lookup(503, 103, "Manager Name", relationshipId: OtherRelId);
    private static readonly AppField FullLabel = Field(504, 104, "Department Label", "Formula_Text");
    private static readonly AppField LegacyFormula = Field(505, 105, "Legacy Formula", "Formula");
    private static readonly AppField HeadCount = Field(506, 106, "Head Count", "Summary");
    private static readonly AppField Notes = Field(507, 107, "Notes", "Text");
    private static readonly AppField Reference = Field(41, ReferenceFid, "Related Department", "Reference");

    private static Relationship Rel(long? proxyFieldId) => new()
    {
        Id = RelId,
        PublicId = RelPublicId,
        ParentTableId = ParentTableId,
        ChildTableId = ChildTableId,
        ReferenceFieldId = Reference.Id,
        ReferenceFid = ReferenceFid,
        ProxyFieldId = proxyFieldId,
    };

    public ReferenceProxyTests()
    {
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(Parent);
        _tableRepo.GetByIdAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(Child);
        _tableRepo.GetByPublicIdAsync(Child.PublicId, Arg.Any<CancellationToken>()).Returns(Child);
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)new List<AppField>());
    }

    private void SetChildFields(params AppField[] fields) =>
        _fieldRepo.ListByTableAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)fields.ToList());

    private void SetRelationship(Relationship rel)
    {
        _relRepo.GetByPublicIdAsync(RelPublicId, Arg.Any<CancellationToken>()).Returns(rel);
        _relRepo.ListByChildTableAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<Relationship>)[rel]);
        _relRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<Relationship>)[rel]);
    }

    // ── IsEligible ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEligible_LookupOfThisRelationship_True() =>
        ReferenceProxyRules.IsEligible(DeptName, Rel(null)).Should().BeTrue();

    [Fact]
    public void IsEligible_FormulaVariantAndGenericFormula_True()
    {
        ReferenceProxyRules.IsEligible(FullLabel, Rel(null)).Should().BeTrue();
        ReferenceProxyRules.IsEligible(LegacyFormula, Rel(null)).Should().BeTrue();
    }

    [Fact]
    public void IsEligible_LookupOfAnotherRelationship_False() =>
        ReferenceProxyRules.IsEligible(ManagerName, Rel(null)).Should().BeFalse();

    [Fact]
    public void IsEligible_SummaryStoredAndReferenceFields_False()
    {
        ReferenceProxyRules.IsEligible(HeadCount, Rel(null)).Should().BeFalse();
        ReferenceProxyRules.IsEligible(Notes, Rel(null)).Should().BeFalse();
        ReferenceProxyRules.IsEligible(Reference, Rel(null)).Should().BeFalse();
    }

    [Fact]
    public void IsEligible_FormulaOnTheParentTable_False() =>
        ReferenceProxyRules.IsEligible(Field(600, 200, "Parent Formula", "Formula_Text", tableId: ParentTableId), Rel(null))
            .Should().BeFalse();

    // ── PickDefault ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PickDefault_ReturnsFirstLookupOfThisRelationship_SkippingExcludedAndOtherRelationships()
    {
        var childFields = new[] { ManagerName, FullLabel, DeptName, DeptCode };

        ReferenceProxyRules.PickDefault(childFields, Rel(null)).Should().BeSameAs(DeptName);
        ReferenceProxyRules.PickDefault(childFields, Rel(null), [DeptName.Id]).Should().BeSameAs(DeptCode);
        ReferenceProxyRules.PickDefault(childFields, Rel(null), [DeptName.Id, DeptCode.Id]).Should().BeNull();
    }

    // ── ReassignIfRemovedAsync ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reassign_ProxyRemoved_MovesToNextLookup()
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(DeptName, DeptCode);

        await ReferenceProxyRules.ReassignIfRemovedAsync(ChildTableId, [DeptName.Id], _relRepo, _fieldRepo, default);

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, DeptCode.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reassign_FormulaProxyRemovedAndNoLookupLeft_ClearsProxy()
    {
        SetRelationship(Rel(FullLabel.Id));
        SetChildFields(FullLabel, ManagerName);

        await ReferenceProxyRules.ReassignIfRemovedAsync(ChildTableId, [FullLabel.Id], _relRepo, _fieldRepo, default);

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, (long?)null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reassign_RemovedFieldIsNotTheProxy_LeavesProxyAlone()
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(DeptName, DeptCode);

        await ReferenceProxyRules.ReassignIfRemovedAsync(ChildTableId, [DeptCode.Id], _relRepo, _fieldRepo, default);

        await _relRepo.DidNotReceiveWithAnyArgs().UpdateProxyFieldAsync(default, default, default);
    }

    [Fact]
    public async Task Reassign_NothingRemoved_DoesNotQueryRelationships()
    {
        await ReferenceProxyRules.ReassignIfRemovedAsync(ChildTableId, [], _relRepo, _fieldRepo, default);

        await _relRepo.DidNotReceiveWithAnyArgs().ListByChildTableAsync(default, default);
    }

    // ── UpdateProxyFieldCommandHandler ──────────────────────────────────────────────────────────

    private UpdateProxyFieldCommandHandler UpdateHandler() =>
        new(_relRepo, _tableRepo, _fieldRepo, new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo), _auditRepo);

    [Fact]
    public async Task Update_FormulaField_SetsProxyAndAudits()
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(Reference, DeptName, FullLabel);

        await UpdateHandler().HandleAsync(new UpdateProxyFieldCommand(RelPublicId, FullLabel.Fid));

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, FullLabel.Id, Arg.Any<CancellationToken>());
        await _auditRepo.ReceivedWithAnyArgs(1).LogActivityAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task Update_Null_ClearsProxy()
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(Reference, DeptName);

        await UpdateHandler().HandleAsync(new UpdateProxyFieldCommand(RelPublicId, null));

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, (long?)null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_SameAsCurrent_WritesNothing()
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(Reference, DeptName);

        await UpdateHandler().HandleAsync(new UpdateProxyFieldCommand(RelPublicId, DeptName.Fid));

        await _relRepo.DidNotReceiveWithAnyArgs().UpdateProxyFieldAsync(default, default, default);
        await _auditRepo.DidNotReceiveWithAnyArgs().LogActivityAsync(default!, default!, default!, default!);
    }

    [Theory]
    [InlineData(103)] // lookup of another relationship
    [InlineData(106)] // summary
    [InlineData(107)] // stored text
    [InlineData(999)] // not on the child table
    public async Task Update_IneligibleOrUnknownField_ThrowsValidation(int proxyFid)
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(Reference, DeptName, ManagerName, HeadCount, Notes);

        var act = () => UpdateHandler().HandleAsync(new UpdateProxyFieldCommand(RelPublicId, proxyFid));

        await act.Should().ThrowAsync<ValidationException>();
        await _relRepo.DidNotReceiveWithAnyArgs().UpdateProxyFieldAsync(default, default, default);
    }

    [Fact]
    public async Task Update_UnknownRelationship_ThrowsNotFound()
    {
        _relRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Relationship?)null);

        var act = () => UpdateHandler().HandleAsync(new UpdateProxyFieldCommand(Guid.NewGuid(), 101));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Get_ReturnsProxyFidAndName_ForFormulaProxy()
    {
        SetRelationship(Rel(FullLabel.Id));
        SetChildFields(Reference, DeptName, FullLabel);

        var dto = await new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo).GetAsync(RelPublicId);

        dto.ProxyFid.Should().Be(FullLabel.Fid);
        dto.ProxyFieldName.Should().Be("Department Label");
        dto.Fields.Should().NotContain(f => f.Role == "proxy"); // a formula is not a relationship field
    }

    // ── Delete paths ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteField_ProxyFromFieldsPage_MovesProxyToNextLookup()
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(Reference, DeptName, DeptCode);
        _fieldRepo.GetByPublicIdAsync(DeptName.PublicId, Arg.Any<CancellationToken>()).Returns(DeptName);
        _fieldRepo.DeleteAsync(DeptName.PublicId, ChildTableId, Arg.Any<CancellationToken>()).Returns(1);
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        pipelineRepo.GetActivePipelineReferencesForFieldAsync(Arg.Any<AppField>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(string, string)>)new List<(string, string)>());
        var handler = new DeleteFieldCommandHandler(_tableRepo, _fieldRepo, pipelineRepo, _auditRepo,
            Substitute.For<ITenantUnitOfWork>(), _relRepo);

        await handler.HandleAsync(new DeleteFieldCommand(Child.PublicId, DeptName.PublicId));

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, DeptCode.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveRelationshipField_Proxy_MovesProxyToNextLookup()
    {
        SetRelationship(Rel(DeptName.Id));
        SetChildFields(Reference, DeptName, DeptCode);
        _fieldRepo.GetByPublicIdAsync(DeptName.PublicId, Arg.Any<CancellationToken>()).Returns(DeptName);
        var handler = new RemoveRelationshipFieldCommandHandler(_relRepo, _tableRepo, _fieldRepo,
            new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo), _auditRepo);

        await handler.HandleAsync(new RemoveRelationshipFieldCommand(RelPublicId, DeptName.PublicId));

        await _relRepo.Received(1).UpdateProxyFieldAsync(RelId, DeptCode.Id, Arg.Any<CancellationToken>());
    }
}
