using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Where an Update Record / Delete Record / Upload a File step finds its record. By default (every step saved before
/// <c>recordSource</c> existed) it is the record of an earlier step (<c>targetRecordId</c> / <c>fileRecordStepId</c>). With
/// <c>recordSource: "id"</c> the step names a table and a Record ID itself, as Quickbase does when such a step starts a pipeline.
/// </summary>
public static class PipelineRecordSource
{
    public const string RECORD_ID = "id";

    public static bool IsRecordId(string? configJson) =>
        string.Equals(Read(configJson, "recordSource"), RECORD_ID, StringComparison.OrdinalIgnoreCase);

    /// <summary>The table the step names: tableId, tablePublicId or tableLabel (the editor writes the table's id in each).</summary>
    public static string? TableId(string? configJson) =>
        new[] { "tableId", "tablePublicId", "tableLabel" }.Select(name => Read(configJson, name)).FirstOrDefault(v => Guid.TryParse(v, out _));

    public static bool HasTable(string? configJson) => TableId(configJson) is not null;

    /// <summary>The record a Record ID names: a record's PublicId as is, or its Record ID# (a whole number) looked up in the table.</summary>
    public static async Task<Guid> ResolveAsync(string? evaluated, AppTable table, IReadOnlyList<AppField> fields, IRecordRepository records, CancellationToken ct)
    {
        var value = evaluated?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("The Record ID is empty.");
        if (Guid.TryParse(value, out var publicId))
            return publicId;
        if (!long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var recordId))
            throw new InvalidOperationException($"Record ID '{value}' is not a valid Record ID.");

        var rows = await records.GetRowsByIdsAsync(table, fields, new[] { recordId }, ct);
        if (rows == null || !rows.TryGetValue(recordId, out var row) || !row.TryGetValue("PublicId", out var found)
            || !Guid.TryParse(Convert.ToString(found, System.Globalization.CultureInfo.InvariantCulture), out publicId))
            throw new InvalidOperationException($"Record with ID {recordId} was not found in Table '{table.Name}'.");
        return publicId;
    }

    private static string? Read(string? configJson, string name)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
