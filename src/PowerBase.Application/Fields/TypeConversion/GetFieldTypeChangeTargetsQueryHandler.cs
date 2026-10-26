using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Fields.TypeConversion;

public sealed record GetFieldTypeChangeTargetsQuery(Guid TablePublicId, Guid FieldPublicId);

/// <summary>The "to:" dropdown of the Change Type page: every type this field may become, in catalog order,
/// limited to types this tenant's catalog actually has.</summary>
public sealed class GetFieldTypeChangeTargetsQueryHandler
{
    private readonly FieldTypeChangePlanner _planner;
    private readonly FieldTypeChangeAnalyzer _analyzer;
    private readonly IFieldTypeRepository _fieldTypeRepo;

    public GetFieldTypeChangeTargetsQueryHandler(FieldTypeChangePlanner planner, FieldTypeChangeAnalyzer analyzer, IFieldTypeRepository fieldTypeRepo)
    {
        _planner = planner;
        _analyzer = analyzer;
        _fieldTypeRepo = fieldTypeRepo;
    }

    public async Task<FieldTypeChangeTargetsDto> HandleAsync(GetFieldTypeChangeTargetsQuery query, CancellationToken ct = default)
    {
        var (table, field) = await _planner.LoadFieldAsync(query.TablePublicId, query.FieldPublicId, ct);
        var constraints = await _analyzer.GetConstraintsAsync(table, field, ct);
        var catalog = await _fieldTypeRepo.ListAllAsync(ct);

        var allowed = FieldTypeConversionMatrix.GetAllowedTargets(field.TypeCode, constraints).ToDictionary(c => c.TargetTypeCode);
        var targets = catalog
            .Where(t => allowed.ContainsKey(t.Code))
            .Select(t => new FieldTypeChangeTargetDto(
                t.Code, t.DisplayName, t.Category, t.Icon, allowed[t.Code].Kind.ToString()!, allowed[t.Code].Loss.ToString()!))
            .ToList();

        var currentName = catalog.FirstOrDefault(t => t.Code == field.TypeCode)?.DisplayName ?? field.TypeCode;
        string? reason = null;
        if (targets.Count == 0)
            reason = FieldTypeConversionMatrix.Check(field.TypeCode, "Text", constraints).BlockedReason
                     ?? "This field's type can't be changed.";

        return new FieldTypeChangeTargetsDto(field.TypeCode, currentName, targets.Count > 0, reason, targets);
    }
}
