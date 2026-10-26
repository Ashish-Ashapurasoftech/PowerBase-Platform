using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Search;

/// <summary>The index column(s) a field is stored in: <see cref="Slot"/>, plus <see cref="EndSlot"/> for a range's second value.</summary>
public sealed record SlotRef(string Slot, string TypeCode, string? EndSlot = null);

/// <summary>A slot decision for one field. <see cref="OldSlot"/>/<see cref="OldEndSlot"/> are what the field held, so the update can
/// be applied only if nobody changed them in the meantime.</summary>
public sealed record SlotAssignment(long FieldId, string Slot, string? EndSlot, string? OldSlot, string? OldEndSlot);

public static class SearchSlotResolver
{
    public const int MaxNumericSlots = 60;
    public const int MaxDateSlots = 40;
    public const int MaxStringSlots = 60;
    public const int MaxBooleanSlots = 20;

    /// <summary>
    /// A table with more search-eligible fields than this is moved to its own index (pb-tbl-{tableId}) with one column per
    /// field. The move is a migration (see SearchWideMigration): until its documents are copied the table keeps using the
    /// shared index, and the switch is stored on the fields (<see cref="IsWideTable"/>), not recomputed from the count.
    /// </summary>
    public const int WideTableThreshold = 150;

    // ---- which fields the index holds

    /// <summary>Types that have no faithful index representation. A time of day has no date, and stamping one on it (what parsing
    /// it as a date does) makes the stored value depend on the day it was indexed. They are answered from SQL.</summary>
    public static bool IsIndexableType(string? typeCode) => typeCode is not "Time";

    /// <summary>Types whose filters may be sent to the index. Ranges and durations are stored there, but their filter operators
    /// (overlap, contains-date, "1h 30m" values) are not defined for the index yet, so those filters stay in SQL.</summary>
    public static bool IsAiFilterableType(string? typeCode) =>
        IsIndexableType(typeCode) && typeCode is not ("NumericRange" or "DateRange" or "Duration");

    public static bool IsRange(string? typeCode) => typeCode is "NumericRange" or "DateRange";

    public static bool IsSearchEligible(AppField field) =>
        (field.IsFilterable || field.IsSearchable) && IsIndexableType(field.TypeCode);

    public static bool IsNumeric(string? typeCode) =>
        typeCode is "Number" or "Currency" or "Percent" or "Rating" or "NumericRange" or "Duration" or "Formula_Number";

    public static bool IsDate(string? typeCode) =>
        typeCode is "Date" or "DateTime" or "DateRange" or "Formula_Date" or "Formula_Time";

    public static bool IsBoolean(string? typeCode) =>
        typeCode is "Boolean";

    public static bool IsText(string? typeCode) =>
        !IsNumeric(typeCode) && !IsDate(typeCode) && !IsBoolean(typeCode);

    // ---- wide tables (stored routing)

    public static bool IsWideSlot(string? slot) => slot != null && slot.StartsWith("f_", StringComparison.Ordinal);

    /// <summary>True when the table's documents live in its own index. Stored, so it changes only when the migration says so.</summary>
    public static bool IsWideTable(IReadOnlyList<AppField> tableFields) =>
        tableFields.Any(f => !f.IsDeleted && IsWideSlot(f.SearchSlot));

    /// <summary>True when the table has outgrown the shared index but has not been moved yet (the migration is due or running).</summary>
    public static bool ShouldBeWide(IReadOnlyList<AppField> tableFields) =>
        !IsWideTable(tableFields) && tableFields.Count(f => !f.IsDeleted && IsSearchEligible(f)) > WideTableThreshold;

    public static string GetTableIndexName(long tableId) =>
        $"pb-tbl-{tableId}";

    public static string GetTableIndexName(long tenantId, long tableId, IReadOnlyList<AppField> tableFields, string defaultIndex) =>
        IsWideTable(tableFields) ? GetTableIndexName(tableId) : defaultIndex;

    public static string WideSlot(int fid) => $"f_{fid}";
    public static string WideEndSlot(int fid) => $"f_{fid}_e";

    // ---- slots

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

    /// <summary>A stored slot is only valid for the family its field type belongs to (a Duration that was once put in a date slot
    /// must move to a numeric one).</summary>
    public static bool SlotMatchesType(string? slot, string? typeCode) =>
        !string.IsNullOrWhiteSpace(slot) && slot!.StartsWith(SlotPrefix(typeCode) + "_", StringComparison.Ordinal);

    private static string LegacyName(AppField field) =>
        field.Fid.HasValue ? $"f_{field.Fid.Value}" : $"f_{field.Id}";

    /// <summary>
    /// The index column a field is stored in. A slot is a stored decision (<see cref="AppField.SearchSlot"/>), never recomputed
    /// from the field's position: position-based slots move every later field whenever one field is added, removed or
    /// switched on/off, leaving existing documents with values in the wrong columns.
    /// A field with no valid stored slot (not eligible, not yet allocated, or its pool is full) resolves to its legacy
    /// "f_{fid}" name, which the filter builder treats as "answer this in SQL". A wide table uses "f_{fid}" in its own index.
    /// </summary>
    public static string ResolveSlotName(AppField field, IReadOnlyList<AppField> tableFields) =>
        ResolveSlotName(field, IsWideTable(tableFields));

    private static string ResolveSlotName(AppField field, bool isWideTable)
    {
        if (isWideTable || !IsSearchEligible(field)) return LegacyName(field);
        return SlotMatchesType(field.SearchSlot, field.TypeCode) ? field.SearchSlot! : LegacyName(field);
    }

    /// <summary>Builds a dictionary mapping field Fid (or Id) -> SlotName for the entire table.</summary>
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

