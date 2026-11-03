using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.UpdateProxyField;

/// <summary>Sets or clears a relationship's reference proxy (<see cref="Relationship.ProxyFieldId"/>). Display only:
/// the Reference keeps storing the parent row Id, and every write still goes to the Reference.</summary>
public class UpdateProxyFieldCommandHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;

    public UpdateProxyFieldCommandHandler(
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

    public async Task<RelationshipDto> HandleAsync(UpdateProxyFieldCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);

        AppField? proxy = null;
        if (command.ProxyFid is int proxyFid)
        {
            var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);
            proxy = childFields.FirstOrDefault(f => f.Fid == proxyFid)
                ?? throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["proxyFid"] = [$"Field {proxyFid} not found on the {child.Name} table."],
                });
            if (!ReferenceProxyRules.IsEligible(proxy, rel))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["proxyFid"] = [$"The proxy field must be a lookup field of this relationship or a formula field on {child.Name}."],
                });
        }

        if (rel.ProxyFieldId != proxy?.Id)
        {
            await _relRepo.UpdateProxyFieldAsync(rel.Id, proxy?.Id, ct);

            var message = proxy is null
                ? $"Proxy field cleared for relationship {child.Name} → {parent.Name}"
                : $"Proxy field set to '{proxy.Label ?? proxy.Name}' for relationship {child.Name} → {parent.Name}";
            await _auditRepo.LogActivityAsync(
                AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
                message, appId: child.AppId, ct: ct);
        }

        return await _queries.GetAsync(rel.PublicId, ct);
    }
}
