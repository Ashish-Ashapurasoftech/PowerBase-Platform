using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Records;

public class RecordResult
{
    public Guid Id { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new();

    public static RecordResult FromRow(IReadOnlyDictionary<string, object?> row, IReadOnlyList<AppField> fields)
    {
        var fieldData = new Dictionary<string, object?>();
        foreach (var field in fields)
        {
            var col = PhysicalNaming.ColumnName(field.Id);
            if (row.TryGetValue(col, out var val))
                fieldData[field.Id.ToString()] = val;
        }

        return new RecordResult
        {
            Id = (Guid)row["PublicId"]!,
            CreatedOn = (DateTime)row["CreatedOn"]!,
            ModifiedOn = row.TryGetValue("ModifiedOn", out var mo) && mo is DateTime moDate ? moDate : null,
            Fields = fieldData,
        };
    }
}
