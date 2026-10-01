using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships.Commands.UpdateReferenceFilter;

/// <summary>Configures the "values in this field depend on a selection in another field" filter of a
/// relationship's Reference field: stored as <see cref="ReferenceSettings.FilterConditions"/>.</summary>
public class UpdateReferenceFilterCommandHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;

    public UpdateReferenceFilterCommandHandler(
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

    public async Task<RelationshipDto> HandleAsync(UpdateReferenceFilterCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var referenceField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId)
            ?? throw new NotFoundException("AppField", rel.ReferenceFieldId);

        var conditions = new List<ReferenceFilterCondition>();
        for (var i = 0; i < command.Conditions.Count; i++)
        {
            var input = command.Conditions[i];
            var key = $"conditions[{i}]";

            var formField = childFields.FirstOrDefault(f => f.Fid == input.FormFid);
            if (formField is null || formField.Id == referenceField.Id)
                throw Invalid(key, "The controlling field must be another field on this table.");
            if (formField.TypeCode == "Summary")
                throw Invalid(key, "A Summary field cannot control a dropdown.");

            if (input.JunctionTablePublicId is Guid junctionPublicId)
            {
                var junction = await _tableRepo.GetByPublicIdAsync(junctionPublicId, ct);
                if (junction.AppId != child.AppId)
                    throw Invalid(key, "The junction table must belong to the same app.");
                var jFields = await _fieldRepo.ListByTableAsync(junction.Id, ct);
                var jParent = jFields.FirstOrDefault(f => f.Fid == input.JunctionParentFid);
                var jValue = jFields.FirstOrDefault(f => f.Fid == input.JunctionValueFid);
                if (jParent is null || jValue is null)
                    throw Invalid(key, "Choose the junction fields that hold the parent record and the controlling value.");
                if (PhysicalNaming.IsComputedTypeCode(jParent.TypeCode) || PhysicalNaming.IsComputedTypeCode(jValue.TypeCode))
                    throw Invalid(key, "Computed fields cannot be used as junction fields.");
                conditions.Add(new ReferenceFilterCondition
                {
                    FormFid = input.FormFid,
                    JunctionTableId = junction.Id,
                    JunctionTablePublicId = junction.PublicId,
                    JunctionParentFid = input.JunctionParentFid,
                    JunctionValueFid = input.JunctionValueFid,
                });
            }
            else
            {
                var parentField = parentFields.FirstOrDefault(f => f.Fid == input.ParentFid);
                if (parentField is null)
                    throw Invalid(key, "Choose the field on the parent table to compare with.");
                if (PhysicalNaming.IsComputedTypeCode(parentField.TypeCode))
                    throw Invalid(key, "A computed field cannot be used to filter the dropdown.");
                conditions.Add(new ReferenceFilterCondition { FormFid = input.FormFid, ParentFid = input.ParentFid });
            }
        }

        var settings = FormulaTypeMap.ParseReferenceSettings(referenceField.Settings)
            ?? new ReferenceSettings { RelationshipId = rel.Id, ParentTableId = parent.Id };
        settings.FilterConditions = conditions.Count == 0 ? null : conditions;
        await _fieldRepo.UpdateSettingsAsync(referenceField.Id, JsonSerializer.Serialize(settings, JsonOpts), ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, referenceField.Id.ToString(),
            $"Dependent dropdown filter updated on {child.Name}.{referenceField.Label ?? referenceField.Name} ({conditions.Count} condition(s))",
            appId: child.AppId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }

    private static ValidationException Invalid(string key, string message)
        => new(new Dictionary<string, string[]> { [key] = [message] });
}
