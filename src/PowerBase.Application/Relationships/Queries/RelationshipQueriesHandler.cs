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
                fields.Add(new(refField.PublicId, refField.Fid ?? 0, refField.Label ?? refField.Name, "reference", "Reference"));

            // Key field on parent: this relationship's own display-key override takes precedence,
            // then the parent table's global Set Key, then the default Record ID# — same precedence
            // KeyFieldResolver.ResolveDisplayKey applies for the reference picker/grid/filter, so this
            // settings screen (and the reference field's displayed type, derived from this) matches them.
            var parentKeyField = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields)
                ?? parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == 3)
                ?? parentFields.FirstOrDefault(f => f.Fid == 3);
            if (parentKeyField is not null)
            {
                var keyTypeCode = parentKeyField.Fid == 3 ? "Record ID#" : parentKeyField.TypeCode;
                fields.Add(new(parentKeyField.PublicId, parentKeyField.Fid ?? 3, parentKeyField.Label ?? parentKeyField.Name, "key", keyTypeCode));
            }

            // Lookups (child); the proxy lookup gets the distinct "proxy" role. TypeCode = looked-up source type.
            foreach (var f in childFields.Where(f => f.TypeCode == "Lookup"
                && FormulaTypeMap.ParseLookupSettings(f.Settings)?.RelationshipId == rel.Id))
            {
                var role = f.Id == rel.ProxyFieldId ? "proxy" : "lookup";
                var ls = FormulaTypeMap.ParseLookupSettings(f.Settings);
                var srcType = ls?.SourceTypeCode ?? "Text";
                var srcField = ls?.SourceFid is int sfid ? parentFields.FirstOrDefault(pf => pf.Fid == sfid) : null;
                var srcLabel = srcField is not null ? (srcField.Label ?? srcField.Name) : null;
                fields.Add(new(f.PublicId, f.Fid ?? 0, f.Label ?? f.Name, role, srcType)
                {
                    SourceFieldLabel = srcLabel is not null ? $"{parent.Name}: {srcLabel}" : null,
                    SourceFieldPublicId = srcField?.PublicId,
                });
            }

            // Summaries (parent). TypeCode = the aggregate function (Count/Exists/Sum/…).
            foreach (var f in parentFields.Where(f => f.TypeCode == "Summary"
                && FormulaTypeMap.ParseSummarySettings(f.Settings)?.RelationshipId == rel.Id))
            {
                var ss = FormulaTypeMap.ParseSummarySettings(f.Settings);
                var fn = Domain.FieldSettings.SummaryFunctions.Normalize(ss?.Function) ?? ss?.Function ?? "Count";
                var tField = ss?.TargetFid is int tfid ? childFields.FirstOrDefault(cf => cf.Fid == tfid) : null;
                var tLabel = tField is not null ? (tField.Label ?? tField.Name) : null;
                fields.Add(new(f.PublicId, f.Fid ?? 0, f.Label ?? f.Name, "summary", fn)
                {
                    TargetFieldLabel = tLabel is not null ? $"{child.Name}: {tLabel}" : null,
                    TargetFieldPublicId = tField?.PublicId,
                });
            }

            // Report Links (parent) for this relationship.
            // Match by explicit RelationshipId (auto-created by wizard) OR by TargetTablePublicId matching the
            // child table (handles old relationships created before auto-creation, and manually-configured links).
            var childPublicIdStr = child.PublicId.ToString();
            foreach (var f in parentFields.Where(f => f.TypeCode == "ReportLink"))
            {
                var rls = FormulaTypeMap.ParseReportLinkSettings(f.Settings);
                if (rls is null) continue;
                var linkedToRel = rls.RelationshipId == rel.Id;
                var targetsChild = string.Equals(rls.TargetTablePublicId, childPublicIdStr, StringComparison.OrdinalIgnoreCase);
                if (!linkedToRel && !targetsChild) continue;

                // Source: null SourceFid → Record ID# on the parent table
                string srcFLabel;
                Guid? srcFPubId = null;
                if (rls.SourceFid is int sFid)
                {
                    var srcF = parentFields.FirstOrDefault(pf => pf.Fid == sFid);
                    srcFLabel = $"{parent.Name}: {(srcF is not null ? (srcF.Label ?? srcF.Name) : $"Field {sFid}")}";
                    srcFPubId = srcF?.PublicId;
                }
                else
                {
                    var recIdF = parentFields.FirstOrDefault(pf => pf.IsSystem && pf.Fid == 3);
                    srcFLabel = $"{parent.Name}: Record ID#";
                    srcFPubId = recIdF?.PublicId;
                }
                // Target: resolve TargetFid against the child table
                string? tFLabel = null;
                Guid? tFPubId = null;
                if (rls.TargetFid is int tFid)
                {
                    var tF = childFields.FirstOrDefault(cf => cf.Fid == tFid);
                    tFLabel = $"{child.Name}: {(tF is not null ? (tF.Label ?? tF.Name) : $"Field {tFid}")}";
                    tFPubId = tF?.PublicId;
                }
                fields.Add(new(f.PublicId, f.Fid ?? 0, f.Label ?? f.Name, "reportlink", "ReportLink")
                {
                    SourceFieldLabel = srcFLabel,
                    SourceFieldPublicId = srcFPubId,
                    TargetFieldLabel = tFLabel,
                    TargetFieldPublicId = tFPubId,
                });
            }

            var proxyFid = rel.ProxyFieldId.HasValue
                ? childFields.FirstOrDefault(f => f.Id == rel.ProxyFieldId.Value)?.Fid
                : null;

            // Resolve DisplayKeyFieldId → its Fid for the frontend (Task 6).
            var displayKeyFid = rel.DisplayKeyFieldId.HasValue
                ? parentFields.FirstOrDefault(f => f.Id == rel.DisplayKeyFieldId.Value)?.Fid
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
                DisplayKeyFid = displayKeyFid,
                Fields = fields,
            });
        }
        return result;
    }
}
