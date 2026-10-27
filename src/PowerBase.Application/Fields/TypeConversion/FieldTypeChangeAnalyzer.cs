using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Queries.GetFieldUsage;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>A dependent formula that compiles today but won't after the change (it will show blank).</summary>
public sealed record BrokenFormula(string TableName, string FieldLabel, string Message);

/// <summary>A Lookup/Summary whose stored copy of the source type must follow the change.</summary>
public sealed record DependentFieldUpdate(AppField Field, string TableName, string NewSettings);

/// <summary>Everything Change Type needs to know about what depends on the field.</summary>
public sealed class FieldTypeChangeImpact
{
    public List<string> Blockers { get; } = [];
    public List<BrokenFormula> BrokenFormulas { get; } = [];
    public List<DependentFieldUpdate> LookupUpdates { get; } = [];
    public List<DependentFieldUpdate> SummaryUpdates { get; } = [];
    public FieldUsageDto? Usage { get; set; }
}

/// <summary>
/// Works out, before anything is written, what a type change does to the rest of the app (spec: "Dependents"):
/// blockers (system field, relationship key, encrypted, Summary that can't aggregate the new type), dependent
/// formulas that stop compiling (same table, and child tables through lookups), the Lookup/Summary settings that
/// store the source type and must be rewritten, and the usage list (reports, forms, rules) for the warning panel.
/// Formulas are compared old-schema vs new-schema, so a formula that is already broken is never blamed on the change.
/// </summary>
public sealed class FieldTypeChangeAnalyzer
{
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly FormulaEngine _engine;

    public FieldTypeChangeAnalyzer(
        IAppFieldRepository fieldRepo, IAppTableRepository tableRepo, IRelationshipRepository relRepo, FormulaEngine engine)
    {
        _fieldRepo = fieldRepo;
        _tableRepo = tableRepo;
        _relRepo = relRepo;
        _engine = engine;
    }

    /// <summary>Facts about the field that limit its targets (system field, relationship key).</summary>
    public async Task<FieldConversionConstraints> GetConstraintsAsync(AppTable table, AppField field, CancellationToken ct)
    {
        var relationships = await _relRepo.ListByTableAsync(table.Id, ct);
        var isKey = relationships.Any(r =>
            (r.ParentTableId == table.Id && table.KeyFieldId == field.Id) || r.DisplayKeyFieldId == field.Id);
        return new FieldConversionConstraints(field.IsSystem, isKey);
    }

    public async Task<FieldTypeChangeImpact> AnalyzeAsync(
        App app, AppTable table, AppField field, IReadOnlyList<AppField> tableFields,
        string targetTypeCode, string? newSettings, CancellationToken ct)
    {
        var impact = new FieldTypeChangeImpact();

        var check = FieldTypeConversionMatrix.Check(field.TypeCode, targetTypeCode, await GetConstraintsAsync(table, field, ct));
        if (!check.IsAllowed) impact.Blockers.Add(check.BlockedReason!);

        var touchesColumn = check.Kind is not ConversionKind.FormulaToFormula;
        if (touchesColumn && (app.IsEncrypted || field.IsEncrypted))
            impact.Blockers.Add("An encrypted field can't change type. Turn encryption off first.");

        try
        {
            await SummaryDependencyGuard.EnsureTypeChangeKeepsSummariesValidAsync(field, targetTypeCode, _relRepo, _tableRepo, _fieldRepo, ct);
        }
        catch (ConflictException ex)
        {
            impact.Blockers.Add(ex.Message);
        }

        var asNew = WithType(field, targetTypeCode, newSettings);

        // Formulas on the field's own table (and the field itself when it stays a formula).
        CollectBrokenFormulas(table, tableFields, Replace(tableFields, asNew), impact, selfId: field.Id);

        // Lookups (and lookup chains) carrying the field into child tables, then the formulas reading them.
        await CollectLookupUpdatesAsync(table, field.Fid, targetTypeCode, impact, depth: 1, ct);
        foreach (var group in impact.LookupUpdates.GroupBy(u => u.Field.AppTableId))
        {
            var childTable = await _tableRepo.GetByIdAsync(group.Key, ct);
            var childFields = await _fieldRepo.ListByTableAsync(group.Key, ct);
            var updated = childFields.Select(f => group.FirstOrDefault(u => u.Field.Id == f.Id) is { } u ? WithType(f, f.TypeCode, u.NewSettings) : f).ToList();
            CollectBrokenFormulas(childTable, childFields, updated, impact, selfId: null);
        }

        await CollectSummaryUpdatesAsync(table, field, targetTypeCode, impact, ct);

        if (field.Fid is int fid)
            impact.Usage = await _fieldRepo.GetFieldUsageAsync(table.Id, field.Id, fid, table.AppId, ct);

        return impact;
    }

