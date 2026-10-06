using System.Text.Json;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>What one field's own settings read from other fields: its formula (every Formula_*
/// type, plus Formula URL's template), a Lookup's reference + source, a Summary's reference,
/// target and child filter, a Report Link's source/target, and an Action Button's capture/target
/// fields and formulas. Settings identify fields by <c>Fid</c>.</summary>
public static class FieldSettingsReferenceExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task ExtractAsync(
        AppField field,
        TableFieldIndex table,
        IFieldIndexProvider otherTables,
        FormulaEngine engine,
        FieldReferenceCollector collector,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(field.Settings)) return;

        if (FormulaTypeMap.ExpressionAndType(field.TypeCode, field.Settings) is { } formula)
        {
            // A formula can't usefully "read itself", and listing a field as used by its own formula would
            // only ever be noise on its Usage tab.
            collector.AddAll(table.FieldsReadBy(engine, formula.Expression).Where(id => id != field.Id), FieldReferenceUsages.Formula);
            return;
        }

        switch (field.TypeCode)
        {
            case "Lookup": await ExtractLookupAsync(field, table, otherTables, collector, ct); break;
            case "Summary": await ExtractSummaryAsync(field, table, otherTables, collector, ct); break;
            case "ReportLink": await ExtractReportLinkAsync(field, table, otherTables, collector, ct); break;
            default:
                if (PhysicalNaming.IsActionButtonTypeCode(field.TypeCode))
                    ExtractActionButton(field, table, engine, collector);
                break;
        }
    }

    private static async Task ExtractLookupAsync(AppField field, TableFieldIndex table, IFieldIndexProvider otherTables, FieldReferenceCollector c, CancellationToken ct)
    {
        var s = Parse<LookupSettings>(field.Settings);
        if (s is null) return;

        c.Add(table.IdOfFid(s.ReferenceFid), FieldReferenceUsages.Lookup);
        if (s.SourceTableId is { } sourceTableId && await otherTables.GetByIdAsync(sourceTableId, ct) is { } source)
            c.Add(source.IdOfFid(s.SourceFid), FieldReferenceUsages.Lookup);
    }

    private static async Task ExtractSummaryAsync(AppField field, TableFieldIndex table, IFieldIndexProvider otherTables, FieldReferenceCollector c, CancellationToken ct)
    {
        var s = Parse<SummarySettings>(field.Settings);
        if (s is null) return;

        // Everything a Summary reads lives on the CHILD table: the reference field pointing back
        // here, the aggregated field, and whatever its filter tests.
        if (s.ChildTableId is not { } childTableId || await otherTables.GetByIdAsync(childTableId, ct) is not { } child) return;

        c.Add(child.IdOfFid(s.ReferenceFid), FieldReferenceUsages.Summary);
        c.Add(child.IdOfFid(s.TargetFid), FieldReferenceUsages.Summary);
        if (!string.IsNullOrWhiteSpace(s.FilterTree) && Parse<FilterGroup>(s.FilterTree) is { } filter)
            ReportReferenceExtractor.ExtractFilterGroup(filter, child, c, FieldReferenceUsages.Summary);
    }

    private static async Task ExtractReportLinkAsync(AppField field, TableFieldIndex table, IFieldIndexProvider otherTables, FieldReferenceCollector c, CancellationToken ct)
    {
        var s = Parse<ReportLinkSettings>(field.Settings);
        if (s is null) return;

        c.Add(table.IdOfFid(s.SourceFid), FieldReferenceUsages.ReportLink);
        if (Guid.TryParse(s.TargetTablePublicId, out var targetTableId) && await otherTables.GetByPublicIdAsync(targetTableId, ct) is { } target)
            c.Add(target.IdOfFid(s.TargetFid), FieldReferenceUsages.ReportLink);
    }

    private static void ExtractActionButton(AppField field, TableFieldIndex table, FormulaEngine engine, FieldReferenceCollector c)
    {
        var s = Parse<ActionButtonSettings>(field.Settings);
        if (s is null) return;

        void Fid(int? fid) => c.Add(table.IdOfFid(fid), FieldReferenceUsages.ActionButton);
        void Value(ValueSource? source)
        {
            if (source is null) return;
            Fid(source.FieldFid);
            if (!string.IsNullOrWhiteSpace(source.Formula))
                c.AddAll(table.FieldsReadBy(engine, source.Formula).Where(id => id != field.Id), FieldReferenceUsages.Formula);
        }

        Fid(s.CaptureFid);
        Fid(s.TimestampFid);
        Fid(s.PromptSourceFid);
        Fid(s.BoolGateFid);
        Fid(s.IpCaptureFid);
        Fid(s.LocationCapture?.TargetFid);

        Value(s.ButtonLabel);
        Value(s.ButtonColor);
        Value(s.FileName);
        Value(s.DefaultValue);
        Value(s.Redirect);
        Value(s.PasswordGate);
        Value(s.LinkExpiration?.Start);

        foreach (var item in s.AddData ?? [])
        {
            Fid(item.TargetFid);
            Value(item.Value);
        }
    }

    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
