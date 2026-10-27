using System.Text.RegularExpressions;
using PowerBase.Application.Apps.Commands;
using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Qbl;

public sealed class QblConversionResult
{
    public string AppRef { get; init; } = string.Empty;
    public PblDocument Document { get; init; } = new();
    public List<PblIssue> Issues { get; init; } = [];
}

/// <summary>
/// Converts a parsed <see cref="QblDocument"/> into a <see cref="PblDocument"/>. Only QBL
/// constructs with a genuine PowerBase equivalent produce PBL elements — everything else
/// becomes a <see cref="PblIssue"/> (never silently dropped or approximated into the wrong
/// construct). See <c>QBL_COMPATIBILITY_MATRIX.md</c> for the full construct-by-construct
/// mapping this implements.
///
/// Runs in three ordered passes because relationships are cross-table and reports can reference
/// relationship-derived fields:
///   Pass A — Tables + scalar/formula Fields. Reference/Lookup/Summary fields are set aside
///            (via <see cref="RelationshipFieldCollector"/>) rather than converted directly.
///   Pass B — Relationships, built from Pass A's collected Reference/Lookup/Summary field info
///            plus each table's own <c>QB::Relationship::Child</c> nodes. Enriches Pass A's
///            per-table field-name-by-ref maps with the Reference/Lookup/Summary field names
///            that will exist once Pass B's relationships are actually created.
///   Pass C — Reports, using the now-enriched field-name maps so a report column referencing a
///            Lookup/Summary/Reference field resolves correctly instead of being flagged
///            unavailable.
///   Pass D — Roles, from the App's own <c>Roles</c> children plus every table's
///            <c>Properties.RolePermissions</c> map (that's where the real CRUD grants live —
///            confirmed real shape — the Role node itself only carries cosmetic toggles).
///            Independent of B/C; only needs Pass A's tables.
/// </summary>
public static class QblToPblConverter
{
    /// <summary>Converts a document expected to hold exactly one application (first one wins when the caller has
    /// already ruled out several). Multi-application files go through <see cref="ConvertAll"/>.</summary>
    public static QblConversionResult Convert(QblDocument qbl) => ConvertAll(qbl)[0];

    /// <summary>Converts every <c>QB::Application</c> in the file, in file order, each independently. A file with no
    /// application yields a single result carrying <c>QBL_MISSING_APPLICATION</c>.</summary>
    public static IReadOnlyList<QblConversionResult> ConvertAll(QblDocument qbl)
    {
        var apps = qbl.Resources.Where(kv => kv.Value.Type == "QB::Application").ToList();
        if (apps.Count == 0)
        {
            return [new QblConversionResult
            {
                Document = new PblDocument(),
                Issues =
                [
                    new PblIssue
                    {
                        Severity = PblIssueSeverity.Error,
                        Code = "QBL_MISSING_APPLICATION",
                        Message = "QBL document has no QB::Application resource.",
                    },
                ],
            }];
        }

        return apps.Select(a => ConvertApplication(a.Key, a.Value, qbl.ExternalResources)).ToList();
    }

    /// <summary>The applications in a document as (ref, name), in file order.</summary>
    public static IReadOnlyList<(string Ref, string Name)> ListApplications(QblDocument qbl) =>
        qbl.Resources.Where(kv => kv.Value.Type == "QB::Application")
            .Select(kv => (kv.Key, ResolveScalar(kv.Value, "Name") ?? kv.Key)).ToList();

    private static QblConversionResult ConvertApplication(string appRef, QblResourceNode appNode, IReadOnlyDictionary<string, QblResourceNode>? externalResources = null)
    {
        var issues = new List<PblIssue>();

        var pblApp = new PblApp
        {
            LogicalRef = appRef,
            Name = ResolveScalar(appNode, "Name") ?? "",
            Description = NullIfBlank(ResolveScalar(appNode, "Description")),
            Icon = QblIconMapper.MapApp(ResolveScalar(appNode, "AppIcon")),
            Color = NullIfBlank(ResolveScalar(appNode, "AppColor")),
            Formatting = QblAppTableMapper.Formatting(appNode, issues, appRef),
            Security = QblAppTableMapper.Security(appNode, issues, appRef),
        };
        var tableOrder = QblAppTableMapper.TableOrder(appNode);
        var roleNameByRef = appNode.ChildMap("Roles").ToDictionary(r => r.Key, r => ResolveScalar(r.Value, "Name") ?? r.Key, StringComparer.Ordinal);
        // Rules name the built-in Administrator role, which the file does not list among the app's own roles.
        roleNameByRef.TryAdd("$Role_Administrator", "Administrator");

        // Pass A.
        var tables = new List<PblTable>();
        var tableNodeByRef = new Dictionary<string, QblResourceNode>(StringComparer.Ordinal);
        var fieldNameByRefPerTable = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var relFieldsPerTable = new Dictionary<string, RelationshipFieldCollector>(StringComparer.Ordinal);
        // Tracks every field/lookup/summary Name already used per table (case-insensitive, same
        // as PblValidator's own DUPLICATE_FIELD_NAME check) so a same-table collision can be
        // caught and skipped here instead of hard-blocking the whole import downstream.
        // Confirmed real data: two distinct fields on the same table can share a display Name
        // (e.g. a Formula field and a Summary field both labeled "Latest Auth Units (OLP)") -
        // Quickbase itself never enforces this, so treat it the same as the
        // QBL_DUPLICATE_REPORT_NAME case below: first wins, later ones flagged and skipped.
        var seenFieldNamesByTableRef = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var addressSubComponentOwnerByTableRef = new Dictionary<string, Dictionary<string, (string CompositeFieldRef, string SubFieldKey)>>(StringComparer.Ordinal);

        foreach (var (tableRef, tableNode) in appNode.ChildMap("Tables"))
        {
            var fieldNameByRef = new Dictionary<string, string>(StringComparer.Ordinal);
            var relFields = new RelationshipFieldCollector();
            var seenFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFieldLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Address composite fields explicitly link to their own sub-component shadow fields
            // via Street1/Street2/City/State/PostalCode/Country !Ref properties (confirmed real
            // shape) - build a reverse map (shadow field ref -> composite field ref + canonical
            // PowerBase sub-field key) so a Lookup/Summary that targets one of these Quickbase-only
            // shadow fields can be redirected to the composite Address field PowerBase actually
            // creates, with a sub-field annotation, instead of permanently failing to resolve
            // (confirmed real pattern: a Lookup pulling "just the city" from a parent's Address).
            // Stored per-table (not consulted until Pass B) since a Lookup lives on the CHILD
            // table but targets a field on the PARENT table - the owning table isn't known until
            // every table has been walked.
            var addressSubComponentOwner = new Dictionary<string, (string CompositeFieldRef, string SubFieldKey)>(StringComparer.Ordinal);
            foreach (var (addrFieldRef, addrFieldNode) in tableNode.ChildMap("Fields"))
            {
                if (addrFieldNode.Type != "QB::Field::Address")
                    continue;
                foreach (var (qblPropName, subFieldKey) in QblFieldTypeMap.AddressSubComponentPropertyToKey)
                {
                    var subRef = addrFieldNode.PropertyRef(qblPropName)?["Field"];
                    if (subRef is not null)
                        addressSubComponentOwner[subRef] = (addrFieldRef, subFieldKey);
                }
            }
            addressSubComponentOwnerByTableRef[tableRef] = addressSubComponentOwner;

            var table = new PblTable
            {
                LogicalRef = tableRef,
                Name = ResolveScalar(tableNode, "Name") ?? "",
                SingularLabel = NullIfBlank(ResolveScalar(tableNode, "RecordNameSingular")),
                PluralLabel = NullIfBlank(ResolveScalar(tableNode, "RecordNamePlural")),
                Icon = QblIconMapper.MapTable(ResolveScalar(tableNode, "TableIconName")),
                Description = NullIfBlank(ResolveScalar(tableNode, "Description")),
                AliasHint = QblAppTableMapper.AliasHint(tableNode),
                IsShowInBar = QblAppTableMapper.IsShowInBar(tableNode),
                AutoSaveOnLeave = QblAppTableMapper.AutoSaveOnLeave(tableNode),
                DisplayOrder = tableOrder is not null && tableOrder.IndexOf(tableRef) is var orderIndex && orderIndex >= 0 ? orderIndex : int.MaxValue,
            };

            foreach (var (fieldRef, fieldNode) in tableNode.ChildMap("Fields"))
            {
                CollectRelationshipField(fieldRef, fieldNode, relFields, issues);

                // System fields (Record ID#, Date Created, etc.) never become a standalone
                // PblField - PowerBase's own seeder already creates one per table - but real
                // Quickbase data can have a relationship Summary/Lookup that targets one directly
                // (confirmed: Function: Maximum over QB::Field::RecordID, a common "latest
                // related record" pattern). Register the PowerBase-seeded field's Name here so
                // that later resolution succeeds instead of leaving the Summary/Lookup - and
                // everything chained off it - permanently unresolved.
                if (QblFieldTypeMap.SystemFieldNames.TryGetValue(fieldNode.Type, out var systemFieldName))
                {
                    fieldNameByRef[fieldRef] = systemFieldName;
                    continue;
                }

                var field = ConvertField(tableRef, fieldRef, fieldNode, issues);
                if (field is null)
                    continue;

                // Two fields of one table can share a name in a real export; PowerBase needs unique names, so the later one is kept
                // under a numbered name (and label) instead of being dropped with its data, form elements and report columns.
                var uniqueFieldName = NumberedName(seenFieldNames, field.Name);
                if (!string.Equals(uniqueFieldName, field.Name, StringComparison.Ordinal))
                {
                    issues.Add(Warning("QBL_DUPLICATE_FIELD_NAME", $"Field '{field.Name}' is already used on table '{table.Name}'; imported as '{uniqueFieldName}'.", fieldRef));
                    field.Label = (field.Label ?? field.Name) + uniqueFieldName[field.Name.Length..];
                    field.Name = uniqueFieldName;
                }

                // PowerBase also needs labels unique per table. Two fields can share a label in a
                // real export; keep both, numbering the later one, and say so.
                var wantedLabel = (field.Label ?? field.Name).Trim();
                var uniqueLabel = wantedLabel;
                for (var n = 2; !seenFieldLabels.Add(uniqueLabel); n++)
                    uniqueLabel = $"{wantedLabel} ({n})";
                if (!string.Equals(uniqueLabel, wantedLabel, StringComparison.Ordinal))
                {
                    field.Label = uniqueLabel;
                    issues.Add(Warning("QBL_DUPLICATE_FIELD_LABEL", $"Field label '{wantedLabel}' is already used on table '{table.Name}'; imported as '{uniqueLabel}'.", fieldRef));
                }

                table.Fields.Add(field);
                fieldNameByRef[fieldRef] = field.Name;
            }

            tables.Add(table);
            tableNodeByRef[tableRef] = tableNode;
            fieldNameByRefPerTable[tableRef] = fieldNameByRef;
            relFieldsPerTable[tableRef] = relFields;
            seenFieldNamesByTableRef[tableRef] = seenFieldNames;
        }

        // Runs once Pass A has seen every table, since a formula on the first table can name the
        // last one.
        RewriteQuickbaseTableIdRefs(tables, issues);

        // Pass B.
        var relationships = new List<PblRelationship>();
        foreach (var (childTableRef, tableNode) in tableNodeByRef)
        {
            foreach (var (relRef, relNode) in tableNode.ChildMap("Relationships"))
            {
                if (relNode.Type is "QB::Relationship::CrossAppChild" or "QB::Relationship::CrossAppParent")
                {
                    issues.Add(Warning("QBL_UNSUPPORTED_CROSS_APP_RELATIONSHIP", $"Cross-app relationship '{relRef}' is out of scope (Master/Child App mode not yet supported).", relRef));
                    continue;
                }

                if (relNode.Type != "QB::Relationship::Child")
                    continue; // Parent-side anchors carry no data of their own.

                var skeleton = BuildRelationshipSkeleton(relRef, relNode, childTableRef, relFieldsPerTable, fieldNameByRefPerTable, issues);
                if (skeleton is not null)
                {
                    EnsureUniqueReferenceName(skeleton, relRef, relFieldsPerTable[childTableRef].References[relRef].FieldRef, fieldNameByRefPerTable[childTableRef], seenFieldNamesByTableRef[childTableRef], tables, issues);
                    relationships.Add(skeleton);
                }
            }
        }

        // Resolve Lookups/Summaries as a fixed point rather than in one pass: a chained lookup
        // (a real Quickbase pattern — confirmed present in real exports, e.g. a Lookup whose
        // TargetField is itself another table's Lookup field) depends on that other
        // relationship having already enriched the field-name maps, and relationship/table
        // iteration order isn't guaranteed to respect that dependency. Loop until a full pass
        // makes no further progress, then flag whatever's still unresolved.
        var tableNameByRef = tables.ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);
        ResolveRelationshipLookupsAndSummaries(relationships, relFieldsPerTable, fieldNameByRefPerTable, seenFieldNamesByTableRef, tableNameByRef, addressSubComponentOwnerByTableRef, issues);

        // Dependent dropdowns, now that every field of both tables has a name.
        foreach (var rel in relationships)
        {
            if (relFieldsPerTable.TryGetValue(rel.ChildTableRef, out var ownFields) && ownFields.References.TryGetValue(rel.LogicalRef, out var ownReference))
                rel.ReferenceFieldDescription = ownReference.Description;
            if (!relFieldsPerTable.TryGetValue(rel.ChildTableRef, out var childFields)
                || !childFields.References.TryGetValue(rel.LogicalRef, out var referenceInfo)
                || referenceInfo.FilterFieldRef is null || referenceInfo.MatchFieldRef is null)
                continue;
            if (fieldNameByRefPerTable[rel.ChildTableRef].TryGetValue(referenceInfo.FilterFieldRef, out var formFieldName)
                && fieldNameByRefPerTable[rel.ParentTableRef].TryGetValue(referenceInfo.MatchFieldRef, out var parentFieldName))
            {
                rel.ReferenceFilterFormFieldName = formFieldName;
                rel.ReferenceFilterParentFieldName = parentFieldName;
            }
            else
                issues.Add(Warning("QBL_REFERENCE_FILTER_FIELD_UNAVAILABLE", $"The choices of reference '{rel.ReferenceFieldName}' depend on a field that was not converted; the dependent dropdown was not set up.", rel.LogicalRef));
        }

        // Relationship/Lookup/Summary refs are QBL keys scoped to their owning table's own
        // Fields/Relationships map, not globally unique — Quickbase auto-names them from the
        // label (e.g. every table's "Name" field can independently end up as local key
        // "$Field_Name"), so the same local key legitimately recurs across different tables in
        // real exports (confirmed: a real 55K-line export produced duplicate LogicalRefs like
        // "$Field_Name" and "$Field_Email" across dozens of unrelated tables). Qualify by table
        // now, after all raw-ref-keyed resolution above is done — nothing above or below this
        // point resolves a relationship/lookup/summary by its own LogicalRef, only by table ref
        // + field name, so requalifying here is safe.
        foreach (var rel in relationships)
        {
            foreach (var lookup in rel.Lookups)
                lookup.LogicalRef = Qualify(rel.ChildTableRef, lookup.LogicalRef);
            foreach (var summary in rel.Summaries)
                summary.LogicalRef = Qualify(rel.ParentTableRef, summary.LogicalRef);
            rel.LogicalRef = Qualify(rel.ChildTableRef, rel.LogicalRef);
        }

        // Source field Id → field name, per table. Every QBL field node carries a numeric Id, and the things that
        // point at fields by number (record filters, key/picker/title settings, formulas) need it resolved.
        var fieldNameBySourceId = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        foreach (var (tableRef, tableNode) in tableNodeByRef)
        {
            var byId = new Dictionary<int, string>();
            var namesByRef = fieldNameByRefPerTable[tableRef];
            foreach (var (fieldRef, fieldNode) in tableNode.ChildMap("Fields"))
                if (fieldNode.SourceId is { } sourceId && namesByRef.TryGetValue(fieldRef, out var resolvedName))
                    byId[sourceId] = resolvedName;
            fieldNameBySourceId[tableRef] = byId;
        }

        // A bracketed number can also be an app variable that happens to be named with digits (Quickbase files have variables "1202", "1226"…).
        // The formula engine binds a variable before a field, so such a token must stay as written and is not a field id at all.
        var variableNames = ConvertVariables(appNode, []).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        RewriteNumericFieldRefs(tables, fieldNameBySourceId, variableNames, issues);

        foreach (var table in tables)
            ApplyTableFieldSettings(table, tableNodeByRef[table.LogicalRef], fieldNameByRefPerTable[table.LogicalRef], issues);

        // Summary "only where…" queries refer to the CHILD table's fields. A query that cannot be translated would make the
        // summary count/sum every child record — a wrong number — so that summary is left out and reported instead.
        foreach (var rel in relationships)
        {
            var childFields = fieldNameBySourceId.GetValueOrDefault(rel.ChildTableRef) ?? [];
            foreach (var summary in rel.Summaries.ToList().Where(sm => !string.IsNullOrWhiteSpace(sm.QueryCriteria)))
            {
                var result = QblCriteriaTranslator.Translate(summary.QueryCriteria, id => childFields.GetValueOrDefault(id), allowRelativeDates: true);
                if (result.Succeeded)
                {
                    summary.Filter = result.Filter;
                    continue;
                }
                summary.SkipReason = $"its filter query cannot be translated ({result.Problem}); creating it would count every related record";
                issues.Add(Warning("QBL_SUMMARY_QUERY_UNTRANSLATABLE", $"Summary '{summary.Name}': {summary.SkipReason}; it was not created.", summary.LogicalRef));
            }
        }

        // Create tables in the source app's own order so PowerBase's table order matches.
        tables = tables.Select((t, i) => (Table: t, Index: i)).OrderBy(x => x.Table.DisplayOrder).ThenBy(x => x.Index).Select(x => x.Table).ToList();

        // Pass C.
        var forms = new List<PblForm>();
        var formRoleDefaults = new List<PblFormRoleDefault>();
        var connectedTables = new List<PblConnectedTable>();
        foreach (var table in tables)
        {
            var tableNode = tableNodeByRef[table.LogicalRef];
            var fieldNameByRef = fieldNameByRefPerTable[table.LogicalRef];

            // Confirmed real data: Quickbase can carry multiple auto-generated "Embedded for X"
            // reports with the IDENTICAL name on the same table (one per relationship, with no
            // dedup on Quickbase's side) - PowerBase requires report names unique per table, so
            // reusing PBL's ordinary DUPLICATE_REPORT_NAME error here would hard-block the whole
            // import over a report Quickbase itself doesn't treat as load-bearing. Flag and skip
            // the repeat instead, same as any other unsupported-construct gap.
            var seenReportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Pie / stacked-bar / gauge charts sit under the table's own "Charts" key, beside "Reports".
            foreach (var (reportRef, reportNode) in tableNode.ChildMap("Reports").Concat(tableNode.ChildMap("Charts")))
            {
                var report = ConvertReport(table.LogicalRef, reportRef, reportNode, fieldNameByRef, fieldNameBySourceId.GetValueOrDefault(table.LogicalRef) ?? [], issues);
                if (report is null)
                    continue;

                // Report names are unique per table in PowerBase: a repeat (Quickbase has many auto-named "Embedded for X") is kept, numbered.
                var uniqueReportName = NumberedName(seenReportNames, report.Name);
                if (!string.Equals(uniqueReportName, report.Name, StringComparison.Ordinal))
                {
                    issues.Add(Warning("QBL_DUPLICATE_REPORT_NAME", $"Report '{report.Name}' is already used on table '{table.Name}'; imported as '{uniqueReportName}'.", reportRef));
                    report.Name = uniqueReportName;
                }

                table.Reports.Add(report);
            }

            // Report-link fields: Quickbase's "File records" lists the Files of a Folder through Files' reference field.
            // A report link is not converted as a field (PowerBase makes its own per relationship), so the form sees it under a marked name
            // and the element is then tied to the child table instead of a field name.
            const string ReportLinkMark = "\u0001ReportLink:";
            var reportLinks = new Dictionary<string, (string ChildTableRef, string ReferenceFieldName)>(StringComparer.Ordinal);
            var formFieldNameByRef = fieldNameByRef;
            foreach (var (linkRef, linkNode) in tableNode.ChildMap("Fields").Where(x => x.Value.Type == "QB::Field::ReportLink"))
            {
                var linkTarget = NestedRef(linkNode, "FieldRelationship", "Target");
                if (linkTarget?["Table"] is { } linkTable && linkTarget["Field"] is { } linkField
                    && fieldNameByRefPerTable.TryGetValue(linkTable, out var linkTableFields) && linkTableFields.TryGetValue(linkField, out var referenceName))
                {
                    if (ReferenceEquals(formFieldNameByRef, fieldNameByRef)) formFieldNameByRef = new Dictionary<string, string>(fieldNameByRef, StringComparer.Ordinal);
                    var marked = ReportLinkMark + linkRef;
                    formFieldNameByRef[linkRef] = marked;
                    reportLinks[marked] = (linkTable, referenceName);
                }
            }

            foreach (var (formRef, formV2Node) in GetFormV2Nodes(tableNode))
            {
                var convertedForm = ConvertForm(formRef, formV2Node, table.LogicalRef, formFieldNameByRef, roleNameByRef, FieldTypesByName(table, tables, relationships), FieldHelpByName(table), issues);
                if (reportLinks.Count > 0)
                    foreach (var element in convertedForm.Sections.SelectMany(s => s.Blocks).SelectMany(b => b.Elements)
                                 .Where(e => e.ElementType == "Field" && e.FieldName is not null && reportLinks.ContainsKey(e.FieldName)))
                    {
                        var (childTableRef, referenceFieldName) = reportLinks[element.FieldName!];
                        element.FieldName = null;
                        element.ReportLinkChildTableRef = childTableRef;
                        element.ReportLinkReferenceFieldName = referenceFieldName;
                    }
                forms.Add(convertedForm);
            }

            formRoleDefaults.AddRange(ConvertFormRoleDefaults(tableNode, table.LogicalRef));
            if (ConvertConnectedTable(tableNode, table, externalResources, fieldNameByRef, issues) is { } connected)
                connectedTables.Add(connected);
        }

