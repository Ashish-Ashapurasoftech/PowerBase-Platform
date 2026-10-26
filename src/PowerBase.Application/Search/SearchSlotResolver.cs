using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Search;

public static class SearchSlotResolver
{
    public const int MaxNumericSlots = 60;
    public const int MaxDateSlots = 40;
    public const int MaxStringSlots = 60;
    public const int MaxBooleanSlots = 20;

    /// <summary>
    /// If a table has more than this number of active (IsFilterable || IsSearchable) fields,
    /// it is treated as a Wide Table and routed to its own dedicated index (pb-tbl-{tableId}).
    /// </summary>
    public const int WideTableThreshold = 150;

    /// <summary>Types the index cannot hold faithfully. A range is two values and a duration is not a point in time, so
    /// mapping either to one number/date slot stores nothing useful. They are answered from SQL until they get a real
    /// mapping (two slots for a range).</summary>
    public static bool IsIndexableType(string? typeCode) =>
        typeCode is not ("NumericRange" or "DateRange" or "Duration");

    public static bool IsSearchEligible(AppField field) =>
        (field.IsFilterable || field.IsSearchable) && IsIndexableType(field.TypeCode);

    public static bool IsWideTable(IReadOnlyList<AppField> tableFields) =>
        tableFields.Count(IsSearchEligible) > WideTableThreshold;

    public static string GetTableIndexName(long tableId) =>
        $"pb-tbl-{tableId}";

    public static string GetTableIndexName(long tenantId, long tableId, IReadOnlyList<AppField> tableFields, string defaultIndex)
    {
        if (IsWideTable(tableFields))
        {
            return GetTableIndexName(tableId);
        }
        return defaultIndex;
    }

    public static bool IsNumeric(string? typeCode) =>
        typeCode is "Number" or "Currency" or "Percent" or "Rating" or "NumericRange" or "Formula_Number";

    public static bool IsDate(string? typeCode) =>
        typeCode is "Date" or "DateTime" or "Time" or "Duration" or "DateRange" or "Formula_Date" or "Formula_Time";

    public static bool IsBoolean(string? typeCode) =>
        typeCode is "Boolean";

    public static bool IsText(string? typeCode) =>
        !IsNumeric(typeCode) && !IsDate(typeCode) && !IsBoolean(typeCode);

    /// <summary>The slot column family a field type lives in: "num", "date", "bool" or "str".</summary>
    public static string SlotPrefix(string? typeCode) =>
        IsNumeric(typeCode) ? "num" : IsDate(typeCode) ? "date" : IsBoolean(typeCode) ? "bool" : "str";

    public static int MaxSlots(string prefix) => prefix switch
    {
        "num" => MaxNumericSlots,
        "date" => MaxDateSlots,
        "bool" => MaxBooleanSlots,
        _ => MaxStringSlots
    };

    public static string SlotName(string prefix, int number) => $"{prefix}_{number:D2}";

    private static string LegacyName(AppField field) =>
        field.Fid.HasValue ? $"f_{field.Fid.Value}" : $"f_{field.Id}";

    /// <summary>
    /// The index column a field is stored in. A slot is a stored decision (<see cref="AppField.SearchSlot"/>), never
    /// recomputed from the field's position: position-based slots move every later field whenever one field is added,
    /// removed or switched on/off, leaving existing documents with values in the wrong columns.
    /// A field with no stored slot (not eligible, not yet allocated, or its pool is full) resolves to its legacy
    /// "f_{fid}" name, which the filter builder treats as "answer this in SQL". Wide tables use "f_{fid}" in their own index.
    /// </summary>
    public static string ResolveSlotName(AppField field, IReadOnlyList<AppField> tableFields) =>
        ResolveSlotName(field, IsWideTable(tableFields));

    private static string ResolveSlotName(AppField field, bool isWideTable)
    {
        if (isWideTable) return LegacyName(field);
        if (!IsSearchEligible(field)) return LegacyName(field);
        return string.IsNullOrWhiteSpace(field.SearchSlot) ? LegacyName(field) : field.SearchSlot!;
    }

