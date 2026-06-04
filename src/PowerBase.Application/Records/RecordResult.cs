using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Records;

public class RecordResult
{
    public Guid Id { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
    /// <summary>Internal userId (long) of the user who created this record. Used by the UI for OwnRecords scope gating.</summary>
    public long CreatedBy { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new();

    public static RecordResult FromRow(IReadOnlyDictionary<string, object?> row, IReadOnlyList<AppField> fields)
    {
        var fieldData = new Dictionary<string, object?>();
        foreach (var field in fields)
        {
            var col = field.IsSystem && !string.IsNullOrEmpty(field.PhysicalColumnName)
                ? field.PhysicalColumnName
                : PhysicalNaming.ColumnName(field.Id);
            if (row.TryGetValue(col, out var val))
                fieldData[field.Id.ToString()] = val;
        }

        var createdBy = row.TryGetValue("CreatedBy", out var cb) && cb is not null ? Convert.ToInt64(cb) : 0L;

        return new RecordResult
        {
            Id = (Guid)row["PublicId"]!,
            CreatedOn = (DateTime)row["CreatedOn"]!,
            ModifiedOn = row.TryGetValue("ModifiedOn", out var mo) && mo is DateTime moDate ? moDate : null,
            CreatedBy = createdBy,
            Fields = fieldData,
        };
    }
}
