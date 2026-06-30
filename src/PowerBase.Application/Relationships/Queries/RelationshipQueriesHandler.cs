using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>Read-side queries for relationships: list by app, list by table, get one. Builds
/// <see cref="RelationshipDto"/>s including their participating reference/lookup/summary fields.</summary>
public class RelationshipQueriesHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;

    public RelationshipQueriesHandler(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
    }

    public async Task<IReadOnlyList<RelationshipDto>> ByAppAsync(Guid appPublicId, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var rels = await _relRepo.ListByAppAsync(appId, ct);
        return await BuildAsync(rels, ct);
    }

    public async Task<IReadOnlyList<RelationshipDto>> ByTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var rels = await _relRepo.ListByTableAsync(table.Id, ct);
        return await BuildAsync(rels, ct);
    }

    public async Task<RelationshipDto> GetAsync(Guid relationshipPublicId, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);
        var dtos = await BuildAsync([rel], ct);
        return dtos[0];
    }

    private async Task<IReadOnlyList<RelationshipDto>> BuildAsync(IReadOnlyList<Relationship> rels, CancellationToken ct)
    {
        if (rels.Count == 0) return [];

        // Cache tables and their field lists across the batch.
        var tableIds = rels.SelectMany(r => new[] { r.ParentTableId, r.ChildTableId }).Distinct().ToList();
        var tables = new Dictionary<long, AppTable>();
        var fieldsByTable = new Dictionary<long, IReadOnlyList<AppField>>();
        foreach (var tid in tableIds)
        {
            tables[tid] = await _tableRepo.GetByIdAsync(tid, ct);
            fieldsByTable[tid] = await _fieldRepo.ListByTableAsync(tid, ct);
        }

        var result = new List<RelationshipDto>(rels.Count);
        foreach (var rel in rels)
        {
            var parent = tables[rel.ParentTableId];
            var child = tables[rel.ChildTableId];
            var childFields = fieldsByTable[rel.ChildTableId];
            var parentFields = fieldsByTable[rel.ParentTableId];

            var refField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);
            var fields = new List<RelationshipFieldDto>();
            if (refField is not null)
                fields.Add(new(refField.PublicId, refField.Fid ?? 0, refField.Name, "reference", "Reference"));

            // Lookups (child); the proxy lookup gets the distinct "proxy" role. TypeCode = looked-up source type.
            foreach (var f in childFields.Where(f => f.TypeCode == "Lookup"
                && FormulaTypeMap.ParseLookupSettings(f.Settings)?.RelationshipId == rel.Id))
            {
                var role = f.Id == rel.ProxyFieldId ? "proxy" : "lookup";
                var srcType = FormulaTypeMap.ParseLookupSettings(f.Settings)?.SourceTypeCode ?? "Text";
                fields.Add(new(f.PublicId, f.Fid ?? 0, f.Name, role, srcType));
            }

            // Summaries (parent). TypeCode = the aggregate function (Count/Exists/Sum/…).
            foreach (var f in parentFields.Where(f => f.TypeCode == "Summary"
                && FormulaTypeMap.ParseSummarySettings(f.Settings)?.RelationshipId == rel.Id))
            {
                var fn = FormulaTypeMap.ParseSummarySettings(f.Settings)?.Function ?? "Count";
                fields.Add(new(f.PublicId, f.Fid ?? 0, f.Name, "summary", fn));
            }

            var proxyFid = rel.ProxyFieldId.HasValue
                ? childFields.FirstOrDefault(f => f.Id == rel.ProxyFieldId.Value)?.Fid
                : null;

            result.Add(new RelationshipDto
            {
                PublicId = rel.PublicId,
                ParentTablePublicId = parent.PublicId,
                ParentTableName = parent.Name,
                ChildTablePublicId = child.PublicId,
                ChildTableName = child.Name,
                ReferenceFid = rel.ReferenceFid,
                ReferenceFieldName = refField?.Name ?? string.Empty,
                ProxyFid = proxyFid,
                Fields = fields,
            });
        }
        return result;
    }
}
