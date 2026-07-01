using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.RemoveRelationshipField;

/// <summary>Soft-deletes one Lookup (child) or Summary (parent) field of a relationship. Lookups
/// and summaries are computed (no physical column), so removal is a meta delete. If the removed
/// field is the reference proxy, the proxy is reassigned to another lookup (or cleared).</summary>
public class RemoveRelationshipFieldCommandHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;

    public RemoveRelationshipFieldCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        RelationshipQueriesHandler queries,
        IAuditRepository auditRepo)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _queries = queries;
        _auditRepo = auditRepo;
    }

    public async Task<RelationshipDto> HandleAsync(RemoveRelationshipFieldCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var field = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct)
            ?? throw new NotFoundException("Field", command.FieldPublicId);

        if (field.Id == rel.ReferenceFieldId)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["field"] = ["The reference field can't be removed on its own — delete the relationship instead."],
            });

        // If this is the reference proxy, hand the proxy role to another lookup (or clear it).
        if (rel.ProxyFieldId == field.Id)
        {
            var childFields = await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
            var nextProxy = childFields.FirstOrDefault(f => f.TypeCode == "Lookup" && f.Id != field.Id
                && FormulaTypeMap.ParseLookupSettings(f.Settings)?.RelationshipId == rel.Id);
            await _relRepo.UpdateProxyFieldAsync(rel.Id, nextProxy?.Id, ct);
        }

        await _fieldRepo.DeleteAsync(field.PublicId, field.AppTableId, ct);

        var appId = (await _tableRepo.GetByIdAsync(field.AppTableId, ct)).AppId;
        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            $"Field '{field.Name}' removed from relationship", appId: appId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }
}
