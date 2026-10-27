using System.Globalization;

namespace PowerBase.Application.Fields.TypeConversion;

public sealed class FieldTypeChangeDryRunResult
{
    public int TotalRows { get; init; }
    public int NonEmpty { get; init; }
    public int WillBecomeBlank { get; init; }
    public IReadOnlyList<ColumnConversionSample> Samples { get; init; } = [];
    /// <summary>Converted values that repeat an earlier one (what a Unique field can't keep).</summary>
    public int DuplicateValues { get; init; }
    /// <summary>SingleSelect target only: the choices the converted values produce.</summary>
    public SelectChoiceBuilder? Choices { get; init; }
}

/// <summary>
/// Runs the whole conversion in memory without writing: counts, before/after samples, duplicate values (for Unique)
/// and SingleSelect choices. Used by the preview, and by Change Type itself before it writes, so Unique and the
/// choices are decided before the data moves.
/// </summary>
public static class FieldTypeChangeDryRun
{
    public static async Task<FieldTypeChangeDryRunResult> RunAsync(
        FieldTypeChangePlan plan, IFieldColumnConversionService columns, int sampleSize, CancellationToken ct)
    {
        var choices = plan.TargetTypeCode == "SingleSelect" ? new SelectChoiceBuilder() : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;

        ConvertedValue Track(ConvertedValue converted)
        {
            if (converted.Value is null) return converted;
            var key = converted.Value is decimal d ? d.ToString("0.##########", CultureInfo.InvariantCulture)
                : Convert.ToString(converted.Value, CultureInfo.InvariantCulture) ?? "";
            if (!seen.Add(key)) duplicates++;
            choices?.Add(key);
            return converted;
        }

        switch (plan.Check.Kind)
        {
            case ConversionKind.ScalarToScalar or ConversionKind.NumericFamily:
            {
                var preview = await columns.PreviewAsync(plan.Table, plan.Field, v => Track(plan.Convert(v)), sampleSize, ct);
                return Result(preview.TotalRows, preview.NonEmpty, preview.WillBecomeBlank, preview.Samples);
            }
            case ConversionKind.ScalarToFormula:
            {
                // Everything stored is discarded.
                var preview = await columns.PreviewAsync(plan.Table, plan.Field, _ => new ConvertedValue(null, true), sampleSize, ct);
                return Result(preview.TotalRows, preview.NonEmpty, preview.NonEmpty, preview.Samples);
            }
            case ConversionKind.FormulaToScalar:
            {
                int nonEmpty = 0, lost = 0;
                var samples = new List<ColumnConversionSample>();
                foreach (var (rowId, before) in plan.FormulaValues ?? new Dictionary<long, object?>())
                {
                    var after = Track(plan.Convert(before));
                    if (before is null || before is string s && string.IsNullOrWhiteSpace(s)) continue;
                    nonEmpty++;
                    if (after.Lost) lost++;
                    if (samples.Count < sampleSize) samples.Add(new ColumnConversionSample(rowId, before, after.Value));
                }
                return Result(plan.FormulaValues?.Count ?? 0, nonEmpty, lost, samples);
            }
            default: // Formula → Formula stores nothing
                return Result(0, 0, 0, []);
        }

        FieldTypeChangeDryRunResult Result(int total, int nonEmpty, int blank, IReadOnlyList<ColumnConversionSample> samples) => new()
        {
            TotalRows = total, NonEmpty = nonEmpty, WillBecomeBlank = blank, Samples = samples,
            DuplicateValues = duplicates, Choices = choices,
        };
    }
}
