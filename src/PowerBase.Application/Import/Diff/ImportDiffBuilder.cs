using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Mapping;
using PowerBase.Application.Import.Pbl;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Import.Diff;

/// <summary>
/// Compares a PBL document against an existing app and classifies every element as New, Changed,
/// Identical or Conflict (see <see cref="ImportDiffStatus"/>). Matching is by logical ref — a
/// recorded <see cref="ImportSourceRef"/> first, then (for apps not built by import) by name —
/// never by DBID; a user mapping can override either. Nothing is written.
///
/// What "Overwrite" may change is deliberately narrow: table labels/description, a field's
/// label/description, and an app variable's value/description. Everything else
/// that matches (reports, forms, roles, relationships, pages) is Identical and skip-only.
/// </summary>
public class ImportDiffBuilder
{
    private static readonly HashSet<string> RelationshipFieldTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Reference", "Lookup", "Summary", "ReportLink" };

    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IFormRepository _formRepo;
    private readonly IAppRoleRepository _roleRepo;
    private readonly IAppVariableRepository _variableRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IImportSourceRefRepository _sourceRefRepo;

    public ImportDiffBuilder(
        IAppRepository appRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        IReportRepository reportRepo, IFormRepository formRepo, IAppRoleRepository roleRepo,
        IAppVariableRepository variableRepo, IPageRepository pageRepo, IImportSourceRefRepository sourceRefRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _formRepo = formRepo;
        _roleRepo = roleRepo;
        _variableRepo = variableRepo;
        _pageRepo = pageRepo;
        _sourceRefRepo = sourceRefRepo;
    }

    public async Task<ImportDiffResult> BuildAsync(PblDocument document, Guid appPublicId, ImportMapping? mapping, CancellationToken ct)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var app = await _appRepo.GetByPublicIdAsync(appPublicId, ct);

        var refs = await _sourceRefRepo.ListByAppAsync(appId, ct);
        var elementIdByRef = refs.ToDictionary(r => Key(r.ElementType, r.SourceLogicalRef), r => r.ElementId, StringComparer.Ordinal);
        var overrides = (mapping?.Elements ?? [])
            .Where(e => e.TargetElementPublicId.HasValue)
            .GroupBy(e => Key(e.ElementType, e.SourceLogicalRef), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TargetElementPublicId!.Value, StringComparer.Ordinal);

        var items = new List<ImportDiffItem>();
        var onlyInApp = new List<ImportOnlyInAppItem>();
        var tables = document.Tables ?? [];

        // ----- Tables -----
        var existingTables = await _tableRepo.ListByAppAsync(appId, ct);
        var usedTableIds = new HashSet<long>();
        var matchedTableByRef = new Dictionary<string, AppTable>(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            var (existing, matchedBy) = Match(ImportElementTypes.Table, table.LogicalRef, table.Name,
                existingTables, t => t.Id, t => t.PublicId, t => t.Name, elementIdByRef, overrides, usedTableIds);

            if (existing is null)
            {
                items.Add(NewItem(ImportElementTypes.Table, table.LogicalRef, table.Name, null, ""));
                continue;
            }

            matchedTableByRef[table.LogicalRef] = existing;
            var changes = new List<ImportPropertyChange>();
            Compare(changes, "Singular label", existing.SingularLabel, table.SingularLabel, onlyWhenProvided: true);
            Compare(changes, "Plural label", existing.PluralLabel, table.PluralLabel, onlyWhenProvided: true);
            Compare(changes, "Description", existing.Description, table.Description, onlyWhenProvided: true);

            items.Add(Matched(ImportElementTypes.Table, table.LogicalRef, table.Name, null, "", existing.PublicId, existing.Id, matchedBy, changes, canOverwrite: true));
        }

        foreach (var existing in existingTables.Where(t => !usedTableIds.Contains(t.Id)))
            onlyInApp.Add(new ImportOnlyInAppItem { ElementType = ImportElementTypes.Table, Name = existing.Name });

