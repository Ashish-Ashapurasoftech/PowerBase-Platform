using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Formatting;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Forms;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>Lists selectable parent records for a Reference field picker, labelled by the
/// parent table's display field (or its first non-system field). A form element can instead base its picker on a
/// Table report of the parent table or on one lookup field of the relationship (see <see cref="RecordPickerRules"/>).</summary>
public class GetParentOptionsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRelationalProjector _relationalProjector;
    private readonly IFormulaProjector _formulaProjector;
    private readonly IQueryContext _queryContext;
    private readonly IRolePermissionEnforcer? _enforcer;
    private readonly IFormRepository? _formRepo;
    private readonly IReportRepository? _reportRepo;
    private readonly IUserRepository? _userRepo;
    private readonly IAppUserRepository? _appUserRepo;

    public GetParentOptionsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relRepo,
        IRecordRepository recordRepo,
        IRelationalProjector relationalProjector,
        IFormulaProjector formulaProjector,
        IQueryContext queryContext,
        IRolePermissionEnforcer? enforcer = null,
        IFormRepository? formRepo = null,
        IReportRepository? reportRepo = null,
        IUserRepository? userRepo = null,
        IAppUserRepository? appUserRepo = null)
    {
        _queryContext = queryContext;
        _enforcer = enforcer;
        _formRepo = formRepo;
        _reportRepo = reportRepo;
        _userRepo = userRepo;
        _appUserRepo = appUserRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
        _recordRepo = recordRepo;
        _relationalProjector = relationalProjector;
        _formulaProjector = formulaProjector;
    }

    public async Task<GetParentOptionsResult> HandleAsync(
        Guid relationshipPublicId, string? search, int take,
        IReadOnlyDictionary<int, string?>? filterValues = null,
        string? selectedRecordId = null,
        Guid? elementId = null,
        CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var childFields = await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
        var access = _enforcer is null ? null : await _enforcer.GetTableAccessAsync(parent, parentFields, ct);

        // "Select using record picker": the form element's own choice (a Table report of the parent, or one lookup of this
        // relationship) replaces the default columns. Anything missing or no longer eligible falls back to the default picker.
        var choice = await LoadChoiceAsync(elementId, rel, childFields, ct);
        var plan = choice.Mode is null ? null : await BuildPickerPlanAsync(choice, rel, parent, parentFields, childFields, access, ct);

        // The columns are what the parent's record picker lists (or the form element's own choice, above), as in Quickbase:
        // verified in the YSL Dev Docs app, a table keyed on "Email Template ID" whose picker is Name / Subject / Related File
        // Type lists exactly those 3 columns, never the key. The parent's Set Key / relationship Display Key
        // (KeyFieldResolver.ResolveDisplayKey) is therefore not a column; it only rides along as ReferenceOption.KeyValue, so
        // an existing value (which the API reads back as the key's text) still finds its option. Quickbase's default picker is
        // the key field, so a table with a key and no picker field configured lists the key (see ResolveLabelFields).
        var displayKey = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields);
        IReadOnlyList<AppField> labelFields;
        AppField? primaryLabelField = null;
        if (plan is not null)
        {
            labelFields = plan.LabelFields;
            primaryLabelField = plan.PrimaryLabelField;
        }
        else
        {
            labelFields = ResolveLabelFields(parent, parentFields, displayKey);
        }

        var headers = labelFields.Select(f => f.Label ?? f.Name).ToList();

        // The reference column always stores the parent row Id, so the picker always submits it
        // (option.Id). SearchForReferenceAsync already returns the row Id as text; a Set Key / Display Key only
        // supplies ReferenceOption.KeyValue (and the one column when no picker field is configured).
        var effectiveTake = take == 0 ? 50 : take;

        // Dependent dropdown: narrow the parent records by the reference field's filter conditions
        // against the form's current values (see ReferenceSettings.FilterConditions).
        var referenceField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);
        var filters = await ReferenceFilterResolver.BuildAsync(
            FormulaTypeMap.ParseReferenceSettings(referenceField?.Settings), childFields, parentFields,
            filterValues ?? new Dictionary<int, string?>(), _tableRepo, _fieldRepo, ct, _queryContext.UserId, _relRepo, _recordRepo);

        // Offer only the parent records the user's role may view: the picker must not reveal records hidden by
        // the parent table's Custom Rules / Own records scope (or any record of a table the role cannot view).
        IReadOnlyCollection<long>? onlyRowIds = null;
        FilterGroup? roleFilter = null;
        long? restrictToCreatedBy = null;

        if (access is not null)
        {
            if (!access.CanView) return new GetParentOptionsResult(headers, []);

            if (!access.Unrestricted)
            {
                var formulaFids = parentFields
                    .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
                    .Select(f => (long)f.Fid!.Value)
                    .ToHashSet();
                var (physical, formula) = FormulaFilterSorter.SplitFilterTree(access.ViewFilter, formulaFids);

                // If role filter contains NO formula conditions, pass roleFilter directly to SQL!
                // Eliminates loading up to 50,000 entities and passing OPENJSON!
                if (formula is null)
                {
                    roleFilter = physical;
                    restrictToCreatedBy = access.RestrictToCreatedBy;
                }
                else
                {
                    // Fall back to ViewableRowIds for formula rules
                    onlyRowIds = await Records.ViewableRowIds.ResolveAsync(
                        access, parent, parentFields, _recordRepo, _relationalProjector, _formulaProjector, ct);
                    if (onlyRowIds is { Count: 0 }) return new GetParentOptionsResult(headers, []);
                }
            }
        }

        // A report-based picker's filter on Formula/Lookup/Summary fields arrives as row ids; they narrow the role's rows.
        // The role-only ids are kept for resolving an already-selected record, which may sit outside the report.
        var roleRowIds = onlyRowIds;
        if (plan?.FilterRowIds is { } reportRowIds)
        {
            onlyRowIds = onlyRowIds is null ? reportRowIds : onlyRowIds.Intersect(reportRowIds).ToList();
            if (onlyRowIds.Count == 0) return new GetParentOptionsResult(headers, []);
        }

        var hasComputed = labelFields.Any(IsComputedLabel) || (primaryLabelField is not null && IsComputedLabel(primaryLabelField));
        IReadOnlyList<ReferenceOption> options;

        if (!hasComputed)
        {
            options = await _recordRepo.SearchForReferenceAsync(
                parent, labelFields, search, effectiveTake, primaryLabelField, ct, filters, onlyRowIds,
                roleFilter: roleFilter, restrictToCreatedBy: restrictToCreatedBy, timeoutSeconds: 30, allParentFields: parentFields,
                extraFilter: plan?.Filter, orderBy: plan?.Sort, keyField: displayKey);
        }
        else
        {
            // A computed label (Formula/Lookup/Summary) has no SQL column, so it can't be searched in
            // SQL: fetch the (capped) page unfiltered, compute the labels, then filter in memory.
            var hasSearch = !string.IsNullOrWhiteSpace(search);
            options = await _recordRepo.SearchForReferenceAsync(
                parent, labelFields, null, hasSearch ? 200 : effectiveTake, primaryLabelField, ct, filters, onlyRowIds,
                roleFilter: roleFilter, restrictToCreatedBy: restrictToCreatedBy, timeoutSeconds: 30, allParentFields: parentFields,
                extraFilter: plan?.Filter, orderBy: plan?.Sort, keyField: displayKey);
            await FillComputedLabelsAsync(parent, parentFields, labelFields, primaryLabelField, options, ct);

            if (hasSearch)
            {
                options = options
                    .Where(o => new[] { o.Value1, o.Value2, o.Value3 }
                        .Any(v => v is not null && v.Contains(search!, StringComparison.OrdinalIgnoreCase)))
                    .Take(effectiveTake)
                    .ToList();
            }
        }

        // If a previously selected record is outside the current page, resolve it securely:
        if (!string.IsNullOrWhiteSpace(selectedRecordId) && !options.Any(o => string.Equals(o.Id, selectedRecordId, StringComparison.OrdinalIgnoreCase)))
        {
            var selectedOption = await ResolveSelectedRecordOptionAsync(
                parent, parentFields, labelFields, primaryLabelField, selectedRecordId, filters, roleRowIds, roleFilter, restrictToCreatedBy, displayKey, ct);
            if (selectedOption != null)
            {
                options = new List<ReferenceOption> { selectedOption }.Concat(options).ToList();
            }
        }

        return new GetParentOptionsResult(headers, options);
    }

    /// <summary>The columns, filter and sort a form element's record picker choice stands for.</summary>
    /// <param name="Filter">The report's filter on physical fields, AND-ed into the picker's SQL.</param>
    /// <param name="FilterRowIds">When the report's filter touches Formula/Lookup/Summary fields: the parent row ids that pass it.</param>
    private sealed record PickerPlan(
        IReadOnlyList<AppField> LabelFields, AppField PrimaryLabelField,
        FilterGroup? Filter = null, IReadOnlyCollection<long>? FilterRowIds = null, IReadOnlyList<SortSpec>? Sort = null);

    /// <summary>The element's saved choice, or the default when the element is unknown, belongs to another table, or is
    /// neither this relationship's Reference field nor its proxy (the picker never trusts the client's idea of the element).</summary>
    private async Task<RecordPickerRules.Choice> LoadChoiceAsync(
        Guid? elementId, Relationship rel, IReadOnlyList<AppField> childFields, CancellationToken ct)
    {
        if (elementId is not { } id || _formRepo is null) return RecordPickerRules.Choice.Default;

        var element = await _formRepo.GetRecordPickerElementAsync(id, ct);
        if (element is null || string.IsNullOrEmpty(element.Mode) || element.AppTableId != rel.ChildTableId || element.AppFieldFid is not { } fid)
            return RecordPickerRules.Choice.Default;

        var field = childFields.FirstOrDefault(f => f.Fid.HasValue && f.Fid.Value == fid);
        if (field is null || (field.Id != rel.ReferenceFieldId && field.Id != rel.ProxyFieldId))
            return RecordPickerRules.Choice.Default;

        return new RecordPickerRules.Choice(element.Mode, element.ReportPublicId, element.FieldId);
    }

    /// <summary>Null (= use the default picker) when the target is gone, no longer eligible, or shows nothing the viewer's role may see.</summary>
    private async Task<PickerPlan?> BuildPickerPlanAsync(
        RecordPickerRules.Choice choice, Relationship rel, AppTable parent,
        IReadOnlyList<AppField> parentFields, IReadOnlyList<AppField> childFields, TableAccessContext? access, CancellationToken ct)
    {
        // Columns the role may not see are not offered as picker columns (the default picker's own columns are unchanged).
        var visibleFids = access is null || access.Unrestricted
            ? null
            : access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();
        bool Visible(AppField f) => visibleFids is null || (f.Fid.HasValue && visibleFids.Contains(f.Fid.Value));

        if (choice.Mode == RecordPickerModes.Field)
        {
            var lookup = childFields.FirstOrDefault(f => f.Fid.HasValue && f.Fid.Value == choice.FieldId && RecordPickerRules.IsEligibleLookup(f, rel));
            var settings = lookup is null ? null : FormulaTypeMap.ParseLookupSettings(lookup.Settings);
            // A lookup that pulls out one part of an Address has no parent column of its own to list: use the default picker.
            if (settings is null || settings.SourceSubField is not null) return null;

            var source = parentFields.FirstOrDefault(f => f.Fid.HasValue && f.Fid.Value == settings.SourceFid && CanServeAsLabel(f) && Visible(f));
            return source is null ? null : new PickerPlan([source], source);
        }

        if (choice.Mode != RecordPickerModes.Report || _reportRepo is null || choice.ReportPublicId is not { } reportId)
            return null;

        Report report;
        try { report = await _reportRepo.GetByPublicIdAsync(reportId, ct); }
        catch (NotFoundException) { return null; }
        if (!RecordPickerRules.IsEligibleReport(report, parent.Id)) return null;

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();

        // Columns: the report's own, in order (first three that can serve as a label).
        var allFids = visibleFids ?? parentFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();
        var columns = await ReportColumnResolver.ResolveAsync(definition, parentFields, allFids, _reportRepo, parent.PublicId, ct);
        var labelFields = columns.Where(CanServeAsLabel).Take(3).ToList();
        if (labelFields.Count == 0) return null;

        // Filter: the saved tree as the report runner resolves it (current user, relative dates). "Ask the user" conditions
        // stay unanswered, which makes them no-ops: a dropdown has nobody to ask.
        var tree = definition.FilterTree;
        if (tree is null && definition.Filters.Count > 0)
        {
            tree = new FilterGroup
            {
                Logic = "and",
                Nodes = definition.Filters.Select(f => new FilterNode
                {
                    Condition = new FilterCondition { FieldId = f.FieldId, Operator = f.Operator, Value = f.Value }
                }).ToList()
            };
        }
        tree = RunReportQueryHandler.ResolveAskAnswers(tree, null, []);
        if (_userRepo is not null)
        {
            var fieldLookup = parentFields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
            tree = await RunReportQueryHandler.ResolveUserFieldValuesAsync(
                tree, fieldLookup, _queryContext.UserId, new Dictionary<Guid, long>(), _userRepo, ct,
                _appUserRepo, parent.AppId, _queryContext.UserEmail, _queryContext.UserName);
        }
        tree = RunReportQueryHandler.ResolveDateValueModeConditions(tree);

        // Conditions on physical fields run in SQL; conditions on Formula/Lookup/Summary fields have no column, so the rows
        // passing the whole tree are worked out in memory (the same path a role's Custom Rules take).
        FilterGroup? filter = null;
        IReadOnlyCollection<long>? filterRowIds = null;
        if (tree is { Nodes.Count: > 0 })
        {
            var formulaFids = parentFields
                .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
                .Select(f => (long)f.Fid!.Value)
                .ToHashSet();
            var (physical, formula) = FormulaFilterSorter.SplitFilterTree(tree, formulaFids);
            if (formula is null)
            {
                filter = physical;
            }
            else
            {
                var byTree = new TableAccessContext
                {
                    Unrestricted = false,
                    ViewScope = RecordScopes.AllRecords,
                    ViewFilter = tree,
                    VisibleFields = parentFields,
                };
                filterRowIds = await Records.ViewableRowIds.ResolveAsync(
                    byTree, parent, parentFields, _recordRepo, _relationalProjector, _formulaProjector, ct);
            }
        }

        // Sort: the report's own (sort levels on computed fields have no column; the repository skips them).
        IReadOnlyList<SortSpec> sort = report.ReportType == "Table" && definition.TableSortGroup.Count > 0
            ? definition.TableSortGroup.Select(l => new SortSpec { FieldId = l.FieldId, Desc = l.Desc }).ToList()
            : definition.SortFields.Count > 0
                ? definition.SortFields
                : definition.SortFieldId.HasValue
                    ? [new SortSpec { FieldId = definition.SortFieldId.Value, Desc = definition.SortDesc }]
                    : [];

        return new PickerPlan(labelFields, labelFields[0], filter, filterRowIds, sort);
    }

    private async Task<ReferenceOption?> ResolveSelectedRecordOptionAsync(
        AppTable parent, IReadOnlyList<AppField> parentFields, IReadOnlyList<AppField> labelFields,
        AppField? primaryLabelField, string selectedRecordId,
        IReadOnlyList<ReferenceFilterClause>? filters,
        IReadOnlyCollection<long>? onlyRowIds,
        FilterGroup? roleFilter,
        long? restrictToCreatedBy,
        AppField? keyField,
        CancellationToken ct)
    {
        if (onlyRowIds != null && long.TryParse(selectedRecordId, out var numId) && !onlyRowIds.Contains(numId))
            return null;

        var selectedClause = new List<ReferenceFilterClause>();
        if (filters != null) selectedClause.AddRange(filters);

        var idField = parentFields.FirstOrDefault(f => f.Fid == 3 || (f.IsSystem && f.TypeCode == "RecordId"));
        if (idField != null)
        {
            selectedClause.Add(new ReferenceFilterClause(idField, null, null, null, selectedRecordId));
        }

        var results = await _recordRepo.SearchForReferenceAsync(
            parent, labelFields, null, 1, primaryLabelField, ct, selectedClause, onlyRowIds,
            roleFilter: roleFilter, restrictToCreatedBy: restrictToCreatedBy, timeoutSeconds: 30, allParentFields: parentFields,
            keyField: keyField);

        var opt = results.FirstOrDefault(o => string.Equals(o.Id, selectedRecordId, StringComparison.OrdinalIgnoreCase));
        if (opt != null && (labelFields.Any(IsComputedLabel) || (primaryLabelField != null && IsComputedLabel(primaryLabelField))))
        {
            await FillComputedLabelsAsync(parent, parentFields, labelFields, primaryLabelField, new[] { opt }, ct);
        }
        return opt;
    }

    /// <summary>
    /// Computes the values of computed label fields (Formula/Lookup/Summary) for the fetched
    /// options — the same relational → formula projection every record read uses — and writes
    /// them into Value1..3 / Label. The SQL query only selects NULL placeholders for these.
    /// </summary>
    private async Task FillComputedLabelsAsync(
        AppTable parent, IReadOnlyList<AppField> parentFields, IReadOnlyList<AppField> labelFields,
        AppField? primaryLabelField, IReadOnlyList<ReferenceOption> options, CancellationToken ct)
    {
        if (options.Count == 0) return;

        var ids = options.Select(o => long.TryParse(o.Id, out var id) ? id : (long?)null)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var rowsById = await _recordRepo.GetRowsByIdsAsync(parent, parentFields, ids, ct);

        var pairs = options
            .Select(o => (Option: o, Row: long.TryParse(o.Id, out var id) && rowsById.TryGetValue(id, out var row) ? row : null))
            .Where(x => x.Row is not null)
            .ToList();
        if (pairs.Count == 0) return;

        var rows = pairs.Select(x => x.Row!).ToList();
        var relational = await _relationalProjector.ProjectAsync(parent, parentFields, rows, ct);
        var computed = _formulaProjector.Project(parentFields, rows, relational, parent);
        var appFormatting = new AppFormattingSettings();

        string? Compute(AppField field, IReadOnlyDictionary<long, object?> values)
        {
            var typeCode = field.TypeCode.StartsWith("Formula_", StringComparison.Ordinal)
                ? field.TypeCode["Formula_".Length..] switch { "Bool" => "Boolean", var t => t }
                : "Text";
            return values.TryGetValue(field.Fid!.Value, out var raw) && raw is not null
                ? DisplayValueFormatter.Format(raw, typeCode, field.Settings, appFormatting)
                : null;
        }

        for (var i = 0; i < pairs.Count; i++)
        {
            var (opt, _) = pairs[i];
            var values = computed[i];
            if (labelFields.Count > 0 && IsComputedLabel(labelFields[0])) opt.Value1 = Compute(labelFields[0], values);
            if (labelFields.Count > 1 && IsComputedLabel(labelFields[1])) opt.Value2 = Compute(labelFields[1], values);
            if (labelFields.Count > 2 && IsComputedLabel(labelFields[2])) opt.Value3 = Compute(labelFields[2], values);
            if (primaryLabelField is not null && IsComputedLabel(primaryLabelField)) opt.Label = Compute(primaryLabelField, values);
            else if (primaryLabelField is null && labelFields.Count > 0 && IsComputedLabel(labelFields[0])) opt.Label = opt.Value1;
        }
    }

    /// <summary>True for a label field whose value is computed at read time (Formula variants,
    /// Lookup, Summary) instead of being stored in a physical SQL column.</summary>
    private static bool IsComputedLabel(AppField f)
        => f.Fid.HasValue && Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode);

    /// <summary>
    /// Returns true when <paramref name="f"/> can serve as a picker label: it either has a
    /// physical storage column (projected directly in SQL), or it is a Formula / Lookup / Summary
    /// field computed on read (<see cref="FillComputedLabelsAsync"/>). ReportLink and ActionButton
    /// are computed too but have no meaningful text value, so they never serve as a label.
    /// </summary>
    private static bool CanServeAsLabel(AppField f)
        => f.Fid.HasValue && (!Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode)
            || f.TypeCode is "Lookup" or "Summary"
            || FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings));

    /// <param name="displayKey">The parent's Set Key / relationship Display Key. Quickbase's default picker is the key field, so
    /// when a key exists and no picker field is configured (nothing set, or only the Record ID# a new table is seeded with), the
    /// key is the one column. Never added next to configured picker fields.</param>
    private static IReadOnlyList<AppField> ResolveLabelFields(AppTable parent, IReadOnlyList<AppField> parentFields, AppField? displayKey = null)
    {
        var fields = new List<AppField>();

        if (parent.DefaultRecordPickerField1Id.HasValue)
        {
            // Skip fields that cannot serve as a label (ReportLink/ActionButton have no text value).
            // Formula/Lookup/Summary are allowed: their values are computed after the SQL query
            // (FillComputedLabelsAsync). The resolution falls through to the next tier otherwise.
            var f1 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField1Id.Value && CanServeAsLabel(f));
            if (f1 != null) fields.Add(f1);

            if (parent.DefaultRecordPickerField2Id.HasValue)
            {
                var f2 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField2Id.Value && CanServeAsLabel(f));
                if (f2 != null) fields.Add(f2);
            }

            if (parent.DefaultRecordPickerField3Id.HasValue)
            {
                var f3 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField3Id.Value && CanServeAsLabel(f));
                if (f3 != null) fields.Add(f3);
            }

            if (fields.Count > 0 && !(displayKey is not null && fields.All(f => f.IsSystem))) return fields;
        }

        if (parent.DisplayFieldId.HasValue)
        {
            // Same guard: skip if the configured display field cannot serve as a label.
            var display = parentFields.FirstOrDefault(f => f.Id == parent.DisplayFieldId.Value && CanServeAsLabel(f));
            if (display is not null) return [display];
        }

        if (displayKey is not null && CanServeAsLabel(displayKey)) return [displayKey];

        // Fall back to the first non-system, non-hidden field that can serve as a label.
        var fallback = parentFields.FirstOrDefault(f => !f.IsSystem && !f.IsHidden && CanServeAsLabel(f));
        if (fallback != null) return [fallback];

        // Ultimate fallback: Record ID# (Fid 3), so a table with zero business fields still shows
        // something in the picker instead of blank headers/options.
        var recordId = parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == 3)
            ?? parentFields.FirstOrDefault(f => f.IsSystem);
        return recordId != null ? [recordId] : [];
    }
}
