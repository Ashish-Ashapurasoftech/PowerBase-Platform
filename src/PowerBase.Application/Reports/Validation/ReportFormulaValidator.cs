using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Checks a report's own formulas on save: at most <see cref="ReportFormulaFields.MaxFormulas"/>, only on a Table report using
/// Custom columns (or on a Summary/Chart report), a reserved id each, a unique name, an allowed result type and an expression
/// that compiles against the table's real fields. A formula can't reference another report formula — they are not in the
/// field list its expression is compiled against, so such a reference fails as an unknown field.
/// </summary>
public static class ReportFormulaValidator
{
    private const int MaxNameLength = 100;

    public static void Validate(
        string reportType, string columnsMode, IReadOnlyList<ReportFormula> formulas,
        IReadOnlyList<AppField> tableFields, IFormulaExpressionValidator? expressionValidator,
        IDictionary<string, string[]> errors)
    {
        if (formulas.Count == 0) return;

        if (formulas.Count > ReportFormulaFields.MaxFormulas)
            CommonReportValidationHelpers.AddError(errors, "reportFormulas", $"A report can have at most {ReportFormulaFields.MaxFormulas} report formulas.");

        if (reportType == "Table" && !string.Equals(columnsMode, "Custom", StringComparison.OrdinalIgnoreCase))
            CommonReportValidationHelpers.AddError(errors, "reportFormulas", "Report formulas can only be used with custom columns.");

        var usedIds = new HashSet<long>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fieldNames = tableFields
            .SelectMany(f => new[] { f.Name, f.Label })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < formulas.Count; i++)
        {
            var f = formulas[i];
            var key = $"reportFormulas[{i}]";

            if (!ReportFormulaFields.IsReportFormulaFid(f.Id) || !usedIds.Add(f.Id))
                CommonReportValidationHelpers.AddError(errors, key, $"Report formula id must be a unique value from {ReportFormulaFields.FirstId} to {ReportFormulaFields.FirstId + ReportFormulaFields.MaxFormulas - 1}.");

            var name = f.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
                CommonReportValidationHelpers.AddError(errors, key, "Report formula name is required.");
            else if (name.Length > MaxNameLength)
                CommonReportValidationHelpers.AddError(errors, key, $"Report formula name must be at most {MaxNameLength} characters.");
            else if (!usedNames.Add(name))
                CommonReportValidationHelpers.AddError(errors, key, $"Report formula name '{name}' is used more than once.");
            else if (fieldNames.Contains(name))
                CommonReportValidationHelpers.AddError(errors, key, $"'{name}' is already a field on this table. Choose a different report formula name.");

            if (!ReportFormulaFields.IsAllowedTypeCode(f.TypeCode))
            {
                CommonReportValidationHelpers.AddError(errors, key, $"Unknown report formula type '{f.TypeCode}'.");
                continue;
            }
            if (!ReportFormulaFields.IsAllowedDisplayAs(f.DisplayAs))
                CommonReportValidationHelpers.AddError(errors, key, $"Unknown numeric display '{f.DisplayAs}'.");

            if (string.IsNullOrWhiteSpace(f.Expression))
            {
                CommonReportValidationHelpers.AddError(errors, key, $"Report formula '{name}' needs a formula.");
                continue;
            }

            if (expressionValidator is null) continue;
            var expectedType = FormulaTypeMap.ExpressionAndType(f.TypeCode, ReportFormulaFields.BuildSettings(f))?.Type;
            var problems = expressionValidator.Validate(f.Expression, tableFields, expectedType);
            if (problems.Count > 0)
                CommonReportValidationHelpers.AddError(errors, key, $"Report formula '{name}': {problems[0]}");
        }
    }
}
