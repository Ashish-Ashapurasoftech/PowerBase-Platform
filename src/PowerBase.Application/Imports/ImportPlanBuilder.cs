using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Imports;

/// <summary>One destination field and where each row's value for it comes from: a source field, a fixed value, or a formula.</summary>
public sealed class ResolvedMapping
{
    public required AppField Destination { get; init; }
    /// <summary>One of <see cref="ImportMappingSource"/>.</summary>
    public required string Kind { get; init; }
    /// <summary>Dynamic: the source field, and the column of the row it is read from.</summary>
    public AppField? Source { get; init; }
    public string? SourceColumn { get; init; }
    /// <summary>Static: the fixed value converted to the destination's type, and as typed (shown in the details file).</summary>
    public object? Constant { get; init; }
    public string? StaticText { get; init; }
    /// <summary>Formula: compiled once for the run, evaluated against each row.</summary>
    public CompiledFormula? Formula { get; init; }
}

/// <summary>Where a Reference field's values point: the parent table, the field of it that people know a record by (null when that is the Record ID#,
/// or it has none; an encrypted field is read decrypted and searched in memory), and the hidden "(original)" text field that keeps a value that found no parent record.</summary>
public sealed record ImportReferenceTarget(AppTable Parent, AppField? DisplayKey, int? OriginalFid, IReadOnlyList<AppField> ParentFields);

/// <summary>Everything the engine needs to run a definition, resolved and permission-checked once.</summary>
public sealed class ImportPlan
{
    public required AppTable Source { get; init; }
    /// <summary>Set when the rows come from a file rather than a table; <see cref="Source"/> is then a stand-in for the file.</summary>
    public ImportFileSource? File { get; init; }
    public required IReadOnlyList<AppField> SourceFields { get; init; }
    public required AppTable Destination { get; init; }
    public required IReadOnlyList<AppField> DestinationFields { get; init; }
    public required IReadOnlyList<ResolvedMapping> Mappings { get; init; }
    public required string ImportType { get; init; }
    /// <summary>The part of the source conditions (plus the user's role filter) that SQL evaluates.</summary>
    public FilterGroup? SourceFilter { get; init; }
    /// <summary>The part SQL cannot evaluate (formula, lookup, summary and encrypted fields), applied to each row read.</summary>
    public ImportRowFilter? RowFilter { get; init; }
    /// <summary>Merge only: the unique destination field records are matched on.</summary>
    public AppField? MergeKey { get; init; }
    /// <summary>Per-column rules by destination Fid. Only columns with at least one rule switched on are present.</summary>
    public IReadOnlyDictionary<int, ImportColumnRule> Rules { get; init; } = new Dictionary<int, ImportColumnRule>();
    public string ConstraintPolicy { get; init; } = ImportConstraintPolicy.ImportValid;
    public long? SourceOwnerRestriction { get; init; }
    /// <summary>Fields to select from the source: mapped + condition fields + Record ID#.</summary>
    public required IReadOnlyList<AppField> ReadFields { get; init; }
    /// <summary>True when mapped or filtered source fields are computed and must be projected at read time.</summary>
    public bool NeedsProjection { get; init; }
    /// <summary>The clock and user every formula of the run sees. The clock is fixed when the run starts, so Today() and Now()
    /// give the same answer for every row.</summary>
    public EvaluationOptions FormulaOptions { get; init; } = EvaluationOptions.Default;
    /// <summary>The import's virtual columns, in the order to calculate them. Empty for an import without any.</summary>
    public IReadOnlyList<ResolvedVirtual> Virtuals { get; init; } = [];
    /// <summary>This table's own filter, for an import that fills several tables: checked on each row read, in memory, to decide whether this
    /// table gets it. Null when the table takes every row that was read.</summary>
    public ImportRowFilter? TableFilter { get; init; }
    /// <summary>Which of the import's tables this plan fills: 0 is the import's own table. A table read sheet by sheet has a plan per sheet, all with
    /// the same index, and the run keeps one set of counts and one details file for the table.</summary>
    public int TableIndex { get; set; }
    /// <summary>The sheet this plan reads, when its table reads a workbook sheet by sheet; null otherwise.</summary>
    public string? SheetName { get; set; }
    /// <summary>For each Reference field the import writes, how its values find the parent record.</summary>
    public IReadOnlyDictionary<int, ImportReferenceTarget> ReferenceTargets { get; init; } = new Dictionary<int, ImportReferenceTarget>();
    /// <summary>A sheet plan of a table that combines its sheets: the destination field its rows are combined on, when this sheet has one.</summary>
    public int? JoinFieldFid { get; set; }
    /// <summary>The plan of a table that combines its sheets into one record per value: the sheets' own plans, in the order they are read. This
    /// plan's mappings take their values from the combined records.</summary>
    public IReadOnlyList<ImportPlan>? JoinedFrom { get; set; }
}

