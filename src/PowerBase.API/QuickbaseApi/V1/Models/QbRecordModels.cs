using System.Text.Json;

namespace PowerBase.API.QuickbaseApi.V1.Models;

/// <summary>POST /v1/records body (Quickbase upsert).</summary>
public sealed class QbUpsertRequest
{
    public string? To { get; set; }
    /// <summary>Rows of <c>{"&lt;fid&gt;": {"value": ...}}</c>.</summary>
    public List<Dictionary<string, JsonElement>>? Data { get; set; }
    /// <summary>Integer or numeric string (Quickbase oneOf).</summary>
    public JsonElement? MergeFieldId { get; set; }
    public List<JsonElement>? FieldsToReturn { get; set; }
}

/// <summary>POST /v1/records response (200, or 207 when some rows failed).</summary>
public sealed class QbUpsertResponse
{
    public IReadOnlyList<IDictionary<string, QbCell>> Data { get; init; } = Array.Empty<IDictionary<string, QbCell>>();
    public QbUpsertMetadata Metadata { get; init; } = new();
}

public sealed class QbUpsertMetadata
{
    public IReadOnlyList<long> CreatedRecordIds { get; init; } = Array.Empty<long>();
    /// <summary>Row number (1-based, as Quickbase reports it) → error messages for that row.</summary>
    /// <summary>Quickbase leaves "lineErrors" out of the response when every row succeeded.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, IReadOnlyList<string>>? LineErrors { get; init; }
    /// <summary>Quickbase writes the metadata keys in this (alphabetical) order.</summary>
    public int TotalNumberOfRecordsProcessed { get; init; }
    public IReadOnlyList<long> UnchangedRecordIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> UpdatedRecordIds { get; init; } = Array.Empty<long>();
}

/// <summary>DELETE /v1/records body (Quickbase deleteRecords).</summary>
public sealed class QbDeleteRecordsRequest
{
    public string? From { get; set; }
    public string? Where { get; set; }
}

public sealed record QbDeleteRecordsResponse(int NumberDeleted);

/// <summary>POST /v1/records/query body (Quickbase "Query for data").</summary>
public sealed class QbQueryRequest
{
    public string? From { get; set; }
    public List<JsonElement>? Select { get; set; }
    public string? Where { get; set; }
    /// <summary>Array of <c>{fieldId, order}</c>, or <c>false</c>.</summary>
    public JsonElement? SortBy { get; set; }
    public List<QbQueryGroupBy>? GroupBy { get; set; }
    public QbQueryOptions? Options { get; set; }
}

public sealed class QbQueryGroupBy
{
    public long? FieldId { get; set; }
    public string? Grouping { get; set; }
}

public sealed class QbQueryOptions
{
    public int? Skip { get; set; }
    public int? Top { get; set; }
    public bool? CompareWithAppLocalTime { get; set; }
}
