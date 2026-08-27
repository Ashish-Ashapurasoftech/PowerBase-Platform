using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Import.Pbl;

public enum PblIssueSeverity
{
    /// <summary>Blocks import.</summary>
    Error,
    /// <summary>Import proceeds; the affected element is skipped/flagged rather than silently dropped.</summary>
    Warning,
}

public sealed class PblIssue
{
    public PblIssueSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ElementRef { get; init; }
}

public sealed class PblValidationResult
{
    public List<PblIssue> Issues { get; init; } = [];
    public bool IsValid => Issues.All(i => i.Severity != PblIssueSeverity.Error);
    public IEnumerable<PblIssue> Errors => Issues.Where(i => i.Severity == PblIssueSeverity.Error);
    public IEnumerable<PblIssue> Warnings => Issues.Where(i => i.Severity == PblIssueSeverity.Warning);
}

/// <summary>
/// Structural validation for a PBL document ahead of import: unique/resolvable logical refs,
/// required names, and supported field types. Phase 1 only creates scalar fields — any field
/// whose TypeCode isn't in <see cref="SupportedFieldTypeCodes"/> is flagged as a warning (skipped
/// on import, never silently dropped) rather than failing the whole import.
/// </summary>
public class PblValidator
{
    /// <summary>PowerBase FieldTypeCode values this import phase can create. Matches the
    /// scalar set defined for Phase 1 (see QBL_COMPATIBILITY_MATRIX.md), plus File/User/MultiUser
    /// - these have no type-specific Settings (no
    /// <see cref="PowerBase.Application.Fields.Settings.IFieldSettingsValidator"/> registered for
    /// them) and are otherwise ordinary physical-column field types, fully wired
    /// in <c>core.FieldType</c> and <c>CreateFieldCommandHandler</c> already; QBL's own
    /// <c>QB::Field::FileAttachment</c>/<c>QB::Field::User</c>/<c>QB::Field::ListUser</c> were
    /// already mapped to them in <c>QblFieldTypeMap.ScalarTypes</c> - this allowlist was the only
    /// thing rejecting them.</summary>
    public static readonly IReadOnlyCollection<string> SupportedFieldTypeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TextMultiLine", "RichText", "SingleSelect", "MultiSelect",
        "Number", "Currency", "Percent", "Rating",
        "Date", "DateTime", "Time", "Duration",
        "Boolean", "Email", "Phone", "Url", "Address",
        "File", "User", "MultiUser",
    };

    /// <summary>TypeCode for a formula field. Handled separately from
    /// <see cref="SupportedFieldTypeCodes"/> because it requires a second creation pass
    /// (its expression is validated against the table's already-created fields).</summary>
    public const string FormulaTypeCode = "Formula";

    /// <summary>True for any field type this import phase will attempt to create — either
    /// directly (<see cref="SupportedFieldTypeCodes"/>) or via the formula translation pass.</summary>
    public static bool IsCreatableFieldType(string typeCode) =>
        SupportedFieldTypeCodes.Contains(typeCode) || string.Equals(typeCode, FormulaTypeCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every table gets these fields auto-seeded by <c>IAppSeeder</c> (see
    /// <c>AppSeeder.cs</c>'s <c>systemFieldDefs</c>) regardless of what's declared in this PBL
    /// document's own Fields list — a report column, form element, or form rule condition can
    /// validly reference one of these even though it's never a standalone PblField entry.</summary>
    private static readonly string[] ImplicitSystemFieldNames =
    [
        "Record ID#",
        "Date Created",
        "Date Modified",
        "Record Owner",
        "Last Modified By",
    ];

    public PblValidationResult Validate(PblDocument document)
    {
        var issues = new List<PblIssue>();

        if (document.App is null)
        {
            issues.Add(Error("MISSING_APP", "PBL document must define an App."));
            return new PblValidationResult { Issues = issues };
        }

        // Ordinal, not OrdinalIgnoreCase: a LogicalRef is an internal identifier, not a
        // human-facing name, and QBL-derived refs are literal YAML resource keys — Quickbase's
        // own auto-naming can legitimately produce two distinct keys differing only in case
        // (confirmed real: "$Field_Other_Reason_for_release_of" and
        // "$Field_Other_reason_for_release_of" are two different fields — a Checkbox and its
        // companion Text field). Case-insensitive comparison collided them as duplicates.
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);

        ValidateRef(document.App.LogicalRef, "App", issues, seenRefs);
        if (string.IsNullOrWhiteSpace(document.App.Name))
            issues.Add(Error("MISSING_NAME", "App is missing a Name.", document.App.LogicalRef));

        if (document.Tables is null || document.Tables.Count == 0)
        {
            issues.Add(Warning("NO_TABLES", "PBL document defines no tables; the app will be created empty.", document.App.LogicalRef));
            return new PblValidationResult { Issues = issues };
        }

        var seenTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableRefs = new HashSet<string>(document.Tables.Select(t => t.LogicalRef), StringComparer.OrdinalIgnoreCase);

        // Field names that will exist on each table once import completes — seeded with
        // scalar/formula fields, then enriched by ValidateRelationships with the
        // Reference/Lookup/Summary field names a valid relationship will also create. Reports
        // (validated last) need the full picture to correctly resolve columns that reference
        // relationship-derived fields.
        var fieldNamesByTableRef = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in document.Tables)
        {
            ValidateRef(table.LogicalRef, "Table", issues, seenRefs);

            if (string.IsNullOrWhiteSpace(table.Name))
                issues.Add(Error("MISSING_NAME", "Table is missing a Name.", table.LogicalRef));
            else if (!seenTableNames.Add(table.Name))
                issues.Add(Error("DUPLICATE_TABLE_NAME", $"Table name '{table.Name}' is used more than once.", table.LogicalRef));

            var tableFieldNames = ValidateFields(table, issues, seenRefs);
            foreach (var systemFieldName in ImplicitSystemFieldNames)
                tableFieldNames.Add(systemFieldName);
            fieldNamesByTableRef[table.LogicalRef] = tableFieldNames;
        }

        // A relationship's Lookup/Summary can reference a field created by a *different*
        // relationship (a chained lookup/summary - confirmed real Quickbase pattern, e.g. a
        // Summary counting through a Reference field that is itself another relationship's
        // Lookup). Relationship processing order isn't guaranteed to respect that dependency, so
        // pre-populate the full set of names every relationship will contribute before running
        // any cross-reference checks - otherwise a relationship validated before the one it
        // depends on would be falsely flagged as referencing an unknown field.
        var fullFieldNamesByTableRef = fieldNamesByTableRef.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase));
        foreach (var rel in document.Relationships ?? [])
        {
            if (!string.IsNullOrWhiteSpace(rel.ReferenceFieldName) && fullFieldNamesByTableRef.TryGetValue(rel.ChildTableRef, out var childSet))
                childSet.Add(rel.ReferenceFieldName);
            foreach (var lookup in rel.Lookups ?? [])
            {
                if (!string.IsNullOrWhiteSpace(lookup.Name) && fullFieldNamesByTableRef.TryGetValue(rel.ChildTableRef, out var lookupChildSet))
                    lookupChildSet.Add(lookup.Name);
            }
            foreach (var summary in rel.Summaries ?? [])
            {
                if (!string.IsNullOrWhiteSpace(summary.Name) && fullFieldNamesByTableRef.TryGetValue(rel.ParentTableRef, out var summaryParentSet))
                    summaryParentSet.Add(summary.Name);
            }
        }

        ValidateRelationships(document.Relationships ?? [], tableRefs, fieldNamesByTableRef, fullFieldNamesByTableRef, issues, seenRefs);

        foreach (var table in document.Tables)
            ValidateReports(table, fieldNamesByTableRef.GetValueOrDefault(table.LogicalRef, []), issues, seenRefs);

        foreach (var form in document.Forms ?? [])
            ValidateForm(form, tableRefs, fieldNamesByTableRef, issues, seenRefs);

        var seenRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in document.Roles ?? [])
            ValidateRole(role, tableRefs, fieldNamesByTableRef, issues, seenRefs, seenRoleNames);

        return new PblValidationResult { Issues = issues };
    }

    private static readonly IReadOnlyCollection<string> ValidRecordScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "None", "OwnRecords", "AllRecords",
    };

    private static readonly IReadOnlyCollection<string> ValidFieldAccessLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "FullAccess", "CustomAccess",
    };

    private static readonly IReadOnlyCollection<string> ValidFieldAccesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "View", "Modify", "None",
    };

    private static void ValidateRole(
        PblRole role,
        HashSet<string> tableRefs,
        Dictionary<string, HashSet<string>> fieldNamesByTableRef,
        List<PblIssue> issues,
        HashSet<string> seenRefs,
        HashSet<string> seenRoleNames)
    {
        ValidateRef(role.LogicalRef, "Role", issues, seenRefs);

        if (string.IsNullOrWhiteSpace(role.Name))
            issues.Add(Error("MISSING_NAME", "Role is missing a Name.", role.LogicalRef));
        else if (!seenRoleNames.Add(role.Name))
            issues.Add(Error("DUPLICATE_ROLE_NAME", $"Role name '{role.Name}' is used more than once.", role.LogicalRef));

        foreach (var perm in role.TablePermissions ?? [])
        {
            var tableOk = tableRefs.Contains(perm.TableRef);
            if (!tableOk)
                issues.Add(Error("UNKNOWN_ROLE_TABLE", $"Role '{role.Name}' references unknown table '{perm.TableRef}'.", role.LogicalRef));

            if (!ValidRecordScopes.Contains(perm.ViewScope))
                issues.Add(Error("INVALID_VIEW_SCOPE", $"ViewScope must be one of: {string.Join(", ", ValidRecordScopes)}.", role.LogicalRef));

            if (!ValidRecordScopes.Contains(perm.ModifyScope))
                issues.Add(Error("INVALID_MODIFY_SCOPE", $"ModifyScope must be one of: {string.Join(", ", ValidRecordScopes)}.", role.LogicalRef));

            if (!ValidFieldAccessLevels.Contains(perm.FieldAccessLevel))
                issues.Add(Error("INVALID_FIELD_ACCESS_LEVEL", $"FieldAccessLevel must be one of: {string.Join(", ", ValidFieldAccessLevels)}.", role.LogicalRef));

            var validFieldNames = tableOk ? fieldNamesByTableRef.GetValueOrDefault(perm.TableRef, []) : [];
            foreach (var fieldPerm in perm.FieldPermissions ?? [])
            {
                if (tableOk && !validFieldNames.Contains(fieldPerm.FieldName))
                    issues.Add(Error("UNKNOWN_FIELD_PERMISSION_FIELD", $"Role '{role.Name}' references unknown field '{fieldPerm.FieldName}' on table '{perm.TableRef}'.", role.LogicalRef));

                if (!ValidFieldAccesses.Contains(fieldPerm.Access))
                    issues.Add(Error("INVALID_FIELD_ACCESS", $"Field access must be one of: {string.Join(", ", ValidFieldAccesses)}.", role.LogicalRef));
            }
        }
    }

    private static void ValidateRelationships(
        List<PblRelationship> relationships,
        HashSet<string> tableRefs,
        Dictionary<string, HashSet<string>> fieldNamesByTableRef,
        Dictionary<string, HashSet<string>> fullFieldNamesByTableRef,
        List<PblIssue> issues,
        HashSet<string> seenRefs)
    {
        foreach (var rel in relationships ?? [])
        {
            ValidateRef(rel.LogicalRef, "Relationship", issues, seenRefs);

            var parentOk = tableRefs.Contains(rel.ParentTableRef);
            if (!parentOk)
                issues.Add(Error("UNKNOWN_RELATIONSHIP_PARENT_TABLE", $"Relationship '{rel.LogicalRef}' references unknown parent table '{rel.ParentTableRef}'.", rel.LogicalRef));

            var childOk = tableRefs.Contains(rel.ChildTableRef);
            if (!childOk)
                issues.Add(Error("UNKNOWN_RELATIONSHIP_CHILD_TABLE", $"Relationship '{rel.LogicalRef}' references unknown child table '{rel.ChildTableRef}'.", rel.LogicalRef));

            if (string.IsNullOrWhiteSpace(rel.ReferenceFieldName))
                issues.Add(Error("MISSING_NAME", $"Relationship '{rel.LogicalRef}' is missing a ReferenceFieldName.", rel.LogicalRef));
            else if (childOk)
            {
                var childNames = fieldNamesByTableRef[rel.ChildTableRef];
                if (!childNames.Add(rel.ReferenceFieldName))
                    issues.Add(Error("DUPLICATE_FIELD_NAME", $"Reference field name '{rel.ReferenceFieldName}' is already used on table '{rel.ChildTableRef}'.", rel.LogicalRef));
            }

            foreach (var lookup in rel.Lookups ?? [])
            {
                ValidateRef(lookup.LogicalRef, "Lookup", issues, seenRefs);

                if (string.IsNullOrWhiteSpace(lookup.Name))
                    issues.Add(Error("MISSING_NAME", $"Lookup field is missing a Name.", lookup.LogicalRef));
                else if (childOk && !fieldNamesByTableRef[rel.ChildTableRef].Add(lookup.Name))
                    issues.Add(Error("DUPLICATE_FIELD_NAME", $"Lookup field name '{lookup.Name}' is already used on table '{rel.ChildTableRef}'.", lookup.LogicalRef));

                if (parentOk && string.IsNullOrWhiteSpace(lookup.SourceFieldName))
                    issues.Add(Error("MISSING_LOOKUP_SOURCE_FIELD", $"Lookup field '{lookup.Name}' is missing SourceFieldName.", lookup.LogicalRef));
                else if (parentOk && !fullFieldNamesByTableRef[rel.ParentTableRef].Contains(lookup.SourceFieldName))
                    issues.Add(Error("UNKNOWN_LOOKUP_SOURCE_FIELD", $"Lookup field '{lookup.Name}' references unknown parent field '{lookup.SourceFieldName}'.", lookup.LogicalRef));
            }

            foreach (var summary in rel.Summaries ?? [])
            {
                ValidateRef(summary.LogicalRef, "Summary", issues, seenRefs);

                if (string.IsNullOrWhiteSpace(summary.Name))
                    issues.Add(Error("MISSING_NAME", $"Summary field is missing a Name.", summary.LogicalRef));
                else if (parentOk && !fieldNamesByTableRef[rel.ParentTableRef].Add(summary.Name))
                    issues.Add(Error("DUPLICATE_FIELD_NAME", $"Summary field name '{summary.Name}' is already used on table '{rel.ParentTableRef}'.", summary.LogicalRef));

                if (string.IsNullOrWhiteSpace(summary.Function) || !SummaryFunctions.All.Contains(summary.Function, StringComparer.OrdinalIgnoreCase))
                    issues.Add(Error("UNSUPPORTED_SUMMARY_FUNCTION", $"Summary function must be one of: {string.Join(", ", SummaryFunctions.All)}.", summary.LogicalRef));

                if (summary.TargetFieldName is not null && childOk && !fullFieldNamesByTableRef[rel.ChildTableRef].Contains(summary.TargetFieldName))
                    issues.Add(Error("UNKNOWN_SUMMARY_TARGET_FIELD", $"Summary field '{summary.Name}' references unknown child field '{summary.TargetFieldName}'.", summary.LogicalRef));
            }
        }
    }

    /// <summary>Validates the table's fields and returns the set of valid field Names, for
    /// use by <see cref="ValidateReports"/> when checking report field references.</summary>
    private static HashSet<string> ValidateFields(PblTable table, List<PblIssue> issues, HashSet<string> seenRefs)
    {
        var seenFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Name stays the import cross-reference key (see PblFormModels/PblReportModels doc comments) and
        // is preserved as-is rather than regenerated — a literal Name collision would still break that
        // cross-referencing, so it's still checked. Label is what users now actually see/edit, so its
        // uniqueness is validated too (mirrors the live Create/BulkCreate Field API's Label-duplicate check).
        var seenFieldLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in table.Fields ?? [])
        {
            ValidateRef(field.LogicalRef, "Field", issues, seenRefs);

            if (string.IsNullOrWhiteSpace(field.Name))
            {
                issues.Add(Error("MISSING_NAME", "Field is missing a Name.", field.LogicalRef));
            }
            else if (!seenFieldNames.Add(field.Name))
            {
                issues.Add(Error("DUPLICATE_FIELD_NAME", $"Field name '{field.Name}' is used more than once in table '{table.Name}'.", field.LogicalRef));
            }

            var effectiveLabel = string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;
            if (!string.IsNullOrWhiteSpace(effectiveLabel) && !seenFieldLabels.Add(effectiveLabel))
            {
                issues.Add(Error("DUPLICATE_FIELD_LABEL", $"Field label '{effectiveLabel}' is used more than once in table '{table.Name}'.", field.LogicalRef));
            }

            if (string.IsNullOrWhiteSpace(field.TypeCode))
            {
                issues.Add(Error("MISSING_TYPE_CODE", "Field is missing a TypeCode.", field.LogicalRef));
            }
            else if (string.Equals(field.TypeCode, FormulaTypeCode, StringComparison.OrdinalIgnoreCase))
            {
                ValidateFormulaField(field, issues);
            }
            else if (!SupportedFieldTypeCodes.Contains(field.TypeCode))
            {
                issues.Add(Warning(
                    "UNSUPPORTED_FIELD_TYPE",
                    $"Field type '{field.TypeCode}' is not supported by this import phase and will be skipped.",
                    field.LogicalRef));
            }
        }

        return seenFieldNames;
    }

    /// <summary>Structural-only check: a full compile against the table's schema happens at
    /// import time (Fids don't exist yet during validation).</summary>
    private static void ValidateFormulaField(PblField field, List<PblIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(field.FormulaExpression))
            issues.Add(Error("MISSING_FORMULA_EXPRESSION", "Formula field is missing an expression.", field.LogicalRef));

        if (string.IsNullOrWhiteSpace(field.ResultType) || !FormulaResultTypes.All.Contains(field.ResultType, StringComparer.OrdinalIgnoreCase))
            issues.Add(Error(
                "INVALID_FORMULA_RESULT_TYPE",
                $"Formula result type must be one of: {string.Join(", ", FormulaResultTypes.All)}.",
                field.LogicalRef));
    }

    /// <summary>Matches ReportConfigValidatorRegistry's supported report types. GridEdit is no
    /// longer a distinct report type (now a session-only client-side toggle on Table reports —
    /// see QblToPblConverter.ConvertReport, which maps an imported QB::Report::GridEdit to
    /// "Table"). Chart added here since it's a genuinely supported PowerBase report type that
    /// this list had never been updated for.</summary>
    private static readonly IReadOnlyCollection<string> SupportedReportTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Table", "Summary", "Chart",
    };

    private static void ValidateReports(PblTable table, HashSet<string> validFieldNames, List<PblIssue> issues, HashSet<string> seenRefs)
    {
        var seenReportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var report in table.Reports ?? [])
        {
            ValidateRef(report.LogicalRef, "Report", issues, seenRefs);

            if (string.IsNullOrWhiteSpace(report.Name))
                issues.Add(Error("MISSING_NAME", "Report is missing a Name.", report.LogicalRef));
            else if (!seenReportNames.Add(report.Name))
                issues.Add(Error("DUPLICATE_REPORT_NAME", $"Report name '{report.Name}' is used more than once in table '{table.Name}'.", report.LogicalRef));

            if (!SupportedReportTypes.Contains(report.ReportType))
                issues.Add(Error("UNSUPPORTED_REPORT_TYPE", $"Report type must be one of: {string.Join(", ", SupportedReportTypes)}.", report.LogicalRef));

            foreach (var column in report.Columns ?? [])
            {
                if (!validFieldNames.Contains(column))
                    issues.Add(Error("UNKNOWN_REPORT_FIELD", $"Report '{report.Name}' references unknown field '{column}'.", report.LogicalRef));
            }

            foreach (var sort in report.SortFields ?? [])
            {
                if (!validFieldNames.Contains(sort.FieldName))
                    issues.Add(Error("UNKNOWN_REPORT_FIELD", $"Report '{report.Name}' references unknown field '{sort.FieldName}'.", report.LogicalRef));
            }
        }
    }

    /// <summary>Matches SaveFormLayoutCommandValidator's own allow-lists exactly.</summary>
    private static readonly IReadOnlyCollection<string> ValidElementTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Field", "StaticText", "Divider", "Button", "Report",
    };

    private static readonly IReadOnlyCollection<string> ValidLabelModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Default", "Custom", "Hide",
    };

    private static void ValidateForm(
        PblForm form,
        HashSet<string> tableRefs,
        Dictionary<string, HashSet<string>> fieldNamesByTableRef,
        List<PblIssue> issues,
        HashSet<string> seenRefs)
    {
        ValidateRef(form.LogicalRef, "Form", issues, seenRefs);

        if (string.IsNullOrWhiteSpace(form.Name))
            issues.Add(Error("MISSING_NAME", $"Form '{form.LogicalRef}' is missing a Name.", form.LogicalRef));

        var tableOk = tableRefs.Contains(form.TableRef);
        if (!tableOk)
            issues.Add(Error("UNKNOWN_FORM_TABLE", $"Form '{form.LogicalRef}' references unknown table '{form.TableRef}'.", form.LogicalRef));

        var validFieldNames = tableOk ? fieldNamesByTableRef.GetValueOrDefault(form.TableRef, []) : [];
        var seenSectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collected while walking sections so rule action targets (below) can be checked
        // against "does this section/block/element actually exist on this form" without a
        // second tree walk.
        var sectionRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elementRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in form.Sections ?? [])
        {
            ValidateRef(section.LogicalRef, "FormSection", issues, seenRefs);
            sectionRefs.Add(section.LogicalRef);

            if (string.IsNullOrWhiteSpace(section.Name))
                issues.Add(Error("MISSING_NAME", $"Form section is missing a Name.", section.LogicalRef));
            else if (!seenSectionNames.Add(section.Name))
                issues.Add(Error("DUPLICATE_FORM_SECTION_NAME", $"Section name '{section.Name}' is used more than once in form '{form.Name}'.", section.LogicalRef));

            if (section.Blocks is null || section.Blocks.Count == 0 || section.Blocks.Count > 5)
                issues.Add(Error("INVALID_FORM_SECTION_BLOCK_COUNT", $"Section '{section.Name}' must have between 1 and 5 blocks.", section.LogicalRef));

            foreach (var block in section.Blocks ?? [])
            {
                ValidateRef(block.LogicalRef, "FormBlock", issues, seenRefs);
                blockRefs.Add(block.LogicalRef);

                foreach (var element in block.Elements ?? [])
                {
                    ValidateRef(element.LogicalRef, "FormElement", issues, seenRefs);
                    elementRefs.Add(element.LogicalRef);

                    if (string.IsNullOrWhiteSpace(element.ElementType) || !ValidElementTypes.Contains(element.ElementType))
                        issues.Add(Error("INVALID_FORM_ELEMENT_TYPE", $"Form element type must be one of: {string.Join(", ", ValidElementTypes)}.", element.LogicalRef));

                    if (!ValidLabelModes.Contains(element.LabelMode))
                        issues.Add(Error("INVALID_FORM_ELEMENT_LABEL_MODE", $"LabelMode must be one of: {string.Join(", ", ValidLabelModes)}.", element.LogicalRef));

                    if (string.Equals(element.ElementType, "Field", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(element.FieldName))
                            issues.Add(Error("MISSING_FORM_ELEMENT_FIELD", $"Field element is missing a FieldName.", element.LogicalRef));
                        else if (tableOk && !validFieldNames.Contains(element.FieldName))
                            issues.Add(Error("UNKNOWN_FORM_ELEMENT_FIELD", $"Form element references unknown field '{element.FieldName}' on table '{form.TableRef}'.", element.LogicalRef));
                    }
                }
            }
        }

        foreach (var rule in form.Rules ?? [])
            ValidateFormRule(rule, form, validFieldNames, sectionRefs, blockRefs, elementRefs, issues, seenRefs);
    }

    private static readonly IReadOnlyCollection<string> ValidRunTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AnyChange", "EditOrAdd", "Save", "SaveAfterValidating",
    };

    private static readonly IReadOnlyCollection<string> ValidConditionLogics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "all", "any",
    };

    private static readonly IReadOnlyCollection<string> ValidFormRuleOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "eq", "ne", "contains", "notContains", "startsWith", "endsWith", "isEmpty", "isNotEmpty", "gt", "gte", "lt", "lte",
    };

    private static readonly IReadOnlyCollection<string> ValidFormRuleActionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Show", "Hide", "Enable", "Disable", "Require", "NotRequired", "ChangeLabel", "ChangeValue", "SetColor", "DisplayMessage", "PreventSave",
    };

    private static readonly IReadOnlyCollection<string> ValidFormRuleTargetTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Field", "Section", "Block",
    };

    private static void ValidateFormRule(
        PblFormRule rule,
        PblForm form,
        HashSet<string> validFieldNames,
        HashSet<string> sectionRefs,
        HashSet<string> blockRefs,
        HashSet<string> elementRefs,
        List<PblIssue> issues,
        HashSet<string> seenRefs)
    {
        ValidateRef(rule.LogicalRef, "FormRule", issues, seenRefs);

        if (string.IsNullOrWhiteSpace(rule.Name))
            issues.Add(Error("MISSING_NAME", "Form rule is missing a Name.", rule.LogicalRef));

        if (!ValidRunTriggers.Contains(rule.RunTrigger))
            issues.Add(Error("INVALID_FORM_RULE_RUN_TRIGGER", $"RunTrigger must be one of: {string.Join(", ", ValidRunTriggers)}.", rule.LogicalRef));

        if (!ValidConditionLogics.Contains(rule.ConditionLogic))
            issues.Add(Error("INVALID_FORM_RULE_CONDITION_LOGIC", $"ConditionLogic must be one of: {string.Join(", ", ValidConditionLogics)}.", rule.LogicalRef));

        if (!rule.IsExpressionMode)
        {
            foreach (var condition in rule.Conditions ?? [])
            {
                if (string.IsNullOrWhiteSpace(condition.FieldName) || !validFieldNames.Contains(condition.FieldName))
                    issues.Add(Error("UNKNOWN_FORM_RULE_CONDITION_FIELD", $"Form rule '{rule.Name}' condition references unknown field '{condition.FieldName}'.", rule.LogicalRef));

                if (!ValidFormRuleOperators.Contains(condition.Operator))
                    issues.Add(Error("INVALID_FORM_RULE_OPERATOR", $"Operator must be one of: {string.Join(", ", ValidFormRuleOperators)}.", rule.LogicalRef));
            }
        }
        else if (string.IsNullOrWhiteSpace(rule.ExpressionText))
        {
            issues.Add(Error("MISSING_FORM_RULE_EXPRESSION", $"Form rule '{rule.Name}' is in expression mode but has no ExpressionText.", rule.LogicalRef));
        }

        foreach (var action in rule.Actions ?? [])
        {
            if (!ValidFormRuleActionTypes.Contains(action.ActionType))
                issues.Add(Error("INVALID_FORM_RULE_ACTION_TYPE", $"ActionType must be one of: {string.Join(", ", ValidFormRuleActionTypes)}.", rule.LogicalRef));

            if (!ValidFormRuleTargetTypes.Contains(action.TargetType))
            {
                issues.Add(Error("INVALID_FORM_RULE_TARGET_TYPE", $"TargetType must be one of: {string.Join(", ", ValidFormRuleTargetTypes)}.", rule.LogicalRef));
                continue;
            }

            var resolved = action.TargetType switch
            {
                "Field" => action.TargetElementRef is not null && elementRefs.Contains(action.TargetElementRef),
                "Section" => action.TargetSectionRef is not null && sectionRefs.Contains(action.TargetSectionRef),
                "Block" => action.TargetBlockRef is not null && blockRefs.Contains(action.TargetBlockRef),
                _ => false,
            };

            if (!resolved)
                issues.Add(Error("UNKNOWN_FORM_RULE_ACTION_TARGET", $"Form rule '{rule.Name}' action target does not exist on form '{form.Name}'.", rule.LogicalRef));
        }
    }

    private static void ValidateRef(string? logicalRef, string kind, List<PblIssue> issues, HashSet<string> seenRefs)
    {
        if (string.IsNullOrWhiteSpace(logicalRef))
        {
            issues.Add(Error("MISSING_LOGICAL_REF", $"{kind} is missing a LogicalRef."));
            return;
        }

        if (!seenRefs.Add(logicalRef))
            issues.Add(Error("DUPLICATE_LOGICAL_REF", $"LogicalRef '{logicalRef}' is used more than once.", logicalRef));
    }

    private static PblIssue Error(string code, string message, string? elementRef = null) =>
        new() { Severity = PblIssueSeverity.Error, Code = code, Message = message, ElementRef = elementRef };

    private static PblIssue Warning(string code, string message, string? elementRef = null) =>
        new() { Severity = PblIssueSeverity.Warning, Code = code, Message = message, ElementRef = elementRef };
}
