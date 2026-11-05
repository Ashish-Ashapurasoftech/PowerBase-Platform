namespace PowerBase.API.QuickbaseApi.V1.Models;

/// <summary>GET /v1/reports/{reportId} (Quickbase getReport). All documented fields always present.</summary>
public sealed class QbReportResponse
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Quickbase: true only for a report it could not load completely.</summary>
    public bool Incomplete { get; init; }
    /// <summary>Quickbase: "showed only for personal reports". We always write the key; null for shared reports.</summary>
    /// <summary>Only for a personal report (Quickbase leaves it out otherwise).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerId { get; init; }
    public QbReportQuery Query { get; init; } = new();
    /// <summary>Report-type specific properties (developer.quickbase.com/reportTypes).</summary>
    public IDictionary<string, object?> Properties { get; init; } = new Dictionary<string, object?>();
    public string? UsedLast { get; init; }
    public long? UsedCount { get; init; }
}

public sealed class QbReportQuery
{
    public string TableId { get; init; } = "";
    public string? Filter { get; init; }
    public IReadOnlyList<QbFormulaField> FormulaFields { get; init; } = Array.Empty<QbFormulaField>();
    /// <summary>Table and summary reports only (Quickbase leaves these out for charts).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<long>? Fields { get; init; } = Array.Empty<long>();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QbSortBy>? SortBy { get; init; } = Array.Empty<QbSortBy>();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QbGroupBy>? GroupBy { get; init; } = Array.Empty<QbGroupBy>();
}

public sealed record QbFormulaField(long Id, string Label, string FieldType, string Formula, int? DecimalPrecision);
public sealed record QbSortBy(long FieldId, string Order);
public sealed record QbGroupBy(long FieldId, string Grouping);

/// <summary>POST /v1/reports/{reportId}/run (Quickbase runReport).</summary>
public sealed class QbRunReportResponse
{
    public IReadOnlyList<IDictionary<string, QbCell>> Data { get; init; } = Array.Empty<IDictionary<string, QbCell>>();
    public IReadOnlyList<QbReportField> Fields { get; init; } = Array.Empty<QbReportField>();
    public QbRunMetadata Metadata { get; init; } = new();
}

public sealed record QbCell(object? Value);

public sealed record QbReportField(long Id, string Label, string Type,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? LabelOverride);

public sealed class QbRunMetadata
{
    public int TotalRecords { get; init; }
    public int NumRecords { get; init; }
    public int NumFields { get; init; }
    public int? Skip { get; init; }
    /// <summary>Only present when the caller sent "top" (as in Quickbase).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Top { get; init; }
}
