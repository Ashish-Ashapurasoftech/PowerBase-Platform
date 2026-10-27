namespace PowerBase.Application.Fields.TypeConversion;

public sealed record PreviewFieldTypeChangeQuery(Guid TablePublicId, Guid FieldPublicId, string TargetTypeCode);

/// <summary>
/// What Change Type would do, without changing anything: record counts, how many values become blank, a before →
/// after sample, Unique conflicts, and every dependent (blocking Summaries, formulas that stop compiling, Lookups and
/// Summaries that follow the new type, reports/forms/rules that use the field).
/// </summary>
public sealed class PreviewFieldTypeChangeQueryHandler
{
    public const int SampleSize = 10;

    private readonly FieldTypeChangePlanner _planner;
    private readonly FieldTypeChangeAnalyzer _analyzer;
    private readonly IFieldColumnConversionService _columns;

    public PreviewFieldTypeChangeQueryHandler(FieldTypeChangePlanner planner, FieldTypeChangeAnalyzer analyzer, IFieldColumnConversionService columns)
    {
        _planner = planner;
        _analyzer = analyzer;
        _columns = columns;
    }

    public async Task<FieldTypeChangePreviewDto> HandleAsync(PreviewFieldTypeChangeQuery query, CancellationToken ct = default)
    {
        var plan = await _planner.BuildAsync(query.TablePublicId, query.FieldPublicId, query.TargetTypeCode, ct);
        if (!plan.Check.IsAllowed)
            return new FieldTypeChangePreviewDto
            {
                SourceTypeCode = plan.Field.TypeCode, TargetTypeCode = plan.TargetTypeCode, CanChange = false,
                Blockers = [plan.Check.BlockedReason!],
            };

        var kind = plan.Check.Kind!.Value;
        var impact = await _analyzer.AnalyzeAsync(plan.App, plan.Table, plan.Field, plan.TableFields, plan.TargetTypeCode,
            FieldTypeChangeSettings.Build(plan.Field, plan.TargetTypeCode, kind), ct);

        FieldTypeChangeDryRunResult? dryRun = null;
        if (impact.Blockers.Count == 0)
            dryRun = await FieldTypeChangeDryRun.RunAsync(plan, _columns, SampleSize, ct);

        return new FieldTypeChangePreviewDto
        {
            SourceTypeCode = plan.Field.TypeCode,
            TargetTypeCode = plan.TargetTypeCode,
            Kind = kind.ToString(),
            DataLoss = plan.Check.Loss.ToString(),
            CanChange = impact.Blockers.Count == 0,
            Blockers = impact.Blockers,
            Warnings = FieldTypeChangeWarnings.Build(plan, impact, dryRun),
            TotalRecords = dryRun?.TotalRows ?? 0,
            NonEmpty = dryRun?.NonEmpty ?? 0,
            WillBecomeBlank = dryRun?.WillBecomeBlank ?? 0,
            Samples = dryRun?.Samples.Select(s => new FieldTypeChangeSampleDto(s.RowId,
                FieldTypeChangeWarnings.Display(s.Before, plan.SourceValueType, plan.ConversionContext),
                FieldTypeChangeWarnings.Display(s.After, plan.TargetValueType, plan.ConversionContext))).ToList() ?? [],
            DuplicateValues = plan.Field.IsUnique ? dryRun?.DuplicateValues ?? 0 : 0,
            ChoiceCount = dryRun?.Choices?.Choices.Count,
            BrokenFormulas = impact.BrokenFormulas.Select(b => new BrokenFormulaDto(b.TableName, b.FieldLabel, b.Message)).ToList(),
            LookupsUpdated = impact.LookupUpdates.Select(u => new DependentFieldDto(u.TableName, Label(u.Field))).ToList(),
            SummariesUpdated = impact.SummaryUpdates.Select(u => new DependentFieldDto(u.TableName, Label(u.Field))).ToList(),
            Usage = impact.Usage,
        };
    }

    private static string Label(Domain.Entities.AppField f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label!;
}
