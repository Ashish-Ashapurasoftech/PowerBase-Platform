using System.Text.Json;

namespace PowerBase.Application.QbMigration;

// QuickBase -> PowerBase migration (standalone module, docs/qb-migration). These types describe what the QuickBase
// RESTful API returns; nothing here touches PowerBase data.

/// <summary>A QuickBase table, from GET /v1/tables?appId=.</summary>
public sealed record QbTable(string Id, string Name, string? Alias);

/// <summary>A QuickBase field, from GET /v1/fields?tableId=. <see cref="Mode"/> is "lookup", "summary", "formula" or empty for a stored field.
/// <see cref="CompositeFids"/> lists an Address field's sub-fields; <see cref="ParentFid"/> is set on those sub-fields.</summary>
public sealed record QbField(int Id, string Label, string FieldType, string? Mode, IReadOnlyList<int> CompositeFids, int? ParentFid = null)
{
    public bool IsComputed => Mode is "lookup" or "summary" or "formula" || FieldType is "dblink";
}

/// <summary>One record as QuickBase returned it: field id -> the raw "value" element.</summary>
public sealed class QbRow
{
    public QbRow(IReadOnlyDictionary<int, JsonElement> values) => Values = values;
    public IReadOnlyDictionary<int, JsonElement> Values { get; }

    /// <summary>The record's Record ID# (fid 3), which every query selects.</summary>
    public long RecordId => Values.TryGetValue(QbSystemFids.RecordId, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetInt64() : throw new InvalidOperationException("A QuickBase row came back without its Record ID# (fid 3).");
}

/// <summary>One page of POST /v1/records/query. QuickBase sizes pages itself ("intelligent pagination"), so
/// <see cref="NumRecords"/> can be smaller than the requested top while more records remain.</summary>
public sealed record QbQueryPage(IReadOnlyList<QbRow> Rows, int TotalRecords, int NumRecords, int Skip);

/// <summary>QuickBase's built-in field ids; PowerBase seeds the same five with the same numbers.</summary>
public static class QbSystemFids
{
    public const int DateCreated = 1;
    public const int DateModified = 2;
    public const int RecordId = 3;
    public const int RecordOwner = 4;
    public const int LastModifiedBy = 5;
    public static bool IsSystem(int fid) => fid is >= 1 and <= 5;
}

/// <summary>What the loader does with one QuickBase field.</summary>
public enum QbFieldAction
{
    /// <summary>Stored PowerBase field: transform and write.</summary>
    Load,
    /// <summary>One of the five system fields, written into the fixed columns.</summary>
    System,
    /// <summary>A sub-field of an Address; its value goes into the composite field's JSON.</summary>
    AddressPart,
    /// <summary>Formula / Lookup / Summary / Report Link: PowerBase computes it, nothing is written.</summary>
    Computed,
    /// <summary>Deliberately not migrated (reason recorded).</summary>
    Skip,
    /// <summary>Cannot be migrated safely: the table is blocked until this is fixed.</summary>
    Block,
}

/// <summary>The outcome of mapping one QuickBase field.</summary>
public sealed record QbFieldMapping(QbField Qb, int? PbFid, string? PbTypeCode, QbFieldAction Action, string? Reason = null)
{
    /// <summary>For an address part: the composite field's id and the PowerBase address key (street1, city, ...).</summary>
    public int? CompositeFid { get; init; }
    public string? AddressKey { get; init; }
}

/// <summary>A per-record problem worth showing a person; the run keeps going.</summary>
public sealed record QbIssue(string Severity, string Code, string Message, long? QbRecordId = null, int? QbFid = null);