        // ----- Fields, reports, forms (per table) -----
        foreach (var table in tables)
        {
            if (!matchedTableByRef.TryGetValue(table.LogicalRef, out var existingTable))
            {
                // Everything on a table that is new to the app is new too.
                foreach (var field in table.Fields ?? [])
                    items.Add(NewItem(ImportElementTypes.Field, field.LogicalRef, field.Name, table.Name, table.LogicalRef));
                foreach (var report in table.Reports ?? [])
                    items.Add(NewItem(ImportElementTypes.Report, report.LogicalRef, report.Name, table.Name, table.LogicalRef));
                foreach (var form in (document.Forms ?? []).Where(f => f.TableRef == table.LogicalRef))
                    items.Add(NewItem(ImportElementTypes.Form, form.LogicalRef, form.Name, table.Name, table.LogicalRef));
                continue;
            }

            var existingFields = (await _fieldRepo.ListByTableAsync(existingTable.Id, ct)).ToList();
            var usedFieldIds = new HashSet<long>();
            foreach (var field in table.Fields ?? [])
            {
                var (existing, matchedBy) = Match(ImportElementTypes.Field, field.LogicalRef, field.Name,
                    existingFields, f => f.Id, f => f.PublicId, f => f.Name, elementIdByRef, overrides, usedFieldIds);

                if (existing is null)
                {
                    items.Add(NewItem(ImportElementTypes.Field, field.LogicalRef, field.Name, table.Name, table.LogicalRef));
                    continue;
                }

                if (!string.Equals(existing.TypeCode, field.TypeCode, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new ImportDiffItem
                    {
                        ElementType = ImportElementTypes.Field, SourceLogicalRef = field.LogicalRef, Name = field.Name,
                        TableName = table.Name, TableRef = table.LogicalRef, Status = ImportDiffStatus.Conflict,
                        MatchedBy = matchedBy, TargetElementPublicId = existing.PublicId, CanOverwrite = false,
                        Reason = $"Field type differs (app: {existing.TypeCode}, file: {field.TypeCode}). Field types are never changed by an import.",
                    });
                    continue;
                }

                var changes = new List<ImportPropertyChange>();
                Compare(changes, "Label", existing.Label, field.Label ?? field.Name, onlyWhenProvided: false);
                Compare(changes, "Description", existing.Description, field.Description, onlyWhenProvided: true);
                items.Add(Matched(ImportElementTypes.Field, field.LogicalRef, field.Name, table.Name, table.LogicalRef, existing.PublicId, existing.Id, matchedBy, changes, canOverwrite: true));
            }

            foreach (var existing in existingFields.Where(f => !usedFieldIds.Contains(f.Id) && !f.IsSystem && !f.IsHidden && !RelationshipFieldTypes.Contains(f.TypeCode)))
                onlyInApp.Add(new ImportOnlyInAppItem { ElementType = ImportElementTypes.Field, Name = existing.Name, TableName = table.Name });

            var reports = table.Reports ?? [];
            if (reports.Count > 0)
            {
                var existingReports = (await _reportRepo.ListByTableAsync(existingTable.PublicId, ct)).ToList();
                var usedReportIds = new HashSet<long>();
                foreach (var report in reports)
                {
                    var (existing, matchedBy) = Match(ImportElementTypes.Report, report.LogicalRef, report.Name,
                        existingReports, r => r.Id, r => r.PublicId, r => r.Name, elementIdByRef, overrides, usedReportIds);
                    items.Add(existing is null
                        ? NewItem(ImportElementTypes.Report, report.LogicalRef, report.Name, table.Name, table.LogicalRef)
                        : Matched(ImportElementTypes.Report, report.LogicalRef, report.Name, table.Name, table.LogicalRef, existing.PublicId, existing.Id, matchedBy, [], canOverwrite: false));
                }
            }

            var tableForms = (document.Forms ?? []).Where(f => f.TableRef == table.LogicalRef).ToList();
            if (tableForms.Count > 0)
            {
                var existingForms = (await _formRepo.ListByTableAsync(existingTable.PublicId, ct)).ToList();
                var usedFormIds = new HashSet<long>();
                foreach (var form in tableForms)
                {
                    var (existing, matchedBy) = Match(ImportElementTypes.Form, form.LogicalRef, form.Name,
                        existingForms, f => f.Id, f => f.PublicId, f => f.Name, elementIdByRef, overrides, usedFormIds);
                    items.Add(existing is null
                        ? NewItem(ImportElementTypes.Form, form.LogicalRef, form.Name, table.Name, table.LogicalRef)
                        : Matched(ImportElementTypes.Form, form.LogicalRef, form.Name, table.Name, table.LogicalRef, existing.PublicId, existing.Id, matchedBy, [], canOverwrite: false));
                }
            }
        }

