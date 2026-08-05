using System.Text.RegularExpressions;
using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Qbl;

public sealed class QblConversionResult
{
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
    public static QblConversionResult Convert(QblDocument qbl)
    {
        var issues = new List<PblIssue>();

        var appEntry = qbl.Resources.FirstOrDefault(kv => kv.Value.Type == "QB::Application");
        if (appEntry.Value is null)
        {
            issues.Add(new PblIssue
            {
                Severity = PblIssueSeverity.Error,
                Code = "QBL_MISSING_APPLICATION",
                Message = "QBL document has no QB::Application resource.",
            });
            return new QblConversionResult { Document = new PblDocument(), Issues = issues };
        }

        var (appRef, appNode) = (appEntry.Key, appEntry.Value);

        var pblApp = new PblApp
        {
            LogicalRef = appRef,
            Name = ResolveScalar(appNode, "Name") ?? "",
            Description = NullIfBlank(ResolveScalar(appNode, "Description")),
            Icon = NullIfBlank(ResolveScalar(appNode, "AppIcon")),
            Color = NullIfBlank(ResolveScalar(appNode, "AppColor")),
        };

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
                Icon = NullIfBlank(ResolveScalar(tableNode, "TableIconName")),
                Description = NullIfBlank(ResolveScalar(tableNode, "Description")),
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

                if (!seenFieldNames.Add(field.Name))
                {
                    issues.Add(Warning("QBL_DUPLICATE_FIELD_NAME", $"Field '{field.Name}' is already used on table '{table.Name}'; only the first is imported.", fieldRef));
                    continue;
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
                    relationships.Add(skeleton);
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

        // Pass C.
        var forms = new List<PblForm>();
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
            foreach (var (reportRef, reportNode) in tableNode.ChildMap("Reports"))
            {
                var report = ConvertReport(table.LogicalRef, reportRef, reportNode, fieldNameByRef, issues);
                if (report is null)
                    continue;

                if (!seenReportNames.Add(report.Name))
                {
                    issues.Add(Warning("QBL_DUPLICATE_REPORT_NAME", $"Report '{report.Name}' is already used on table '{table.Name}'; only the first is imported.", reportRef));
                    continue;
                }

                table.Reports.Add(report);
            }

            foreach (var (formRef, formV2Node) in GetFormV2Nodes(tableNode))
                forms.Add(ConvertForm(formRef, formV2Node, table.LogicalRef, fieldNameByRef, issues));
        }

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

                var permission = ConvertTablePermission(table.LogicalRef, table.Name, roleRefKey, perms, issues);
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

        var document = new PblDocument { App = pblApp, Tables = tables, Relationships = relationships, Forms = forms, Roles = roles };
        return new QblConversionResult { Document = document, Issues = issues };
    }

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

    private static readonly Regex QuickbaseTableIdRef = new(@"\[_DBID_(?<slug>[A-Za-z0-9_]+)\]", RegexOptions.Compiled);

    /// <summary>Applies <paramref name="replace"/> only to the parts of <paramref name="source"/>
    /// outside double-quoted strings. Real formulas embed <c>[_DBID_X]</c> inside URL string
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
            if (source[i] != '"')
            {
                i++;
                continue;
            }

