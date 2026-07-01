using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships.Commands.AddLookupFields;

/// <summary>Creates additional Lookup fields on an existing relationship's child table. If the
/// relationship has no reference proxy yet, the first new lookup becomes it.</summary>
public class AddLookupFieldsCommandHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RelationshipFieldFactory _fieldFactory;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;

    public AddLookupFieldsCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        RelationshipFieldFactory fieldFactory,
        RelationshipQueriesHandler queries,
        IAuditRepository auditRepo)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldFactory = fieldFactory;
        _queries = queries;
        _auditRepo = auditRepo;
    }

    public async Task<RelationshipDto> HandleAsync(AddLookupFieldsCommand command, CancellationToken ct = default)
    {
        if (command.Lookups.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["lookups"] = ["At least one lookup field is required."] });

        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);

        var addedFids = new List<int>();
        AppField? firstCreated = null;
        foreach (var spec in command.Lookups)
        {
            var src = parentFields.FirstOrDefault(f => f.Fid == spec.SourceFid)
                ?? throw new NotFoundException("Field", spec.SourceFid);
            var lookup = await _fieldFactory.CreateAsync(child, nameof(Domain.Enums.FieldTypeCode.Lookup),
                spec.Name.Trim(), spec.Label?.Trim(), false,
                new LookupSettings
                {
                    RelationshipId = rel.Id,
                    ReferenceFid = rel.ReferenceFid,
                    SourceTableId = parent.Id,
                    SourceFid = spec.SourceFid,
                    SourceTypeCode = src.TypeCode,
                }, ct);
            firstCreated ??= lookup;
            addedFids.Add(lookup.Fid!.Value);
        }

        // If the relationship had no proxy lookup, the first new one becomes the display proxy.
        if (rel.ProxyFieldId is null && firstCreated is not null)
            await _relRepo.UpdateProxyFieldAsync(rel.Id, firstCreated.Id, ct);

        await _fieldFactory.AppendToAutoAddFormsAsync(child.PublicId, addedFids, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            $"Lookup field(s) added to relationship {child.Name} → {parent.Name}", appId: child.AppId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }
}
