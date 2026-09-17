using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.UpdateDisplayKey;

/// <summary>Changes an existing relationship's display key override (the field the reference
/// picker, grid, and filter show instead of Record ID#). Scoped to this one relationship —
/// see <see cref="Relationship.DisplayKeyFieldId"/>.</summary>
public class UpdateDisplayKeyCommandHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RelationshipKeyCarryOverService _carryOver;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;

    public UpdateDisplayKeyCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        RelationshipKeyCarryOverService carryOver,
        RelationshipQueriesHandler queries,
        IAuditRepository auditRepo)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _carryOver = carryOver;
        _queries = queries;
        _auditRepo = auditRepo;
    }

    public async Task<RelationshipDto> HandleAsync(UpdateDisplayKeyCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);

        // Same rule as relationship creation (CreateRelationshipCommandHandler): only uniqueness is
        // required — any field type is eligible, this isn't restricted to Set Key's scalar-type allowlist.
        long? displayKeyFieldId = null;
        if (command.DisplayKeyFieldFid is int dkFid && dkFid != 3 /* 3 = Record ID# = standard */)
        {
            var dkField = parentFields.FirstOrDefault(f => f.Fid == dkFid)
                ?? throw new ValidationException(new Dictionary<string, string[]> { ["displayKeyFieldFid"] = [$"Field {dkFid} not found on the parent table."] });
            if (!dkField.IsUnique)
                throw new ValidationException(new Dictionary<string, string[]> { ["displayKeyFieldFid"] = ["The display key field must be unique across all parent records."] });
            displayKeyFieldId = dkField.Id;
        }

        // Resolve the effective key field before AND after this change the same way
        // KeyFieldResolver.ResolveDisplayKey does everywhere else (relationship override → parent's
        // global Set Key → Record ID#), so Standard key (Record ID#) is treated like any other field
        // by RelationshipKeyCarryOverService below instead of being a special case it doesn't know about.
        var oldKeyField = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields)
            ?? parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == 3)
            ?? parentFields.FirstOrDefault(f => f.Fid == 3);

        await _relRepo.UpdateDisplayKeyFieldAsync(rel.Id, displayKeyFieldId, ct);
        rel.DisplayKeyFieldId = displayKeyFieldId; // keep rel in sync so ResolveDisplayKey reflects the new state

        var newKeyField = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields)
            ?? parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == 3)
            ?? parentFields.FirstOrDefault(f => f.Fid == 3);

        var carryOverResult = await _carryOver.ApplyAsync(rel, parent, child, oldKeyField, newKeyField, ct);

        var auditMessage = $"Display key changed for relationship {child.Name} → {parent.Name}";
        if (carryOverResult.CarriedOverLabel is not null)
            auditMessage += $" (kept '{carryOverResult.CarriedOverLabel}' as a lookup field on {child.Name})";
        if (carryOverResult.RemovedLabel is not null)
            auditMessage += $" (removed redundant '{carryOverResult.RemovedLabel}' lookup field on {child.Name})";
        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            auditMessage, appId: child.AppId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }
}