    /// <summary>The columns of a field for a table that is (or is about to be) wide: f_{fid}, plus f_{fid}_e for a range.</summary>
    public static SlotRef WideRef(AppField field) =>
        new(WideSlot(field.Fid!.Value), field.TypeCode, IsRange(field.TypeCode) ? WideEndSlot(field.Fid!.Value) : null);

    /// <summary>
    /// Decides slots for eligible fields whose slot is missing or of the wrong family (and a range's second slot). Pure: pass EVERY
    /// field of the table, deleted ones included, so a slot held by a deleted field is never handed to another field (old
    /// documents may still carry its value there). New slots are the lowest free ones of the field's family, in field-number
    /// order, so the result is deterministic. A field whose family is full gets nothing and is answered from SQL. A wide
    /// table uses no slots. <paramref name="supportsEndSlots"/> is false while the SearchSlotEnd column does not exist yet:
    /// ranges are then left alone.
    /// </summary>
    public static IReadOnlyList<SlotAssignment> AllocateMissingSlots(IReadOnlyList<AppField> allTableFields, bool supportsEndSlots = true)
    {
        var live = allTableFields.Where(f => !f.IsDeleted).ToList();
        if (IsWideTable(live)) return [];

        // Every slot anyone holds stays reserved, including a stale one being replaced (it may still hold old values).
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in allTableFields)
        {
            if (!string.IsNullOrWhiteSpace(f.SearchSlot)) used.Add(f.SearchSlot!);
            if (!string.IsNullOrWhiteSpace(f.SearchSlotEnd)) used.Add(f.SearchSlotEnd!);
        }

        string? Take(string prefix)
        {
            for (var n = 1; n <= MaxSlots(prefix); n++)
            {
                var name = SlotName(prefix, n);
                if (used.Add(name)) return name;
            }
            return null;
        }

        var result = new List<SlotAssignment>();
        foreach (var field in live.Where(IsSearchEligible).OrderBy(f => f.Fid ?? int.MaxValue).ThenBy(f => f.Id))
        {
            var wantsEnd = supportsEndSlots && IsRange(field.TypeCode);
            var needSlot = !SlotMatchesType(field.SearchSlot, field.TypeCode);
            var needEnd = wantsEnd && !SlotMatchesType(field.SearchSlotEnd, field.TypeCode);
            if (!needSlot && !needEnd) continue;

            var prefix = SlotPrefix(field.TypeCode);
            var slot = needSlot ? Take(prefix) : field.SearchSlot;
            var end = needEnd ? Take(prefix) : (wantsEnd ? field.SearchSlotEnd : null);
            if (slot == null || (wantsEnd && end == null)) continue; // pool full: stays in SQL (anything taken above stays reserved, which is harmless)

            result.Add(new SlotAssignment(field.Id, slot, end, field.SearchSlot, field.SearchSlotEnd));
        }
        return result;
    }

    /// <summary>True when the table has an eligible field that still needs a slot (and could get one).</summary>
    public static bool NeedsSlotAllocation(IReadOnlyList<AppField> liveTableFields, bool supportsEndSlots = true) =>
        AllocateMissingSlots(liveTableFields, supportsEndSlots).Count > 0;

    // ---- values

    /// <summary>Converts a stored value to what its slot column holds (number, date, boolean or text). Null stays null.</summary>
    public static object? ToSlotValue(string? typeCode, object? value)
    {
        if (value is null || value is DBNull) return null;
        if (IsNumeric(typeCode)) return ParseDouble(value);
        if (IsDate(typeCode)) return ParseDateTimeOffset(value);
        if (IsBoolean(typeCode)) return ParseBoolean(value);
        return ParseString(value);
    }

    /// <summary>Splits a range value ({"start":..,"end":..} as an object, a JSON string, or a dictionary) into its two ends.
    /// A plain scalar counts as the start only (same rule the record writer applies).</summary>
    public static (object? Start, object? End) SplitRange(object? value)
    {
        if (value is null || value is DBNull) return (null, null);
        if (value is string js && js.TrimStart().StartsWith('{'))
        {
            try { using var doc = JsonDocument.Parse(js); value = doc.RootElement.Clone(); }
            catch (JsonException) { return (value, null); }
        }
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Object)
                return (RangeEnd(je, "start"), RangeEnd(je, "end"));
            return (je.ValueKind == JsonValueKind.Null ? null : je.ToString(), null);
        }
        if (value is IDictionary<string, object?> d)
            return (d.TryGetValue("start", out var s) ? s : null, d.TryGetValue("end", out var e) ? e : null);
        if (value is IDictionary dict)
            return (dict.Contains("start") ? dict["start"] : null, dict.Contains("end") ? dict["end"] : null);
        return (value, null);
    }

    private static object? RangeEnd(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return null;
        var text = p.ToString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Writes a field's value into <paramref name="target"/> under its slot(s): one column, or both ends of a range.</summary>
    public static void AddSlotValues(IDictionary<string, object?> target, SlotRef slot, object? value)
    {
        if (IsRange(slot.TypeCode))
        {
            var (start, end) = SplitRange(value);
            target[slot.Slot] = ToSlotValue(slot.TypeCode, start);
            if (slot.EndSlot != null) target[slot.EndSlot] = ToSlotValue(slot.TypeCode, end);
            return;
        }
        target[slot.Slot] = ToSlotValue(slot.TypeCode, value);
    }

    /// <summary>Writes a cleared (null) value into every column the field occupies.</summary>
    public static void AddClearedSlots(IDictionary<string, object?> target, SlotRef slot)
    {
        target[slot.Slot] = null;
        if (slot.EndSlot != null) target[slot.EndSlot] = null;
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