            // Code since the last string ends here — rewrite it, then copy the string verbatim.
            result.Append(pattern.Replace(source[segmentStart..i], replace));

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
        string tableRef, string tableName, string roleRefKey, Dictionary<string, object?> perms, List<PblIssue> issues)
    {
        return new PblTablePermission
        {
            TableRef = tableRef,
            ViewScope = ParseRecordAccess(perms, "CanViewRecords", tableName, roleRefKey, issues),
            ModifyScope = ParseRecordAccess(perms, "CanModifyRecords", tableName, roleRefKey, issues),
            CanAdd = GetBool(perms, "CanAddRecords"),
            CanDelete = GetBool(perms, "CanDeleteRecords"),
            CanSaveSharedReports = GetBool(perms, "CanSaveCommonReports"),
            CanEditFieldProperties = GetBool(perms, "CanEditFieldProperties"),
        };
    }

    private static string ParseRecordAccess(Dictionary<string, object?> perms, string key, string tableName, string roleRefKey, List<PblIssue> issues)
    {
        if (!perms.TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> access)
            return "None";

        var when = access.TryGetValue("When", out var w) ? w?.ToString() : null;
        var hasCriteria = access.TryGetValue("CustomAccessCriteria", out var crit) && crit is string s && !string.IsNullOrWhiteSpace(s);

        if (hasCriteria)
        {
            issues.Add(Warning(
                "QBL_ROLE_ACCESS_CRITERIA_UNSUPPORTED",
                $"Table '{tableName}' has a custom access filter for role '{roleRefKey}' with no PowerBase translation (Quickbase's own query-criteria syntax); importing without the filter.",
                roleRefKey));
        }

        return when switch
        {
            "Always" => "AllRecords",
            "Never" => "None",
            _ => WarnUnknownAccessWhen(when, key, tableName, roleRefKey, issues),
        };
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

    private sealed record ReferenceFieldInfo(string FieldRef, string Name, string? Label, bool IsRequired);
    private sealed record LookupFieldInfo(string FieldRef, string Name, string? Label, string? TargetFieldRef);
    private sealed record SummaryFieldInfo(string FieldRef, string Name, string? Label, string Function, string? TargetFieldRef);

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
                collector.References[relRef] = new ReferenceFieldInfo(fieldRef, name, NullIfBlank(label), fieldNode.PropertyBool("IsRequired"));
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
                list.Add(new LookupFieldInfo(fieldRef, name, NullIfBlank(label), targetFieldRef));
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
                slist.Add(new SummaryFieldInfo(fieldRef, name, NullIfBlank(label), function, targetFieldRef));
                return;
            }
            default:
                // QB::Field::ReportLink (auto-created by CreateRelationshipCommandHandler) and
                // QB::Field::ReferenceProxy (auto-derived from the first Lookup) need nothing
                // collected here.
                return;
        }
    }

    /// <summary>PowerBase's SummaryFunctions is {Count, Exists, Sum, Avg, Min, Max}; QBL's is
    /// {Count, Total, Average, Maximum, Minimum, StdDeviation, CombinedText, DistinctCount}.
    /// StdDeviation/CombinedText/DistinctCount have no PowerBase equivalent — flagged by the
    /// caller, never approximated into a different function.</summary>
    private static string? MapSummaryFunction(string qblFunction) => qblFunction switch
    {
        "Count" => "Count",
        "Total" => "Sum",
        "Average" => "Avg",
        "Maximum" => "Max",
        "Minimum" => "Min",
        "AnyRelatedRecords" => "Exists",
        _ => null,
    };

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
                if (childSeenNames is not null && !childSeenNames.Add(lookup.Name))
                {
                    issues.Add(Warning("QBL_DUPLICATE_FIELD_NAME", $"Lookup field '{lookup.Name}' is already used on table '{tableNameByRef.GetValueOrDefault(rel.ChildTableRef, rel.ChildTableRef)}'; skipped.", lookup.FieldRef));
                    pendingLookups.RemoveAt(i);
                    progress = true;
                    continue;
                }

                rel.Lookups.Add(new PblLookupField { LogicalRef = lookup.FieldRef, Name = lookup.Name, Label = lookup.Label, SourceFieldName = sourceFieldName, SourceSubField = lookupTargetSubField });
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
                if (parentSeenNames is not null && !parentSeenNames.Add(summary.Name))
                {
                    issues.Add(Warning("QBL_DUPLICATE_FIELD_NAME", $"Summary field '{summary.Name}' is already used on table '{tableNameByRef.GetValueOrDefault(rel.ParentTableRef, rel.ParentTableRef)}'; skipped.", summary.FieldRef));
                    pendingSummaries.RemoveAt(i);
                    progress = true;
                    continue;
                }

                rel.Summaries.Add(new PblSummaryField { LogicalRef = summary.FieldRef, Name = summary.Name, Label = summary.Label, Function = summary.Function, TargetFieldName = targetFieldName, TargetSubField = summaryTargetSubField });
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
            return new PblField
            {
                LogicalRef = Qualify(tableRef, fieldRef),
                Name = name,
                Label = NullIfBlank(label),
                TypeCode = scalarTypeCode,
                IsRequired = fieldNode.PropertyBool("IsRequired"),
            };
        }

        if (QblFieldTypeMap.TryMapFormulaResultType(type, out var resultType))
        {
            var formula = ResolveScalar(fieldNode, "Formula");
            if (string.IsNullOrWhiteSpace(formula))
            {
                issues.Add(Warning("QBL_EMPTY_FORMULA", $"Formula field '{name}' has no expression; skipped.", fieldRef));
                return null;
            }

            return new PblField
            {
                LogicalRef = Qualify(tableRef, fieldRef),
                Name = name,
                Label = NullIfBlank(label),
                TypeCode = "Formula",
                ResultType = resultType,
                FormulaExpression = formula,
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
        List<PblIssue> issues)
    {
        var reportType = reportNode.Type switch
        {
            "QB::Report::Table" => "Table",
            "QB::Report::Summary" => "Summary",
            "QB::Report::GridEdit" => "GridEdit",
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
                report.SortFields.Add(new PblSortSpec { FieldName = fieldName, Desc = descending });
            }
        }

        return report;
    }

    /// <summary>QBL's <c>Columns</c> property is either an explicit list of field <c>!Ref</c>s,
    /// or the literal string <c>"Default"</c> meaning "show every field in the table's own order".
    /// PowerBase expresses that same idea as a report with *no* columns listed — that's exactly
    /// how its own seeded "List All" is defined, and the runtime expands an empty column list to
    /// every reportable field. Leaving it empty is therefore both the more faithful mapping and a
    /// sturdier one: enumerating every field instead would make the table's main view depend on
    /// all of them importing successfully, so one formula that failed to translate would take
    /// "List All" down with it.</summary>
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

    private static PblForm ConvertForm(string formRef, QblResourceNode formNode, string tableRef, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues)
    {
        var qualifiedFormRef = Qualify(tableRef, formRef);
        var form = new PblForm
        {
            LogicalRef = qualifiedFormRef,
            TableRef = tableRef,
            Name = ResolveScalar(formNode, "Name") ?? formRef,
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

        // QBL's Page tier has no PowerBase equivalent (PowerBase forms are Section→Block→Element,
        // confirmed no Page level) — flatten each Page's Sections into the form in document order.
        // Confirmed real data: a single form can carry two distinct sections with the identical
        // Title (e.g. two independently-added "Data Import details" sections) - Quickbase never
        // enforces section-name uniqueness, but PowerBase's DUPLICATE_FORM_SECTION_NAME check
        // does. Same class of issue as duplicate report/field names above - skip and flag rather
        // than hard-blocking the whole import.
        var seenSectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, pageNode) in formNode.ChildMap("Pages"))
        {
            foreach (var (sectionRef, sectionNode) in pageNode.ChildMap("Sections"))
            {
                var section = ConvertFormSection(sectionRef, sectionNode, qualifiedFormRef, fieldNameByRef, qualifiedBlockRefByRaw, issues);
                if (section is null)
                    continue;

                if (!seenSectionNames.Add(section.Name))
                {
                    issues.Add(Warning("QBL_DUPLICATE_FORM_SECTION_NAME", $"Section '{section.Name}' is already used on form '{form.Name}'; only the first is imported.", sectionRef));
                    continue;
                }

                form.Sections.Add(section);
                qualifiedSectionRefByRaw[sectionRef] = section.LogicalRef;
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
            var rule = ConvertFormRule(ruleRef, ruleNode, qualifiedFormRef, fieldNameByRef, elementRefByFieldName, qualifiedSectionRefByRaw, qualifiedBlockRefByRaw, issues);
            if (rule is not null)
                form.Rules.Add(rule);
        }

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
    private static List<PblFormElement> ConvertFormElements(string elementRef, QblResourceNode elementNode, string qualifiedParentRef, Dictionary<string, string> fieldNameByRef, List<PblIssue> issues)
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
                    issues.Add(Warning("QBL_FORM_ELEMENT_FIELD_UNAVAILABLE", $"Form element '{elementRef}' references a field not available in this import phase; skipped.", elementRef));
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
                issues.Add(Warning("QBL_FORM_ELEMENT_GROUP_FLATTENED", $"Form element group '{elementRef}' has no PowerBase equivalent; its fields were imported individually, ungrouped.", elementRef));
                foreach (var (childRef, childNode) in elementNode.ChildMap("Elements"))
                    result.AddRange(ConvertFormElements(childRef, childNode, qualifiedElementRef, fieldNameByRef, issues));
                return result;
            }
            case "QB::FormV2::Element::Report":
            {
                issues.Add(Warning("QBL_UNSUPPORTED_FORM_ELEMENT", $"Embedded report form element '{elementRef}' is not yet supported.", elementRef));
                return result;
            }
            default:
                issues.Add(Warning("QBL_UNKNOWN_FORM_ELEMENT_TYPE", $"Form element type '{elementNode.Type}' is not recognized by this import phase.", elementRef));
                return result;
        }
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
        Dictionary<string, string> elementRefByFieldName,
        Dictionary<string, string> qualifiedSectionRefByRaw,
        Dictionary<string, string> qualifiedBlockRefByRaw,
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

        // A rule with more than one top-level condition, or a top-level condition that isn't a
        // Group, has no confirmed real-world shape and no clean PowerBase mapping — flagged
        // rather than guessed at.
        if (whenList.Count != 1 || whenList[0] is not QblResourceNode topGroup || topGroup.Type != "QB::FormV2::Rule::Condition::Group")
        {
            issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_CONDITION_STRUCTURE", $"Form rule '{name}' has a condition structure with no PowerBase equivalent; skipped.", ruleRef));
            return null;
        }

        rule.ConditionLogic = string.Equals(ResolveScalar(topGroup, "TrueWhen"), "Any", StringComparison.OrdinalIgnoreCase) ? "any" : "all";

        var innerConditions = topGroup.Children.TryGetValue("When", out var innerRaw) && innerRaw is List<object?> innerList ? innerList : [];

        // "Formula is true" escape hatch: PowerBase's own IsExpressionMode/ExpressionText is a
        // real, confirmed mapping target for this — not an approximation — but only when it's
        // the rule's *sole* condition, since PowerBase can't mix expression-mode with discrete
        // Field conditions in the same rule.
        if (innerConditions.Count == 1 && innerConditions[0] is QblResourceNode soleCondition && soleCondition.Type == "QB::FormV2::Rule::Condition::FormulaIsTrue")
        {
            var formula = ResolveScalar(soleCondition, "Formula");
            if (string.IsNullOrWhiteSpace(formula))
            {
                issues.Add(Warning("QBL_FORM_RULE_EMPTY_FORMULA_CONDITION", $"Form rule '{name}' has an empty formula condition; skipped.", ruleRef));
                return null;
            }

            rule.IsExpressionMode = true;
            rule.ExpressionText = formula;
        }
        else
        {
            var order = 0;
            foreach (var conditionRaw in innerConditions)
            {
                if (conditionRaw is not QblResourceNode conditionNode)
                    continue;

                var condition = ConvertFormRuleCondition(conditionNode, fieldNameByRef, order, name, ruleRef, issues);
                if (condition is null)
                    continue;

                rule.Conditions.Add(condition);
                order++;
            }

            if (rule.Conditions.Count == 0)
            {
                issues.Add(Warning("QBL_FORM_RULE_NO_SUPPORTED_CONDITIONS", $"Form rule '{name}' has no conditions PowerBase can represent; skipped.", ruleRef));
                return null;
            }
        }

        var actionsRaw = ruleNode.Children.TryGetValue("Actions", out var a) && a is List<object?> actionsList ? actionsList : [];
        var actionOrder = 0;
        foreach (var actionRaw in actionsRaw)
        {
            if (actionRaw is not QblResourceNode actionNode)
                continue;

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

    private static string? MapRunTrigger(string? qblRunOn) => qblRunOn switch
    {
        "RecordOpenForEdit" => "EditOrAdd",
        "RecordSave" => "Save",
        "RecordSaveAfterValidating" => "SaveAfterValidating",
        "RecordOpenSaveOrChange" => "AnyChange",
        _ => null,
    };

    /// <summary>Only <c>QB::FormV2::Rule::Condition::Field</c> maps to PowerBase — confirmed
    /// real occurrences of <c>Condition::Role</c> (e.g. "IsInRole") have no equivalent, since
    /// PowerBase's real operator vocabulary (below) has no role-membership or date-relative
    /// operators at all.</summary>
    private static PblFormRuleCondition? ConvertFormRuleCondition(
        QblResourceNode conditionNode, Dictionary<string, string> fieldNameByRef, int order, string ruleName, string ruleRef, List<PblIssue> issues)
    {
        if (conditionNode.Type != "QB::FormV2::Rule::Condition::Field")
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
        var op = MapComparisonOperator(qblComparison);
        if (op is null)
        {
            issues.Add(Warning("QBL_UNSUPPORTED_FORM_RULE_OPERATOR", $"Form rule '{ruleName}' uses comparison '{qblComparison}', which has no PowerBase equivalent.", ruleRef));
            return null;
        }

        return new PblFormRuleCondition
        {
            FieldName = fieldName,
            Operator = op,
            Value = ResolveScalar(conditionNode, "Value"),
            DisplayOrder = order,
        };
    }

    /// <summary>Confirmed real values: IsEqualTo, IsNotEqualTo, Contains (a second real export
    /// uses "Contains", not "Includes" - both accepted since neither sample contradicts the
    /// other). The rest of Quickbase's documented comparison vocabulary is mapped defensively but
    /// unverified against a real sample.</summary>
    private static string? MapComparisonOperator(string? qblComparison) => qblComparison switch
    {
        "IsEqualTo" => "eq",
        "IsNotEqualTo" => "ne",
        "Includes" or "Contains" => "contains",
        "DoesNotInclude" or "DoesNotContain" => "notContains",
        "StartsWith" => "startsWith",
        "EndsWith" => "endsWith",
        "IsEmpty" => "isEmpty",
        "IsNotEmpty" => "isNotEmpty",
        "IsGreaterThan" => "gt",
        "IsGreaterThanOrEqualTo" => "gte",
        "IsLessThan" => "lt",
        "IsLessThanOrEqualTo" => "lte",
        _ => null,
    };

    /// <summary>Confirmed real action types: Show, Require, and Change all map cleanly. Change
    /// sets a field's value — PowerBase's <c>FormRuleActionType.ChangeValue</c> covers the same
    /// case <c>ChangeLabel</c>/<c>SetColor</c> already do (a static <c>ActionValue</c> string
    /// applied at evaluation time); see <see cref="ConvertFormRuleAction"/> for the one real
    /// Change case that can't be represented (its value pointing at another field, not a
    /// literal). The rest of PowerBase's <c>FormRuleActionType</c> enum is mapped defensively.</summary>
    private static string? MapActionType(string qblActionType) => qblActionType switch
    {
        "QB::FormV2::Rule::Action::Show" => "Show",
        "QB::FormV2::Rule::Action::Hide" => "Hide",
        "QB::FormV2::Rule::Action::MakeEditable" => "Enable",
        "QB::FormV2::Rule::Action::MakeReadOnly" => "Disable",
        "QB::FormV2::Rule::Action::Require" => "Require",
        "QB::FormV2::Rule::Action::Unrequire" => "NotRequired",
        "QB::FormV2::Rule::Action::SetLabel" => "ChangeLabel",
        "QB::FormV2::Rule::Action::Change" => "ChangeValue",
        "QB::FormV2::Rule::Action::SetColor" => "SetColor",
        "QB::FormV2::Rule::Action::DisplayMessage" => "DisplayMessage",
        "QB::FormV2::Rule::Action::AbortSave" => "PreventSave",
        _ => null,
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

        if (isChangeValue && actionNode.Properties.TryGetValue("Value", out var rawChangeValue) && rawChangeValue is QblRef)
        {
            issues.Add(Warning("QBL_FORM_RULE_ACTION_VALUE_UNRESOLVABLE", $"Form rule '{ruleName}' action sets a field's value to another field's live value, which PowerBase's static action value can't represent; skipped.", ruleRef));
            return null;
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
            ActionValue = ResolveScalar(actionNode, "Value"),
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
    private static string Qualify(string parentRef, string localRef) => $"{parentRef}::{localRef}";

    private static PblIssue Warning(string code, string message, string? elementRef) =>
        new() { Severity = PblIssueSeverity.Warning, Code = code, Message = message, ElementRef = elementRef };
}
