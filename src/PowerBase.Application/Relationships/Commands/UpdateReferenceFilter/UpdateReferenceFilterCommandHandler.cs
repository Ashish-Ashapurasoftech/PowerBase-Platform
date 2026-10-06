using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships.Commands.UpdateReferenceFilter;

/// <summary>Configures the "values in this field depend on a selection in another field" filter of a
/// relationship's Reference field: stored as <see cref="ReferenceSettings.FilterTree"/> (and, for older
/// configurations, <see cref="ReferenceSettings.FilterConditions"/>).</summary>
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

        var tree = command.FilterTree is { Nodes.Count: > 0 } ? command.FilterTree : null;
        await ValidateTreeAsync(tree, parentFields, childFields, referenceField, ct);
        settings.FilterTree = tree is null ? null : JsonSerializer.Serialize(tree, JsonOpts);
        await _fieldRepo.UpdateSettingsAsync(referenceField.Id, JsonSerializer.Serialize(settings, JsonOpts), ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, referenceField.Id.ToString(),
            $"Dependent dropdown filter updated on {child.Name}.{referenceField.Label ?? referenceField.Name} ({conditions.Count} condition(s), {(tree is null ? "no" : "a")} filter tree)",
            appId: child.AppId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }

    /// <summary>The tree filters PARENT records by their stored fields (a calculated one has no column), and a
    /// "parentField" comparison reads the value of another stored field on this (child) form. As in a
    /// summary's criteria, a parent Lookup is filtered on the grandparent column it pulls down (not a
    /// range, not a calculated source). Encrypted parent fields are allowed: the tree is then judged in
    /// memory over decrypted rows (<see cref="EncryptedRowFilter"/>).</summary>
    private async Task ValidateTreeAsync(
        FilterGroup? tree, IReadOnlyList<AppField> parentFields, IReadOnlyList<AppField> childFields, AppField referenceField,
        CancellationToken ct)
    {
        if (tree is null) return;

        static HashSet<long> StoredFids(IEnumerable<AppField> fields) => fields
            .Where(f => f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode))
            .Select(f => (long)f.Fid!.Value).ToHashSet();

        var lookupSources = await SummaryLookupSources.LoadAsync(parentFields, _fieldRepo, ct);
        var parentFids = StoredFids(parentFields);
        foreach (var f in parentFields)
            if (f.Fid.HasValue && SummaryLookupSources.IsLookup(f)
                && SummaryLookupSources.IsReadable(f, lookupSources)
                && lookupSources[f.Fid.Value] is { } source
                && !PhysicalNaming.IsRangeTypeCode(source.TypeCode))
                parentFids.Add(f.Fid.Value);
        // Formula and Summary fields have no column but a value per record: allowed too (judged in memory).
        foreach (var f in parentFields)
            if (f.Fid.HasValue && SummaryComputedTargets.ResultKind(f) is not null) parentFids.Add(f.Fid.Value);
        var formFids = StoredFids(childFields.Where(f => f.Id != referenceField.Id));

        var errors = new Dictionary<string, string[]>();
        CommonReportValidationHelpers.ValidateFilterGroup(tree, parentFids, errors, validParentFieldIds: formFids);
        if (errors.Count > 0)
            throw Invalid("filterTree", errors.Values.SelectMany(m => m).First());

        if (FindUnsupported(tree) is { } problem)
            throw Invalid("filterTree", problem);
    }

    private static string? FindUnsupported(FilterGroup group)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                if (string.Equals(c.ValueMode, "ask", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase))
                    return "A dropdown filter can only compare to a value or to a field on this form.";
            }
            if (node.Group is { } sub && FindUnsupported(sub) is { } problem) return problem;
        }
        return null;
    }

    private static ValidationException Invalid(string key, string message)
        => new(new Dictionary<string, string[]> { [key] = [message] });
}
