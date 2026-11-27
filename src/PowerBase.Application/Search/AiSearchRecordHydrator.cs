using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Search;

/// <summary>
/// Splits a read's required fields into those directly hydratable from Azure AI Search documents and the remaining fields
/// that must be fetched dynamically from SQL, minimizing database column reads and skipping SQL completely when every
/// required column is already in the search index.
/// </summary>
public static class AiSearchRecordHydrator
{
    public sealed record HydratedField(AppField Field, string SlotName, string EffectiveType, bool IsComputed);

    public sealed record HydrationPlan(
        IReadOnlyList<HydratedField> AiFields,
        IReadOnlyList<AppField> MissingSqlFields,
        IReadOnlyList<string> SelectSlots,
        bool NeedSqlRecordId,
        bool NeedSqlAuditColumns,
        IReadOnlySet<long> PreHydratedComputedFids)
    {
        public bool NeedsSqlFetch => MissingSqlFields.Count > 0 || NeedSqlRecordId || NeedSqlAuditColumns;
    }

    public static HydrationPlan Plan(
        IReadOnlyList<AppField> fieldsToRead,
        IReadOnlyList<AppField> allFields,
        bool needRecordIdForRemainingProjections,
        bool needAuditColumns,
        IReadOnlyDictionary<long, bool>? inSearchComputed = null)
    {
        var isWide = SearchSlotResolver.IsWideTable(allFields);
        var slotMap = SearchSlotResolver.BuildSlotMap(allFields);
        var aiFields = new List<HydratedField>();
        var missingSql = new List<AppField>();
        var selectSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preHydratedComputed = new HashSet<long>();

        bool hasFid1 = false, hasFid2 = false, hasFid3 = false, hasFid4 = false;

        foreach (var f in fieldsToRead)
        {
            if (CanHydrate(f, slotMap, isWide, out var slot, out var effectiveType, out var isComputed, inSearchComputed))
            {
                aiFields.Add(new HydratedField(f, slot, effectiveType, isComputed));
                selectSlots.Add(slot);
                if (isComputed && f.Fid.HasValue) preHydratedComputed.Add(f.Fid.Value);
                if (f.Fid == 1) hasFid1 = true;
                else if (f.Fid == 2) hasFid2 = true;
                else if (f.Fid == 3) hasFid3 = true;
                else if (f.Fid == 4) hasFid4 = true;
            }
            else
            {
                missingSql.Add(f);
            }
        }

        // System fields (CreatedOn=1, ModifiedOn=2, Id=3, CreatedBy=4) may also live in the index even if not in fieldsToRead.
        if (needAuditColumns && (!hasFid1 || !hasFid2 || !hasFid4))
        {
            foreach (var sysFid in new[] { 1, 2, 4 })
            {
                var sf = allFields.FirstOrDefault(x => x.Fid == sysFid);
                if (sf != null && CanHydrate(sf, slotMap, isWide, out var slot, out var eff, out _, inSearchComputed))
                {
                    if (!aiFields.Any(a => a.Field.Fid == sysFid))
                    {
                        aiFields.Add(new HydratedField(sf, slot, eff, false));
                        selectSlots.Add(slot);
                    }
                    if (sysFid == 1) hasFid1 = true;
                    if (sysFid == 2) hasFid2 = true;
                    if (sysFid == 4) hasFid4 = true;
                }
            }
        }

        if (needRecordIdForRemainingProjections && !hasFid3)
        {
            var idField = allFields.FirstOrDefault(x => x.Fid == 3);
            if (idField != null && CanHydrate(idField, slotMap, isWide, out var slot, out var eff, out _, inSearchComputed))
            {
                aiFields.Add(new HydratedField(idField, slot, eff, false));
                selectSlots.Add(slot);
                hasFid3 = true;
            }
        }

        var needSqlRecordId = needRecordIdForRemainingProjections && !hasFid3;
        var needSqlAudit = needAuditColumns && (!hasFid1 || !hasFid2 || !hasFid4);
        return new HydrationPlan(aiFields, missingSql, selectSlots.ToList(), needSqlRecordId, needSqlAudit, preHydratedComputed);
    }