        // ----- Relationships (matched through the child table's Reference field) -----
        foreach (var rel in document.Relationships ?? [])
        {
            var tableName = tables.FirstOrDefault(t => t.LogicalRef == rel.ChildTableRef)?.Name;
            var label = rel.ReferenceFieldLabel ?? rel.ReferenceFieldName;
            AppField? existingRef = null;
            string? matchedBy = null;

            if (matchedTableByRef.TryGetValue(rel.ChildTableRef, out var childTable))
            {
                var references = (await _fieldRepo.ListByTableAsync(childTable.Id, ct))
                    .Where(f => string.Equals(f.TypeCode, "Reference", StringComparison.OrdinalIgnoreCase)).ToList();

                if (elementIdByRef.TryGetValue(Key(ImportElementTypes.Relationship, rel.LogicalRef), out var refFieldId))
                {
                    existingRef = references.FirstOrDefault(f => f.Id == refFieldId);
                    if (existingRef is not null) matchedBy = ImportMatchedBy.SourceRef;
                }
                if (existingRef is null)
                {
                    existingRef = references.FirstOrDefault(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase));
                    if (existingRef is not null) matchedBy = ImportMatchedBy.Name;
                }
            }

            items.Add(existingRef is null
                ? NewItem(ImportElementTypes.Relationship, rel.LogicalRef, label, tableName, rel.ChildTableRef)
                : Matched(ImportElementTypes.Relationship, rel.LogicalRef, label, tableName, rel.ChildTableRef, existingRef.PublicId, existingRef.Id, matchedBy, [], canOverwrite: false));
        }

        // ----- Roles -----
        var existingRoles = await _roleRepo.ListDetailsByAppIdAsync(appId, ct);
        var usedRoleIds = new HashSet<long>();
        foreach (var role in document.Roles ?? [])
        {
            var (existing, matchedBy) = Match(ImportElementTypes.Role, role.LogicalRef, role.Name,
                existingRoles, r => r.Id, r => r.PublicId, r => r.Name, elementIdByRef, overrides, usedRoleIds);
            items.Add(existing is null
                ? NewItem(ImportElementTypes.Role, role.LogicalRef, role.Name, null, "")
                : Matched(ImportElementTypes.Role, role.LogicalRef, role.Name, null, "", existing.PublicId, existing.Id, matchedBy, [], canOverwrite: false));
        }
        foreach (var existing in existingRoles.Where(r => !usedRoleIds.Contains(r.Id) && !r.IsSystem))
            onlyInApp.Add(new ImportOnlyInAppItem { ElementType = ImportElementTypes.Role, Name = existing.Name });

        // ----- Variables -----
        var existingVariables = await _variableRepo.ListAsync(appId, ct);
        var usedVariableIds = new HashSet<long>();
        foreach (var variable in document.Variables ?? [])
        {
            var (existing, matchedBy) = Match(ImportElementTypes.Variable, variable.LogicalRef, variable.Name,
                existingVariables, v => v.Id, v => v.PublicId, v => v.Name, elementIdByRef, overrides, usedVariableIds);
            if (existing is null)
            {
                items.Add(NewItem(ImportElementTypes.Variable, variable.LogicalRef, variable.Name, null, ""));
                continue;
            }
            var changes = new List<ImportPropertyChange>();
            Compare(changes, "Value", existing.Value, variable.Value, onlyWhenProvided: false);
            Compare(changes, "Description", existing.Description, variable.Description, onlyWhenProvided: true);
            items.Add(Matched(ImportElementTypes.Variable, variable.LogicalRef, variable.Name, null, "", existing.PublicId, existing.Id, matchedBy, changes, canOverwrite: true));
        }
        foreach (var existing in existingVariables.Where(v => !usedVariableIds.Contains(v.Id)))
            onlyInApp.Add(new ImportOnlyInAppItem { ElementType = ImportElementTypes.Variable, Name = existing.Name });

        // ----- Pages -----
        if ((document.Pages ?? []).Count > 0)
        {
            var existingPages = await _pageRepo.ListAllByAppAsync(appId, null, ct);
            var usedPageIds = new HashSet<long>();
            foreach (var page in document.Pages!)
            {
                var (existing, matchedBy) = Match(ImportElementTypes.Page, page.LogicalRef, page.Name,
                    existingPages, p => p.Id, p => p.PublicId, p => p.Name, elementIdByRef, overrides, usedPageIds);
                items.Add(existing is null
                    ? NewItem(ImportElementTypes.Page, page.LogicalRef, page.Name, null, "")
                    : Matched(ImportElementTypes.Page, page.LogicalRef, page.Name, null, "", existing.PublicId, existing.Id, matchedBy, [], canOverwrite: false));
            }
        }

        return new ImportDiffResult
        {
            TargetAppPublicId = appPublicId,
            TargetAppName = app.Name,
            Items = items,
            OnlyInApp = onlyInApp,
        };
    }

    private static string Key(string elementType, string logicalRef) => $"{elementType}|{logicalRef}";

    /// <summary>Mapping override, then recorded source ref, then name. Each existing element can be
    /// claimed by at most one file element.</summary>
    private static (T? Element, string? MatchedBy) Match<T>(
        string elementType, string logicalRef, string name, IReadOnlyList<T> existing,
        Func<T, long> id, Func<T, Guid> publicId, Func<T, string> nameOf,
        Dictionary<string, long> elementIdByRef, Dictionary<string, Guid> overrides, HashSet<long> used) where T : class
    {
        var key = Key(elementType, logicalRef);

        if (overrides.TryGetValue(key, out var targetPublicId))
        {
            var byMapping = existing.FirstOrDefault(e => publicId(e) == targetPublicId && !used.Contains(id(e)));
            if (byMapping is not null)
            {
                used.Add(id(byMapping));
                return (byMapping, ImportMatchedBy.Mapping);
            }
        }

        if (elementIdByRef.TryGetValue(key, out var recordedId))
        {
            var byRef = existing.FirstOrDefault(e => id(e) == recordedId && !used.Contains(id(e)));
            if (byRef is not null)
            {
                used.Add(id(byRef));
                return (byRef, ImportMatchedBy.SourceRef);
            }
        }

        var byName = existing.FirstOrDefault(e => string.Equals(nameOf(e), name, StringComparison.OrdinalIgnoreCase) && !used.Contains(id(e)));
        if (byName is not null)
        {
            used.Add(id(byName));
            return (byName, ImportMatchedBy.Name);
        }

        return (null, null);
    }

    private static void Compare(List<ImportPropertyChange> changes, string property, string? before, string? after, bool onlyWhenProvided)
    {
        if (onlyWhenProvided && string.IsNullOrWhiteSpace(after))
            return;
        if (string.Equals(Norm(before), Norm(after), StringComparison.Ordinal))
            return;
        changes.Add(new ImportPropertyChange { Property = property, Before = before, After = after });
    }

    private static string Norm(string? value) => (value ?? "").Trim();

    private static ImportDiffItem NewItem(string type, string logicalRef, string name, string? tableName, string tableRef) => new()
    {
        ElementType = type, SourceLogicalRef = logicalRef, Name = name, TableName = tableName, TableRef = tableRef,
        Status = ImportDiffStatus.New,
    };

    private static ImportDiffItem Matched(
        string type, string logicalRef, string name, string? tableName, string tableRef,
        Guid targetPublicId, long targetId, string? matchedBy, List<ImportPropertyChange> changes, bool canOverwrite) => new()
    {
        ElementType = type, SourceLogicalRef = logicalRef, Name = name, TableName = tableName, TableRef = tableRef,
        Status = changes.Count > 0 ? ImportDiffStatus.Changed : ImportDiffStatus.Identical,
        MatchedBy = matchedBy, TargetElementPublicId = targetPublicId, TargetElementId = targetId, CanOverwrite = canOverwrite && changes.Count > 0, Changes = changes,
    };
}
