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

    public static bool IsSearchEligible(AppField field) =>
        field.IsFilterable || field.IsSearchable;

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

    /// <summary>
    /// Resolves the dedicated index slot name for a given field within the table (e.g. num_01, date_01, str_01).
    /// If the table is a wide table (> 150 fields), fields map directly to f_{Fid} for its dedicated index.
    /// Only fields that are search-eligible (IsFilterable || IsSearchable) take up slots in standard shared tables.
    /// </summary>
    public static string ResolveSlotName(AppField field, IReadOnlyList<AppField> tableFields)
    {
        // Wide tables (> 150 active fields) map directly to f_{Fid} in their dedicated index
        if (IsWideTable(tableFields))
        {
            return field.Fid.HasValue ? $"f_{field.Fid.Value}" : $"f_{field.Id}";
        }

        // If table has fields with explicit eligibility flags, fields that are neither filterable nor searchable
        // do not occupy slots in the shared index.
        if (tableFields.Any(IsSearchEligible) && !IsSearchEligible(field))
        {
            return field.Fid.HasValue ? $"f_{field.Fid.Value}" : $"f_{field.Id}";
        }

        var eligibleFields = tableFields.Any(IsSearchEligible)
            ? tableFields.Where(IsSearchEligible).ToList()
            : tableFields;

        var typeCode = field.TypeCode ?? string.Empty;

        if (IsNumeric(typeCode))
        {
            var index = eligibleFields
                .Where(f => IsNumeric(f.TypeCode))
                .OrderBy(f => f.Fid ?? (int)f.Id)
                .ToList()
                .FindIndex(f => (f.Fid.HasValue && field.Fid.HasValue && f.Fid == field.Fid) || f.Id == field.Id);

            if (index >= 0 && index < MaxNumericSlots)
                return $"num_{index + 1:D2}";
        }
        else if (IsDate(typeCode))
        {
            var index = eligibleFields
                .Where(f => IsDate(f.TypeCode))
                .OrderBy(f => f.Fid ?? (int)f.Id)
                .ToList()
                .FindIndex(f => (f.Fid.HasValue && field.Fid.HasValue && f.Fid == field.Fid) || f.Id == field.Id);

            if (index >= 0 && index < MaxDateSlots)
                return $"date_{index + 1:D2}";
        }
        else if (IsBoolean(typeCode))
        {
            var index = eligibleFields
                .Where(f => IsBoolean(f.TypeCode))
                .OrderBy(f => f.Fid ?? (int)f.Id)
                .ToList()
                .FindIndex(f => (f.Fid.HasValue && field.Fid.HasValue && f.Fid == field.Fid) || f.Id == field.Id);

            if (index >= 0 && index < MaxBooleanSlots)
                return $"bool_{index + 1:D2}";
        }
        else
        {
            var index = eligibleFields
                .Where(f => IsText(f.TypeCode))
                .OrderBy(f => f.Fid ?? (int)f.Id)
                .ToList()
                .FindIndex(f => (f.Fid.HasValue && field.Fid.HasValue && f.Fid == field.Fid) || f.Id == field.Id);

            if (index >= 0 && index < MaxStringSlots)
                return $"str_{index + 1:D2}";
        }

        // Fallback to legacy field name if slot capacity is exceeded
        return field.Fid.HasValue ? $"f_{field.Fid.Value}" : $"f_{field.Id}";
    }

    /// <summary>
    /// Builds a dictionary mapping field Fid (or Id) -> SlotName for the entire table.
    /// </summary>
    public static Dictionary<long, string> BuildSlotMap(IReadOnlyList<AppField> tableFields)
    {
        var map = new Dictionary<long, string>();
        foreach (var field in tableFields)
        {
            var key = field.Fid.HasValue ? (long)field.Fid.Value : field.Id;
            map[key] = ResolveSlotName(field, tableFields);
        }
        return map;
    }

    public static double? ParseDouble(object? val)
    {
        if (val == null || val is DBNull) return null;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is decimal dec) return (double)dec;
        if (val is int i) return i;
        if (val is long l) return l;
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var jd)) return jd;
            if (je.ValueKind == JsonValueKind.String && double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var jsDouble)) return jsDouble;
            return null;
        }

        var s = val.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
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