    /// <summary>
    /// Builds a dictionary mapping field Fid (or Id) -> SlotName for the entire table.
    /// </summary>
    public static Dictionary<long, string> BuildSlotMap(IReadOnlyList<AppField> tableFields)
    {
        var isWide = IsWideTable(tableFields);
        var map = new Dictionary<long, string>();
        foreach (var field in tableFields)
        {
            var key = field.Fid.HasValue ? (long)field.Fid.Value : field.Id;
            map[key] = ResolveSlotName(field, isWide);
        }
        return map;
    }

    /// <summary>
    /// Decides slots for eligible fields that have none. Pure: pass EVERY field of the table, deleted ones included, so a
    /// slot held by a deleted field is never handed to another field (old documents may still carry its value there).
    /// Each new field takes the lowest free slot of its family, in field-number order, so the result is deterministic.
    /// A field whose family is full gets nothing and is answered from SQL. Wide tables use no slots.
    /// </summary>
    public static IReadOnlyList<(long FieldId, string Slot)> AllocateMissingSlots(IReadOnlyList<AppField> allTableFields)
    {
        var live = allTableFields.Where(f => !f.IsDeleted).ToList();
        if (IsWideTable(live)) return [];

        var used = new HashSet<string>(
            allTableFields.Where(f => !string.IsNullOrWhiteSpace(f.SearchSlot)).Select(f => f.SearchSlot!),
            StringComparer.OrdinalIgnoreCase);

        var assigned = new List<(long, string)>();
        foreach (var field in live
                     .Where(f => IsSearchEligible(f) && string.IsNullOrWhiteSpace(f.SearchSlot))
                     .OrderBy(f => f.Fid ?? int.MaxValue).ThenBy(f => f.Id))
        {
            var prefix = SlotPrefix(field.TypeCode);
            for (var n = 1; n <= MaxSlots(prefix); n++)
            {
                var name = SlotName(prefix, n);
                if (used.Add(name))
                {
                    assigned.Add((field.Id, name));
                    break;
                }
            }
        }
        return assigned;
    }

    /// <summary>True when the table has an eligible field that still needs a slot (and could get one).</summary>
    public static bool NeedsSlotAllocation(IReadOnlyList<AppField> liveTableFields) =>
        !IsWideTable(liveTableFields) &&
        liveTableFields.Any(f => IsSearchEligible(f) && string.IsNullOrWhiteSpace(f.SearchSlot));

    /// <summary>Converts a stored value to what its slot column holds (number, date, boolean or text). Null stays null.</summary>
    public static object? ToSlotValue(string? typeCode, object? value)
    {
        if (value is null || value is DBNull) return null;
        if (IsNumeric(typeCode)) return ParseDouble(value);
        if (IsDate(typeCode)) return ParseDateTimeOffset(value);
        if (IsBoolean(typeCode)) return ParseBoolean(value);
        return ParseString(value);
    }

    public static double? ParseDouble(object? val)
    {
        if (val == null || val is DBNull) return null;
        double? d = null;
        if (val is double dVal) d = dVal;
        else if (val is float f) d = f;
        else if (val is decimal dec) d = (double)dec;
        else if (val is int i) d = i;
        else if (val is long l) d = l;
        else if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var jd)) d = jd;
            else if (je.ValueKind == JsonValueKind.String && double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var jsDouble)) d = jsDouble;
        }
        else
        {
            var s = val.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) d = parsed;
        }

        if (d.HasValue && (double.IsNaN(d.Value) || double.IsInfinity(d.Value)))
            return null;

        return d;
    }

    public static DateTimeOffset? ParseDateTimeOffset(object? val)
    {
        if (val == null || val is DBNull) return null;
        if (val is DateTimeOffset dto) return dto.ToUniversalTime();
        if (val is DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
            return new DateTimeOffset(utc);
        }
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(je.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var jDto))
                return jDto;
            return null;
        }

        var s = val.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    public static bool? ParseBoolean(object? val)
    {
        if (val == null || val is DBNull) return null;
        if (val is bool b) return b;
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True) return true;
            if (je.ValueKind == JsonValueKind.False) return false;
            if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var jb)) return jb;
            return null;
        }

        var s = val.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (bool.TryParse(s, out var parsed)) return parsed;
        if (s == "1") return true;
        if (s == "0") return false;
        return null;
    }

    public static string ParseString(object? val)
    {
        if (val == null || val is DBNull) return string.Empty;
        if (val is JsonElement je)
        {
            return (je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText()) ?? string.Empty;
        }
        return val.ToString() ?? string.Empty;
    }
}
