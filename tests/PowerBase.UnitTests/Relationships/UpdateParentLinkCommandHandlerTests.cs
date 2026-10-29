using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Commands.UpdateParentLink;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// "Display as a link to the parent record" (Quickbase's DisplayAsLinkToParent) on a relationship's Reference field
/// and its Lookup fields. Employee (child) → Department (parent).
/// </summary>
public class UpdateParentLinkCommandHandlerTests
{
    private const long ParentTableId = 10;
    private const long ChildTableId = 20;
    private const long RelId = 1;
    private static readonly Guid RelPublicId = Guid.NewGuid();

    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly UpdateParentLinkCommandHandler _handler;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static T? Parse<T>(string? json) => json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOpts);

    private static AppField Field(long id, int fid, string name, string typeCode, string? settings) =>
        new() { Id = id, Fid = fid, Name = name, Label = name, TypeCode = typeCode, AppTableId = ChildTableId, Settings = settings, PublicId = Guid.NewGuid() };

    private static readonly AppField Reference = Field(41, 8, "Related Department", "Reference",
        $"{{\"relationshipId\":{RelId},\"parentTableId\":{ParentTableId},\"filterTree\":\"{{}}\"}}");
    private static readonly AppField DeptName = Field(501, 101, "Department Name", "Lookup",
        $"{{\"relationshipId\":{RelId},\"referenceFid\":8,\"sourceTableId\":{ParentTableId},\"sourceFid\":6,\"sourceTypeCode\":\"Text\"}}");
    private static readonly AppField ManagerName = Field(502, 102, "Manager Name", "Lookup",
        "{\"relationshipId\":2,\"referenceFid\":9,\"sourceTableId\":30,\"sourceFid\":6,\"sourceTypeCode\":\"Text\"}");
    private static readonly AppField Notes = Field(503, 103, "Notes", "Text", null);

    public UpdateParentLinkCommandHandlerTests()
    {
        var rel = new Relationship
        {
            Id = RelId, PublicId = RelPublicId, ParentTableId = ParentTableId, ChildTableId = ChildTableId,
            ReferenceFieldId = Reference.Id, ReferenceFid = 8,
        };
        _relRepo.GetByPublicIdAsync(RelPublicId, Arg.Any<CancellationToken>()).Returns(rel);
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ParentTableId, PublicId = Guid.NewGuid(), Name = "Department" });
        _tableRepo.GetByIdAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ChildTableId, PublicId = Guid.NewGuid(), Name = "Employee" });
        _fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)[Reference, DeptName, ManagerName, Notes]);
        foreach (var f in new[] { Reference, DeptName, ManagerName, Notes })
            _fieldRepo.GetByPublicIdAsync(f.PublicId, Arg.Any<CancellationToken>()).Returns(f);

        _handler = new UpdateParentLinkCommandHandler(_relRepo, _tableRepo, _fieldRepo,
            new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo), _auditRepo);
    }

    private string? SavedSettings(AppField field) =>
        _fieldRepo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAppFieldRepository.UpdateSettingsAsync) && (long)c.GetArguments()[0]! == field.Id)
            .Select(c => (string?)c.GetArguments()[1])
            .SingleOrDefault();

    [Fact]
    public async Task Handle_ReferenceField_SetsFlagAndKeepsEveryOtherSetting()
    {
        await _handler.HandleAsync(new UpdateParentLinkCommand(RelPublicId, Reference.PublicId, true));

        var saved = Parse<ReferenceSettings>(SavedSettings(Reference));
        saved!.DisplayAsLinkToParent.Should().BeTrue();
        saved.RelationshipId.Should().Be(RelId);
        saved.ParentTableId.Should().Be(ParentTableId);
        saved.FilterTree.Should().Be("{}");
        await _auditRepo.ReceivedWithAnyArgs(1).LogActivityAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task Handle_LookupOfThisRelationship_SetsFlag()
    {
        await _handler.HandleAsync(new UpdateParentLinkCommand(RelPublicId, DeptName.PublicId, true));

        var saved = Parse<LookupSettings>(SavedSettings(DeptName));
        saved!.DisplayAsLinkToParent.Should().BeTrue();
        saved.SourceFid.Should().Be(6);
        saved.ReferenceFid.Should().Be(8);
    }

    [Fact]
    public async Task Handle_SameValueAsStored_WritesNothing()
    {
        await _handler.HandleAsync(new UpdateParentLinkCommand(RelPublicId, DeptName.PublicId, false));

        await _fieldRepo.DidNotReceiveWithAnyArgs().UpdateSettingsAsync(default, default, default);
        await _auditRepo.DidNotReceiveWithAnyArgs().LogActivityAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task Handle_LookupOfAnotherRelationshipOrStoredField_ThrowsValidation()
    {
        var other = () => _handler.HandleAsync(new UpdateParentLinkCommand(RelPublicId, ManagerName.PublicId, true));
        var stored = () => _handler.HandleAsync(new UpdateParentLinkCommand(RelPublicId, Notes.PublicId, true));

        await other.Should().ThrowAsync<ValidationException>();
        await stored.Should().ThrowAsync<ValidationException>();
        await _fieldRepo.DidNotReceiveWithAnyArgs().UpdateSettingsAsync(default, default, default);
    }

    [Fact]
    public void WithFlag_ReplacesADifferentlyCasedCopyAndSurvivesBadJson()
    {
        var json = UpdateParentLinkCommandHandler.WithFlag("{\"DisplayAsLinkToParent\":true,\"sourceFid\":6}", false);
        var obj = JsonNode.Parse(json)!.AsObject();

        obj.Select(p => p.Key).Should().BeEquivalentTo("sourceFid", "displayAsLinkToParent");
        obj["displayAsLinkToParent"]!.GetValue<bool>().Should().BeFalse();

        JsonNode.Parse(UpdateParentLinkCommandHandler.WithFlag("not json", true))!["displayAsLinkToParent"]!
            .GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void ReferenceSettings_RoundTripKeepsTheFlag()
    {
        // UpdateReferenceFilter rewrites the whole ReferenceSettings object: the flag must survive that.
        var parsed = Parse<ReferenceSettings>("{\"relationshipId\":1,\"displayAsLinkToParent\":true}");
        var rewritten = PowerBase.Application.Relationships.RelationshipFieldFactory.Serialize(parsed!);

        Parse<ReferenceSettings>(rewritten)!.DisplayAsLinkToParent.Should().BeTrue();
    }
}
