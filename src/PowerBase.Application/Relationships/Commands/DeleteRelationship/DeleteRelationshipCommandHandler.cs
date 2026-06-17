using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.DeleteRelationship;

public class DeleteRelationshipCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;

    public DeleteRelationshipCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteRelationshipCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);

        var refField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);

        // Restrict: deleting the reference field would orphan child links.
        if (!command.Force && refField is not null && await _recordRepo.HasAnyDataAsync(child, refField, ct))
            throw new ConflictException(
                $"This relationship is in use — some {child.Name} records reference a {parent.Name}. " +
                "Clear those references first, or force-delete the relationship.");

        // Soft-delete the participating fields.
        if (refField is not null)
            await _fieldRepo.DeleteAsync(refField.PublicId, child.Id, ct);

        foreach (var f in childFields.Where(f => f.TypeCode == "Lookup"
            && FormulaTypeMap.ParseLookupSettings(f.Settings)?.RelationshipId == rel.Id))
            await _fieldRepo.DeleteAsync(f.PublicId, child.Id, ct);

        foreach (var f in parentFields.Where(f => f.TypeCode == "Summary"
            && FormulaTypeMap.ParseSummarySettings(f.Settings)?.RelationshipId == rel.Id))
            await _fieldRepo.DeleteAsync(f.PublicId, parent.Id, ct);

        await _relRepo.SoftDeleteAsync(rel.PublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            $"Relationship deleted: {child.Name} → {parent.Name}", appId: rel.AppId, ct: ct);
    }
}
