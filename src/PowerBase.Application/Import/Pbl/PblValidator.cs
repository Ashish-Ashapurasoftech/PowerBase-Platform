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
    /// scalar set defined for Phase 1 (see QBL_COMPATIBILITY_MATRIX.md).</summary>
    public static readonly IReadOnlyCollection<string> SupportedFieldTypeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TextMultiLine", "RichText", "SingleSelect", "MultiSelect",
        "Number", "Currency", "Percent", "Rating",
        "Date", "DateTime", "Time", "Duration",
        "Boolean", "Email", "Phone", "Url", "Address",
    };

    /// <summary>TypeCode for a formula field. Handled separately from
    /// <see cref="SupportedFieldTypeCodes"/> because it requires a second creation pass
    /// (its expression is validated against the table's already-created fields).</summary>
    public const string FormulaTypeCode = "Formula";

    /// <summary>True for any field type this import phase will attempt to create — either
    /// directly (<see cref="SupportedFieldTypeCodes"/>) or via the formula translation pass.</summary>
    public static bool IsCreatableFieldType(string typeCode) =>
        SupportedFieldTypeCodes.Contains(typeCode) || string.Equals(typeCode, FormulaTypeCode, StringComparison.OrdinalIgnoreCase);

    public PblValidationResult Validate(PblDocument document)
    {
        var issues = new List<PblIssue>();

        if (document.App is null)
        {
            issues.Add(Error("MISSING_APP", "PBL document must define an App."));
            return new PblValidationResult { Issues = issues };
        }

        var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ValidateRef(document.App.LogicalRef, "App", issues, seenRefs);
        if (string.IsNullOrWhiteSpace(document.App.Name))
            issues.Add(Error("MISSING_NAME", "App is missing a Name.", document.App.LogicalRef));

        if (document.Tables is null || document.Tables.Count == 0)
        {
            issues.Add(Warning("NO_TABLES", "PBL document defines no tables; the app will be created empty.", document.App.LogicalRef));
            return new PblValidationResult { Issues = issues };
        }

        var seenTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in document.Tables)
        {
            ValidateRef(table.LogicalRef, "Table", issues, seenRefs);

            if (string.IsNullOrWhiteSpace(table.Name))
                issues.Add(Error("MISSING_NAME", "Table is missing a Name.", table.LogicalRef));
            else if (!seenTableNames.Add(table.Name))
                issues.Add(Error("DUPLICATE_TABLE_NAME", $"Table name '{table.Name}' is used more than once.", table.LogicalRef));

            var fieldNames = ValidateFields(table, issues, seenRefs);
            ValidateReports(table, fieldNames, issues, seenRefs);
        }

        return new PblValidationResult { Issues = issues };
    }

    /// <summary>Validates the table's fields and returns the set of valid field Names, for
    /// use by <see cref="ValidateReports"/> when checking report field references.</summary>
    private static HashSet<string> ValidateFields(PblTable table, List<PblIssue> issues, HashSet<string> seenRefs)
    {
        var seenFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