    private void CollectBrokenFormulas(
        AppTable table, IReadOnlyList<AppField> before, IReadOnlyList<AppField> after, FieldTypeChangeImpact impact, long? selfId)
    {
        var oldSchema = new AppFieldSchema(before);
        var newSchema = new AppFieldSchema(after);
        foreach (var newField in after)
        {
            if (!FormulaTypeMap.IsFormulaComputed(newField.TypeCode, newField.Settings)) continue;
            if (FormulaTypeMap.ExpressionAndType(newField.TypeCode, newField.Settings) is not { } next) continue;

            var compiledNew = _engine.Compile(next.Expression, newSchema, next.Type);
            if (!compiledNew.HasErrors) continue;

            // A formula that was already broken (or is the converted field itself, whose expression is
            // re-typed) is only reported when the change is what breaks it.
            var oldField = before.FirstOrDefault(f => f.Id == newField.Id);
            if (newField.Id != selfId && oldField is not null
                && FormulaTypeMap.ExpressionAndType(oldField.TypeCode, oldField.Settings) is { } prev
                && _engine.Compile(prev.Expression, oldSchema, prev.Type).HasErrors)
                continue;

            var message = compiledNew.Diagnostics.FirstOrDefault(d => d.Severity == PowerBase.Formula.Diagnostics.FormulaSeverity.Error)?.Message
                          ?? "The formula no longer compiles.";
            impact.BrokenFormulas.Add(new BrokenFormula(table.Name, Display(newField), message));
        }
    }

    /// <summary>Lookups store the root source type (LookupChain) — direct lookups of the field and lookups of
    /// those lookups (up to <see cref="LookupChain.MaxLength"/> deep) all get the new type.</summary>
    private async Task CollectLookupUpdatesAsync(
        AppTable sourceTable, int? sourceFid, string targetTypeCode, FieldTypeChangeImpact impact, int depth, CancellationToken ct)
    {
        if (sourceFid is null || depth > LookupChain.MaxLength) return;

        foreach (var rel in await _relRepo.ListByParentTableAsync(sourceTable.Id, ct))
        {
            var childTable = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
            foreach (var lookup in await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct))
            {
                if (!SummaryLookupSources.IsLookup(lookup)) continue;
                var ls = FormulaTypeMap.ParseLookupSettings(lookup.Settings);
                if (ls is null || ls.SourceTableId != sourceTable.Id || ls.SourceFid != sourceFid) continue;
                if (impact.LookupUpdates.Any(u => u.Field.Id == lookup.Id)) continue;

                impact.LookupUpdates.Add(new DependentFieldUpdate(lookup, childTable.Name,
                    FieldTypeChangeSettings.SetProperty(lookup.Settings, "sourceTypeCode", targetTypeCode)));
                await CollectLookupUpdatesAsync(childTable, lookup.Fid, targetTypeCode, impact, depth + 1, ct);
            }
        }
    }

    /// <summary>Summaries over the field keep a copy of its type (SummarySettings.TargetTypeCode). Ones that can't
    /// aggregate the new type were already reported as blockers by SummaryDependencyGuard.</summary>
    private async Task CollectSummaryUpdatesAsync(
        AppTable table, AppField field, string targetTypeCode, FieldTypeChangeImpact impact, CancellationToken ct)
    {
        if (field.Fid is not int fid) return;
        foreach (var rel in await _relRepo.ListByChildTableAsync(table.Id, ct))
        {
            AppTable? parentTable = null;
            foreach (var summary in await _fieldRepo.ListByTableAsync(rel.ParentTableId, ct))
            {
                if (summary.TypeCode != nameof(Domain.Enums.FieldTypeCode.Summary)) continue;
                var s = FormulaTypeMap.ParseSummarySettings(summary.Settings);
                if (s is null || s.TargetFid != fid || s.ChildTableId != table.Id || s.ReferenceFid != rel.ReferenceFid) continue;
                parentTable ??= await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
                impact.SummaryUpdates.Add(new DependentFieldUpdate(summary, parentTable.Name,
                    FieldTypeChangeSettings.SetProperty(summary.Settings, "targetTypeCode", targetTypeCode)));
            }
        }
    }

    private static AppField WithType(AppField f, string typeCode, string? settings) => new()
    {
        Id = f.Id, PublicId = f.PublicId, AppTableId = f.AppTableId, Fid = f.Fid, Name = f.Name, Label = f.Label,
        TypeCode = typeCode, Settings = settings, IsSystem = f.IsSystem,
    };

    private static List<AppField> Replace(IReadOnlyList<AppField> fields, AppField replacement) =>
        fields.Select(f => f.Id == replacement.Id ? replacement : f).ToList();

    private static string Display(AppField f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label!;
}