        RewriteRuleFormulas(forms, fieldNameBySourceId, variableNames, issues);

        // Pass D.
        var roles = new List<PblRole>();
        foreach (var (roleRef, roleNode) in appNode.ChildMap("Roles"))
        {
            roles.Add(new PblRole
            {
                LogicalRef = roleRef,
                Name = ResolveScalar(roleNode, "Name") ?? roleRef,
                Description = NullIfBlank(ResolveScalar(roleNode, "Description")),
                IsDefault = roleNode.PropertyBool("Default"),
                ManageUsers = roleNode.PropertyBool("ManageUsers"),
                EditApp = roleNode.PropertyBool("EditApp"),
                DisableAccess = roleNode.PropertyBool("DisableAccess"),
                RestrictAccessToNewTables = roleNode.PropertyBool("RestrictAccessToNewTablesForThisRole"),
            });
        }

        var tablePermissionsByRoleRef = new Dictionary<string, List<PblTablePermission>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var tableNode = tableNodeByRef[table.LogicalRef];
            if (!tableNode.Properties.TryGetValue("RolePermissions", out var rpRaw) || rpRaw is not Dictionary<string, object?> rolePermsMap)
                continue;

            foreach (var (roleRefKey, permsRaw) in rolePermsMap)
            {
                if (permsRaw is not Dictionary<string, object?> perms)
                    continue;

                var permission = ConvertTablePermission(table.LogicalRef, table.Name, roleRefKey, perms, fieldNameBySourceId.GetValueOrDefault(table.LogicalRef) ?? [], issues);
                if (!tablePermissionsByRoleRef.TryGetValue(roleRefKey, out var list))
                    tablePermissionsByRoleRef[roleRefKey] = list = [];
                list.Add(permission);
            }
        }

        foreach (var role in roles)
        {
            if (tablePermissionsByRoleRef.TryGetValue(role.LogicalRef, out var perms))
                role.TablePermissions = perms;
        }

        ApplyFieldPermissions(tables, tableNodeByRef, fieldNameByRefPerTable, roles, issues);

        // "Disable access" means the role may do nothing: every table permission is cut to none.
        foreach (var role in roles.Where(r => r.DisableAccess))
        {
            foreach (var permission in role.TablePermissions)
            {
                permission.ViewScope = "None";
                permission.ModifyScope = "None";
                permission.CanAdd = false;
                permission.CanDelete = false;
                permission.CanSaveSharedReports = false;
                permission.CanEditFieldProperties = false;
                permission.RecordFilter = null;
            }
        }

        var variables = ConvertVariables(appNode, issues);
        var pages = ConvertCodePages(appNode, issues);
        var dashboards = ConvertDashboards(appNode, issues);

        var document = new PblDocument { App = pblApp, Tables = tables, Relationships = relationships, Forms = forms, Roles = roles, Variables = variables, Pages = pages, FormRoleDefaults = formRoleDefaults, ConnectedTables = connectedTables, Dashboards = dashboards };
        return new QblConversionResult { AppRef = appRef, Document = document, Issues = issues };
    }

    /// <summary>Table settings that name fields — key field, record title, record picker, custom data rule. Resolved once every
    /// field name is known. A reference that does not resolve is reported and left at the PowerBase default.</summary>
    private static void ApplyTableFieldSettings(PblTable table, QblResourceNode tableNode, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues)
    {
        string? NameOf(QblRef? r) => r?["Field"] is { } fieldRef && fieldNameByRef.TryGetValue(fieldRef, out var n) ? n : null;

        // Record ID# is PowerBase's built-in key; only a different key field needs recording.
        var keyName = NameOf(tableNode.PropertyRef("KeyField"));
        if (keyName is not null && !string.Equals(keyName, "Record ID#", StringComparison.OrdinalIgnoreCase))
            table.KeyFieldName = keyName;
        else if (keyName is null && tableNode.PropertyRef("KeyField") is not null)
            issues.Add(Warning("QBL_TABLE_KEY_FIELD_UNRESOLVED", $"Table '{table.Name}': key field not found; Record ID# is used.", table.LogicalRef));

        if (tableNode.PropertyBool("UseCustomRecordTitleField"))
        {
            var titleName = NameOf(tableNode.PropertyRef("RecordTitleField"));
            if (titleName is not null) table.RecordTitleFieldName = titleName;
            else issues.Add(Warning("QBL_TABLE_TITLE_FIELD_UNRESOLVED", $"Table '{table.Name}': record title field not found.", table.LogicalRef));
        }

        if (tableNode.Properties.TryGetValue("DefaultRecordPicker", out var picker) && picker is IEnumerable<object?> pickerList && picker is not string)
        {
            foreach (var item in pickerList)
            {
                var pickerName = NameOf(item as QblRef);
                if (pickerName is not null && table.RecordPickerFieldNames.Count < 3 && !table.RecordPickerFieldNames.Contains(pickerName))
                    table.RecordPickerFieldNames.Add(pickerName);
            }
        }

        if (tableNode.Properties.TryGetValue("CustomDataRules", out var rulesRaw) && rulesRaw is IReadOnlyDictionary<string, object?> rules)
        {
            var formula = rules.TryGetValue("Formula", out var f) ? f?.ToString() : null;
            var on = rules.TryGetValue("TurnedOn", out var t) && (t is true || string.Equals(t?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(formula))
            {
                table.CustomDataRule = formula;
                table.CustomDataRuleEnabled = on;
            }
        }
    }

    /// <summary>QBL <c>QB::Application::Variable</c> nodes (under the app's <c>Variables</c> map)
    /// map 1:1 to PowerBase app variables. A name PowerBase can't accept (bracket characters, or an
    /// id-looking name like <c>var_5</c>) or a value over the column limit is flagged, not altered.</summary>
    private static List<PblVariable> ConvertVariables(QblResourceNode appNode, List<PblIssue> issues)
    {
        var result = new List<PblVariable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (varRef, varNode) in appNode.ChildMap("Variables"))
        {
            if (varNode.Type != "QB::Application::Variable")
                continue;

            var name = ResolveScalar(varNode, "Name")?.Trim();
            var value = ResolveScalar(varNode, "Value") ?? "";

            if (string.IsNullOrEmpty(name))
            {
                issues.Add(Warning("QBL_VARIABLE_NO_NAME", $"Variable '{varRef}' has no name; skipped.", varRef));
                continue;
            }
            if (name.Length > MAX_VARIABLE_NAME_LENGTH)
            {
                issues.Add(Warning("QBL_VARIABLE_NAME_TOO_LONG", $"Variable '{name}' name exceeds {MAX_VARIABLE_NAME_LENGTH} characters; skipped.", varRef));
                continue;
            }
            if (!AppVariableNameRules.HasNoBracketCharacters(name) || AppVariableNameRules.IsReserved(name))
            {
                issues.Add(Warning("QBL_VARIABLE_INVALID_NAME", $"Variable '{name}' has a name PowerBase cannot accept (brackets or a reserved id-style name); skipped.", varRef));
                continue;
            }
            if (value.Length > MAX_VARIABLE_VALUE_LENGTH)
            {
                issues.Add(Warning("QBL_VARIABLE_VALUE_TOO_LONG", $"Variable '{name}' value exceeds {MAX_VARIABLE_VALUE_LENGTH} characters; skipped.", varRef));
                continue;
            }
            if (!seen.Add(name))
            {
                issues.Add(Warning("QBL_DUPLICATE_VARIABLE_NAME", $"Variable '{name}' is already defined; only the first is imported.", varRef));
                continue;
            }

            result.Add(new PblVariable { LogicalRef = varRef, Name = name, Value = value, Description = NullIfBlank(ResolveScalar(varNode, "Description")) });
        }

        return result;
    }

    /// <summary>QBL <c>QB::CodePage</c> nodes (under the app's <c>Pages</c> map) carry one
    /// <c>RawCode</c> blob. Any other page type (homepages, rich-text pages) has no PowerBase
    /// equivalent and is flagged.</summary>
    private static List<PblPage> ConvertCodePages(QblResourceNode appNode, List<PblIssue> issues)
    {
        var result = new List<PblPage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pageRef, pageNode) in PageNodes(appNode))
        {
            if (pageNode.Type == "QB::Dashboard")
                continue; // converted by ConvertDashboards

            if (pageNode.Type != "QB::CodePage")
            {
                issues.Add(Warning("QBL_UNSUPPORTED_PAGE_TYPE", $"Page '{pageRef}' of type '{pageNode.Type}' has no PowerBase equivalent; skipped.", pageRef));
                continue;
            }

            var name = ResolveScalar(pageNode, "Name")?.Trim();
            if (string.IsNullOrEmpty(name))
                name = pageRef;
            if (name.Length > MAX_PAGE_NAME_LENGTH)
                name = name[..MAX_PAGE_NAME_LENGTH];

            if (!seen.Add(name))
            {
                issues.Add(Warning("QBL_DUPLICATE_PAGE_NAME", $"Code page '{name}' is already defined; only the first is imported.", pageRef));
                continue;
            }

            result.Add(new PblPage { LogicalRef = pageRef, Name = name, Code = ResolveScalar(pageNode, "RawCode") ?? "" });
        }

        return result;
    }

    /// <summary>Quickbase dashboards → PowerBase dashboard pages. Report, summary and chart widgets point at reports by (table, report); buttons
    /// become one-button button bars. A search or KPI widget has no PowerBase widget and is reported.</summary>
    private static List<PblDashboard> ConvertDashboards(QblResourceNode appNode, List<PblIssue> issues)
    {
        var result = new List<PblDashboard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pageRef, pageNode) in PageNodes(appNode))
        {
            if (pageNode.Type != "QB::Dashboard") continue;

            var name = ResolveScalar(pageNode, "Name")?.Trim();
            if (string.IsNullOrEmpty(name)) name = pageRef;
            if (name.Length > MAX_PAGE_NAME_LENGTH) name = name[..MAX_PAGE_NAME_LENGTH];
            var unique = NumberedName(seen, name);
            if (!string.Equals(unique, name, StringComparison.Ordinal))
                issues.Add(Warning("QBL_DUPLICATE_PAGE_NAME", $"Dashboard '{name}' is already defined; imported as '{unique}'.", pageRef));

            var dashboard = new PblDashboard { LogicalRef = pageRef, Name = unique };
            foreach (var (_, tabNode) in pageNode.ChildMap("Tabs"))
            {
                var tab = new PblDashboardTab { Name = ResolveScalar(tabNode, "Name")?.Trim() is { Length: > 0 } tabName ? tabName : $"Tab {dashboard.Tabs.Count + 1}" };
                foreach (var (widgetRef, widgetNode) in tabNode.ChildMap("Widgets"))
                {
                    var widget = ConvertDashboardWidget(widgetNode);
                    if (widget is null)
                    {
                        issues.Add(Warning("QBL_UNSUPPORTED_DASHBOARD_WIDGET", $"Dashboard '{unique}': widget '{widgetRef}' of type '{widgetNode.Type}' has no PowerBase widget; skipped.", pageRef));
                        continue;
                    }
                    tab.Widgets.Add(widget);
                }
                dashboard.Tabs.Add(tab);
            }

            if (dashboard.Tabs.Count == 0)
                dashboard.Tabs.Add(new PblDashboardTab { Name = "Tab 1" });
            result.Add(dashboard);
        }

        return result;
    }

    private static PblDashboardWidget? ConvertDashboardWidget(QblResourceNode node)
    {
        int Number(string key, int fallback) => int.TryParse(ResolveScalar(node, key), out var n) ? n : fallback;
        // Quickbase counts its canvas in small rows; PowerBase's dashboard grid has 12 columns of taller rows.
        var widget = new PblDashboardWidget
        {
            Title = ResolveScalar(node, "Title")?.Trim() ?? "",
            ShowTitle = !string.Equals(ResolveScalar(node, "ShowTitle"), "false", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ResolveScalar(node, "Title")),
            X = Math.Clamp(Number("X", 0), 0, 11),
            Y = Math.Max(0, Number("Y", 0) / 6),
            W = Math.Clamp(Number("Width", 4), 1, 12),
            H = Math.Max(2, (int)Math.Ceiling(Number("Height", 12) / 6.0)),
        };
        widget.X = Math.Min(widget.X, 12 - widget.W);

        string? ReportRef()
        {
            var reference = node.PropertyRef("Report");
            var table = reference?["Table"];
            var report = reference?["Report"] ?? reference?["Chart"];
            return table is null || report is null ? null : Qualify(table, report);
        }

        switch (node.Type)
        {
            case "QB::Dashboard::Widget::Table" or "QB::Dashboard::Widget::Summary" or "QB::Dashboard::Widget::Chart":
                widget.WidgetType = "Report";
                widget.ReportRef = ReportRef();
                return widget.ReportRef is null ? null : widget;
            case "QB::Dashboard::Widget::Spacer":
                widget.WidgetType = "Spacer";
                return widget;
            case "QB::Dashboard::Widget::RichText":
                widget.WidgetType = "Text";
                widget.Content = ResolveScalar(node, "Content") ?? "";
                return widget;
            case "QB::Dashboard::Widget::WebPage":
                widget.WidgetType = "WebPage";
                widget.Url = ResolveScalar(node, "Url");
                return string.IsNullOrWhiteSpace(widget.Url) ? null : widget;
            case "QB::Dashboard::Widget::Button::OpenLink":
                widget.WidgetType = "ButtonBar";
                widget.ButtonLabel = ResolveScalar(node, "Text") ?? "Open";
                widget.Url = ResolveScalar(node, "Link");
                widget.ButtonColor = NullIfBlank(ResolveScalar(node, "Color"));
                widget.OpenInNewTab = string.Equals(ResolveScalar(node, "OpenInNewWindow"), "true", StringComparison.OrdinalIgnoreCase);
                return string.IsNullOrWhiteSpace(widget.Url) ? null : widget;
            case "QB::Dashboard::Widget::Button::OpenReport":
                widget.WidgetType = "ButtonBar";
                widget.ButtonLabel = ResolveScalar(node, "Text") ?? "Open report";
                widget.ReportRef = ReportRef();
                widget.ButtonColor = NullIfBlank(ResolveScalar(node, "Color"));
                widget.OpenInNewTab = string.Equals(ResolveScalar(node, "OpenInNewWindow"), "true", StringComparison.OrdinalIgnoreCase);
                return widget.ReportRef is null ? null : widget;
            default:
                return null; // Search, KPI …
        }
    }

    /// <summary>The app's pages. A real export wraps them like it wraps forms — <c>Pages: { Resources: { $Page_X: … } }</c> — while a
    /// hand-written one may list them directly under <c>Pages</c>; both are read.</summary>
    private static IReadOnlyDictionary<string, QblResourceNode> PageNodes(QblResourceNode appNode)
    {
        var result = new Dictionary<string, QblResourceNode>(StringComparer.Ordinal);

        if (appNode.Children.TryGetValue("Pages", out var raw) && raw is IReadOnlyDictionary<string, object?> wrapper)
        {
            if (wrapper.TryGetValue("Resources", out var resourcesRaw) && resourcesRaw is IReadOnlyDictionary<string, object?> resources)
            {
                foreach (var (key, value) in resources)
                    if (value is QblResourceNode node) result[key] = node;
            }
            else
            {
                foreach (var (key, value) in wrapper)
                    if (value is QblResourceNode node) result[key] = node;
            }
        }

        return result;
    }

    private const int MAX_VARIABLE_NAME_LENGTH = 100;
    private const int MAX_VARIABLE_VALUE_LENGTH = 500;
    private const int MAX_PAGE_NAME_LENGTH = 200;

    /// <summary>Quickbase writes another table's id as the pseudo-field <c>[_DBID_TABLE_NAME]</c>
    /// (the table's name upper-cased, non-alphanumerics collapsed to underscores). PowerBase
    /// spells the same thing <c>Dbid("Table Name")</c> — a real function that resolves a table id
    /// by name at evaluation time — so this is a direct rename rather than an approximation.
    /// These appear throughout record-URL formulas, which is what makes them worth translating.
    ///
    /// A ref naming a table that isn't in this import (real exports carry plenty, left over from
    /// other apps or deleted tables) is left untouched and flagged: there's nothing to point it at,
    /// and inventing a target would be worse than saying so.</summary>
    private static void RewriteQuickbaseTableIdRefs(List<PblTable> tables, List<PblIssue> issues)
    {
        var tableNameBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tables)
            tableNameBySlug[QuickbaseTableSlug(t.Name)] = t.Name;

        // Tables keep their source alias (see PblTable.AliasHint), so [_DBID_X] naming one of them already resolves as written.
        var aliasSlugs = tables.Where(t => t.AliasHint is not null).Select(t => t.AliasHint!["_DBID_".Length..]).ToHashSet(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            foreach (var field in table.Fields)
            {
                if (!string.Equals(field.TypeCode, "Formula", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(field.FormulaExpression))
                    continue;

                var unresolved = new List<string>();
                field.FormulaExpression = ReplaceOutsideStringLiterals(field.FormulaExpression, QuickbaseTableIdRef, m =>
                {
                    var slug = m.Groups["slug"].Value;
                    if (aliasSlugs.Contains(slug))
                        return m.Value;
                    if (tableNameBySlug.TryGetValue(slug, out var tableName))
                        return $"Dbid(\"{tableName}\")";

                    unresolved.Add(slug);
                    return m.Value;
                });

                foreach (var slug in unresolved.Distinct())
                {
                    issues.Add(Warning(
                        "QBL_FORMULA_UNKNOWN_TABLE_REF",
                        $"Formula field '{field.Name}' references Quickbase table '{slug}', which is not part of this import; left as-is for manual review.",
                        field.LogicalRef));
                }
            }
        }
    }

    /// <summary>Quickbase formulas can name a field by its numeric id — <c>[1226]</c>. Each is rewritten to the field's name through the table's
    /// source-id map; an id that does not resolve is left as written and reported (the formula then needs manual review).</summary>
    private static void RewriteNumericFieldRefs(List<PblTable> tables, Dictionary<string, Dictionary<int, string>> fieldNameBySourceId, HashSet<string> variableNames, List<PblIssue> issues)
    {
        foreach (var table in tables)
        {
            var byId = fieldNameBySourceId.GetValueOrDefault(table.LogicalRef);
            if (byId is null) continue;

            foreach (var field in table.Fields.Where(f => string.Equals(f.TypeCode, "Formula", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.FormulaExpression)))
            {
                var unresolved = new List<string>();
                field.FormulaExpression = ReplaceOutsideStringLiterals(field.FormulaExpression!, NumericFieldRef, m =>
                {
                    if (variableNames.Contains(m.Groups["id"].Value))
                        return m.Value;
                    if (int.TryParse(m.Groups["id"].Value, out var id) && byId.TryGetValue(id, out var name))
                        return $"[{name}]";
                    unresolved.Add(m.Groups["id"].Value);
                    return m.Value;
                });

                foreach (var id in unresolved.Distinct())
                    issues.Add(Warning("QBL_FORMULA_UNKNOWN_FIELD_ID", $"Formula field '{field.Name}' refers to field id {id}, which is not on its table; left as written for manual review.", field.LogicalRef));
            }
        }
    }

    /// <summary>The formulas inside form rules (a "formula is true" condition, or a mixed condition tree turned into a formula) name fields the same
    /// way table formulas do, so they get the same numeric-id rewrite.</summary>
    private static void RewriteRuleFormulas(List<PblForm> forms, Dictionary<string, Dictionary<int, string>> fieldNameBySourceId, HashSet<string> variableNames, List<PblIssue> issues)
    {
        foreach (var form in forms)
        {
            var byId = fieldNameBySourceId.GetValueOrDefault(form.TableRef);
            if (byId is null) continue;

            foreach (var rule in form.Rules.Where(r => r.IsExpressionMode && !string.IsNullOrWhiteSpace(r.ExpressionText)))
            {
                var unresolved = new List<string>();
                rule.ExpressionText = ReplaceOutsideStringLiterals(rule.ExpressionText!, NumericFieldRef, m =>
                {
                    if (variableNames.Contains(m.Groups["id"].Value))
                        return m.Value;
                    if (int.TryParse(m.Groups["id"].Value, out var id) && byId.TryGetValue(id, out var name))
                        return $"[{name}]";
                    unresolved.Add(m.Groups["id"].Value);
                    return m.Value;
                });

                foreach (var id in unresolved.Distinct())
                    issues.Add(Warning("QBL_FORMULA_UNKNOWN_FIELD_ID", $"Form rule '{rule.Name}' refers to field id {id}, which is not on its table; left as written for manual review.", rule.LogicalRef));
            }
        }
    }

    private static readonly Regex NumericFieldRef = new(@"\[(?:_fid_)?(?<id>\d+)\]", RegexOptions.Compiled);

    private static readonly Regex QuickbaseTableIdRef = new(@"\[_DBID_(?<slug>[A-Za-z0-9_]+)\]", RegexOptions.Compiled);

    /// <summary>Applies <paramref name="replace"/> only to the parts of <paramref name="source"/>
    /// outside double-quoted strings and // comments. Real formulas embed <c>[_DBID_X]</c> inside URL string
    /// literals as part of Quickbase's own <c>&lt;!~db~…~db~&gt;</c> markup, where it is text rather
    /// than a reference — substituting there would splice quotes into the middle of a string and
    /// corrupt the formula rather than translate it.</summary>
    private static string ReplaceOutsideStringLiterals(string source, Regex pattern, MatchEvaluator replace)
    {
        var result = new System.Text.StringBuilder(source.Length);
        var segmentStart = 0;
        var i = 0;

        while (i < source.Length)
        {
            var isComment = source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/';
            if (source[i] != '"' && !isComment)
            {
                i++;
                continue;
            }

            // Code since the last string or comment ends here - rewrite it, then copy the string / comment verbatim.
            result.Append(pattern.Replace(source[segmentStart..i], replace));

            if (isComment)
            {
                // A // comment runs to the end of its line; an id such as [16] inside it is commented-out code, not a reference.
                var lineEnd = source.IndexOf((char)10, i);
                var end = lineEnd < 0 ? source.Length : lineEnd;
                result.Append(source[i..end]);
                i = end;
                segmentStart = i;
                continue;
            }

            var stringStart = i;
            i++; // opening quote
            while (i < source.Length)
            {
                if (source[i] == '\\' && i + 1 < source.Length) { i += 2; continue; } // escape
                if (source[i] == '"') { i++; break; }
                i++;
            }

            result.Append(source[stringStart..i]);
            segmentStart = i;
        }

        result.Append(pattern.Replace(source[segmentStart..], replace));
        return result.ToString();
    }

    private static string QuickbaseTableSlug(string tableName) =>
        new(tableName.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    // ── Roles (Pass D) ───────────────────────────────────────────────────────

    /// <summary>Confirmed real shape: the actual CRUD grants live on the TABLE's own
    /// <c>RolePermissions</c> map, keyed by role ref — not on the Role node. <c>When:
    /// Always/Never</c> maps cleanly to PowerBase's RecordScopes; a <c>CustomAccessCriteria</c>
    /// (Quickbase's own query-criteria mini-language, a different syntax entirely from
    /// PowerBase.Formula) has no translation and is flagged rather than guessed at.</summary>
    private static PblTablePermission ConvertTablePermission(
        string tableRef, string tableName, string roleRefKey, Dictionary<string, object?> perms,
        Dictionary<int, string> fieldNameBySourceId, List<PblIssue> issues)
    {
        var view = ParseRecordAccess(perms, "CanViewRecords", tableName, roleRefKey, fieldNameBySourceId, issues);
        var modify = ParseRecordAccess(perms, "CanModifyRecords", tableName, roleRefKey, fieldNameBySourceId, issues);

        var permission = new PblTablePermission
        {
            TableRef = tableRef,
            ViewScope = view.Scope,
            RecordFilter = view.Filter,
            CanAdd = GetBool(perms, "CanAddRecords"),
            CanDelete = GetBool(perms, "CanDeleteRecords"),
            CanSaveSharedReports = GetBool(perms, "CanSaveCommonReports"),
            CanEditFieldProperties = GetBool(perms, "CanEditFieldProperties"),
        };

        // PowerBase keeps a separate "modify" record filter beside the view filter (what the role may edit or delete, bounded by what it can
        // see). A modify rule that differs from the view rule is carried as that second filter. If its criteria could not be translated
        // there is nothing to carry, and modify access stays at None (least privilege) with the reason reported.
        if (modify.Scope == "AllRecords" && modify.IsCustomRule && !string.Equals(modify.CriteriaKey, view.CriteriaKey, StringComparison.Ordinal))
        {
            if (view.Scope != "None" && modify.Filter is not null)
            {
                permission.ModifyScope = "AllRecords";
                permission.ModifyRecordFilter = modify.Filter;
            }
            else
            {
                issues.Add(Warning(
                    "QBL_ROLE_MODIFY_CRITERIA_DIFFERS",
                    $"Table '{tableName}' role '{roleRefKey}': the modify rule differs from the view rule and could not be carried; modify access was set to none.",
                    roleRefKey));
                permission.ModifyScope = "None";
            }
        }
        else if (view.Scope == "None")
        {
            permission.ModifyScope = "None"; // cannot modify what you cannot see
        }
        else
        {
            permission.ModifyScope = modify.Scope;
        }

        return permission;
    }

    private sealed record RecordAccess(string Scope, PblFilterGroup? Filter, bool IsCustomRule, string? CriteriaKey);

    /// <summary>
    /// Quickbase's <c>When</c> is Always / Never / CustomRule; the criteria text only counts for CustomRule (every role carries
    /// stale placeholder criteria next to Always/Never — in cares, 2,500 of them — which must be ignored, not applied). A CustomRule
    /// that cannot be translated into a PowerBase filter becomes "no access": an access rule is never silently widened.
    /// </summary>
    private static RecordAccess ParseRecordAccess(
        Dictionary<string, object?> perms, string key, string tableName, string roleRefKey,
        Dictionary<int, string> fieldNameBySourceId, List<PblIssue> issues)
    {
        if (!perms.TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> access)
            return new RecordAccess("None", null, false, null);

        var when = access.TryGetValue("When", out var w) ? w?.ToString() : null;

        switch (when)
        {
            case "Always":
                return new RecordAccess("AllRecords", null, false, null);
            case "Never":
                return new RecordAccess("None", null, false, null);
            case "CustomRule":
            {
                var criteria = access.TryGetValue("CustomAccessCriteria", out var crit) ? crit?.ToString() : null;
                var result = QblCriteriaTranslator.Translate(criteria, id => fieldNameBySourceId.GetValueOrDefault(id));
                if (result.Succeeded)
                    return new RecordAccess("AllRecords", result.Filter, true, string.Concat((criteria ?? "").Where(c => !char.IsWhiteSpace(c))));

                issues.Add(Warning(
                    "QBL_ROLE_CUSTOM_RULE_UNTRANSLATABLE",
                    $"Table '{tableName}' role '{roleRefKey}': {key} uses a custom rule that cannot be translated ({result.Problem}); the role has no record access on this table.",
                    roleRefKey));
                return new RecordAccess("None", null, true, null);
            }
            default:
                return new RecordAccess(WarnUnknownAccessWhen(when, key, tableName, roleRefKey, issues), null, false, null);
        }
    }

    /// <summary>Field-level <c>RolePermissions</c> (role → View/Modify/None) onto the owning role's table permission. Only fields a role is
    /// restricted on are listed — anything not listed keeps PowerBase's default (Modify), the same as in the source.</summary>
    private static void ApplyFieldPermissions(
        List<PblTable> tables, Dictionary<string, QblResourceNode> tableNodeByRef,
        Dictionary<string, Dictionary<string, string>> fieldNameByRefPerTable, List<PblRole> roles, List<PblIssue> issues)
    {
        var roleByRef = roles.ToDictionary(r => r.LogicalRef, StringComparer.Ordinal);

        foreach (var table in tables)
        {
            var names = fieldNameByRefPerTable[table.LogicalRef];
            foreach (var (fieldRef, fieldNode) in tableNodeByRef[table.LogicalRef].ChildMap("Fields"))
            {
                if (!fieldNode.Properties.TryGetValue("RolePermissions", out var rpRaw) || rpRaw is not Dictionary<string, object?> byRole)
                    continue;
                if (!names.TryGetValue(fieldRef, out var fieldName))
                    continue;

                foreach (var (roleRef, accessRaw) in byRole)
                {
                    var access = accessRaw?.ToString();
                    if (access is not ("View" or "Modify" or "None"))
                    {
                        issues.Add(Warning("QBL_FIELD_PERMISSION_UNKNOWN", $"Field '{fieldName}' role '{roleRef}': access '{access}' is not View/Modify/None; not applied.", fieldRef));
                        continue;
                    }
                    if (access == "Modify")
                        continue;

                    var permission = roleByRef.GetValueOrDefault(roleRef)?.TablePermissions.FirstOrDefault(p => p.TableRef == table.LogicalRef);
                    if (permission is null)
                    {
                        issues.Add(Warning("QBL_FIELD_PERMISSION_ROLE_UNRESOLVED", $"Field '{fieldName}': role '{roleRef}' has no permission entry for table '{table.Name}'; field restriction not applied.", fieldRef));
                        continue;
                    }

                    permission.FieldAccessLevel = "CustomAccess";
                    permission.FieldPermissions.Add(new PblFieldPermission { FieldName = fieldName, Access = access });
                }
            }
        }
    }

    private static string WarnUnknownAccessWhen(string? when, string key, string tableName, string roleRefKey, List<PblIssue> issues)
    {
        issues.Add(Warning(
            "QBL_UNSUPPORTED_ROLE_ACCESS_WHEN",
            $"Table '{tableName}' role '{roleRefKey}' uses {key} value '{when}', which has no confirmed PowerBase equivalent; defaulting to no access.",
            roleRefKey));
        return "None";
    }

    private static bool GetBool(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v) || v is null)
            return false;
        if (v is bool b)
            return b;
        return bool.TryParse(v.ToString(), out var parsed) && parsed;
    }

    // ── Relationships (Pass B) ──────────────────────────────────────────────

    private sealed class RelationshipFieldCollector
    {
        /// <summary>Keyed by the relationship ref from Reference.Relationship.</summary>
        public Dictionary<string, ReferenceFieldInfo> References { get; } = new(StringComparer.Ordinal);

        /// <summary>Keyed by the relationship ref from Lookup.Relationship.</summary>
        public Dictionary<string, List<LookupFieldInfo>> Lookups { get; } = new(StringComparer.Ordinal);

        /// <summary>Keyed by the Reference field's own ref, from Summary.ReferenceField.</summary>
        public Dictionary<string, List<SummaryFieldInfo>> SummariesByReferenceFieldRef { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ReferenceFieldInfo(string FieldRef, string Name, string? Label, bool IsRequired, int? SourceId = null, string? FilterFieldRef = null, string? MatchFieldRef = null, string? Description = null);
    private sealed record LookupFieldInfo(string FieldRef, string Name, string? Label, string? TargetFieldRef, int? SourceId = null, string? Description = null);
    private sealed record SummaryFieldInfo(string FieldRef, string Name, string? Label, string Function, string? TargetFieldRef, string? Query = null, int? SourceId = null, string? Description = null);

    /// <summary>If the raw target ref is one of <paramref name="tableRef"/>'s Address shadow
    /// sub-component fields (see the per-table maps built in <see cref="Convert"/>), redirects it
    /// to the actual composite Address field's ref plus the canonical sub-field key - otherwise
    /// passes the ref through unchanged with no sub-field. Consulted from Pass B (relationship
    /// resolution), not Pass A's field-collection loop, because a Lookup/Summary lives on one
    /// table but targets a field on a *different* table (parent for Lookup, child for Summary) -
    /// that owning table's own field-level scan isn't visible until every table has been walked.</summary>
    private static (string? TargetFieldRef, string? TargetSubField) ResolveAddressTarget(
        string tableRef,
        string? rawTargetFieldRef,
        Dictionary<string, Dictionary<string, (string CompositeFieldRef, string SubFieldKey)>> addressSubComponentOwnerByTableRef)
    {
        if (rawTargetFieldRef is not null &&
            addressSubComponentOwnerByTableRef.TryGetValue(tableRef, out var tableMap) &&
            tableMap.TryGetValue(rawTargetFieldRef, out var owner))
            return (owner.CompositeFieldRef, owner.SubFieldKey);
        return (rawTargetFieldRef, null);
    }

    private static void CollectRelationshipField(string fieldRef, QblResourceNode fieldNode, RelationshipFieldCollector collector, List<PblIssue> issues)
    {
        var label = ResolveScalar(fieldNode, "Label");
        var name = InferFieldName(fieldRef, label);

        switch (fieldNode.Type)
        {
            case "QB::Field::Reference":
            {
                var relRef = NestedRef(fieldNode, "Reference", "Relationship")?["Relationship"];
                if (relRef is null)
                {
                    issues.Add(Warning("QBL_REFERENCE_MISSING_RELATIONSHIP", $"Reference field '{name}' has no resolvable relationship; skipped.", fieldRef));
                    return;
                }
                // "Conditional values": only parent records whose MatchField equals this record's FilterField are offered.
                string? filterFieldRef = null, matchFieldRef = null;
                if (fieldNode.Properties.TryGetValue("Reference", out var referenceBag) && referenceBag is Dictionary<string, object?> referenceMap
                    && referenceMap.TryGetValue("ConditionalValues", out var conditional) && conditional is Dictionary<string, object?> conditionalMap)
                {
                    filterFieldRef = (conditionalMap.GetValueOrDefault("FilterField") as QblRef)?["Field"];
                    matchFieldRef = (conditionalMap.GetValueOrDefault("MatchField") as QblRef)?["Field"];
                }
                collector.References[relRef] = new ReferenceFieldInfo(fieldRef, name, NullIfBlank(label), fieldNode.PropertyBool("IsRequired"), fieldNode.SourceId, filterFieldRef, matchFieldRef, NullIfBlank(ResolveScalar(fieldNode, "Comments")));
                return;
            }
            case "QB::Field::Lookup":
            {
                var relRef = NestedRef(fieldNode, "Lookup", "Relationship")?["Relationship"];
                if (relRef is null)
                {
                    issues.Add(Warning("QBL_LOOKUP_MISSING_RELATIONSHIP", $"Lookup field '{name}' has no resolvable relationship; skipped.", fieldRef));
                    return;
                }
                var targetFieldRef = NestedRef(fieldNode, "Lookup", "TargetField")?["Field"];
                if (!collector.Lookups.TryGetValue(relRef, out var list))
                    collector.Lookups[relRef] = list = [];
                list.Add(new LookupFieldInfo(fieldRef, name, NullIfBlank(label), targetFieldRef, fieldNode.SourceId, NullIfBlank(ResolveScalar(fieldNode, "Comments"))));
                return;
            }
            case "QB::Field::Summary":
            {
                // Confirmed real shape: ReferenceField is !Ref{Table, Field} - both parts. The
                // Field part alone is NOT enough to identify which relationship this Summary
                // belongs to: real exports show different child tables independently
                // auto-naming their own Reference field with the identical local key (e.g. two
                // unrelated child tables both ending up with "$Field_Related_Import"), so two
                // different relationships into the same parent can share that raw Field ref.
                // Keying by the (Table, Field) pair - not Field alone - is what disambiguates
                // them; matched against the relationship's own ChildTableRef in
                // ResolveRelationshipLookupsAndSummaries.
                var referenceField = NestedRef(fieldNode, "Summary", "ReferenceField");
                var referenceFieldTableRef = referenceField?["Table"];
                var referenceFieldRef = referenceField?["Field"];
                var summaryProps = fieldNode.Properties.TryGetValue("Summary", out var s) ? s as Dictionary<string, object?> : null;
                var qblFunction = summaryProps?.TryGetValue("Function", out var fn) == true ? fn?.ToString() : null;

                if (referenceFieldTableRef is null || referenceFieldRef is null || qblFunction is null)
                {
                    issues.Add(Warning("QBL_SUMMARY_UNRESOLVED_REFERENCE", $"Summary field '{name}' has a broken relationship/function reference; skipped.", fieldRef));
                    return;
                }

                var function = MapSummaryFunction(qblFunction);
                if (function is null)
                {
                    issues.Add(Warning("QBL_UNSUPPORTED_SUMMARY_FUNCTION", $"Summary field '{name}' uses function '{qblFunction}', which has no PowerBase equivalent.", fieldRef));
                    return;
                }

                var targetFieldRef = NestedRef(fieldNode, "Summary", "FieldToSummarize")?["Field"];
                var referenceFieldKey = Qualify(referenceFieldTableRef, referenceFieldRef);
                if (!collector.SummariesByReferenceFieldRef.TryGetValue(referenceFieldKey, out var slist))
                    collector.SummariesByReferenceFieldRef[referenceFieldKey] = slist = [];
                var query = summaryProps?.TryGetValue("Query", out var q) == true ? NullIfBlank(q?.ToString()) : null;
                slist.Add(new SummaryFieldInfo(fieldRef, name, NullIfBlank(label), function, targetFieldRef, query, fieldNode.SourceId, NullIfBlank(ResolveScalar(fieldNode, "Comments"))));
                return;
            }
            default:
                // QB::Field::ReportLink (auto-created by CreateRelationshipCommandHandler) and
                // QB::Field::ReferenceProxy (auto-derived from the first Lookup) need nothing
                // collected here.
                return;
        }
    }

    /// <summary>PowerBase's SummaryFunctions is {Count, Exists, Sum, Avg, Min, Max, DistinctCount,
    /// CombinedText}; QBL's is {Count, Total, Average, Maximum, Minimum, StdDeviation, CombinedText,
    /// DistinctCount}. StdDeviation has no PowerBase equivalent — flagged by the caller, never
    /// approximated into a different function.</summary>
    private static string? MapSummaryFunction(string qblFunction) => qblFunction switch
    {
        "Count" => "Count",
        "Total" => "Sum",
        "Average" => "Avg",
        "Maximum" => "Max",
        "Minimum" => "Min",
        "AnyRelatedRecords" => "Exists",
        "DistinctCount" => "DistinctCount",
        "CombinedText" => "CombinedText",
        _ => null,
    };

    /// <summary>A Reference field can share its name with a PowerBase system field ("Last Modified By" is a common one, pointing
    /// at a users table) or with another field on the child table. PowerBase needs names and labels unique per table, so the
    /// reference field is renamed with a number — same treatment as a repeated field label — instead of blocking the import.</summary>
    private static void EnsureUniqueReferenceName(
        PblRelationship relationship, string relRef, string referenceFieldRef, Dictionary<string, string> fieldNameByRef, HashSet<string> seenFieldNames,
        List<PblTable> tables, List<PblIssue> issues)
    {
        var systemNames = QblFieldTypeMap.SystemFieldNames.Values;
        bool Taken(string name) => seenFieldNames.Contains(name) || systemNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        var original = relationship.ReferenceFieldName;
        var unique = original;
        for (var n = 2; Taken(unique); n++)
            unique = $"{original} ({n})";

        seenFieldNames.Add(unique);
        if (string.Equals(unique, original, StringComparison.Ordinal))
            return;

        var suffix = unique[original.Length..];
        relationship.ReferenceFieldName = unique;
        relationship.ReferenceFieldLabel = (relationship.ReferenceFieldLabel ?? original) + suffix;
        fieldNameByRef[referenceFieldRef] = unique;

        var tableName = tables.FirstOrDefault(t => t.LogicalRef == relationship.ChildTableRef)?.Name ?? relationship.ChildTableRef;
        issues.Add(Warning("QBL_DUPLICATE_FIELD_NAME", $"Reference field '{original}' clashes with another field on table '{tableName}'; imported as '{unique}'.", relRef));
    }

    /// <summary>Builds a relationship's Reference-field identity only — this part never depends
    /// on another relationship, so it's always resolvable in one pass. Lookups/Summaries are
    /// resolved separately (see <see cref="ResolveRelationshipLookupsAndSummaries"/>) since those
    /// can depend on another relationship having run first.</summary>
    private static PblRelationship? BuildRelationshipSkeleton(
        string relRef,
        QblResourceNode relNode,
        string childTableRef,
        Dictionary<string, RelationshipFieldCollector> relFieldsPerTable,
        Dictionary<string, Dictionary<string, string>> fieldNameByRefPerTable,
        List<PblIssue> issues)
    {
        var parentTableRef = relNode.PropertyRef("Parent")?["Table"];
        if (parentTableRef is null)
        {
            issues.Add(Warning("QBL_RELATIONSHIP_MISSING_PARENT", $"Relationship '{relRef}' has no resolvable parent table; skipped.", relRef));
            return null;
        }

        if (!relFieldsPerTable.TryGetValue(childTableRef, out var childRelFields) || !childRelFields.References.TryGetValue(relRef, out var referenceInfo))
        {
            issues.Add(Warning("QBL_RELATIONSHIP_MISSING_REFERENCE_FIELD", $"Relationship '{relRef}' has no matching Reference field; skipped.", relRef));
            return null;
        }

        var relationship = new PblRelationship
        {
            LogicalRef = relRef,
            ParentTableRef = parentTableRef,
            ChildTableRef = childTableRef,
            ReferenceFieldName = referenceInfo.Name,
            ReferenceFieldLabel = referenceInfo.Label,
            IsReferenceRequired = referenceInfo.IsRequired,
            ReferenceFieldSourceId = referenceInfo.SourceId,
        };

        // Enrich Pass A's field-name maps so Pass C (Reports) — and other relationships'
        // chained Lookups/Summaries — can resolve against this relationship's Reference field,
        // which doesn't exist as a standalone PblField.
        fieldNameByRefPerTable[childTableRef][referenceInfo.FieldRef] = referenceInfo.Name;

        return relationship;
    }

    /// <summary>Resolves every relationship's Lookups/Summaries as a fixed point: each round
    /// resolves whatever's newly resolvable given the current field-name maps, enriching those
    /// maps as it goes, until a full round makes no further progress. This is what lets a
    /// chained lookup (Lookup A's TargetField is itself Lookup B's field, on a different
    /// relationship) resolve correctly regardless of which relationship happened to be
    /// processed first — a real pattern confirmed present in real QBL exports, not a
    /// hypothetical edge case.</summary>
    private static void ResolveRelationshipLookupsAndSummaries(
        List<PblRelationship> relationships,
        Dictionary<string, RelationshipFieldCollector> relFieldsPerTable,
        Dictionary<string, Dictionary<string, string>> fieldNameByRefPerTable,
        Dictionary<string, HashSet<string>> seenFieldNamesByTableRef,
        Dictionary<string, string> tableNameByRef,
        Dictionary<string, Dictionary<string, (string CompositeFieldRef, string SubFieldKey)>> addressSubComponentOwnerByTableRef,
        List<PblIssue> issues)
    {
        var pendingLookups = new List<(PblRelationship Rel, LookupFieldInfo Lookup)>();
        var pendingSummaries = new List<(PblRelationship Rel, SummaryFieldInfo Summary, string ReferenceFieldRef)>();

        foreach (var rel in relationships)
        {
            if (relFieldsPerTable.TryGetValue(rel.ChildTableRef, out var childRelFields) &&
                childRelFields.Lookups.TryGetValue(rel.LogicalRef, out var lookups))
            {
                pendingLookups.AddRange(lookups.Select(l => (rel, l)));
            }

            // The Reference field's own ref is what Summary.ReferenceField points at — recover
            // it from the child table's collected References (already keyed by relationship ref).
            var referenceFieldRef = relFieldsPerTable.TryGetValue(rel.ChildTableRef, out var crf) && crf.References.TryGetValue(rel.LogicalRef, out var refInfo)
                ? refInfo.FieldRef
                : null;

            // Matched by (ChildTableRef, referenceFieldRef) - the same composite key
            // CollectRelationshipField builds from ReferenceField's own !Ref{Table, Field}. Field
            // ref alone isn't enough: different child tables can independently auto-name their
            // Reference field with the same local key, and without the table half two unrelated
            // relationships into the same parent would both match the same Summary field.
            if (referenceFieldRef is not null &&
                relFieldsPerTable.TryGetValue(rel.ParentTableRef, out var parentRelFields) &&
                parentRelFields.SummariesByReferenceFieldRef.TryGetValue(Qualify(rel.ChildTableRef, referenceFieldRef), out var summaries))
            {
                pendingSummaries.AddRange(summaries.Select(s => (rel, s, referenceFieldRef)));
            }
        }

        bool progress = true;
        while (progress && (pendingLookups.Count > 0 || pendingSummaries.Count > 0))
        {
            progress = false;

            for (var i = pendingLookups.Count - 1; i >= 0; i--)
            {
                var (rel, lookup) = pendingLookups[i];
                var parentFieldNames = fieldNameByRefPerTable.GetValueOrDefault(rel.ParentTableRef, []);
                var (lookupTargetFieldRef, lookupTargetSubField) = ResolveAddressTarget(rel.ParentTableRef, lookup.TargetFieldRef, addressSubComponentOwnerByTableRef);
                if (lookupTargetFieldRef is null || !parentFieldNames.TryGetValue(lookupTargetFieldRef, out var sourceFieldName))
                    continue;

                var childSeenNames = seenFieldNamesByTableRef.GetValueOrDefault(rel.ChildTableRef);
                if (childSeenNames is not null)
                {
                    var uniqueLookupName = NumberedName(childSeenNames, lookup.Name);
                    if (!string.Equals(uniqueLookupName, lookup.Name, StringComparison.Ordinal))
                    {
                        issues.Add(Warning("QBL_DUPLICATE_FIELD_NAME", $"Lookup field '{lookup.Name}' is already used on table '{tableNameByRef.GetValueOrDefault(rel.ChildTableRef, rel.ChildTableRef)}'; imported as '{uniqueLookupName}'.", lookup.FieldRef));
                        lookup = lookup with { Label = (lookup.Label ?? lookup.Name) + uniqueLookupName[lookup.Name.Length..], Name = uniqueLookupName };
                    }
                }

                rel.Lookups.Add(new PblLookupField { LogicalRef = lookup.FieldRef, Name = lookup.Name, Label = lookup.Label, SourceFieldName = sourceFieldName, SourceSubField = lookupTargetSubField, SourceId = lookup.SourceId, Description = lookup.Description });
                fieldNameByRefPerTable[rel.ChildTableRef][lookup.FieldRef] = lookup.Name;
                pendingLookups.RemoveAt(i);
                progress = true;
            }

            for (var i = pendingSummaries.Count - 1; i >= 0; i--)
            {
                var (rel, summary, _) = pendingSummaries[i];
                var childFieldNames = fieldNameByRefPerTable.GetValueOrDefault(rel.ChildTableRef, []);
                var (summaryTargetFieldRef, summaryTargetSubField) = ResolveAddressTarget(rel.ChildTableRef, summary.TargetFieldRef, addressSubComponentOwnerByTableRef);

                if (summaryTargetSubField is not null && summary.Function is "Sum" or "Avg")
                {
                    issues.Add(Warning("QBL_UNSUPPORTED_SUMMARY_ADDRESS_SUBFIELD", $"Summary field '{summary.Name}' uses function '{summary.Function}' over an address sub-component, which PowerBase can't aggregate numerically; skipped.", summary.FieldRef));
                    pendingSummaries.RemoveAt(i);
                    progress = true;
                    continue;
                }

                string? targetFieldName = null;
                if (summaryTargetFieldRef is not null && !childFieldNames.TryGetValue(summaryTargetFieldRef, out targetFieldName))
                    continue;

                var parentSeenNames = seenFieldNamesByTableRef.GetValueOrDefault(rel.ParentTableRef);
                if (parentSeenNames is not null)
                {
                    var uniqueSummaryName = NumberedName(parentSeenNames, summary.Name);
                    if (!string.Equals(uniqueSummaryName, summary.Name, StringComparison.Ordinal))
                    {
                        issues.Add(Warning("QBL_DUPLICATE_FIELD_NAME", $"Summary field '{summary.Name}' is already used on table '{tableNameByRef.GetValueOrDefault(rel.ParentTableRef, rel.ParentTableRef)}'; imported as '{uniqueSummaryName}'.", summary.FieldRef));
                        summary = summary with { Label = (summary.Label ?? summary.Name) + uniqueSummaryName[summary.Name.Length..], Name = uniqueSummaryName };
                    }
                }

                rel.Summaries.Add(new PblSummaryField { LogicalRef = summary.FieldRef, Name = summary.Name, Label = summary.Label, Function = summary.Function, TargetFieldName = targetFieldName, TargetSubField = summaryTargetSubField, QueryCriteria = summary.Query, SourceId = summary.SourceId, Description = summary.Description });
                fieldNameByRefPerTable[rel.ParentTableRef][summary.FieldRef] = summary.Name;
                pendingSummaries.RemoveAt(i);
                progress = true;
            }
        }

        foreach (var (_, lookup) in pendingLookups)
            issues.Add(Warning("QBL_LOOKUP_FIELD_UNRESOLVED", $"Lookup field '{lookup.Name}' references a parent field not available in this import phase; skipped.", lookup.FieldRef));

        foreach (var (_, summary, _) in pendingSummaries)
            issues.Add(Warning("QBL_SUMMARY_FIELD_UNRESOLVED", $"Summary field '{summary.Name}' references a child field not available in this import phase; skipped.", summary.FieldRef));
    }

    /// <summary><paramref name="name"/>, or "name (2)", "name (3)"… when it is already in <paramref name="seen"/>; the returned name is added to the set.</summary>
    private static string NumberedName(HashSet<string> seen, string name)
    {
        var candidate = name;
        for (var n = 2; !seen.Add(candidate); n++)
            candidate = $"{name} ({n})";
        return candidate;
    }

    private static QblRef? NestedRef(QblResourceNode node, string propertyName, string refPropertyName)
    {
        if (node.Properties.TryGetValue(propertyName, out var raw) && raw is Dictionary<string, object?> nested)
            return nested.TryGetValue(refPropertyName, out var refVal) ? refVal as QblRef : null;
        return null;
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    private static PblField? ConvertField(string tableRef, string fieldRef, QblResourceNode fieldNode, List<PblIssue> issues)
    {
        var type = fieldNode.Type;

        // Informational skips — expected, not a gap. System fields and address sub-components
        // are already covered by PowerBase's own auto-seeding / the composite Address field.
        if (QblFieldTypeMap.SystemFieldTypes.Contains(type) || QblFieldTypeMap.AddressSubComponentTypes.Contains(type))
            return null;

        // Relationship-family fields are folded into their owning PblRelationship (see
        // CollectRelationshipField/ConvertRelationship above), never created as a standalone
        // field here.
        if (QblFieldTypeMap.RelationshipFieldTypes.Contains(type))
            return null;

        var label = ResolveScalar(fieldNode, "Label");
        var name = InferFieldName(fieldRef, label);

        if (QblFieldTypeMap.TryMapScalar(type, out var scalarTypeCode))
        {
            var mapped = QblFieldMapper.Map(fieldNode, scalarTypeCode);
            foreach (var note in mapped.Notes)
                issues.Add(Warning("QBL_FIELD_SETTING_NOT_CARRIED", $"Field '{name}': {note}", fieldRef));

            return new PblField
            {
                LogicalRef = Qualify(tableRef, fieldRef),
                Name = name,
                Label = NullIfBlank(label),
                TypeCode = scalarTypeCode,
                SourceId = fieldNode.SourceId,
                IsRequired = mapped.IsRequired,
                Settings = mapped.SettingsJson,
                DefaultValue = mapped.DefaultValue,
                Description = mapped.Description,
                HelpText = mapped.HelpText,
                IsUnique = mapped.IsUnique,
                IsAuditable = mapped.IsAuditable,
                IsSearchable = mapped.IsSearchable,
                IsReportable = mapped.IsReportable,
            };
        }

        if (QblFieldTypeMap.TryMapFormulaResultType(type, out var resultType))
        {
            var formula = ResolveScalar(fieldNode, "Formula");
            if (string.IsNullOrWhiteSpace(formula) || HasNoResultLine(formula))
            {
                // PowerBase will not save a blank expression; a typed constant that evaluates to blank gives the same result as
                // Quickbase's empty formula, and keeps the field (and everything that points at it).
                formula = BlankFormulaFor(resultType);
                if (formula is null)
                {
                    issues.Add(Warning("QBL_EMPTY_FORMULA", $"Formula field '{name}' has no expression; skipped.", fieldRef));
                    return null;
                }
                issues.Add(Warning("QBL_EMPTY_FORMULA", $"Formula field '{name}' has no expression in the source; imported with a blank result ({formula}).", fieldRef));
            }

            // The source type, not only the compiled result, picks Formula_Url / _Email / _Phone / _RichText.
            var variant = type switch
            {
                "QB::Field::URL::Formula" => "Url",
                "QB::Field::Email::Formula" => "Email",
                "QB::Field::PhoneNumber::Formula" => "Phone",
                "QB::Field::RichText::Formula" => "RichText",
                _ => null,
            };
            var mapperVariant = variant ?? resultType switch { "TextList" => "MultiSelect", _ => resultType };
            var mappedFormula = QblFieldMapper.Map(fieldNode, "Formula_" + mapperVariant);
            foreach (var note in mappedFormula.Notes)
                issues.Add(Warning("QBL_FIELD_SETTING_NOT_CARRIED", $"Field '{name}': {note}", fieldRef));

            return new PblField
            {
                LogicalRef = Qualify(tableRef, fieldRef),
                Name = name,
                Label = NullIfBlank(label),
                TypeCode = "Formula",
                ResultType = resultType,
                FormulaExpression = formula,
                FormulaVariant = variant,
                SourceId = fieldNode.SourceId,
                Settings = mappedFormula.SettingsJson,
                Description = mappedFormula.Description,
                HelpText = mappedFormula.HelpText,
                IsAuditable = mappedFormula.IsAuditable,
                IsSearchable = mappedFormula.IsSearchable,
                IsReportable = mappedFormula.IsReportable,
            };
        }

        if (QblFieldTypeMap.Unsupported.Contains(type))
        {
            var explanation = ResolveScalar(fieldNode, "Explanation");
            issues.Add(Warning(
                "QBL_UNSUPPORTED_FIELD_TYPE",
                string.IsNullOrWhiteSpace(explanation) ? $"QBL field type '{type}' has no PowerBase equivalent." : explanation,
                fieldRef));
            return null;
        }

        if (QblFieldTypeMap.UnsupportedFormulaTypes.Contains(type))
        {
            issues.Add(Warning(
                "QBL_UNSUPPORTED_FORMULA_RESULT_TYPE",
                $"Formula field '{name}' has no PowerBase formula result type equivalent for '{type}'.",
                fieldRef));
            return null;
        }

        issues.Add(Warning("QBL_UNKNOWN_FIELD_TYPE", $"QBL field type '{type}' is not recognized by this import phase.", fieldRef));
        return null;
    }

    // ── Reports (Pass C) ─────────────────────────────────────────────────────

    private static PblReport? ConvertReport(
        string tableRef,
        string reportRef,
        QblResourceNode reportNode,
        Dictionary<string, string> fieldNameByRef,
        Dictionary<int, string> fieldNameBySourceId,
        List<PblIssue> issues)
    {
        var reportType = reportNode.Type switch
        {
            "QB::Report::Table" => "Table",
            "QB::Report::Summary" => "Summary",
            // GridEdit is no longer a distinct PowerBase report type (it's now a session-only
            // client-side view toggle on Table reports) — a QuickBase grid-edit report imports
            // as a plain Table report.
            "QB::Report::GridEdit" => "Table",
            "QB::Chart::Pie" or "QB::Chart::StackedBar" or "QB::Chart::Gauge" => "Chart",
            _ => null,
        };

        if (reportType is null)
        {
            // QB::ReportGroup (organizational only) and Calendar/Timeline/Kanban/Map/Chart
            // report types all land here — no PowerBase report type equivalent.
            if (reportNode.Type != "QB::ReportGroup")
            {
                issues.Add(Warning(
                    "QBL_UNSUPPORTED_REPORT_TYPE",
                    $"Report type '{reportNode.Type}' has no PowerBase equivalent.",
                    reportRef));
            }
            return null;
        }

        var name = ResolveScalar(reportNode, "Name") ?? reportRef;
        var report = new PblReport { LogicalRef = Qualify(tableRef, reportRef), Name = name, ReportType = reportType };

        if (reportType == "Chart")
        {
            if (!ApplyChartReport(report, reportNode, fieldNameByRef, issues, reportRef))
                return null;
        }
        else if (reportType == "Summary")
            ApplySummaryReport(report, reportNode, fieldNameByRef, issues, reportRef);
        else
            report.Columns.AddRange(ResolveReportColumns(reportNode, fieldNameByRef, reportRef, issues));

        var sortKey = reportNode.Properties.ContainsKey("SortAndGroup") ? "SortAndGroup" : "Sort";
        if (reportNode.Properties.TryGetValue(sortKey, out var sortRaw) && sortRaw is List<object?> sortList)
        {
            foreach (var item in sortList)
            {
                if (item is not Dictionary<string, object?> sortItem)
                    continue;

                var fieldRefValue = (sortItem.TryGetValue("Field", out var f) ? f as QblRef : null)?["Field"];
                if (fieldRefValue is null || !fieldNameByRef.TryGetValue(fieldRefValue, out var fieldName))
                    continue;

                var descending = string.Equals(sortItem.TryGetValue("SortOrder", out var so) ? so?.ToString() : null, "Descending", StringComparison.OrdinalIgnoreCase);
                // "Unknown"/"None" = sort only; Value/Month/… = also group, combining values that way.
                var groupBy = sortItem.TryGetValue("GroupBy", out var gb) ? gb?.ToString() : null;
                var isGroup = !string.IsNullOrWhiteSpace(groupBy) && !string.Equals(groupBy, "Unknown", StringComparison.OrdinalIgnoreCase) && !string.Equals(groupBy, "None", StringComparison.OrdinalIgnoreCase);
                report.SortFields.Add(new PblSortSpec { FieldName = fieldName, Desc = descending, IsGroup = isGroup, GroupMode = isGroup ? GroupMode(groupBy) : null });
            }
        }

        ApplyReportOptions(report, reportNode, fieldNameByRef, fieldNameBySourceId, issues, reportRef);
        return report;
    }

    /// <summary>Quickbase "group by" value → PowerBase combine mode; anything else groups by equal values.</summary>
    private static string GroupMode(string? qb) => qb switch
    {
        "Month" => "Month", "Day" => "Day", "Week" => "Week", "Quarter" => "Quarter", "Year" => "Year", "Decade" => "Decade",
        "FirstWord" => "FirstWord", "FirstLetter" => "FirstLetter",
        _ => "EqualValues",
    };

    /// <summary>Report filters, totals, grouping display, column header text, grid options, dynamic filters and role visibility.
    /// A filter that cannot be translated is reported and the report is imported without it (never with a wrong one).</summary>
    private static void ApplyReportOptions(PblReport report, QblResourceNode node, Dictionary<string, string> fieldNameByRef, Dictionary<int, string> fieldNameBySourceId, List<PblIssue> issues, string reportRef)
    {
        report.Description = NullIfBlank(ResolveScalar(node, "Description"));
        report.ShowDescription = node.PropertyBool("ShowDescription");

        if (node.Properties.TryGetValue("Filters", out var filtersRaw) && filtersRaw is IReadOnlyDictionary<string, object?> filters)
        {
            var criteria = filters.TryGetValue("Criteria", out var c) ? c?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(criteria) && !string.Equals(criteria.Trim(), "Default", StringComparison.OrdinalIgnoreCase))
            {
                var result = QblCriteriaTranslator.Translate(criteria, id => fieldNameBySourceId.GetValueOrDefault(id), allowPrompts: true, allowRelativeDates: true);
                if (result.Succeeded) report.Filter = result.Filter;
                else issues.Add(Warning("QBL_REPORT_FILTER_UNTRANSLATABLE", $"Report '{report.Name}': its filter could not be translated ({result.Problem}); imported without the filter.", reportRef));
            }

            if (filters.TryGetValue("AllowQuickSearch", out var qs) && qs is bool quick) report.AllowQuickSearch = quick;

            if (filters.TryGetValue("DynamicFields", out var dyn))
            {
                if (dyn is IEnumerable<object?> list && dyn is not string)
                {
                    foreach (var item in list)
                        if ((item as QblRef)?["Field"] is { } dynRef && fieldNameByRef.TryGetValue(dynRef, out var dynName))
                            report.DynamicFilterFieldNames.Add(dynName);
                    if (report.DynamicFilterFieldNames.Count > 0) report.DynamicFilterType = "Custom";
                }
                else if (string.Equals(dyn?.ToString(), "None", StringComparison.OrdinalIgnoreCase))
                {
                    report.DynamicFilterType = "None";
                }
            }
        }

        if (node.PropertyBool("HideTotals")) report.HideTotals = true;

        report.GroupDefaultCollapsed = ResolveScalar(node, "GroupDisplay") switch { "Collapsed" => true, "Expanded" => false, _ => null };

        var header = ResolveScalar(node, "ColumnHeaderText");
        if (header is "Wrap" or "Truncate") report.ColumnHeaderText = header;

        if (node.Properties.ContainsKey("AllowEditingRecords") && !node.PropertyBool("AllowEditingRecords", true)) report.ShowEditIcon = false;
        if (node.Properties.ContainsKey("AllowViewingRecords") && !node.PropertyBool("AllowViewingRecords", true)) report.ShowViewIcon = false;
        if (node.PropertyBool("DisableDeleteCheckbox")) report.DisableBulkDelete = true;
        if (ResolveScalar(node, "InlineEditing") is "false" or "Off" or "No" or "Disabled") report.DisableGridEdit = true;

        if (node.Properties.TryGetValue("RolePermissions", out var rpRaw) && rpRaw is IReadOnlyDictionary<string, object?> byRole)
        {
            report.VisibleToRoleRefs = byRole
                .Where(kv => kv.Value is true || string.Equals(kv.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
        }
    }


    /// <summary>QBL's <c>Columns</c> property is either an explicit list of field <c>!Ref</c>s,
    /// or the literal string <c>"Default"</c> meaning "show every field in the table's own order".
    /// PowerBase expresses that same idea as a report with *no* columns listed — that's exactly
    /// how its own seeded "List All" is defined, and the runtime expands an empty column list to
    /// every reportable field. Leaving it empty is therefore both the more faithful mapping and a
    /// sturdier one: enumerating every field instead would make the table's main view depend on
    /// all of them importing successfully, so one formula that failed to translate would take
    /// "List All" down with it.</summary>
    /// <summary>A Quickbase summary report's grouping (<c>GroupingAndCrosstabs.Rows</c> and the crosstab column) and its summarize columns
    /// (<c>Summary::Column</c>: a function over a field, or the record count). PowerBase needs at least one group; when the source has none
    /// that can be created the importer groups by Record ID# so the report still exists and can be repaired.</summary>
    private static void ApplySummaryReport(PblReport report, QblResourceNode node, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues, string reportRef)
    {
        if (node.Properties.TryGetValue("GroupingAndCrosstabs", out var gcRaw) && gcRaw is IReadOnlyDictionary<string, object?> gc)
        {
            if (gc.TryGetValue("Rows", out var rowsRaw) && rowsRaw is IEnumerable<object?> rows && rowsRaw is not string)
            {
                foreach (var row in rows)
                {
                    if (row is not IReadOnlyDictionary<string, object?> r) continue;
                    var group = SummaryGroup(r, fieldNameByRef, report.Name, issues, reportRef);
                    if (group is not null) report.SummaryRows.Add(group);
                }
            }

            if (gc.TryGetValue("Crosstabs", out var ctRaw) && ctRaw is IReadOnlyDictionary<string, object?> ct)
                report.SummaryCrosstab = SummaryGroup(ct, fieldNameByRef, report.Name, issues, reportRef);
        }

        foreach (var (_, column) in node.ChildMap("Columns"))
        {
            if (!string.Equals(column.Type, "QB::Report::Summary::Column", StringComparison.OrdinalIgnoreCase)) continue;

            // "CountOfRecords" is the record count PowerBase always shows; anything else is a field reference.
            var fieldRef = column.PropertyRef("Source")?["Field"];
            if (fieldRef is null) continue;
            if (!fieldNameByRef.TryGetValue(fieldRef, out var fieldName))
            {
                issues.Add(Warning("QBL_REPORT_FIELD_UNAVAILABLE", $"Summary report '{report.Name}': a summarize column uses a field not available in this import phase; skipped.", reportRef));
                continue;
            }

            var function = ResolveScalar(column, "SummarizeBy") switch
            {
                "Total" => "Sum", "Average" => "Avg", "Maximum" => "Max", "Minimum" => "Min", _ => null,
            };
            if (function is null)
            {
                issues.Add(Warning("QBL_UNSUPPORTED_SUMMARY_REPORT_FUNCTION", $"Summary report '{report.Name}': summarize function '{ResolveScalar(column, "SummarizeBy")}' has no PowerBase equivalent; column skipped.", reportRef));
                continue;
            }

            var displayAs = string.Equals(ResolveScalar(column, "DisplayAs"), "PercentOfColumnTotal", StringComparison.OrdinalIgnoreCase) ? "PercentOfColumnTotal" : "Normal";
            report.Aggregations.Add(new PblSummaryAggregation { FieldName = fieldName, Function = function, DisplayAs = displayAs });
        }
    }

    private static IReadOnlyDictionary<string, object?>? AsMap(object? value) => value as IReadOnlyDictionary<string, object?>;

    private static IReadOnlyDictionary<string, object?>? Prop(QblResourceNode node, string name) =>
        node.Properties.TryGetValue(name, out var v) ? AsMap(v) : null;

    /// <summary>Pie, StackedBar and Gauge charts → a PowerBase chart report. Returns false (with the reason reported) when the chart cannot be
    /// expressed: it is grouped by a report formula, or it is a gauge that counts records (PowerBase's gauge measures a field).</summary>
    private static bool ApplyChartReport(PblReport report, QblResourceNode node, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues, string reportRef)
    {
        var chart = new PblChart();
        var details = Prop(node, "DataDetails");

        PblSummaryGroup? Group(IReadOnlyDictionary<string, object?>? item, string what)
        {
            if (item is null) return null;
            var source = item.TryGetValue("Source", out var s) ? s as QblRef : null;
            if (source is null) return null;
            var fieldRef = source["Field"];
            if (fieldRef is null)
            {
                issues.Add(Warning("QBL_UNSUPPORTED_CHART_GROUPING", $"Chart '{report.Name}': its {what} is a report formula, which PowerBase charts cannot group by; chart skipped.", reportRef));
                return new PblSummaryGroup { FieldName = "" };
            }
            if (!fieldNameByRef.TryGetValue(fieldRef, out var fieldName))
            {
                issues.Add(Warning("QBL_REPORT_FIELD_UNAVAILABLE", $"Chart '{report.Name}': its {what} uses a field not available in this import phase; chart skipped.", reportRef));
                return new PblSummaryGroup { FieldName = "" };
            }
            var groupBy = item.TryGetValue("GroupBy", out var gb) ? gb?.ToString() : null;
            return new PblSummaryGroup { FieldName = fieldName, GroupMode = GroupMode(groupBy) };
        }

        void Aggregate(object? source, string? summarizeBy)
        {
            var fieldRef = (source as QblRef)?["Field"];
            if (fieldRef is null || !fieldNameByRef.TryGetValue(fieldRef, out var fieldName)) return; // CountOfRecords (the default) or unavailable
            var function = summarizeBy switch { "Total" or "Sum" => "Sum", "Average" => "Avg", "Maximum" => "Max", "Minimum" => "Min", _ => null };
            if (function is not null)
                report.Aggregations.Add(new PblSummaryAggregation { FieldName = fieldName, Function = function });
        }

        switch (node.Type)
        {
            case "QB::Chart::Pie":
            {
                chart.ChartType = "Pie";
                chart.Category = Group(AsMap(details?.GetValueOrDefault("Series")), "slices");
                var summarize = details?.GetValueOrDefault("SummarizeBy")?.ToString();
                if (details is not null && details.TryGetValue("DataSource", out var ds)) Aggregate(ds, summarize);
                chart.DataLabelsVisible = !string.Equals(details?.GetValueOrDefault("DataLabels")?.ToString(), "None", StringComparison.OrdinalIgnoreCase) && details?.GetValueOrDefault("DataLabels") is not null;
                var sorting = Prop(node, "Sorting");
                chart.SortBy = string.Equals(sorting?.GetValueOrDefault("SortBy")?.ToString(), "Category", StringComparison.OrdinalIgnoreCase) ? "Labels" : "Values";
                chart.SortDirection = string.Equals(sorting?.GetValueOrDefault("Order")?.ToString(), "Descending", StringComparison.OrdinalIgnoreCase) ? "Desc" : "Asc";
                break;
            }
            case "QB::Chart::StackedBar":
            {
                chart.ChartType = "StackedBar";
                chart.Category = Group(AsMap(details?.GetValueOrDefault("CategoryAxis")), "category axis");
                chart.Series = Group(AsMap(details?.GetValueOrDefault("Series")), "series");
                var dataAxis = AsMap(details?.GetValueOrDefault("DataAxis"));
                if (dataAxis?.GetValueOrDefault("Data") is IEnumerable<object?> data && dataAxis.GetValueOrDefault("Data") is not string)
                    foreach (var d in data)
                        if (AsMap(d) is { } dm) Aggregate(dm.GetValueOrDefault("Source"), dm.GetValueOrDefault("SummarizeBy")?.ToString());
                var goal = AsMap(details?.GetValueOrDefault("GoalLine"));
                if (goal is not null && decimal.TryParse(goal.GetValueOrDefault("Goal")?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var goalValue) && goalValue != 0)
                {
                    chart.GoalValue = goalValue;
                    chart.GoalLabel = NullIfBlank(goal.GetValueOrDefault("Label")?.ToString());
                }
                chart.DataLabelsVisible = !string.Equals(details?.GetValueOrDefault("DataLabels")?.ToString(), "None", StringComparison.OrdinalIgnoreCase) && details?.GetValueOrDefault("DataLabels") is not null;
                var sorting = AsMap(details?.GetValueOrDefault("Sorting"));
                chart.SortBy = string.Equals(sorting?.GetValueOrDefault("SortBy")?.ToString(), "Category", StringComparison.OrdinalIgnoreCase) ? "Labels" : "Values";
                chart.SortDirection = string.Equals(sorting?.GetValueOrDefault("Direction")?.ToString(), "Descending", StringComparison.OrdinalIgnoreCase) ? "Desc" : "Asc";
                break;
            }
            default: // Gauge
            {
                chart.ChartType = "Gauge";
                var data = Prop(node, "Data");
                var fieldRef = (data?.GetValueOrDefault("Source") as QblRef)?["Field"];
                if (fieldRef is null)
                {
                    issues.Add(Warning("QBL_UNSUPPORTED_GAUGE_COUNT", $"Gauge '{report.Name}' counts records; PowerBase's gauge measures a field (sum, average, max or min), so it was not created.", reportRef));
                    return false;
                }
                if (!fieldNameByRef.TryGetValue(fieldRef, out var gaugeField))
                {
                    issues.Add(Warning("QBL_REPORT_FIELD_UNAVAILABLE", $"Gauge '{report.Name}' measures a field not available in this import phase; skipped.", reportRef));
                    return false;
                }
                chart.GaugeFieldName = gaugeField;
                chart.GaugeFunction = data?.GetValueOrDefault("SummarizeBy")?.ToString() switch { "Average" => "Avg", "Maximum" => "Max", "Minimum" => "Min", _ => "Sum" };
                var limits = Prop(node, "GaugeLimits");
                if (decimal.TryParse(limits?.GetValueOrDefault("Goal")?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gaugeGoal) && gaugeGoal > 0)
                    chart.GoalValue = gaugeGoal;
                break;
            }
        }

        if (chart.ChartType != "Gauge" && (chart.Category is null || string.IsNullOrEmpty(chart.Category.FieldName)))
        {
            if (chart.Category is null)
                issues.Add(Warning("QBL_CHART_WITHOUT_CATEGORY", $"Chart '{report.Name}' has no field to group by; chart skipped.", reportRef));
            return false;
        }
        if (chart.Series is { FieldName: "" }) return false;

        report.Chart = chart;
        return true;
    }

    private static PblSummaryGroup? SummaryGroup(IReadOnlyDictionary<string, object?> item, Dictionary<string, string> fieldNameByRef, string reportName, List<PblIssue> issues, string reportRef)
    {
        var fieldRef = (item.TryGetValue("Source", out var s) ? s as QblRef : null)?["Field"];
        if (fieldRef is null) return null; // "None" crosstab, or a count source
        if (!fieldNameByRef.TryGetValue(fieldRef, out var fieldName))
        {
            issues.Add(Warning("QBL_REPORT_FIELD_UNAVAILABLE", $"Summary report '{reportName}': a group uses a field not available in this import phase; skipped.", reportRef));
            return null;
        }

        var groupBy = item.TryGetValue("GroupBy", out var gb) ? gb?.ToString() : null;
        return new PblSummaryGroup { FieldName = fieldName, GroupMode = GroupMode(groupBy) };
    }

    private static List<string> ResolveReportColumns(
        QblResourceNode reportNode,
        Dictionary<string, string> fieldNameByRef,
        string reportRef,
        List<PblIssue> issues)
    {
        var columns = new List<string>();

        if (!reportNode.Properties.TryGetValue("Columns", out var raw))
            return columns;

        if (raw is List<object?> list)
        {
            foreach (var item in list)
            {
                var fieldRefValue = (item as QblRef)?["Field"];
                if (fieldRefValue is null)
                {
                    if (item is QblBadRef)
                        issues.Add(Warning("QBL_BAD_REF", $"Report '{reportRef}' has a broken column reference.", reportRef));
                    continue;
                }

                if (fieldNameByRef.TryGetValue(fieldRefValue, out var fieldName))
                    columns.Add(fieldName);
                else
                    issues.Add(Warning("QBL_REPORT_FIELD_UNAVAILABLE", $"Report '{reportRef}' references a field not available in this import phase.", reportRef));
            }
            return columns;
        }

        // "Default" → leave columns empty; see the summary above.
        return columns;
    }

    // ── Forms (Pass C) ───────────────────────────────────────────────────────

    /// <summary>Table.Forms is shaped differently from Fields/Relationships/Reports: it has a
    /// <c>Properties</c> child (RoleDefaults/ReportOverrides — no PowerBase equivalent, not
    /// imported) and a <c>Resources</c> child holding the actual form nodes. Only
    /// <c>QB::FormV2</c> nodes are returned — the legacy <c>QB::Form</c> representation
    /// (confirmed to describe the same form, just older) is intentionally skipped.</summary>
    /// <summary>The table's <c>Forms.Properties.RoleDefaults</c>: for Everyone and for each role, the form to edit and to add with. "Default" and
    /// "UseRoleSettings" are not choices and are left out.</summary>
    private static IEnumerable<PblFormRoleDefault> ConvertFormRoleDefaults(QblResourceNode tableNode, string tableRef)
    {
        if (!tableNode.Children.TryGetValue("Forms", out var formsRaw) || formsRaw is not Dictionary<string, object?> wrapper
            || !wrapper.TryGetValue("Properties", out var propsRaw) || propsRaw is not Dictionary<string, object?> props
            || !props.TryGetValue("RoleDefaults", out var defaultsRaw) || defaultsRaw is not Dictionary<string, object?> defaults)
            yield break;

        foreach (var (roleKey, value) in defaults)
        {
            if (value is not Dictionary<string, object?> choice) continue;
            string? FormRef(string key) => choice.GetValueOrDefault(key) is QblRef r && r["FormV2"] is { } formRef ? Qualify(tableRef, formRef) : null;
            var edit = FormRef("EditForm");
            var add = FormRef("AddForm");
            if (edit is null && add is null) continue;
            yield return new PblFormRoleDefault
            {
                TableRef = tableRef,
                RoleRef = string.Equals(roleKey, "Everyone", StringComparison.Ordinal) ? null : roleKey,
                EditFormRef = edit,
                AddFormRef = add,
            };
        }
    }

    /// <summary>A table's <c>ConnectedData</c>: where its records come from (another Quickbase application's table), how often they are
    /// refreshed, and which of this table's fields hold which source column.</summary>
    private static PblConnectedTable? ConvertConnectedTable(
        QblResourceNode tableNode, PblTable table, IReadOnlyDictionary<string, QblResourceNode>? external, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues)
    {
        if (!tableNode.Properties.TryGetValue("ConnectedData", out var raw) || raw is not Dictionary<string, object?> data)
            return null;
        if (!string.Equals(data.GetValueOrDefault("Connected")?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
            return null;

        var source = data.GetValueOrDefault("DataSource") as QblRef;
        var dataSourceRef = source?["DataSource"];
        var recordSetRef = source?["RecordSet"];
        QblResourceNode? recordSet = null;
        if (dataSourceRef is not null && recordSetRef is not null && external is not null && external.TryGetValue(dataSourceRef, out var dataSource))
            dataSource.ChildMap("RecordSets").TryGetValue(recordSetRef, out recordSet);
        if (dataSourceRef is null || recordSet is null)
        {
            issues.Add(Warning("QBL_CONNECTED_TABLE_SOURCE_MISSING", $"Table '{table.Name}' is a connected table whose source definition is not in the file; it is imported as an ordinary table.", table.LogicalRef));
            return null;
        }

        var columns = recordSet.ChildMap("Columns");
        var connected = new PblConnectedTable
        {
            TableRef = table.LogicalRef,
            TableName = table.Name,
            SourceAppName = dataSourceRef.Replace("$DataSource_", "").Replace('_', ' ').Trim(),
            SourceTableName = ResolveScalar(recordSet, "Name") ?? recordSetRef,
            UpdateMode = data.GetValueOrDefault("UpdateMode")?.ToString() ?? "Destructive",
        };

        if (data.GetValueOrDefault("Schedule") is Dictionary<string, object?> schedule)
        {
            connected.CronTab = NullIfBlank(schedule.GetValueOrDefault("CronTab")?.ToString());
            connected.NotifyOnFailure = !string.Equals(schedule.GetValueOrDefault("NotifyOnFailure")?.ToString(), "false", StringComparison.OrdinalIgnoreCase);
        }

        var filter = data.GetValueOrDefault("DataFilter");
        if (filter is Dictionary<string, object?> filterMap)
        {
            var anyOf = filterMap.GetValueOrDefault("AnyOf") is List<object?>;
            connected.FilterLogic = anyOf ? "any" : "all";
            var listed = (filterMap.GetValueOrDefault("AllOf") ?? filterMap.GetValueOrDefault("AnyOf")) as List<object?> ?? [];
            foreach (var item in listed.OfType<Dictionary<string, object?>>())
            {
                var columnRef = (item.GetValueOrDefault("Source") as QblRef)?["ConnectedColumn"];
                var op = item.GetValueOrDefault("Operator")?.ToString();
                var value = item.GetValueOrDefault("Value")?.ToString();
                if (columnRef is null || !columns.TryGetValue(columnRef, out var column))
                {
                    issues.Add(Warning("QBL_CONNECTED_FILTER_UNRESOLVED", $"Connected table '{table.Name}': a filter condition names a column that is not in the source; the filter was not carried.", table.LogicalRef));
                    continue;
                }
                var mapped = op switch
                {
                    "Equals" or "Is" => "eq",
                    "IsTrue" => "eq",
                    "IsNot" or "NotEquals" => "ne",
                    "Contains" => "contains",
                    "DoesNotContain" => "notContains",
                    _ => null,
                };
                if (mapped is null)
                {
                    issues.Add(Warning("QBL_CONNECTED_FILTER_OPERATOR", $"Connected table '{table.Name}': filter operator '{op}' has no PowerBase equivalent; that condition was not carried.", table.LogicalRef));
                    continue;
                }
                connected.FilterConditions.Add(new PblConnectedFilter
                {
                    SourceColumnName = ResolveScalar(column, "Name") ?? columnRef,
                    SourceFid = column.SourceId,
                    Operator = mapped,
                    Value = op == "IsTrue" ? "true" : value,
                });
            }
            connected.DataFilter = string.Join(connected.FilterLogic == "any" ? " or " : " and ", connected.FilterConditions.Select(c => $"{c.SourceColumnName} {c.Operator} {c.Value}"));
        }

        if (data.GetValueOrDefault("FieldMap") is Dictionary<string, object?> fieldMap)
        {
            foreach (var (fieldRef, mapped) in fieldMap)
            {
                var columnRef = (mapped as QblRef)?["ConnectedColumn"];
                if (columnRef is null || !fieldNameByRef.TryGetValue(fieldRef, out var fieldName) || !columns.TryGetValue(columnRef, out var column))
                    continue;
                connected.Fields.Add(new PblConnectedField
                {
                    FieldName = fieldName,
                    SourceColumnName = ResolveScalar(column, "Name") ?? columnRef,
                    SourceFid = column.SourceId,
                });
            }
        }

        return connected;
    }

    private static IReadOnlyDictionary<string, QblResourceNode> GetFormV2Nodes(QblResourceNode tableNode)
    {
        var result = new Dictionary<string, QblResourceNode>(StringComparer.Ordinal);

        if (tableNode.Children.TryGetValue("Forms", out var formsRaw) && formsRaw is Dictionary<string, object?> formsWrapper &&
            formsWrapper.TryGetValue("Resources", out var resourcesRaw) && resourcesRaw is Dictionary<string, object?> resources)
        {
            foreach (var (key, value) in resources)
            {
                if (value is QblResourceNode node && node.Type == "QB::FormV2")
                    result[key] = node;
            }
        }

        return result;
    }

    private static PblForm ConvertForm(string formRef, QblResourceNode formNode, string tableRef, Dictionary<string, string> fieldNameByRef, Dictionary<string, string> roleNameByRef, IReadOnlyDictionary<string, string> fieldTypeByName, IReadOnlyDictionary<string, string> fieldHelpByName, List<PblIssue> issues)
    {
        var qualifiedFormRef = Qualify(tableRef, formRef);
        var form = new PblForm
        {
            LogicalRef = qualifiedFormRef,
            TableRef = tableRef,
            Name = ResolveScalar(formNode, "Name") ?? formRef,
            AutoAddNewFields = formNode.Properties.ContainsKey("ShouldAutoAddFields") ? formNode.PropertyBool("ShouldAutoAddFields") : null,
            ShowBuiltInFields = formNode.Properties.ContainsKey("ShouldShowBuiltInFieldsFooter") ? formNode.PropertyBool("ShouldShowBuiltInFieldsFooter") : null,
            AlwaysTabsOnView = formNode.Properties.ContainsKey("AlwaysTabsForViewRecord") ? formNode.PropertyBool("AlwaysTabsForViewRecord") : null,
            PageNavMode = string.Equals(ResolveScalar(formNode, "DisplayPagesAs"), "Steps", StringComparison.OrdinalIgnoreCase) ? "steps" : "tabs",
            SaveOptions = MapSaveOptions(formNode),
        };

        // Section/Block LogicalRefs are QBL keys scoped to their own owning Sections/Columns
        // map (not globally unique — same collision risk as Fields, see the note above the
        // relationship-qualification loop), so raw-ref-keyed !Ref targets in form rule actions
        // (below) can no longer be checked by simple containment once qualified. These maps
        // record the raw→qualified translation as each level is converted, so rule actions can
        // both validate "does this raw target actually exist on this form" and recover the
        // matching qualified ref to store on the PblFormRuleAction.
        var qualifiedSectionRefByRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        var qualifiedBlockRefByRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        var pageTargetByRaw = new Dictionary<string, PageTarget>(StringComparer.Ordinal);

        // QBL's Page tier has no PowerBase equivalent (PowerBase forms are Section→Block→Element,
        // confirmed no Page level) — flatten each Page's Sections into the form in document order.
        // Confirmed real data: a single form can carry two distinct sections with the identical
        // Title (e.g. two independently-added "Data Import details" sections) - Quickbase never
        // enforces section-name uniqueness, but PowerBase's DUPLICATE_FORM_SECTION_NAME check
        // does. Same class of issue as duplicate report/field names above - skip and flag rather
        // than hard-blocking the whole import.
        var seenSectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pinned section(s) first, then each page's sections. Pages become real pages when there is more than one.
        foreach (var (pinnedRef, pinnedNode) in formNode.ChildMap("PinnedSection"))
        {
            var pinned = ConvertFormSection(pinnedRef, pinnedNode, qualifiedFormRef, fieldNameByRef, qualifiedBlockRefByRaw, issues);
            if (pinned is null) continue;
            pinned.IsPinned = true;
            seenSectionNames.Add(pinned.Name);
            form.Sections.Add(pinned);
            qualifiedSectionRefByRaw[pinnedRef] = pinned.LogicalRef;
        }

        var pageNodes = formNode.ChildMap("Pages").ToList();
        var multiPage = pageNodes.Count > 1;
        var pageOrder = 0;
        foreach (var (pageRef, pageNode) in pageNodes)
        {
            PblFormPage? page = null;
            if (multiPage)
            {
                page = new PblFormPage { LogicalRef = Qualify(qualifiedFormRef, pageRef), Heading = NullIfBlank(ResolveScalar(pageNode, "Name")) ?? InferFieldName(pageRef, null), DisplayOrder = pageOrder++ };
                form.Pages.Add(page);
            }
            var pageTarget = new PageTarget(page?.LogicalRef, []);
            pageTargetByRaw[pageRef] = pageTarget;

            foreach (var (sectionRef, sectionNode) in pageNode.ChildMap("Sections"))
            {
                var section = ConvertFormSection(sectionRef, sectionNode, qualifiedFormRef, fieldNameByRef, qualifiedBlockRefByRaw, issues);
                if (section is null)
                    continue;
                section.PageRef = page?.LogicalRef;

                var uniqueSectionName = NumberedName(seenSectionNames, section.Name);
                if (!string.Equals(uniqueSectionName, section.Name, StringComparison.Ordinal))
                {
                    issues.Add(Warning("QBL_DUPLICATE_FORM_SECTION_NAME", $"Section '{section.Name}' is already used on form '{form.Name}'; imported as '{uniqueSectionName}'.", sectionRef));
                    section.Name = uniqueSectionName;
                }

                form.Sections.Add(section);
                qualifiedSectionRefByRaw[sectionRef] = section.LogicalRef;
                pageTarget.SectionRefs.Add(section.LogicalRef);
            }
        }

        // Rule actions target a field's form ELEMENT (not the raw field) — build a lookup map
        // from the sections just converted so ConvertFormRule can resolve it without re-walking
        // the tree.
        var elementRefByFieldName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in form.Sections)
        {
            foreach (var block in section.Blocks)
            {
                foreach (var element in block.Elements)
                {
                    if (element.FieldName is not null)
                        elementRefByFieldName[element.FieldName] = element.LogicalRef;
                }
            }
        }

        foreach (var (ruleRef, ruleNode) in formNode.ChildMap("Rules"))
        {
            var rule = ConvertFormRule(ruleRef, ruleNode, qualifiedFormRef, fieldNameByRef, roleNameByRef, fieldTypeByName, elementRefByFieldName, qualifiedSectionRefByRaw, qualifiedBlockRefByRaw, pageTargetByRaw, issues);
            if (rule is not null)
                form.Rules.Add(rule);
        }

        // A field's own help text shows on every form unless the form's element overrides it; PowerBase keeps the form-level text only,
        // so the field's text becomes the element's text where the element has none.
        foreach (var element in form.Sections.SelectMany(sec => sec.Blocks).SelectMany(b => b.Elements))
            if (element.HelpTextOverride is null && element.FieldName is { } fn && fieldHelpByName.TryGetValue(fn, out var help))
                element.HelpTextOverride = help;

        return form;
    }

    /// <summary>A section needs 1–5 blocks — matches <c>SaveFormLayoutCommandValidator</c>'s own
    /// limit exactly, so an empty or over-full section is flagged here rather than failing at
    /// import time with a less specific error.</summary>
    private static PblFormSection? ConvertFormSection(
        string sectionRef,
        QblResourceNode sectionNode,
        string qualifiedFormRef,
        Dictionary<string, string> fieldNameByRef,
        Dictionary<string, string> qualifiedBlockRefByRaw,
        List<PblIssue> issues)
    {
        var title = ResolveScalar(sectionNode, "Title");
        var qualifiedSectionRef = Qualify(qualifiedFormRef, sectionRef);
        var section = new PblFormSection
        {
            LogicalRef = qualifiedSectionRef,
            Name = string.IsNullOrWhiteSpace(title) ? InferFieldName(sectionRef, null) : title,
            IsCollapsed = sectionNode.PropertyBool("IsCollapsedByDefault"),
            ShowDividers = sectionNode.Properties.ContainsKey("ShowColumnDividers") ? sectionNode.PropertyBool("ShowColumnDividers") : null,
        };

        foreach (var (columnRef, columnNode) in sectionNode.ChildMap("Columns"))
        {
            var block = ConvertFormBlock(columnRef, columnNode, qualifiedSectionRef, fieldNameByRef, issues);
            section.Blocks.Add(block);
            qualifiedBlockRefByRaw[columnRef] = block.LogicalRef;
        }

        if (section.Blocks.Count == 0)
        {
            issues.Add(Warning("QBL_EMPTY_FORM_SECTION", $"Form section '{section.Name}' has no columns; skipped.", sectionRef));
            return null;
        }

        if (section.Blocks.Count > 5)
        {
            issues.Add(Warning("QBL_TOO_MANY_FORM_BLOCKS", $"Form section '{section.Name}' has {section.Blocks.Count} columns; PowerBase supports at most 5 — extra columns skipped.", sectionRef));
            section.Blocks = section.Blocks.Take(5).ToList();
        }

        return section;
    }

    private static PblFormBlock ConvertFormBlock(string columnRef, QblResourceNode columnNode, string qualifiedSectionRef, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues)
    {
        var qualifiedColumnRef = Qualify(qualifiedSectionRef, columnRef);
        var block = new PblFormBlock
        {
            LogicalRef = qualifiedColumnRef,
            Heading = NullIfBlank(ResolveScalar(columnNode, "Title")),
            BackgroundColor = columnNode.PropertyBool("ShowBackgroundColor") ? NullIfBlank(ResolveScalar(columnNode, "BackgroundColor")) : null,
            Width = ParseColumnWidth(columnNode),
        };

        foreach (var (elementRef, elementNode) in columnNode.ChildMap("Elements"))
            block.Elements.AddRange(ConvertFormElements(elementRef, elementNode, qualifiedColumnRef, fieldNameByRef, issues));

        return block;
    }

    private static int? ParseColumnWidth(QblResourceNode columnNode)
    {
        if (!columnNode.Properties.TryGetValue("LayoutWidth", out var raw))
            return null;

        return raw switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>QBL element nodes can nest arbitrarily via <c>QB::FormV2::Element::Group</c>
    /// (confirmed real shape: a Group carries no Properties of its own, only child Elements) —
    /// PowerBase has no grouping construct, so a Group's children are flattened into the
    /// containing block (every field still imports, only the visual grouping is lost).</summary>
    private static List<PblFormElement> ConvertFormElements(string elementRef, QblResourceNode elementNode, string qualifiedParentRef, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues, string? groupRef = null)
    {
        var qualifiedElementRef = Qualify(qualifiedParentRef, elementRef);
        var result = new List<PblFormElement>();

        switch (elementNode.Type)
        {
            case "QB::FormV2::Element::Field":
            {
                var fieldRef = elementNode.PropertyRef("Field")?["Field"];
                if (fieldRef is null || !fieldNameByRef.TryGetValue(fieldRef, out var fieldName))
                {
                    issues.Add(Warning("QBL_FORM_ELEMENT_FIELD_UNAVAILABLE", $"Form element '{elementRef}' references a field ('{fieldRef}') that has no PowerBase field (unsupported, deleted or not convertible); skipped.", elementRef));
                    return result;
                }

                var (widthMode, widthValue) = ParseElementWidth(elementNode);
                var (showOnAdd, showOnEdit, showOnView) = ParseModesToShowIn(elementNode);

                result.Add(new PblFormElement
                {
                    LogicalRef = qualifiedElementRef,
                    ElementType = "Field",
                    FieldName = fieldName,
                    LabelMode = MapLabelMode(ResolveScalar(elementNode, "LabelMode")),
                    CustomLabel = NullIfBlank(ResolveScalar(elementNode, "LabelOverride")),
                    ShowOnAdd = showOnAdd,
                    ShowOnEdit = showOnEdit,
                    ShowOnView = showOnView,
                    WidthMode = widthMode,
                    WidthValue = widthValue,
                    HelpTextOverride = NullIfBlank(ResolveScalar(elementNode, "HelpText")),
                    IsReadOnly = elementNode.PropertyBool("ForceReadOnlyOnForm"),
                    IsRequired = elementNode.PropertyBool("ForceRequireOnForm"),
                    DisplayAs = NullIfBlank(ResolveScalar(elementNode, "MultipleChoiceDisplayAs")),
                    GroupRef = groupRef,
                });
                return result;
            }
            case "QB::FormV2::Element::RichText":
            {
                // Not observed in the real sample, but documented for FormV2 — direct match to
                // PowerBase's StaticText element type.
                result.Add(new PblFormElement
                {
                    LogicalRef = qualifiedElementRef,
                    ElementType = "StaticText",
                    ElementContent = ResolveScalar(elementNode, "Content"),
                });
                return result;
            }
            case "QB::FormV2::Element::Group":
            {
                // PowerBase keeps grouped elements together through a shared group id.
                foreach (var (childRef, childNode) in elementNode.ChildMap("Elements"))
                    result.AddRange(ConvertFormElements(childRef, childNode, qualifiedElementRef, fieldNameByRef, issues, qualifiedElementRef));
                return result;
            }
            case "QB::FormV2::Element::Report":
            {
                var reportRefNode = elementNode.PropertyRef("Report");
                var reportTableRef = reportRefNode?["Table"];
                var reportRef = reportRefNode?["Report"];
                if (reportTableRef is null || reportRef is null)
                {
                    issues.Add(Warning("QBL_FORM_REPORT_ELEMENT_UNRESOLVED", $"Embedded report element '{elementRef}' has no resolvable report; skipped.", elementRef));
                    return result;
                }

                var (reportShowOnAdd, reportShowOnEdit, reportShowOnView) = ParseModesToShowIn(elementNode);
                result.Add(new PblFormElement
                {
                    LogicalRef = qualifiedElementRef,
                    ElementType = "Report",
                    EmbeddedReportRef = Qualify(reportTableRef, reportRef),
                    EmbeddedReportTableRef = reportTableRef,
                    LabelMode = MapLabelMode(ResolveScalar(elementNode, "LabelMode")),
                    CustomLabel = NullIfBlank(ResolveScalar(elementNode, "LabelOverride")),
                    ShowOnAdd = reportShowOnAdd,
                    ShowOnEdit = reportShowOnEdit,
                    ShowOnView = reportShowOnView,
                    ShowToolbar = !elementNode.PropertyBool("HideToolbar"),
                    AllowCreateRecords = elementNode.PropertyBool("AllowCreateRecords", true),
                    OpenInSubForm = elementNode.PropertyBool("ShouldOpenInEmbedded", true),
                    GroupRef = groupRef,
                });
                return result;
            }
            default:
                issues.Add(Warning("QBL_UNKNOWN_FORM_ELEMENT_TYPE", $"Form element type '{elementNode.Type}' is not recognized by this import phase.", elementRef));
                return result;
        }
    }

    /// <summary>Quickbase save actions → PowerBase's save buttons. An empty list leaves PowerBase's default (all four) — never "no buttons".</summary>
    private static string? MapSaveOptions(QblResourceNode formNode)
    {
        if (!formNode.Properties.TryGetValue("AllowedSaveActions", out var raw) || raw is not IEnumerable<object?> list || raw is string)
            return null;

        var mapped = list.Select(x => x?.ToString() switch
        {
            "SaveAndNew" => "SaveNew",
            "SaveAndNext" => "SaveNext",
            "SaveAndKeepWorking" => "SaveKeepWorking",
            "SaveAndView" => "SaveView",
            _ => null,
        }).Where(x => x is not null).Distinct().ToList();

        return mapped.Count == 0 ? null : string.Join(",", mapped);
    }

    private static (string WidthMode, int? WidthValue) ParseElementWidth(QblResourceNode elementNode)
    {
        var raw = ResolveScalar(elementNode, "Width");
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "Default", StringComparison.OrdinalIgnoreCase))
            return ("Auto", null);

        var numeric = raw.TrimEnd('p', 'x');
        return int.TryParse(numeric, out var value) ? ("Fixed", value) : ("Auto", null);
    }

    /// <summary>QBL's LabelMode value "Hidden" maps to PowerBase's "Hide" — the only name
    /// mismatch between the two vocabularies; Default/Custom match as-is.</summary>
    private static string MapLabelMode(string? qblLabelMode) => qblLabelMode switch
    {
        "Hidden" => "Hide",
        "Custom" => "Custom",
        _ => "Default",
    };

    private static (bool ShowOnAdd, bool ShowOnEdit, bool ShowOnView) ParseModesToShowIn(QblResourceNode elementNode)
    {
        if (!elementNode.Properties.TryGetValue("ModesToShowIn", out var raw) || raw is not List<object?> modes)
            return (true, true, true);

        var set = modes.Select(m => m?.ToString()).Where(m => m is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (set.Contains("Create"), set.Contains("Edit"), set.Contains("View"));
    }

    // ── Form Rules ────────────────────────────────────────────────────────────

    /// <summary>Confirmed real shape: a rule's conditions are always wrapped in exactly one
    /// top-level <c>Condition::Group</c> inside <c>When</c> (a plain list, not a ref-keyed map —
    /// unlike everything else this parser handles); that group's own <c>Properties.TrueWhen</c>
    /// becomes the rule's single ConditionLogic. <c>Actions</c> is a sibling list, same shape.</summary>
    private static PblFormRule? ConvertFormRule(
        string ruleRef,
        QblResourceNode ruleNode,
        string qualifiedFormRef,
        Dictionary<string, string> fieldNameByRef,
        Dictionary<string, string> roleNameByRef,
        IReadOnlyDictionary<string, string> fieldTypeByName,
        Dictionary<string, string> elementRefByFieldName,
        Dictionary<string, string> qualifiedSectionRefByRaw,
        Dictionary<string, string> qualifiedBlockRefByRaw,
        IReadOnlyDictionary<string, PageTarget> pageTargetByRaw,
        List<PblIssue> issues)
    {
        var name = ResolveScalar(ruleNode, "Name") ?? ruleRef;

        var runTrigger = MapRunTrigger(ResolveScalar(ruleNode, "RunOn"));
        if (runTrigger is null)
        {
            issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_TRIGGER", $"Form rule '{name}' uses a RunOn value with no PowerBase equivalent; skipped.", ruleRef));
            return null;
        }

        var rule = new PblFormRule
        {
            LogicalRef = Qualify(qualifiedFormRef, ruleRef),
            Name = name,
            Description = NullIfBlank(ResolveScalar(ruleNode, "Description")),
            IsActive = !ruleNode.PropertyBool("Disabled"),
            RunTrigger = runTrigger,
        };

        if (!ruleNode.Children.TryGetValue("When", out var whenRaw) || whenRaw is not List<object?> whenList || whenList.Count == 0)
        {
            issues.Add(Warning("QBL_FORM_RULE_MISSING_CONDITIONS", $"Form rule '{name}' has no conditions; skipped.", ruleRef));
            return null;
        }

        // The usual shape is one top-level Group. A rule can also list its conditions directly (a single formula condition is the common
        // case — the "Prevent Save" / "Warning" rules); several listed conditions must all hold, so they are an All group.
        RuleGroup tree;
        if (whenList.Count == 1 && whenList[0] is QblResourceNode topGroup && IsType(topGroup, GROUP_CONDITION))
            tree = ReadRuleGroup(topGroup);
        else if (whenList.All(w => w is QblResourceNode))
        {
            tree = new RuleGroup { Logic = "all" };
            foreach (var item in whenList.Cast<QblResourceNode>())
                tree.Items.Add(IsType(item, GROUP_CONDITION) ? ReadRuleGroup(item) : item);
        }
        else
        {
            issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_CONDITION_STRUCTURE", $"Form rule '{name}' has a condition structure with no PowerBase equivalent; skipped.", ruleRef));
            return null;
        }

        // The conditions are a tree: nested groups with their own All/Any. A nested group with the same logic as its parent (or a single
        // member) is the same thing as listing its conditions in the parent, so it is flattened. A genuinely mixed tree (an OR inside an AND…)
        // and a formula condition next to other conditions cannot be written as PowerBase's flat list, so they become the rule's
        // formula (PowerBase's expression mode), built from the same conditions.
        rule.ConditionLogic = tree.Logic;

        var flat = TryFlatten(tree, out var leaves);
        var soleFormula = flat && leaves.Count == 1 && IsType(leaves[0], FORMULA_CONDITION);
        var onlyFieldOrRole = flat && leaves.All(l => IsType(l, FIELD_CONDITION) || IsType(l, ROLE_CONDITION) || IsType(l, DAYS_AGO_CONDITION) || IsType(l, RELATIVE_CONDITION) || IsType(l, RANGE_CONDITION));
        var hasUnexpressibleLeaf = flat && leaves.Any(l => !IsType(l, FIELD_CONDITION) && !IsType(l, ROLE_CONDITION) && !IsType(l, FORMULA_CONDITION)
            && !IsType(l, DAYS_AGO_CONDITION) && !IsType(l, RELATIVE_CONDITION) && !IsType(l, RANGE_CONDITION));

        if (soleFormula)
        {
            var formula = ResolveScalar(leaves[0], "Formula");
            if (string.IsNullOrWhiteSpace(formula))
            {
                issues.Add(Warning("QBL_FORM_RULE_EMPTY_FORMULA_CONDITION", $"Form rule '{name}' has an empty formula condition; skipped.", ruleRef));
                return null;
            }

            rule.IsExpressionMode = true;
            rule.ExpressionText = formula;
        }
        else if (flat && (onlyFieldOrRole || hasUnexpressibleLeaf))
        {
            // A flat list: convert each condition; one with no PowerBase operator (days ago, relative date, range…) is reported.
            var order = 0;
            var issuesBefore = issues.Count;
            var dropped = false;
            foreach (var conditionNode in leaves)
            {
                var condition = ConvertFormRuleCondition(conditionNode, fieldNameByRef, roleNameByRef, order, name, ruleRef, issues);
                if (condition is null)
                {
                    dropped = true;
                    continue;
                }

                rule.Conditions.Add(condition);
                order++;
            }

            // Comparisons the condition list has no operator for (Before / After / HasTrueValue …) can still be written as the rule's
            // formula. A role condition cannot, so those rules keep the list.
            if (dropped && BuildRuleExpression(tree, fieldNameByRef, roleNameByRef, fieldTypeByName, out _) is { } asFormula)
            {
                issues.RemoveRange(issuesBefore, issues.Count - issuesBefore);
                rule.Conditions.Clear();
                rule.IsExpressionMode = true;
                rule.ExpressionText = asFormula;
            }
            else if (rule.Conditions.Count == 0)
            {
                issues.Add(Warning("QBL_FORM_RULE_NO_SUPPORTED_CONDITIONS", $"Form rule '{name}' has no conditions PowerBase can represent; skipped.", ruleRef));
                return null;
            }
        }
        else
        {
            var expression = BuildRuleExpression(tree, fieldNameByRef, roleNameByRef, fieldTypeByName, out var problem);
            if (expression is null)
            {
                issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_CONDITION_STRUCTURE", $"Form rule '{name}' has a condition structure with no PowerBase equivalent ({problem}); skipped.", ruleRef));
                return null;
            }

            rule.IsExpressionMode = true;
            rule.ExpressionText = expression;
        }

        var actionsRaw = ruleNode.Children.TryGetValue("Actions", out var a) && a is List<object?> actionsList ? actionsList : [];
        var actionOrder = 0;
        foreach (var actionRaw in actionsRaw)
        {
            if (actionRaw is not QblResourceNode actionNode)
                continue;

            // A page target: the page itself on a multi-page form, otherwise (PowerBase has no page tier on a single-page form) each
            // section of that page.
            if (PageTargetActions(actionNode, pageTargetByRaw) is { } pageActions)
            {
                foreach (var pageAction in pageActions)
                {
                    pageAction.DisplayOrder = actionOrder++;
                    rule.Actions.Add(pageAction);
                }
                continue;
            }

            var action = ConvertFormRuleAction(actionNode, fieldNameByRef, elementRefByFieldName, qualifiedSectionRefByRaw, qualifiedBlockRefByRaw, actionOrder, name, ruleRef, issues);
            if (action is null)
                continue;

            rule.Actions.Add(action);
            actionOrder++;
        }

        if (rule.Actions.Count == 0)
        {
            issues.Add(Warning("QBL_FORM_RULE_NO_SUPPORTED_ACTIONS", $"Form rule '{name}' has no actions PowerBase can represent; skipped.", ruleRef));
            return null;
        }

        return rule;
    }

    private const string GROUP_CONDITION = "QB::FormV2::Rule::Condition::Group";
    private const string FIELD_CONDITION = "QB::FormV2::Rule::Condition::Field";
    private const string ROLE_CONDITION = "QB::FormV2::Rule::Condition::Role";
    private const string FORMULA_CONDITION = "QB::FormV2::Rule::Condition::FormulaIsTrue";
    private const string DAYS_AGO_CONDITION = "QB::FormV2::Rule::Condition::DaysAgo";
    private const string RELATIVE_CONDITION = "QB::FormV2::Rule::Condition::Relative";
    private const string RANGE_CONDITION = "QB::FormV2::Rule::Condition::Range";

    /// <summary>Quickbase's type names are compared without regard to case: the file spells "MakeReadonly" where the documentation says "MakeReadOnly".</summary>
    private static bool IsType(QblResourceNode node, string type) => string.Equals(node.Type, type, StringComparison.OrdinalIgnoreCase);

    private static string? MapRunTrigger(string? qblRunOn) => RunTriggers.GetValueOrDefault(qblRunOn ?? "");

    private static readonly Dictionary<string, string> RunTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RecordOpenForEdit"] = "EditOrAdd",
        ["RecordSave"] = "Save",
        ["RecordSaveAfterValidating"] = "SaveAfterValidating",
        ["RecordOpenSaveOrChange"] = "AnyChange",
    };

    // ── Rule condition tree ─────────────────────────────────────────────────

    private sealed class RuleGroup
    {
        public string Logic { get; init; } = "all";
        public List<object> Items { get; } = [];   // RuleGroup | QblResourceNode (a leaf condition)
    }

    private static RuleGroup ReadRuleGroup(QblResourceNode group)
    {
        var result = new RuleGroup { Logic = string.Equals(ResolveScalar(group, "TrueWhen"), "Any", StringComparison.OrdinalIgnoreCase) ? "any" : "all" };
        if (!group.Children.TryGetValue("When", out var raw) || raw is not List<object?> list)
            return result;

        foreach (var item in list)
        {
            if (item is not QblResourceNode node) continue;
            if (IsType(node, GROUP_CONDITION)) result.Items.Add(ReadRuleGroup(node));
            else result.Items.Add(node);
        }
        return result;
    }

    /// <summary>True when every nested group can be merged into its parent (same All/Any, or a single member).</summary>
    private static bool TryFlatten(RuleGroup group, out List<QblResourceNode> leaves)
    {
        leaves = [];
        foreach (var item in group.Items)
        {
            if (item is QblResourceNode leaf) { leaves.Add(leaf); continue; }

            var sub = (RuleGroup)item;
            if (sub.Items.Count == 0) continue;
            if (sub.Items.Count > 1 && sub.Logic != group.Logic) return false;
            if (!TryFlatten(sub, out var inner)) return false;
            leaves.AddRange(inner);
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string> FieldHelpByName(PblTable table) =>
        table.Fields.Where(f => !string.IsNullOrWhiteSpace(f.HelpText)).ToDictionary(f => f.Name, f => f.HelpText!, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> FieldTypesByName(PblTable table, IReadOnlyList<PblTable> tables, IReadOnlyList<PblRelationship> relationships)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in table.Fields)
        {
            var type = string.Equals(f.TypeCode, "Formula", StringComparison.OrdinalIgnoreCase) ? f.ResultType ?? "Text" : f.TypeCode;
            map[f.Name] = type;
        }

        // The reference, lookup and summary fields a relationship adds to this table: a rule's condition on one is written for the type it shows.
        foreach (var rel in relationships.Where(r => r.ChildTableRef == table.LogicalRef))
        {
            map[rel.ReferenceFieldName] = "Reference";
            foreach (var lookup in rel.Lookups)
                if (TypeOfFieldIn(rel.ParentTableRef, lookup.SourceFieldName, tables, relationships, 0) is { } lookupType)
                    map[lookup.Name] = lookupType;
        }
        foreach (var rel in relationships.Where(r => r.ParentTableRef == table.LogicalRef))
            foreach (var summary in rel.Summaries)
            {
                var summaryType = summary.Function switch
                {
                    "Count" or "DistinctCount" => "Number",
                    "Exists" => "Boolean",
                    "CombinedText" => "Text",
                    _ => summary.TargetFieldName is null ? null : TypeOfFieldIn(rel.ChildTableRef, summary.TargetFieldName, tables, relationships, 0),
                };
                if (summaryType is not null) map[summary.Name] = summaryType == "Reference" ? "Number" : summaryType;
            }
        return map;
    }

    /// <summary>The type of a field on a table as a rule condition sees it, following lookups of lookups; null when unknown.</summary>
    private static string? TypeOfFieldIn(string tableRef, string fieldName, IReadOnlyList<PblTable> tables, IReadOnlyList<PblRelationship> relationships, int depth)
    {
        if (depth > 6) return null;
        var table = tables.FirstOrDefault(t => t.LogicalRef == tableRef);
        if (table is null) return null;
        if (table.Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase)) is { } field)
            return string.Equals(field.TypeCode, "Formula", StringComparison.OrdinalIgnoreCase) ? field.ResultType ?? "Text" : field.TypeCode;
        foreach (var rel in relationships)
        {
            if (rel.ChildTableRef == tableRef)
            {
                if (string.Equals(rel.ReferenceFieldName, fieldName, StringComparison.OrdinalIgnoreCase)) return "Reference";
                if (rel.Lookups.FirstOrDefault(l => string.Equals(l.Name, fieldName, StringComparison.OrdinalIgnoreCase)) is { } lookup)
                    return TypeOfFieldIn(rel.ParentTableRef, lookup.SourceFieldName, tables, relationships, depth + 1);
            }
            if (rel.ParentTableRef == tableRef && rel.Summaries.FirstOrDefault(s => string.Equals(s.Name, fieldName, StringComparison.OrdinalIgnoreCase)) is { } summary)
                return summary.Function switch
                {
                    "Count" or "DistinctCount" => "Number",
                    "Exists" => "Boolean",
                    "CombinedText" => "Text",
                    _ => summary.TargetFieldName is null ? null : TypeOfFieldIn(rel.ChildTableRef, summary.TargetFieldName, tables, relationships, depth + 1) is { } t ? (t == "Reference" ? "Number" : t) : null,
                };
        }
        return null;
    }

    /// <summary>The whole condition tree as one PowerBase formula (expression mode). Returns null, with the reason, when a part of it has no
    /// formula form — a role condition, an unknown comparison, a value that cannot be written as a literal for the field's type.</summary>
    private static string? BuildRuleExpression(RuleGroup group, Dictionary<string, string> fieldNameByRef, Dictionary<string, string> roleNameByRef, IReadOnlyDictionary<string, string> fieldTypeByName, out string? problem)
    {
        problem = null;
        var parts = new List<string>();
        foreach (var item in group.Items)
        {
            string? part;
            if (item is RuleGroup sub)
            {
                if (sub.Items.Count == 0) continue;
                part = BuildRuleExpression(sub, fieldNameByRef, roleNameByRef, fieldTypeByName, out problem);
                if (part is null) return null;
                part = "(" + part + ")";
            }
            else
            {
                part = RuleLeafExpression((QblResourceNode)item, fieldNameByRef, roleNameByRef, fieldTypeByName, out problem);
                if (part is null) return null;
            }
            parts.Add(part);
        }

        if (parts.Count == 0) { problem = "no conditions"; return null; }
        return string.Join(group.Logic == "any" ? " or " : " and ", parts);
    }

    private static string? RuleLeafExpression(QblResourceNode node, Dictionary<string, string> fieldNameByRef, Dictionary<string, string> roleNameByRef, IReadOnlyDictionary<string, string> fieldTypeByName, out string? problem)
    {
        problem = null;
        if (IsType(node, FORMULA_CONDITION))
        {
            var formula = ResolveScalar(node, "Formula");
            if (string.IsNullOrWhiteSpace(formula)) { problem = "an empty formula condition"; return null; }
            return "(" + formula.Trim() + ")";
        }
        if (IsType(node, ROLE_CONDITION))
        {
            // The current user's roles joined with ";" on both ends, so "Admin" cannot match inside "Administrator".
            var roleRef = node.PropertyRef("Role")?["Role"];
            var roleComparison = ResolveScalar(node, "Comparison");
            if (roleRef is null || !roleNameByRef.TryGetValue(roleRef, out var roleName) || roleComparison is not ("IsInRole" or "IsNotInRole"))
            {
                problem = "a role that was not imported";
                return null;
            }
            if (roleName.Contains('"') || roleName.Contains(';')) { problem = "a role name with a quote or semicolon"; return null; }
            var hasRole = $"Contains(\";\" & Join(UserRoles(\"Name\"), \";\") & \";\", \";{roleName};\")";
            return roleComparison == "IsInRole" ? $"({hasRole})" : $"(not {hasRole})";
        }
        if (IsType(node, DAYS_AGO_CONDITION) || IsType(node, RELATIVE_CONDITION) || IsType(node, RANGE_CONDITION))
            return DateConditionExpression(node, fieldNameByRef, fieldTypeByName, out problem);
        if (!IsType(node, FIELD_CONDITION)) { problem = $"condition type '{node.Type}'"; return null; }

        var fieldRef = node.PropertyRef("Field")?["Field"];
        if (fieldRef is null || !fieldNameByRef.TryGetValue(fieldRef, out var fieldName)) { problem = "a field that was not created"; return null; }
        var comparison = ResolveScalar(node, "Comparison") ?? "";
        var value = ResolveScalar(node, "Value") ?? "";
        if (value.Contains('"')) { problem = "a value containing a double quote"; return null; }

        fieldTypeByName.TryGetValue(fieldName, out var type);
        var f = $"[{fieldName}]";
        var kind = type switch
        {
            "Number" or "Currency" or "Percent" or "Rating" => "number",
            "Boolean" or "Bool" => "bool",
            "Date" => "date",
            "Text" or "TextMultiLine" or "RichText" or "SingleSelect" or "Email" or "Phone" or "Url" => "text",
            _ => "other",
        };

        string? Literal()
        {
            switch (kind)
            {
                case "number":
                    return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? value : null;
                case "bool":
                    return value.ToLowerInvariant() switch { "true" or "1" or "checked" => "true", "false" or "0" or "unchecked" or "" => "false", _ => null };
                case "date":
                    return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                        ? $"ToDate(\"{d:yyyy-MM-dd}\")"
                        : null;
                default:
                    return $"\"{value}\"";
            }
        }

        // For anything but a known number/date/checkbox field the comparison is made on the text of the value, which compiles for every type —
        // except against another field, whose own type the comparison needs.
        var comparesWithField = node.PropertyRef("Value")?["Field"] is not null;
        var lhs = kind == "other" && !comparesWithField ? $"ToText({f})" : f;

        // Compared with another field: its value, not a literal.
        var otherFieldRef = node.PropertyRef("Value")?["Field"];
        var otherField = otherFieldRef is not null && fieldNameByRef.TryGetValue(otherFieldRef, out var otherName) ? $"[{otherName}]" : null;

        string Compare(string symbol)
        {
            var lit = otherField ?? Literal();
            return lit is null ? "" : $"{lhs} {symbol} {lit}";
        }

        var isTextLike = kind is "text" or "other";
        string? result = comparison.ToLowerInvariant() switch
        {
            // No value to compare with: the test is whether the field is blank (for a checkbox, "no value" is not a blank).
            "isequalto" => value.Length == 0 && !comparesWithField && kind != "bool" ? $"ToText({f}) = \"\"" : Compare("="),
            "isnotequalto" => value.Length == 0 && !comparesWithField && kind != "bool" ? $"ToText({f}) <> \"\"" : Compare("<>"),
            "contains" or "includes" => $"Contains(ToText({f}), \"{value}\")",
            "doesnotcontain" or "doesnotinclude" => $"not Contains(ToText({f}), \"{value}\")",
            "isgreaterthan" or "after" => Compare(">"),
            "isgreaterthanorequalto" or "onorafter" => Compare(">="),
            "islessthan" or "before" => Compare("<"),
            "islessthanorequalto" or "onorbefore" => Compare("<="),
            // A checkbox is tested as it is; any other field "has a true value" when it holds something.
            "hastruevalue" => kind == "bool" ? $"{f} = true" : $"ToText({f}) <> \"\"",
            "doesnothavetruevalue" => kind == "bool" ? $"{f} = false" : $"ToText({f}) = \"\"",
            "isempty" => $"ToText({f}) = \"\"",
            "isnotempty" => $"ToText({f}) <> \"\"",
            _ => null,
        };

        if (string.IsNullOrEmpty(result))
        {
            problem = $"comparison '{comparison}' on field '{fieldName}'";
            return null;
        }
        return "(" + result + ")";
    }

    /// <summary>A days-ago / relative-to-today / during-this-period condition as part of a rule formula (used when the rule also holds
    /// a condition the list cannot take).</summary>
    private static string? DateConditionExpression(QblResourceNode node, Dictionary<string, string> fieldNameByRef, IReadOnlyDictionary<string, string> fieldTypeByName, out string? problem)
    {
        problem = null;
        var fieldRef = node.PropertyRef("Field")?["Field"];
        if (fieldRef is null || !fieldNameByRef.TryGetValue(fieldRef, out var fieldName)) { problem = "a field that was not created"; return null; }
        fieldTypeByName.TryGetValue(fieldName, out var type);
        var date = type == "Date" ? $"[{fieldName}]" : $"ToDate([{fieldName}])";
        var comparison = ResolveScalar(node, "Comparison") ?? "";

        if (IsType(node, RANGE_CONDITION))
        {
            var (first, last) = ResolveScalar(node, "TimeFrame")?.ToLowerInvariant() switch
            {
                "week" => ("FirstDayOfWeek(Today())", "LastDayOfWeek(Today())"),
                "month" => ("FirstDayOfMonth(Today())", "LastDayOfMonth(Today())"),
                "year" => ("FirstDayOfYear(Today())", "LastDayOfYear(Today())"),
                "day" => ("Today()", "Today()"),
                _ => ((string?)null, (string?)null),
            };
            if (first is null || !string.Equals(ResolveScalar(node, "RelativeTo"), "This", StringComparison.OrdinalIgnoreCase)
                || comparison is not ("IsDuring" or "IsNotDuring"))
            {
                problem = $"comparison '{comparison}' on field '{fieldName}'";
                return null;
            }
            var inRange = $"({date} >= {first} and {date} <= {last})";
            return comparison == "IsDuring" ? inRange : $"(not {inRange})";
        }

        string? anchor;
        if (IsType(node, DAYS_AGO_CONDITION))
        {
            anchor = int.TryParse(ResolveScalar(node, "DaysAgo"), out var days)
                ? $"DateAdd(Today(), \"days\", {(-days).ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                : null;
        }
        else
        {
            anchor = ResolveScalar(node, "RelativeTo")?.ToLowerInvariant() switch
            {
                "today" => "Today()",
                "yesterday" => "DateAdd(Today(), \"days\", -1)",
                "tomorrow" => "DateAdd(Today(), \"days\", 1)",
                _ => null,
            };
        }
        var symbol = comparison.ToLowerInvariant() switch
        {
            "before" => "<", "after" => ">", "onorbefore" => "<=", "onorafter" => ">=", "isequalto" => "=", "isnotequalto" => "<>", _ => null,
        };
        if (anchor is null || symbol is null)
        {
            problem = $"comparison '{comparison}' on field '{fieldName}'";
            return null;
        }
        return $"({date} {symbol} {anchor})";
    }

    /// <summary>Only <c>QB::FormV2::Rule::Condition::Field</c> maps to PowerBase — confirmed
    /// real occurrences of <c>Condition::Role</c> (e.g. "IsInRole") have no equivalent, since
    /// PowerBase's real operator vocabulary (below) has no role-membership or date-relative
    /// operators at all.</summary>
    private static PblFormRuleCondition? ConvertFormRuleCondition(
        QblResourceNode conditionNode, Dictionary<string, string> fieldNameByRef, Dictionary<string, string> roleNameByRef, int order, string ruleName, string ruleRef, List<PblIssue> issues)
    {
        if (IsType(conditionNode, ROLE_CONDITION))
        {
            var roleRef = conditionNode.PropertyRef("Role")?["Role"];
            var comparison = ResolveScalar(conditionNode, "Comparison");
            if (roleRef is null || !roleNameByRef.TryGetValue(roleRef, out var roleName) || comparison is not ("IsInRole" or "IsNotInRole"))
            {
                issues.Add(Warning("QBL_FORM_RULE_ROLE_CONDITION_UNRESOLVED", $"Form rule '{ruleName}' has a role condition that cannot be resolved; skipped.", ruleRef));
                return null;
            }

            return new PblFormRuleCondition { Kind = "role", Operator = comparison == "IsInRole" ? "includes" : "notIncludes", Value = roleName, DisplayOrder = order };
        }

        if (!IsType(conditionNode, FIELD_CONDITION) && !IsType(conditionNode, DAYS_AGO_CONDITION) && !IsType(conditionNode, RELATIVE_CONDITION) && !IsType(conditionNode, RANGE_CONDITION))
        {
            issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_CONDITION", $"Form rule '{ruleName}' uses condition type '{conditionNode.Type}', which has no PowerBase equivalent.", ruleRef));
            return null;
        }

        var fieldRef = conditionNode.PropertyRef("Field")?["Field"];
        if (fieldRef is null || !fieldNameByRef.TryGetValue(fieldRef, out var fieldName))
        {
            issues.Add(Warning("QBL_FORM_RULE_CONDITION_FIELD_UNAVAILABLE", $"Form rule '{ruleName}' condition references a field not available in this import phase.", ruleRef));
            return null;
        }

        var qblComparison = ResolveScalar(conditionNode, "Comparison");
        var value = ResolveScalar(conditionNode, "Value");
        string? valueType = null, valueFieldName = null;
        string? op;

        if (IsType(conditionNode, DAYS_AGO_CONDITION))
        {
            // "Before 365 days ago": PowerBase compares with the date that many days back (or ahead, for a negative count).
            op = DateComparisonOperators.GetValueOrDefault(qblComparison ?? "");
            if (op is not null && int.TryParse(ResolveScalar(conditionNode, "DaysAgo"), out var daysAgo))
            {
                valueType = daysAgo >= 0 ? "pastDays" : "futureDays";
                value = Math.Abs(daysAgo).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else op = null;
        }
        else if (IsType(conditionNode, RELATIVE_CONDITION))
        {
            op = DateComparisonOperators.GetValueOrDefault(qblComparison ?? "") ?? (string.Equals(qblComparison, "IsEqualTo", StringComparison.OrdinalIgnoreCase) ? "eq" : null);
            valueType = ResolveScalar(conditionNode, "RelativeTo")?.ToLowerInvariant() switch { "today" => "today", "yesterday" => "yesterday", "tomorrow" => "tomorrow", _ => null };
            value = null;
            if (valueType is null) op = null;
        }
        else if (IsType(conditionNode, RANGE_CONDITION))
        {
            // "is during this week / month": PowerBase's own "during" comparison, counted from the current period.
            op = qblComparison switch { "IsDuring" => "during", "IsNotDuring" => "notDuring", _ => null };
            var unit = ResolveScalar(conditionNode, "TimeFrame")?.ToLowerInvariant();
            if (op is not null && string.Equals(ResolveScalar(conditionNode, "RelativeTo"), "This", StringComparison.OrdinalIgnoreCase) && unit is "day" or "week" or "month" or "quarter" or "year")
            {
                valueType = "duringCurrent";
                value = "1:" + unit;
            }
            else op = null;
        }
        else
        {
            // Compared with another field's live value (a reference, not a literal).
            if (conditionNode.PropertyRef("Value")?["Field"] is { } valueFieldRef && fieldNameByRef.TryGetValue(valueFieldRef, out var otherName))
            {
                valueType = "field";
                valueFieldName = otherName;
                value = null;
            }
            op = MapComparisonOperator(qblComparison);
            // Before / After / On or after… need a field to compare with here; against a literal date they are written as the rule's formula.
            if (op is null && valueFieldName is not null)
                op = DateComparisonOperators.GetValueOrDefault(qblComparison ?? "");
        }

        if (op is null)
        {
            issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_OPERATOR", $"Form rule '{ruleName}' uses comparison '{qblComparison}', which has no PowerBase equivalent.", ruleRef));
            return null;
        }

        return new PblFormRuleCondition
        {
            FieldName = fieldName,
            Operator = op,
            Value = value,
            ValueType = valueType,
            ValueFieldName = valueFieldName,
            DisplayOrder = order,
        };
    }

    private static readonly Dictionary<string, string> DateComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Before"] = "lt",
        ["After"] = "gt",
        ["OnOrBefore"] = "lte",
        ["OnOrAfter"] = "gte",
    };

    /// <summary>Confirmed real values: IsEqualTo, IsNotEqualTo, Contains (a second real export
    /// uses "Contains", not "Includes" - both accepted since neither sample contradicts the
    /// other). The rest of Quickbase's documented comparison vocabulary is mapped defensively but
    /// unverified against a real sample.</summary>
    private static string? MapComparisonOperator(string? qblComparison) => ComparisonOperators.GetValueOrDefault(qblComparison ?? "");

    private static readonly Dictionary<string, string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IsEqualTo"] = "eq",
        ["IsNotEqualTo"] = "ne",
        ["Includes"] = "contains",
        ["Contains"] = "contains",
        ["DoesNotInclude"] = "notContains",
        ["DoesNotContain"] = "notContains",
        ["StartsWith"] = "startsWith",
        ["EndsWith"] = "endsWith",
        ["IsEmpty"] = "isEmpty",
        ["IsNotEmpty"] = "isNotEmpty",
        ["IsGreaterThan"] = "gt",
        ["IsGreaterThanOrEqualTo"] = "gte",
        ["IsLessThan"] = "lt",
        ["IsLessThanOrEqualTo"] = "lte",
    };

    /// <summary>The form's pages as rule actions see them: the page's own ref on a multi-page form (null on a single-page one), and the
    /// sections on it.</summary>
    private sealed record PageTarget(string? PageRef, List<string> SectionRefs);

    /// <summary>The actions for a rule action aimed at a form page, or null when the action does not target a page (or the page is unknown).</summary>
    private static List<PblFormRuleAction>? PageTargetActions(QblResourceNode actionNode, IReadOnlyDictionary<string, PageTarget> pageTargetByRaw)
    {
        var actionType = MapActionType(actionNode.Type);
        if (actionType is null or "DisplayMessage" or "PreventSave") return null;
        var target = actionNode.PropertyRef(actionType == "ChangeValue" ? "Field" : "Target");
        if (target?["FormV2Page"] is not { } pageRef || !pageTargetByRaw.TryGetValue(pageRef, out var page)) return null;

        var value = ResolveScalar(actionNode, "Value");
        if (page.PageRef is not null)
            return [new PblFormRuleAction { ActionType = actionType, TargetType = "Page", TargetPageRef = page.PageRef, ActionValue = value }];
        return page.SectionRefs.Select(s => new PblFormRuleAction { ActionType = actionType, TargetType = "Section", TargetSectionRef = s, ActionValue = value }).ToList();
    }

    /// <summary>Confirmed real action types: Show, Require, and Change all map cleanly. Change
    /// sets a field's value — PowerBase's <c>FormRuleActionType.ChangeValue</c> covers the same
    /// case <c>ChangeLabel</c>/<c>SetColor</c> already do (a static <c>ActionValue</c> string
    /// applied at evaluation time); see <see cref="ConvertFormRuleAction"/> for the one real
    /// Change case that can't be represented (its value pointing at another field, not a
    /// literal). The rest of PowerBase's <c>FormRuleActionType</c> enum is mapped defensively.</summary>
    private static string? MapActionType(string qblActionType) => ActionTypes.GetValueOrDefault(qblActionType);

    // Compared without regard to case: the file spells it "MakeReadonly", the documentation "MakeReadOnly".
    private static readonly Dictionary<string, string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QB::FormV2::Rule::Action::Show"] = "Show",
        ["QB::FormV2::Rule::Action::Hide"] = "Hide",
        ["QB::FormV2::Rule::Action::MakeEditable"] = "Enable",
        ["QB::FormV2::Rule::Action::MakeReadOnly"] = "Disable",
        ["QB::FormV2::Rule::Action::Require"] = "Require",
        ["QB::FormV2::Rule::Action::Unrequire"] = "NotRequired",
        ["QB::FormV2::Rule::Action::SetLabel"] = "ChangeLabel",
        ["QB::FormV2::Rule::Action::Change"] = "ChangeValue",
        ["QB::FormV2::Rule::Action::SetColor"] = "SetColor",
        ["QB::FormV2::Rule::Action::DisplayMessage"] = "DisplayMessage",
        ["QB::FormV2::Rule::Action::AbortSave"] = "PreventSave",
    };

    /// <summary>Real actions target via <c>Target: !Ref{...}</c> with varying scope keys
    /// (<c>Field</c>, <c>FormV2Section</c>, <c>FormV2Column</c>) — a Field target resolves to
    /// the form ELEMENT displaying that field on *this* form (PowerBase actions target form
    /// elements, not raw fields), a Section target maps 1:1, and a Column target maps to
    /// PowerBase's Block (the closest structural analog, per the compatibility matrix).
    /// <c>Change</c> is shaped differently from every other action (confirmed real): its target
    /// field ref lives directly under <c>Field:</c>, not nested inside a <c>Target:</c> ref, and
    /// it always targets a Field (never a Section/Column). Its <c>Value:</c> is usually a literal
    /// (maps straight onto PowerBase's static <c>ActionValue</c>, same as ChangeLabel/SetColor)
    /// but a real, confirmed second shape sets it to <c>!Ref{Field: ...}</c> — "copy that other
    /// field's live value in" — which PowerBase's static ActionValue string can't represent
    /// (it would need to re-resolve at evaluation time, not just hold a fixed string); flagged
    /// rather than silently turned into the wrong, frozen-at-import-time value.</summary>
    private static PblFormRuleAction? ConvertFormRuleAction(
        QblResourceNode actionNode,
        Dictionary<string, string> fieldNameByRef,
        Dictionary<string, string> elementRefByFieldName,
        Dictionary<string, string> qualifiedSectionRefByRaw,
        Dictionary<string, string> qualifiedBlockRefByRaw,
        int order,
        string ruleName,
        string ruleRef,
        List<PblIssue> issues)
    {
        var actionType = MapActionType(actionNode.Type);
        if (actionType is null)
        {
            issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_ACTION", $"Form rule '{ruleName}' uses action type '{actionNode.Type}', which has no PowerBase equivalent.", ruleRef));
            return null;
        }

        var isChangeValue = actionType == "ChangeValue";

        // Messages and "block the save" need no target: the message is the point.
        if (actionType is "DisplayMessage" or "PreventSave")
        {
            var message = ResolveScalar(actionNode, "Message");
            var messageAction = new PblFormRuleAction { ActionType = actionType, TargetType = "Field", ActionValue = message, DisplayOrder = order };
            if (actionType == "DisplayMessage")
            {
                messageAction.MessageType = ResolveScalar(actionNode, "MessageType") switch { "Modal" => "modal", "Field" => "field", _ => "notification" };
                messageAction.MessageSeverity = ResolveScalar(actionNode, "MessageLevel") switch { "Success" => "success", "Warning" => "warning", "Error" => "error", _ => "info" };

                if (messageAction.MessageType == "field")
                {
                    var fieldTarget = actionNode.PropertyRef("FieldTarget")?["Field"];
                    if (fieldTarget is not null && fieldNameByRef.TryGetValue(fieldTarget, out var targetName) && elementRefByFieldName.TryGetValue(targetName, out var targetElement))
                    {
                        messageAction.TargetElementRef = targetElement;
                    }
                    else
                    {
                        messageAction.MessageType = "notification";
                        issues.Add(Warning("QBL_FORM_RULE_MESSAGE_FIELD_UNAVAILABLE", $"Form rule '{ruleName}': the message's field is not on this form; shown as a notification instead.", ruleRef));
                    }
                }
            }
            return messageAction;
        }

        // "Set field to another field's live value" — PowerBase copies it at run time.
        string? sourceFieldName = null;
        if (isChangeValue && actionNode.Properties.TryGetValue("Value", out var rawChangeValue) && rawChangeValue is QblRef changeValueRef)
        {
            var sourceRef = changeValueRef["Field"];
            if (sourceRef is null || !fieldNameByRef.TryGetValue(sourceRef, out sourceFieldName))
            {
                issues.Add(Warning("QBL_FORM_RULE_ACTION_VALUE_UNRESOLVABLE", $"Form rule '{ruleName}' action copies another field's value, but that field is not available; skipped.", ruleRef));
                return null;
            }
        }

        var target = actionNode.PropertyRef(isChangeValue ? "Field" : "Target");
        if (target is null)
        {
            issues.Add(Warning("QBL_FORM_RULE_ACTION_MISSING_TARGET", $"Form rule '{ruleName}' action has no resolvable target; skipped.", ruleRef));
            return null;
        }

        string targetType;
        string? targetElementRef = null, targetSectionRef = null, targetBlockRef = null;

        if (target["Field"] is { } fieldRef)
        {
            // elementRefByFieldName is keyed by resolved field Name, not the raw QBL ref.
            if (!fieldNameByRef.TryGetValue(fieldRef, out var fieldName) || !elementRefByFieldName.TryGetValue(fieldName, out targetElementRef))
            {
                issues.Add(Warning("QBL_FORM_RULE_ACTION_TARGET_UNAVAILABLE", $"Form rule '{ruleName}' action targets a field with no form element on this form; skipped.", ruleRef));
                return null;
            }
            targetType = "Field";
        }
        else if (target["FormV2Section"] is { } sectionRef)
        {
            if (!qualifiedSectionRefByRaw.TryGetValue(sectionRef, out targetSectionRef))
            {
                issues.Add(Warning("QBL_FORM_RULE_ACTION_TARGET_UNAVAILABLE", $"Form rule '{ruleName}' action targets a section not on this form; skipped.", ruleRef));
                return null;
            }
            targetType = "Section";
        }
        else if (target["FormV2Column"] is { } columnRef)
        {
            if (!qualifiedBlockRefByRaw.TryGetValue(columnRef, out targetBlockRef))
            {
                issues.Add(Warning("QBL_FORM_RULE_ACTION_TARGET_UNAVAILABLE", $"Form rule '{ruleName}' action targets a column not on this form; skipped.", ruleRef));
                return null;
            }
            targetType = "Block";
        }
        else
        {
            issues.Add(Warning("QBL_FORM_RULE_ACTION_TARGET_UNAVAILABLE", $"Form rule '{ruleName}' action target scope has no PowerBase equivalent; skipped.", ruleRef));
            return null;
        }

        return new PblFormRuleAction
        {
            ActionType = actionType,
            TargetType = targetType,
            TargetElementRef = targetElementRef,
            TargetSectionRef = targetSectionRef,
            TargetBlockRef = targetBlockRef,
            ActionValue = sourceFieldName is null ? ResolveScalar(actionNode, "Value") : null,
            ValueSource = sourceFieldName is null ? null : "field",
            SourceFieldName = sourceFieldName,
            DisplayOrder = order,
        };
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    private static string InferFieldName(string logicalRef, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        var stripped = logicalRef.StartsWith("$Field_", StringComparison.Ordinal) ? logicalRef["$Field_".Length..] : logicalRef.TrimStart('$');
        return stripped.Replace('_', ' ');
    }

    private static string? ResolveScalar(QblResourceNode node, string propertyName)
    {
        if (!node.Properties.TryGetValue(propertyName, out var raw))
            return null;

        return raw switch
        {
            null => null,
            QblBadRef or QblRef => null,
            _ => raw.ToString(),
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>QBL resource keys (e.g. <c>$Field_Name</c>) are only unique within their own
    /// immediate parent's map — Quickbase auto-names them from the label, so the same local key
    /// legitimately recurs across different tables/forms/sections in real exports (confirmed: a
    /// real production export produced the exact same local field key across dozens of unrelated
    /// tables). PBL's LogicalRef, by contrast, must be unique across the *entire* document
    /// (<c>PblValidator</c> checks this with one shared set for every construct). Qualifying by
    /// the already-qualified parent ref at each level guarantees that.</summary>
    /// <summary>An expression of the given result type that evaluates to blank; null when no such constant exists.</summary>
    /// <summary>True for a formula that is only comment lines, or only "var ...;" declarations with nothing after them:
    /// Quickbase evaluates these to blank, and they cannot compile.</summary>
    public static bool HasNoResultLine(string formula)
    {
        var lines = formula.Split((char)10)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))
            .ToList();
        if (lines.Count == 0) return true;
        var code = string.Join(" ", lines);
        return code.StartsWith("var ", StringComparison.OrdinalIgnoreCase) && code.EndsWith(';');
    }

    internal static string? BlankFormulaFor(string resultType) => resultType switch
    {
        "Text" => "\"\"",
        "Number" => "ToNumber(\"\")",
        "Bool" => "false",
        "Date" => "ToDate(\"\")",
        "Time" => "ToTimeOfDay(\"\")",
        "DateTime" => "ToTimestamp(\"\")",
        "User" => "ToUser(\"\")",
        _ => null,
    };

    private static string Qualify(string parentRef, string localRef) => $"{parentRef}::{localRef}";

    private static PblIssue Warning(string code, string message, string? elementRef) =>
        new() { Severity = PblIssueSeverity.Warning, Code = code, Message = message, ElementRef = elementRef };
}