    public static bool CanHydrate(
        AppField field, IReadOnlyDictionary<long, string> slotMap, bool isWide,
        out string slot, out string effectiveType, out bool isComputed,
        IReadOnlyDictionary<long, bool>? inSearchComputed = null)
    {
        slot = string.Empty;
        effectiveType = field.TypeCode;
        isComputed = PhysicalNaming.IsComputedTypeCode(field.TypeCode);

        if (!field.Fid.HasValue || !SearchSlotResolver.IsSearchEligible(field)) return false;

        if (isComputed)
        {
            var isIndexed = inSearchComputed is not null
                ? (inSearchComputed.TryGetValue(field.Id, out var exact) && exact)
                : field.IsInSearchIndex;
            if (!isIndexed || !field.SearchExact) return false;
            effectiveType = SearchSlotResolver.SlotTypeCode(field) ?? field.TypeCode;
        }
        else if (field.TypeCode is "Reference" or "Time" or "Duration" or "DateRange" or "NumericRange" or "File" or "Attachment" or "Signature" or "Address")
        {
            // Reference needs parent display key/RID, Time is not indexed, Ranges/Files/Addresses need raw JSON/multi-column shapes.
            return false;
        }

        if (SearchSlotResolver.IsNumeric(effectiveType) && !field.SearchExact) return false;

        if (!slotMap.TryGetValue(field.Fid.Value, out var resolved))
        {
            if (!isWide) return false;
            resolved = SearchSlotResolver.WideSlot(field.Fid.Value);
        }
        else if (!isWide && resolved.StartsWith("f_", StringComparison.Ordinal))
        {
            return false;
        }

        slot = resolved;
        return true;
    }

    public static (List<IReadOnlyDictionary<string, object?>> Rows, List<Dictionary<long, object?>> ComputedSeeds) BuildRows(
        HydrationPlan plan,
        IReadOnlyList<SearchDocumentRow> aiDocs,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? sqlRows)
    {
        var sqlByPublicId = sqlRows?
            .Where(r => r.TryGetValue("PublicId", out var pid) && pid is Guid)
            .ToDictionary(r => (Guid)r["PublicId"]!)
            ?? new Dictionary<Guid, IReadOnlyDictionary<string, object?>>();

        var rows = new List<IReadOnlyDictionary<string, object?>>(aiDocs.Count);
        var seeds = new List<Dictionary<long, object?>>(aiDocs.Count);

        foreach (var doc in aiDocs)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PublicId"] = doc.PublicId
            };
            if (sqlByPublicId.TryGetValue(doc.PublicId, out var sqlRow))
            {
                foreach (var kv in sqlRow) dict[kv.Key] = kv.Value;
            }

            var seed = new Dictionary<long, object?>();
            foreach (var af in plan.AiFields)
            {
                doc.Values.TryGetValue(af.SlotName, out var raw);
                var converted = ConvertSlotValue(af.Field, af.EffectiveType, raw);
                if (af.IsComputed && af.Field.Fid.HasValue)
                {
                    seed[af.Field.Fid.Value] = converted;
                }
                else
                {
                    var col = af.Field.IsSystem && !string.IsNullOrWhiteSpace(af.Field.PhysicalColumnName)
                        ? af.Field.PhysicalColumnName!
                        : PhysicalNaming.ColumnName(af.Field.Fid!.Value);
                    dict[col] = converted;
                }
            }

            rows.Add(dict);
            seeds.Add(seed);
        }

        return (rows, seeds);
    }

    public static object? ConvertSlotValue(AppField field, string effectiveType, object? raw)
    {
        if (raw is null) return null;

        if (field.IsSystem && field.PhysicalColumnName is "Id" or "CreatedBy" or "ModifiedBy")
            return Convert.ToInt64(raw, CultureInfo.InvariantCulture);

        if (SearchSlotResolver.IsBoolean(effectiveType))
            return raw is bool b ? b : SearchSlotResolver.ParseBoolean(raw);

        if (SearchSlotResolver.IsDate(effectiveType))
        {
            var dto = raw is DateTimeOffset d ? d : SearchSlotResolver.ParseDateTimeOffset(raw);
            if (!dto.HasValue) return null;
            return effectiveType is "Date" or "Formula_Date" ? dto.Value.UtcDateTime.Date : dto.Value.UtcDateTime;
        }

        if (SearchSlotResolver.IsNumeric(effectiveType))
        {
            if (raw is decimal dec) return dec;
            var dbl = raw is double d ? d : SearchSlotResolver.ParseDouble(raw);
            return dbl.HasValue ? Convert.ToDecimal(dbl.Value, CultureInfo.InvariantCulture) : null;
        }

        return raw.ToString();
    }
}
