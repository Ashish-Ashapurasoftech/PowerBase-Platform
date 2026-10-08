using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;

namespace PowerBase.Application.Imports;

public interface IImportDefinitionChecker
{
    /// <summary>Checks one import and records the outcome on it (and in the database when it changed).</summary>
    Task RefreshAsync(ImportDefinition def, CancellationToken ct);
    /// <summary>Checks many imports, loading each table's fields once.</summary>
    Task RefreshAsync(IEnumerable<ImportDefinition> imports, CancellationToken ct);
}

/// <summary>Finds saved imports that can no longer work because the tables or fields they use changed: a field was deleted, a
/// field's type changed so a mapping is no longer allowed, a merge key stopped being unique, a formula now refers to something that
/// is gone. It looks only at the structure of the two tables, never at who is asking, so the answer is the same for everyone, and it
/// changes nothing outside the imports module: field and table code is untouched and is simply read when an import is listed,
/// opened, started or scheduled. An import that is flagged is cleared again as soon as the cause is fixed.</summary>
public sealed class ImportDefinitionChecker(
    IAppTableRepository tables, IAppFieldRepository fields, FormulaEngine formulaEngine, IImportDefinitionRepository definitions)
    : IImportDefinitionChecker
{
    private const int MaxReason = 500;

    private sealed record TableSchema(AppTable? Table, IReadOnlyList<AppField> Fields);

    /// <summary>Checks the imports and records the outcome on each (and in the database when it changed). A table's fields are loaded
    /// once however many imports use it.</summary>
    public async Task RefreshAsync(IEnumerable<ImportDefinition> imports, CancellationToken ct)
    {
        var cache = new Dictionary<long, TableSchema>();
        foreach (var def in imports) await RefreshAsync(def, cache, ct);
    }

    public Task RefreshAsync(ImportDefinition def, CancellationToken ct) => RefreshAsync(def, new Dictionary<long, TableSchema>(), ct);

    private async Task RefreshAsync(ImportDefinition def, Dictionary<long, TableSchema> cache, CancellationToken ct)
    {
        var problem = await FindProblemAsync(def, cache, ct);
        var needsAttention = problem is not null;
        if (def.NeedsAttention == needsAttention && (!needsAttention || def.AttentionReason == problem)) return;
        def.NeedsAttention = needsAttention;
        def.AttentionReason = problem;
        await definitions.SetAttentionAsync(def.Id, needsAttention, problem, ct);
    }

    private async Task<string?> FindProblemAsync(ImportDefinition def, Dictionary<long, TableSchema> cache, CancellationToken ct)
    {
        // A file import has no source table to go missing: its columns are the ones saved with it.
        var isFile = def.SourceKind == ImportSourceKinds.File;
        IReadOnlyList<AppField> sourceFields;
        if (isFile)
        {
            if (ImportConfigMapper.ToConfig(def, Guid.Empty).File is not { } fileOptions) return Cap("The file settings of this import are missing.");
            sourceFields = ImportFileColumns.ToFields(ImportFileValidation.SavedSource(fileOptions).Columns);
        }
        else
        {
            var source = def.SourceTableId is { } sourceId ? await LoadAsync(sourceId, cache, ct) : null;
            if (source?.Table is null) return Cap("The table this import copies from no longer exists.");
            sourceFields = source.Fields;
        }
        var destination = await LoadAsync(def.DestinationTableId, cache, ct);
        if (destination.Table is null) return Cap("The table this import copies into no longer exists.");

        var mappings = ImportJson.Deserialize<List<ImportFieldMapping>>(def.FieldMappingJson) ?? [];
        var conditions = ImportJson.Deserialize<FilterGroup>(def.ConditionsJson);
        // Virtual columns are part of the import: they must still calculate, and mappings may use them as a source.
        var (virtuals, virtualError) = ImportVirtual.Resolve(ImportConfigMapper.ToConfig(def, Guid.Empty).VirtualColumns, sourceFields, formulaEngine);
        if (virtualError is not null) return Cap(virtualError);
        if (virtuals.Count > 0) sourceFields = sourceFields.Concat(virtuals.Select(v => v.Field)).ToList();
        var sourceByFid = Live(sourceFields);
        var destByFid = Live(destination.Fields);

        var schema = new AppFieldSchema(sourceFields);
        if (TargetProblem(def.ImportType, def.MergeKeyFid, mappings, destByFid, sourceByFid, schema, isFile) is { } homeProblem) return Cap(homeProblem);

        // The other tables this import fills are checked the same way, each against its own fields.
        foreach (var target in ImportConfigMapper.ToConfig(def, Guid.Empty).AdditionalTargets)
        {
            var extra = await LoadByPublicIdAsync(target.DestinationTableId, cache, ct);
            if (extra.Table is null) return Cap("A table this import also copies into no longer exists.");
            if (TargetProblem(target.ImportType, target.MergeKeyFid, target.Mappings, Live(extra.Fields), sourceByFid, schema, isFile) is { } problem)
                return Cap($"'{extra.Table.Name}': {problem}");
        }

        if (MissingConditionField(conditions, sourceByFid) is { } missing)
            return Cap($"A field this import filters on was deleted (field {missing}).");
        // A table's own filter reads the same source: a field it filters on can be deleted too.
        var config = ImportConfigMapper.ToConfig(def, Guid.Empty);
        if (MissingConditionField(config.TableConditions, sourceByFid) is { } missingOwn)
            return Cap($"A field this import filters a table on was deleted (field {missingOwn}).");
        foreach (var target in config.AdditionalTargets)
            if (MissingConditionField(target.Conditions, sourceByFid) is { } missingTarget)
                return Cap($"A field this import filters a table on was deleted (field {missingTarget}).");
        return null;
    }

    /// <summary>What is wrong with one table's part of an import (its merge key, and each field it writes), or null. Used for the import's own
    /// table and for every other table it fills.</summary>
    private string? TargetProblem(
        string importType, int? mergeKeyFid, IReadOnlyList<ImportFieldMapping> mappings, Dictionary<int, AppField> destByFid,
        Dictionary<int, AppField> sourceByFid, AppFieldSchema schema, bool isFile)
    {
        AppField? mergeKey = null;
        if (importType == ImportTypes.Merge)
        {
            if (mergeKeyFid is not { } keyFid || !destByFid.TryGetValue(keyFid, out mergeKey))
                return "The field this import matches existing records on was deleted.";
            if (!ImportTypeCompatibility.IsKeyCandidate(mergeKey) || !ImportTypeCompatibility.IsWritable(mergeKey))
                return $"'{ImportTypeCompatibility.DisplayName(mergeKey)}' can no longer be used to match records: it is not unique any more.";
        }

        foreach (var m in mappings.Where(m => !m.DoNotImport))
        {
            if (!destByFid.TryGetValue(m.DestFid, out var dest))
                return "A field this import writes to was deleted (field " + m.DestFid + ").";
            var name = ImportTypeCompatibility.DisplayName(dest);
            if (!ImportTypeCompatibility.IsWritable(dest) || (ImportTypeCompatibility.IsRecordId(dest) && dest.Fid != mergeKey?.Fid))
                return $"'{name}' can no longer be imported into.";

            var problem = m.Source switch
            {
                ImportMappingSource.Dynamic => DynamicProblem(m, dest, sourceByFid, isFile),
                ImportMappingSource.Formula => FormulaProblem(m, dest, schema),
                ImportMappingSource.Static => StaticProblem(m, dest, dest.Fid == mergeKey?.Fid),
                _ => null
            };
            if (problem is not null) return problem;
        }
        return null;
    }

    private async Task<TableSchema> LoadByPublicIdAsync(Guid tablePublicId, Dictionary<long, TableSchema> cache, CancellationToken ct)
    {
        try { return await LoadAsync((await tables.GetByPublicIdAsync(tablePublicId, ct)).Id, cache, ct); }
        catch (NotFoundException) { return new TableSchema(null, []); }
    }

    private static string? DynamicProblem(ImportFieldMapping m, AppField dest, Dictionary<int, AppField> sourceByFid, bool anySourceType)
    {
        if (m.SourceFid is not { } sourceFid || !sourceByFid.TryGetValue(sourceFid, out var src))
            return $"The source field for '{ImportTypeCompatibility.DisplayName(dest)}' was deleted.";
        return anySourceType || ImportVirtual.IsVirtualFid(sourceFid) ? null : ImportTypeCompatibility.CheckMapping(src, dest);
    }

    private string? FormulaProblem(ImportFieldMapping m, AppField dest, AppFieldSchema schema)
    {
        if (string.IsNullOrWhiteSpace(m.Formula)) return null; // an empty formula is reported by the save and run checks
        var name = ImportTypeCompatibility.DisplayName(dest);
        if (ImportFormulaGuard.IsTooDeep(m.Formula)) return $"The formula for '{name}' is nested too deeply.";
        var compiled = formulaEngine.Compile(m.Formula, schema);
        if (compiled.Diagnostics.FirstOrDefault(d => d.Severity == FormulaSeverity.Error) is { } error)
            return $"The formula for '{name}' no longer works: {error.Message}";
        var problem = ImportTypeCompatibility.CheckFormulaResult(compiled.ResultType, dest);
        return problem is null ? null : $"'{name}': {problem}";
    }

    /// <summary>A fixed value that was valid when saved can stop being valid: the field became unique, or its type changed.</summary>
    private static string? StaticProblem(ImportFieldMapping m, AppField dest, bool isKey)
    {
        var name = ImportTypeCompatibility.DisplayName(dest);
        if (isKey || dest.IsUnique) return $"'{name}' is now unique, so it cannot be given one fixed value.";
        if (string.IsNullOrWhiteSpace(m.StaticValue)) return null;
        return ImportTypeCompatibility.TryParseStatic(m.StaticValue, dest, out _, out var error) ? null : $"'{name}': {error}";
    }

    /// <summary>The first field id a condition refers to that no longer exists (Record ID# and "ask" placeholders are skipped).</summary>
    private static long? MissingConditionField(FilterGroup? group, Dictionary<int, AppField> sourceByFid)
    {
        if (group is null) return null;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                if (c.FieldId > 0 && !sourceByFid.ContainsKey((int)c.FieldId)) return c.FieldId;
                if (c.ValueFieldId is > 0 && !sourceByFid.ContainsKey((int)c.ValueFieldId.Value)) return c.ValueFieldId;
            }
            if (MissingConditionField(node.Group, sourceByFid) is { } inner) return inner;
        }
        return null;
    }

    private async Task<TableSchema> LoadAsync(long tableId, Dictionary<long, TableSchema> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(tableId, out var cached)) return cached;
        TableSchema schema;
        try
        {
            var table = await tables.GetByIdAsync(tableId, ct);
            schema = new TableSchema(table, await fields.ListByTableAsync(table.Id, ct));
        }
        catch (NotFoundException) { schema = new TableSchema(null, []); }
        return cache[tableId] = schema;
    }

    private static Dictionary<int, AppField> Live(IReadOnlyList<AppField> list) =>
        list.Where(f => f.Fid.HasValue && !f.IsDeleted).GroupBy(f => f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

    private static string Cap(string reason) => reason.Length > MaxReason ? reason[..MaxReason] : reason;
}