/// <summary>Validates an import configuration against the live schema and the caller's access, and resolves it
/// into an <see cref="ImportPlan"/>. Used on save, on run start and by the worker, so a definition can never run
/// with a stale or unauthorised configuration. Relative dates ("today", "the previous 2 weeks") and "current user"
/// are resolved here, so every run — including scheduled ones — evaluates them as of that run.</summary>
public sealed class ImportPlanBuilder(
    IAppTableRepository tables,
    IAppFieldRepository fields,
    IAppAccessService access,
    IRolePermissionEnforcer enforcer,
    IUserRepository users,
    IQueryContext user,
    IRecordRepository records,
    FormulaEngine formulaEngine,
    IImportDataStore? store = null,
    IRelationshipRepository? relationships = null)
{
    public const int MaxMappings = 250;
    /// <summary>Tables an import may fill besides its own.</summary>
    public const int MaxAdditionalTargets = 4;
    /// <summary>Sheets one table may read separately.</summary>
    public const int MaxSheetsPerTable = 50;
    public const int MaxFormulaLength = 4000;

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["Import"] = [message] });

    /// <param name="file">For a file import, the file about to be read: its columns stand in for the source table's fields, and the
    /// definition's column references are pointed at them. Null for a table import, which behaves exactly as before.</param>
    public async Task<ImportPlan> BuildAsync(ImportDefinitionConfig cfg, Guid destinationTableId, CancellationToken ct, ImportFileSource? file = null, DateTime? runClock = null)
    {
        if (file is not null) cfg = ImportFileRemap.Apply(cfg, file);
        if (string.IsNullOrWhiteSpace(cfg.Name)) throw Invalid("Give the import a name.");
        if (cfg.Name.Length > 200) throw Invalid("The import name is too long.");
        if (cfg.ImportType is not (ImportTypes.Copy or ImportTypes.Merge)) throw Invalid("The import type must be Copy or Merge.");
        var isMerge = cfg.ImportType == ImportTypes.Merge;
        if (isMerge && cfg.MergeKeyFid is null) throw Invalid("Choose the unique field to match existing records on.");

        var active = cfg.Mappings.Where(m => !m.DoNotImport).ToList();
        if (active.Count is < 1 or > MaxMappings) throw Invalid($"Map between 1 and {MaxMappings} fields.");
        if (active.Select(m => m.DestFid).Distinct().Count() != active.Count)
            throw Invalid("A destination field is mapped more than once.");

        if (file is null) await access.RequirePermissionByTablePublicIdAsync(cfg.SourceTableId, PermissionCodes.RecordsRead, ct);
        await access.RequirePermissionByTablePublicIdAsync(destinationTableId, PermissionCodes.RecordsCreate, ct);
        if (isMerge) await access.RequirePermissionByTablePublicIdAsync(destinationTableId, PermissionCodes.RecordsUpdate, ct);

        var destination = await tables.GetByPublicIdAsync(destinationTableId, ct);
        var source = file is null
            ? await tables.GetByPublicIdAsync(cfg.SourceTableId, ct)
            : new AppTable { Id = 0, PublicId = Guid.Empty, AppId = destination.AppId, Name = file.FileName };
        IReadOnlyList<AppField> sourceFields = file is null ? await fields.ListByTableAsync(source.Id, ct) : ImportFileColumns.ToFields(file.Columns);
        var destFields = source.Id == destination.Id ? sourceFields : await fields.ListByTableAsync(destination.Id, ct);

        // A file has no source table, so there is no role on it to apply; the destination's rules apply as always.
        var sourceAccess = file is null ? await enforcer.GetTableAccessAsync(source, sourceFields, ct) : null;
        var destAccess = await enforcer.GetTableAccessAsync(destination, destFields, ct);
        if (sourceAccess is { CanView: false }) throw new UnauthorizedActionException("read the source table");
        if (!destAccess.Unrestricted && (!destAccess.CanAdd || destAccess.ModifyScope == RecordScopes.None))
            throw new UnauthorizedActionException("import records into the destination table");

        // Updating existing records must respect which records a role may edit; matching through such limits is not
        // supported yet, so merge fails closed rather than touching records the user could not edit by hand.
        if (isMerge && !destAccess.Unrestricted
            && (destAccess.ViewFilter is not null || destAccess.RestrictToCreatedBy.HasValue || destAccess.ModifyScope != RecordScopes.AllRecords))
            throw Invalid("Merge is not available into a table where your role limits which records you can edit.");

        var mergeKey = isMerge ? ResolveMergeKey(cfg.MergeKeyFid!.Value, destFields) : null;
        var visibleSource = sourceAccess is null || sourceAccess.Unrestricted ? sourceFields : sourceAccess.VisibleFields;
        var mappings = new List<ResolvedMapping>(active.Count);
        // Virtual columns are sources like any other, but exist only inside the import: they are added after what the user can see.
        var (virtuals, virtualError) = ImportVirtual.Resolve(cfg.VirtualColumns, visibleSource, formulaEngine);
        if (virtualError is not null) throw Invalid(virtualError);
        var sourceWithVirtuals = virtuals.Count == 0 ? visibleSource : visibleSource.Concat(virtuals.Select(v => v.Field)).ToList();
        var sourceSchema = new AppFieldSchema(sourceWithVirtuals);
        foreach (var m in active)
        {
            var dest = destFields.FirstOrDefault(f => f.Fid == m.DestFid && !f.IsDeleted)
                ?? throw Invalid($"Destination field {m.DestFid} no longer exists.");
            // The Record ID# is never written; it can only be mapped as the key an import matches existing records on.
            if (!ImportTypeCompatibility.IsWritable(dest) || (ImportTypeCompatibility.IsRecordId(dest) && dest.Fid != mergeKey?.Fid))
                throw Invalid($"'{ImportTypeCompatibility.DisplayName(dest)}' cannot be imported into.");
            // The Record ID# is only matched on, never written, so it needs no edit right.
            if (!destAccess.Unrestricted && !ImportTypeCompatibility.IsRecordId(dest) && !destAccess.EditableFieldIds.Contains(m.DestFid))
                throw new UnauthorizedActionException($"write to '{ImportTypeCompatibility.DisplayName(dest)}'");

            mappings.Add(m.Source switch
            {
                ImportMappingSource.Dynamic => ResolveDynamic(m, dest, sourceWithVirtuals, anySourceType: file is not null || (m.SourceFid is { } sf && ImportVirtual.IsVirtualFid(sf))),
                ImportMappingSource.Static => ResolveStatic(m, dest, dest.Fid == mergeKey?.Fid),
                ImportMappingSource.Formula => ResolveFormula(m, dest, sourceSchema),
                _ => throw Invalid($"'{ImportTypeCompatibility.DisplayName(dest)}' has an unknown value source.")
            });
        }

        if (mergeKey is not null && mappings.All(m => m.Destination.Fid != mergeKey.Fid))
            throw Invalid($"Map a source field to '{ImportTypeCompatibility.DisplayName(mergeKey)}', the field existing records are matched on.");
        var referenceTargets = await ResolveReferenceTargetsAsync(mappings, destFields, ct);

        var rules = ValidateRules(cfg, mappings, mergeKey, isMerge);
        ValidateConditions(cfg.Conditions, visibleSource);

        // Definition conditions AND the user's role filter, resolved for this run, then split into what SQL can
        // evaluate and what has to be checked on each row (computed and encrypted fields).
        var combined = new FilterGroup();
        if (cfg.Conditions is { Nodes.Count: > 0 }) combined.Nodes.Add(new() { Group = cfg.Conditions });
        if (sourceAccess?.ViewFilter is not null) combined.Nodes.Add(new() { Group = sourceAccess.ViewFilter });
        var resolved = combined.Nodes.Count == 0 ? null : await ResolveAsync(combined, sourceFields, ct);
        // A file's rows are never filtered by SQL: every condition on a file's columns is checked on each row as it is read.
        var rowFids = sourceFields.Where(f => f.Fid.HasValue && (file is not null || PhysicalNaming.IsComputedTypeCode(f.TypeCode) || f.IsEncrypted))
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        var (sqlFilter, rowFilterTree) = FormulaFilterSorter.SplitFilterTree(resolved, rowFids);

        // This table's own filter is checked on each row in memory, whatever the field: the source is read once for every table, so SQL
        // cannot be asked a different question for each. Its fields are read along with everything else the tables need.
        ImportRowFilter? tableFilter = null;
        var tableFids = new HashSet<long>();
        if (cfg.TableConditions is { Nodes.Count: > 0 })
        {
            ValidateConditions(cfg.TableConditions, visibleSource);
            var own = await ResolveAsync(cfg.TableConditions, sourceFields, ct);
            tableFilter = new ImportRowFilter(own, sourceFields);
            CollectFids(own, tableFids);
        }

        var recordId = sourceFields.FirstOrDefault(ImportTypeCompatibility.IsRecordId)
            ?? throw Invalid("The source table has no Record ID# field.");
        var conditionFids = new HashSet<long>();
        CollectFids(resolved, conditionFids);
        var computedFids = sourceFields.Where(f => f.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(f.TypeCode))
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        // The fields the mappings read: source fields, and whatever each formula refers to.
        var mappedFids = mappings.Where(m => m.Source is not null).Select(m => (long)m.Source!.Fid!.Value)
            .Concat(mappings.Where(m => m.Formula is not null).SelectMany(m => m.Formula!.ReferencedFieldIds))
            .Concat(virtuals.Where(v => v.Formula is not null).SelectMany(v => v.Formula!.ReferencedFieldIds)).ToHashSet();
        var needsProjection = file is null && (mappedFids.Any(computedFids.Contains) || tableFids.Any(computedFids.Contains))
            || FormulaFilterSorter.TreeContainsFormulaField(rowFilterTree, computedFids);

        var readFids = mappedFids.Concat(conditionFids.Where(f => f > 0)).Concat(tableFids.Where(f => f > 0)).ToHashSet();
        if (file?.Sheets is not null && ImportFileSheets.MissingColumns(file, readFids) is { Count: > 0 } sheetProblems)
            throw Invalid(string.Join(" ", sheetProblems.Take(5)) + (sheetProblems.Count > 5 ? $" ...and {sheetProblems.Count - 5} more." : ""));
        var readFields = needsProjection
            ? sourceFields
            : sourceFields.Where(f => f.Fid.HasValue && (readFids.Contains(f.Fid.Value) || f.Id == recordId.Id)).ToList();

        return new ImportPlan
        {
            Source = source, SourceFields = sourceFields, Destination = destination, DestinationFields = destFields,
            Mappings = mappings, ReferenceTargets = referenceTargets, ImportType = cfg.ImportType, MergeKey = mergeKey, Rules = rules, ConstraintPolicy = cfg.ConstraintPolicy,
            SourceFilter = sqlFilter, RowFilter = rowFilterTree is null ? null : new ImportRowFilter(rowFilterTree, sourceFields),
            SourceOwnerRestriction = sourceAccess?.RestrictToCreatedBy, File = file, ReadFields = readFields, NeedsProjection = needsProjection,
            FormulaOptions = FormulaOptionsForRun(source, runClock), Virtuals = virtuals, TableFilter = tableFilter
        };
    }

    /// <param name="anySourceType">A file's values are text whatever they look like; each is converted to the destination's type as it
    /// is written, and one that does not convert is reported for its row. So no type pairing is refused up front.</param>
    /// <summary>One plan per table the import fills: the import's own table first, then the others in the order saved. Every table is planned
    /// and permission-checked exactly as a single-table import is (its own mapping, import type, merge key and rules over the shared source
    /// and conditions), against one fixed clock, so a "today" in two tables' formulas is the same day. An import into one table returns
    /// the one plan <see cref="BuildAsync"/> would.</summary>
    public async Task<IReadOnlyList<ImportPlan>> BuildAllAsync(ImportDefinitionConfig cfg, Guid homeTableId, CancellationToken ct, ImportFileSource? file = null)
    {
        // With one table, the import's conditions already say which rows go in; a second set would only be a way to get them wrong.
        if (cfg.AdditionalTargets.Count == 0 && cfg.SheetMappings.Count == 0 && cfg.TableConditions is { Nodes.Count: > 0 })
            throw Invalid("A table's own filter is for an import that fills more than one table. Use the conditions above instead.");
        if (cfg.SheetMappings.Count > 0 && cfg.TableConditions is { Nodes.Count: > 0 })
            throw Invalid("A table that reads the workbook sheet by sheet is filtered in each sheet's own mapping.");
        var clock = DateTime.UtcNow;
        var plans = new List<ImportPlan>();
        plans.AddRange(await BuildTableAsync(cfg, homeTableId, cfg.SheetMappings, file, clock, 0, ct));
        if (cfg.AdditionalTargets.Count == 0) return plans;
        if (cfg.AdditionalTargets.Count > MaxAdditionalTargets)
            throw Invalid($"An import can fill at most {MaxAdditionalTargets + 1} tables.");

        if (cfg.AdditionalTargets.Any(t => t.DestinationTableId == Guid.Empty)) throw Invalid("Choose the table for each extra target.");
        var chosen = cfg.AdditionalTargets.Select(t => t.DestinationTableId).Prepend(homeTableId).ToList();
        if (chosen.Distinct().Count() != chosen.Count) throw Invalid("Each table can be filled only once by an import.");

        var index = 0;
        foreach (var target in cfg.AdditionalTargets)
        {
            index++;
            if (target.SheetMappings.Count > 0 && target.Conditions is { Nodes.Count: > 0 })
                throw Invalid("A table that reads the workbook sheet by sheet is filtered in each sheet's own mapping.");
            var view = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(cfg))!;
            view.AdditionalTargets = [];
            view.SheetMappings = [];
            view.TableConditions = target.Conditions;
            view.ImportType = target.ImportType;
            view.MergeKeyFid = target.MergeKeyFid;
            view.Mappings = target.Mappings;
            view.ColumnRules = target.ColumnRules;
            try { plans.AddRange(await BuildTableAsync(view, target.DestinationTableId, target.SheetMappings, file, clock, index, ct)); }
            catch (ValidationException ex)
            {
                // Say which table the problem is in: the messages themselves speak of "the destination".
                var name = (await tables.GetByPublicIdAsync(target.DestinationTableId, ct)).Name;
                throw Invalid($"'{name}': {ex.Message}");
            }
        }

        var home = plans[0].Destination;
        if (plans.Select(p => p.Destination.Id).Distinct().Count() != chosen.Count)
            throw Invalid("Each table can be filled only once by an import.");
        if (plans.Any(p => p.Destination.AppId != home.AppId))
            throw Invalid("All the tables an import fills must be in the same app.");
        return plans;
    }

    /// <summary>The plans of one table: one for the table as the import reads its source, or, when the table reads a workbook sheet by sheet, one
    /// for each sheet it takes, each built like a single-sheet file import against that sheet's own columns. A sheet's filter is the table's own
    /// filter for that sheet (checked per row, in memory), and its virtual columns are its own.</summary>
    private async Task<List<ImportPlan>> BuildTableAsync(
        ImportDefinitionConfig view, Guid tableId, List<ImportSheetMapping> sheetMappings, ImportFileSource? file, DateTime clock, int tableIndex, CancellationToken ct)
    {
        if (sheetMappings.Count == 0)
        {
            var whole = await BuildAsync(view, tableId, ct, file, clock);
            whole.TableIndex = tableIndex;
            return [whole];
        }
        if (view.SourceKind != ImportSourceKinds.File || file?.SheetSources is null)
            throw Invalid("Sheets can only be mapped separately for an import that reads an Excel workbook.");
        var duplicate = sheetMappings.GroupBy(s => s.Sheet?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw Invalid($"Sheet '{duplicate.Key}' is mapped more than once for the same table.");
        if (sheetMappings.Count > MaxSheetsPerTable) throw Invalid($"A table can read at most {MaxSheetsPerTable} sheets.");

        // 0 says "combine on nothing" (chosen on purpose); no value on a table whose sheets are read together means "work it out" (below).
        var joinFids = sheetMappings.Where(s => s.JoinFieldFid > 0).Select(s => s.JoinFieldFid!.Value).Distinct().ToList();
        var joinOff = sheetMappings.Any(s => s.JoinFieldFid == 0);
        if (joinFids.Count > 1) throw Invalid("The sheets of a table can be combined on one field only.");
        var combine = joinFids.Count == 1 || sheetMappings.Any(s => s.Combined);

        var plans = new List<ImportPlan>(sheetMappings.Count);
        foreach (var sheet in sheetMappings)
        {
            if (!file.SheetSources.TryGetValue(sheet.Sheet ?? "", out var sheetFile))
                throw Invalid($"The workbook has no sheet called '{sheet.Sheet}'.");
            var unit = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(view))!;
            unit.SheetMappings = [];
            unit.File = sheetFile.Options;
            unit.Mappings = sheet.Mappings;
            unit.ColumnRules = sheet.ColumnRules;
            unit.VirtualColumns = sheet.VirtualColumns;
            unit.Conditions = null;               // the import-wide filter belongs to the shared mapping: a sheet has its own
            unit.TableConditions = sheet.Conditions;
            if (combine)
            {
                // The sheet only says what each of its rows holds; the table's type and key are applied to the combined records, which is where
                // a key can be found (a sheet without the key is fine).
                unit.ImportType = ImportTypes.Copy;
                unit.MergeKeyFid = null;
            }
            try { plans.Add(await BuildAsync(unit, tableId, ct, sheetFile, clock)); }
            catch (ValidationException ex) { throw Invalid($"Sheet '{sheet.Sheet}': {ex.Message}"); }
            var plan = plans[^1];
            plan.TableIndex = tableIndex;
            plan.SheetName = sheet.Sheet;
            if (!combine) continue;
            if (plan.Mappings.Any(m => m.Kind != ImportMappingSource.Dynamic))
                throw Invalid($"Sheet '{sheet.Sheet}': when a table's sheets are combined, every row of its mapping takes its value from a column.");
            if (sheet.JoinFieldFid is > 0 and var join)
            {
                if (plan.Mappings.All(m => m.Destination.Fid != join))
                    throw Invalid($"Sheet '{sheet.Sheet}': choose a column for the field its rows are combined on, or leave the sheet out of the combining.");
                plan.JoinFieldFid = join;
            }
        }
        if (!combine) return plans;
        if (joinFids.Count == 0 && !joinOff) InferJoin(plans, view.ImportType == ImportTypes.Merge ? view.MergeKeyFid : null);
        return [await BuildJoinedAsync(view, tableId, plans, clock, tableIndex, ct)];
    }

    /// <summary>A table whose sheets are read together, with no field chosen to combine them on: rows are combined on the field its sheets share, so a
    /// record spread over sheets is not rejected for the fields another sheet holds. The merge key if two or more sheets map it, otherwise a unique field
    /// two or more sheets map (the first, in the first sheet's order); never a field that is not unique, since equal values there mean nothing. When no
    /// field qualifies, each row stays its own record.</summary>
    private static void InferJoin(List<ImportPlan> members, int? mergeKeyFid)
    {
        var sheetsWith = members.SelectMany(p => p.Mappings.Select(m => m.Destination).DistinctBy(d => d.Fid)).GroupBy(d => d.Fid!.Value).ToDictionary(g => g.Key, g => g.Count());
        var shared = members.SelectMany(p => p.Mappings.Select(m => m.Destination)).Where(d => sheetsWith[d.Fid!.Value] >= 2).DistinctBy(d => d.Fid).ToList();
        var chosen = shared.FirstOrDefault(d => d.Fid == mergeKeyFid) ?? shared.FirstOrDefault(d => d.IsUnique && !ImportTypeCompatibility.IsRecordId(d));
        if (chosen is null) return;
        foreach (var plan in members)
            if (plan.Mappings.Any(m => m.Destination.Fid == chosen.Fid)) plan.JoinFieldFid = chosen.Fid;
    }

    /// <summary>The plan of a table whose sheets are combined: its destination fields are every field any sheet writes, and each takes its value
    /// from a column of the combined record. Built like a file import of its own, against those columns, so the table's import type, key, rules
    /// and every check a run makes apply to the whole record, not to each sheet's part of it.</summary>
    private async Task<ImportPlan> BuildJoinedAsync(
        ImportDefinitionConfig view, Guid tableId, List<ImportPlan> members, DateTime clock, int tableIndex, CancellationToken ct)
    {
        var order = new List<AppField>();
        foreach (var m in members.SelectMany(p => p.Mappings))
            if (order.All(f => f.Fid != m.Destination.Fid)) order.Add(m.Destination);
        var names = order.Select(ImportTypeCompatibility.DisplayName).ToArray();
        var columns = ImportFileColumns.FromHeader(names, names.Length);

        var rules = new Dictionary<int, ImportColumnRule>();
        foreach (var (fid, rule) in members.SelectMany(p => p.Rules))
        {
            rules.TryGetValue(fid, out var have);
            rules[fid] = new ImportColumnRule
            {
                DestFid = fid, RemoveDuplicates = rule.RemoveDuplicates || have?.RemoveDuplicates == true,
                RequireField = rule.RequireField || have?.RequireField == true, IgnoreBlanks = rule.IgnoreBlanks || have?.IgnoreBlanks == true
            };
        }

        var unit = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(view))!;
        unit.SheetMappings = [];
        unit.VirtualColumns = [];
        unit.Conditions = null;
        unit.TableConditions = null;
        unit.File = new ImportFileOptions { Format = ImportFileFormats.Xlsx, ColumnBy = ImportSourceKinds.ColumnByPosition, HeaderRow = 1, Columns = columns.Select(c => c.Name).ToList() };
        unit.Mappings = order.Select((f, i) => new ImportFieldMapping { DestFid = f.Fid!.Value, Source = ImportMappingSource.Dynamic, SourceFid = columns[i].Fid }).ToList();
        unit.ColumnRules = rules.Values.ToList();

        var first = members[0].File!;
        var rows = members.Sum(p => p.File?.DataRows ?? 0);
        var file = new ImportFileSource(
            columns, first.FileName, first.Layout, first.StoragePath, rows, rows, unit.File,
            Sheets: members.Select(p => new ImportFileSheet(p.SheetName!, columns, p.File?.DataRows ?? 0, p.File?.LastRow ?? 0)).ToList());
        ImportPlan plan;
        try { plan = await BuildAsync(unit, tableId, ct, file, clock); }
        catch (ValidationException ex) { throw Invalid($"Combining the sheets: {ex.Message}"); }
        plan.TableIndex = tableIndex;
        plan.JoinedFrom = members;
        return plan;
    }

    /// <summary>The plan the source is read with when several tables are filled: the first table's, widened to read every field any of the
    /// tables maps, uses in a formula or filters on. Everything else about reading (source, conditions, file) is the same in every plan.</summary>
    /// <param name="virtualsPerPlan">Each plan has virtual columns of its own (tables reading a sheet differently), so they are calculated for each
    /// plan's rows, not once for the read.</param>
    public static ImportPlan UnionForReading(IReadOnlyList<ImportPlan> plans, bool virtualsPerPlan = false)
    {
        var first = plans[0];
        if (plans.Count == 1 && !virtualsPerPlan) return first;
        return new ImportPlan
        {
            Source = first.Source, File = first.File, SourceFields = first.SourceFields, Destination = first.Destination,
            DestinationFields = first.DestinationFields, Mappings = first.Mappings, ImportType = first.ImportType,
            SourceFilter = first.SourceFilter, RowFilter = first.RowFilter, MergeKey = first.MergeKey, Rules = first.Rules,
            ConstraintPolicy = first.ConstraintPolicy, SourceOwnerRestriction = first.SourceOwnerRestriction,
            ReadFields = plans.SelectMany(p => p.ReadFields).GroupBy(f => f.Id).Select(g => g.First()).ToList(),
            NeedsProjection = plans.Any(p => p.NeedsProjection), FormulaOptions = first.FormulaOptions, Virtuals = virtualsPerPlan ? [] : first.Virtuals,
            TableIndex = first.TableIndex, SheetName = first.SheetName
        };
    }

    private ResolvedMapping ResolveDynamic(ImportFieldMapping m, AppField dest, IReadOnlyList<AppField> visibleSource, bool anySourceType)
    {
        var src = m.SourceFid.HasValue ? visibleSource.FirstOrDefault(f => f.Fid == m.SourceFid && !f.IsDeleted) : null;
        if (src is null) throw Invalid($"The source field for '{ImportTypeCompatibility.DisplayName(dest)}' is missing or not visible to you.");
        var problem = anySourceType ? null : ImportTypeCompatibility.CheckMapping(src, dest);
        if (problem is not null) throw Invalid(problem);
        return new ResolvedMapping { Destination = dest, Kind = ImportMappingSource.Dynamic, Source = src, SourceColumn = PhysicalNaming.GetPhysicalColumnName(src) };
    }

    /// <summary>A fixed value goes to every row, so it is parsed once here. It cannot be the match key (every row would
    /// then match the same record).</summary>
    private static ResolvedMapping ResolveStatic(ImportFieldMapping m, AppField dest, bool isKey)
    {
        var name = ImportTypeCompatibility.DisplayName(dest);
        if (isKey) throw Invalid($"'{name}' is the field records are matched on, so it cannot be a fixed value: every row would match the same record.");
        if (dest.IsUnique) throw Invalid($"'{name}' must be unique, so it cannot be a fixed value: every row would get the same value.");
        if (string.IsNullOrWhiteSpace(m.StaticValue)) throw Invalid($"Enter the fixed value for '{name}'.");
        if (!ImportTypeCompatibility.TryParseStatic(m.StaticValue, dest, out var value, out var error)) throw Invalid($"'{name}': {error}");
        if (ImportFieldFormat.For(dest)?.Check(value) is { } formatError) throw Invalid(formatError);
        return new ResolvedMapping { Destination = dest, Kind = ImportMappingSource.Static, Constant = value, StaticText = m.StaticValue };
    }

    /// <summary>A formula is the platform's own formula language, compiled against the source table's fields the user can see.
    /// Its result type must be one the destination can safely take, which is checked here, before any row is read.</summary>
    private ResolvedMapping ResolveFormula(ImportFieldMapping m, AppField dest, AppFieldSchema sourceSchema)
    {
        var name = ImportTypeCompatibility.DisplayName(dest);
        if (string.IsNullOrWhiteSpace(m.Formula)) throw Invalid($"Enter the formula for '{name}'.");
        if (m.Formula.Length > MaxFormulaLength) throw Invalid($"The formula for '{name}' is too long (at most {MaxFormulaLength} characters).");
        if (ImportFormulaGuard.IsTooDeep(m.Formula)) throw Invalid($"The formula for '{name}' is nested too deeply (at most {ImportFormulaGuard.MaxNesting} levels).");
        var compiled = formulaEngine.Compile(m.Formula, sourceSchema);
        if (compiled.Diagnostics.FirstOrDefault(d => d.Severity == FormulaSeverity.Error) is { } error)
            throw Invalid($"The formula for '{name}' has an error: {error.Message}");
        var problem = ImportTypeCompatibility.CheckFormulaResult(compiled.ResultType, dest);
        if (problem is not null) throw Invalid($"'{name}': {problem}");
        return new ResolvedMapping { Destination = dest, Kind = ImportMappingSource.Formula, Formula = compiled };
    }

    private EvaluationOptions FormulaOptionsForRun(AppTable source, DateTime? runClock) => new()
    {
        UtcNow = runClock ?? DateTime.UtcNow,
        CurrentUser = user.UserId > 0
            ? new UserRef(user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), user.UserEmail, user.UserName)
            : null,
        TableId = source.PublicId.ToString()
    };

    /// <summary>Rules must belong to a mapped column, must not contradict each other, and must be enforceable. Rules with
    /// nothing switched on are dropped. Returns the rules by destination Fid.</summary>
    private static Dictionary<int, ImportColumnRule> ValidateRules(
        ImportDefinitionConfig cfg, IReadOnlyList<ResolvedMapping> mappings, AppField? mergeKey, bool isMerge)
    {
        if (!ImportConstraintPolicy.IsValid(cfg.ConstraintPolicy))
            throw Invalid("Choose how to handle rows that break a unique rule.");

        var mapped = mappings.ToDictionary(m => m.Destination.Fid!.Value, m => m.Destination);
        var rules = new Dictionary<int, ImportColumnRule>();
        foreach (var rule in cfg.ColumnRules.Where(r => r.RemoveDuplicates || r.RequireField || r.IgnoreBlanks))
        {
            if (!mapped.TryGetValue(rule.DestFid, out var field))
                throw Invalid("A column rule is set on a field that is not mapped.");
            var name = ImportTypeCompatibility.DisplayName(field);
            if (!rules.TryAdd(rule.DestFid, rule)) throw Invalid($"'{name}' has more than one set of rules.");
            if (rule.RemoveDuplicates && mappings.First(m => m.Destination.Fid == rule.DestFid).Kind == ImportMappingSource.Static)
                throw Invalid($"'{name}' is a fixed value, so removing duplicates would skip every row but the first.");
            if (rule.RequireField && rule.IgnoreBlanks)
                throw Invalid($"'{name}': choose either Require field or Ignore blanks. They contradict each other.");
            if (rule.IgnoreBlanks && mergeKey?.Fid == rule.DestFid)
                throw Invalid($"'{name}' is the field records are matched on, so a blank value cannot be ignored.");
        }
        return rules;
    }

    /// <summary>For each Reference field the import writes: its parent table, the key a value is looked up by, and where a value that finds no parent is
    /// kept. The person needs to be able to read the parent table (the reference picker asks the same), and a field that only accepts some of its
    /// parent's records (a dependent dropdown) is refused: an import cannot judge that rule.</summary>
    private async Task<Dictionary<int, ImportReferenceTarget>> ResolveReferenceTargetsAsync(
        IReadOnlyList<ResolvedMapping> mappings, IReadOnlyList<AppField> destFields, CancellationToken ct)
    {
        var targets = new Dictionary<int, ImportReferenceTarget>();
        foreach (var m in mappings.Where(m => ImportTypeCompatibility.IsReference(m.Destination)))
        {
            var name = ImportTypeCompatibility.DisplayName(m.Destination);
            var settings = FormulaTypeMap.ParseReferenceSettings(m.Destination.Settings);
            if (settings?.ParentTableId is not long parentId) throw Invalid($"'{name}' is not connected to a parent table.");
            if (ReferenceFilterResolver.HasFilter(settings))
                throw Invalid($"'{name}' only accepts some records of its parent table (a rule on the field), which an import cannot check yet.");
            var parent = await tables.GetByIdAsync(parentId, ct);
            await access.RequirePermissionByTablePublicIdAsync(parent.PublicId, PermissionCodes.RecordsRead, ct);

            // The key a person knows a parent record by: the relationship's own display key, else the table's key, else the Record ID# itself.
            var relationship = settings.RelationshipId is long relId && relationships is not null ? await relationships.GetByIdAsync(relId, ct) : null;
            var parentFields = await fields.ListByTableAsync(parent.Id, ct);
            // A table with no key set is still shown by a field (the one its reference picker labels records with): that is what a person has in the file.
            var displayKey = KeyFieldResolver.ResolveDisplayKey(relationship, parent, parentFields) ?? PickerLabelField(parent, parentFields);
            var original = settings.OriginalFid is int o && destFields.Any(f => f.Fid == o && !f.IsDeleted) ? o : (int?)null;
            targets[m.Destination.Fid!.Value] = new ImportReferenceTarget(parent, displayKey, original, parentFields);
        }
        return targets;
    }

    /// <summary>The field the reference picker labels a parent's records with (its first picker field, else the table's display field, else its first ordinary
    /// stored field), when it is one a value can be looked up in: a stored column, neither computed, hidden nor system.</summary>
    private static AppField? PickerLabelField(AppTable parent, IReadOnlyList<AppField> parentFields)
    {
        static bool Storable(AppField f) => f.Fid.HasValue && !f.IsDeleted && !f.IsHidden && !f.IsSystem && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)
            && !PhysicalNaming.IsRangeTypeCode(f.TypeCode) && f.TypeCode is "Text" or "Email" or "Phone" or "Url" or "Number" or "Currency" or "Percent" or "Rating" or "Date" or "DateTime";
        AppField? Pick(long? id) => id is long x ? parentFields.FirstOrDefault(f => f.Id == x && Storable(f)) : null;
        return Pick(parent.DefaultRecordPickerField1Id) ?? Pick(parent.DisplayFieldId) ?? parentFields.FirstOrDefault(Storable);
    }

    /// <summary>The merge key must be a unique field (or the Record ID#) so a value identifies exactly one record.</summary>
    private static AppField ResolveMergeKey(int fid, IReadOnlyList<AppField> destFields)
    {
        var key = destFields.FirstOrDefault(f => f.Fid == fid && !f.IsDeleted) ?? throw Invalid("The merge key field no longer exists.");
        if (!ImportTypeCompatibility.IsKeyCandidate(key))
            throw Invalid($"'{ImportTypeCompatibility.DisplayName(key)}' is not unique, so it cannot be used to match records.");
        // An encrypted key is allowed: its stored values are ciphertext, so matching uses an index of the decrypted values.
        if (!ImportTypeCompatibility.IsWritable(key))
            throw Invalid($"'{ImportTypeCompatibility.DisplayName(key)}' cannot be used as a merge key.");
        return key;
    }

    /// <summary>A merge key with duplicate values in the destination would make matching ambiguous, so the run is
    /// refused before anything is written. The Record ID# and database-enforced unique fields cannot have duplicates,
    /// but the check is cheap and also catches a unique flag whose index is missing.</summary>
    public async Task EnsureMergeKeyIsUniqueAsync(ImportPlan plan, CancellationToken ct)
    {
        if (plan.MergeKey is null || ImportTypeCompatibility.IsRecordId(plan.MergeKey)) return;
        if (!await records.HasDuplicatesAsync(plan.Destination, plan.MergeKey, ct)) return;
        var message = $"'{ImportTypeCompatibility.DisplayName(plan.MergeKey)}' has duplicate values in {plan.Destination.Name}";
        // Say which values, so they can be found and fixed (an encrypted field's stored values are ciphertext: nothing to show).
        if (store is not null && !plan.MergeKey.IsEncrypted)
        {
            var examples = await store.FindDuplicateValuesAsync(plan.Destination, plan.MergeKey, 5, ct);
            if (examples.Count > 0)
                message += $" (for example {string.Join(", ", examples.Select(e => $"'{e.Value}' on {e.Count} records"))})";
        }
        throw Invalid($"{message}. Fix them before running a merge.");
    }

    /// <summary>Same structural rules a report filter gets, plus the ones that only make sense for a saved import.</summary>
    private static void ValidateConditions(FilterGroup? conditions, IReadOnlyList<AppField> visibleFields)
    {
        if (conditions is null) return;
        var errors = new Dictionary<string, string[]>();
        var validIds = visibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).Append(-1).ToHashSet();
        CommonReportValidationHelpers.ValidateFilterGroup(conditions, validIds, errors);

        var computed = visibleFields.Where(f => f.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(f.TypeCode)).Select(f => (long)f.Fid!.Value).ToHashSet();
        void Walk(FilterGroup g)
        {
            foreach (var n in g.Nodes)
            {
                if (n.Condition is { } c)
                {
                    if (string.Equals(c.ValueMode, "ask", StringComparison.OrdinalIgnoreCase))
                        CommonReportValidationHelpers.AddError(errors, "conditions", "\"Ask the user\" values are not available in an import, which runs without anyone to ask.");
                    if (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && c.ValueFieldId.HasValue && computed.Contains(c.ValueFieldId.Value))
                        CommonReportValidationHelpers.AddError(errors, "conditions", "A condition cannot compare against a formula, lookup or summary field's value.");
                }
                if (n.Group is not null) Walk(n.Group);
            }
        }
        Walk(conditions);
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    /// <summary>Turns run-relative values into literals: today/yesterday/past-N-days, "is during …" ranges, "is current
    /// user" and picked users (sent as public ids, stored as row ids).</summary>
    private async Task<FilterGroup> ResolveAsync(FilterGroup group, IReadOnlyList<AppField> sourceFields, CancellationToken ct)
    {
        var dated = RunReportQueryHandler.ResolveDateValueModeConditions(group)!;
        var userFids = sourceFields.Where(f => f.Fid.HasValue && f.TypeCode is "User" or "MultiUser").Select(f => (long)f.Fid!.Value).ToHashSet();
        return await ResolveUsersAsync(dated, userFids, new Dictionary<Guid, long>(), ct);
    }

    private async Task<FilterGroup> ResolveUsersAsync(FilterGroup group, HashSet<long> userFids, Dictionary<Guid, long> cache, CancellationToken ct)
    {
        var nodes = new List<FilterNode>(group.Nodes.Count);
        foreach (var n in group.Nodes)
        {
            var condition = n.Condition;
            if (condition is { Operator: "isCurrentUser" })
                condition = new FilterCondition { FieldId = condition.FieldId, Operator = "eq", Value = user.UserId.ToString() };
            else if (condition is { Value.Length: > 0 } && userFids.Contains(condition.FieldId)
                     && (condition.ValueMode is null || string.Equals(condition.ValueMode, "literal", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = new List<string>();
                foreach (var part in condition.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    parts.Add(Guid.TryParse(part, out var publicId) ? (await ResolveUserIdAsync(publicId, cache, ct)).ToString() : part);
                condition = new FilterCondition
                {
                    FieldId = condition.FieldId, Operator = condition.Operator, SubField = condition.SubField,
                    ValueMode = condition.ValueMode, Value = string.Join(",", parts)
                };
            }
            nodes.Add(new FilterNode { Condition = condition, Group = n.Group is null ? null : await ResolveUsersAsync(n.Group, userFids, cache, ct) });
        }
        return new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }

    private async Task<long> ResolveUserIdAsync(Guid publicId, Dictionary<Guid, long> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(publicId, out var id)) return id;
        try { id = (await users.GetByPublicIdAsync(publicId, ct)).Id; }
        catch (Exception ex) when (ex is not OperationCanceledException) { id = -1; } // an unknown user simply matches nothing
        return cache[publicId] = id;
    }

    private static void CollectFids(FilterGroup? group, HashSet<long> into)
    {
        if (group is null) return;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                into.Add(c.FieldId);
                if (c.ValueFieldId.HasValue) into.Add(c.ValueFieldId.Value);
            }
            CollectFids(node.Group, into);
        }
    }
}
