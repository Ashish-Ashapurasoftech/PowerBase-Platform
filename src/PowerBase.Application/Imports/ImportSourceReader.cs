using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Imports;

/// <summary>Streams a table's rows in Record ID# order, one bounded chunk at a time (keyset paging, so the cost of
/// each chunk does not grow with how far into a million-row table the import is). Computed source fields are
/// projected at read time so the destination stores resolved scalars.</summary>
public sealed class ImportSourceReader(IRecordRepository records, IRelationalProjector relational, IFormulaProjector formulas) : IImportChunkReader
{
    /// <summary>Reading a table keeps nothing open between chunks, so there is nothing to release.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public const int ChunkSize = 2000;

    /// <summary>One bounded read. <see cref="Rows"/> are the rows that passed the conditions; <see cref="LastId"/> is
    /// the last Record ID# examined (not just kept), so the cursor advances even when a chunk is filtered to nothing.
    /// <see cref="Exhausted"/> is true when the source has no more rows in range.</summary>
    public sealed record Chunk(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, long LastId, bool Exhausted);

    public async Task<Chunk> ReadChunkAsync(ImportPlan plan, long afterId, long maxId, CancellationToken ct)
    {
        var filter = new FilterGroup();
        if (plan.SourceFilter is not null) filter.Nodes.Add(new() { Group = plan.SourceFilter });
        filter.Nodes.Add(IdCondition("gt", afterId));
        filter.Nodes.Add(IdCondition("lte", maxId));

        var rows = await records.ListAsync(plan.Source, plan.ReadFields, 1, ChunkSize, filter, null, plan.SourceOwnerRestriction, ct);
        if (rows.Count == 0) return new Chunk(rows, afterId, true);
        var lastId = RecordId(rows[^1]);

        if (plan.NeedsProjection)
        {
            var related = await relational.ProjectAsync(plan.Source, plan.SourceFields, rows, ct);
            var computed = formulas.Project(plan.SourceFields, rows, related, plan.Source);
            var merged = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = new Dictionary<string, object?>(rows[i]);
                foreach (var (fid, value) in computed[i]) row[PhysicalNaming.ColumnName((int)fid)] = value;
                merged.Add(row);
            }
            rows = merged;
        }
        if (plan.RowFilter is { } rowFilter) rows = rows.Where(rowFilter.Matches).ToList();
        return new Chunk(rows, lastId, false);
    }

    public static long RecordId(IReadOnlyDictionary<string, object?> row) => Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture);

    private static FilterNode IdCondition(string op, long value) => new()
    {
        Condition = new() { FieldId = 3, Operator = op, Value = value.ToString(CultureInfo.InvariantCulture) }
    };
}
