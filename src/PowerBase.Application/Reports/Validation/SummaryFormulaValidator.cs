using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Checks a Summary/Chart report's summary variables and formulas on save: only on those report types; at most
/// <see cref="SummaryFormulaFields.MaxVariables"/> variables (each a known field with a Summarize-by function its type allows, a unique name)
/// and <see cref="SummaryFormulaFields.MaxFormulas"/> formulas (a reserved id each, a unique name, a return type allowed for the report type,
/// and an expression that compiles against the variables — table fields are not in scope, so referencing one fails as an unknown field).
/// </summary>
public static class SummaryFormulaValidator
{
    private const int MaxNameLength = 100;

    public static void Validate(
        string reportType, IReadOnlyList<SummaryVariable> variables, IReadOnlyList<SummaryFormula> formulas,
        IReadOnlyList<AppField> tableFields, IFormulaExpressionValidator? expressionValidator,
        IDictionary<string, string[]> errors)
    {
        if (variables.Count == 0 && formulas.Count == 0) return;

        var isSummaryOrChart = string.Equals(reportType, "Summary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reportType, "Chart", StringComparison.OrdinalIgnoreCase);
        if (!isSummaryOrChart)
        {
            CommonReportValidationHelpers.AddError(errors, "summaryFormulas", "Summary formulas can only be used on Summary and Chart reports.");
            return;
        }

        if (variables.Count > SummaryFormulaFields.MaxVariables)
            CommonReportValidationHelpers.AddError(errors, "summaryVariables", $"A report can have at most {SummaryFormulaFields.MaxVariables} summary variables.");
        if (formulas.Count > SummaryFormulaFields.MaxFormulas)
            CommonReportValidationHelpers.AddError(errors, "summaryFormulas", $"A report can have at most {SummaryFormulaFields.MaxFormulas} summary formulas.");

        var fieldMap = tableFields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        var usedVariableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < variables.Count; i++)
        {
            var v = variables[i];
            var key = $"summaryVariables[{i}]";
            var name = v.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
                CommonReportValidationHelpers.AddError(errors, key, "Summary variable name is required.");
            else if (name.Length > MaxNameLength)
                CommonReportValidationHelpers.AddError(errors, key, $"Summary variable name must be at most {MaxNameLength} characters.");
            else if (!usedVariableNames.Add(name))
                CommonReportValidationHelpers.AddError(errors, key, $"Summary variable name '{name}' is used more than once.");

            if (!fieldMap.TryGetValue(v.FieldId, out var source) || SummaryFormulaFields.IsSummaryFormulaFid(v.FieldId))
            {
                CommonReportValidationHelpers.AddError(errors, key, $"Unknown field ID in summary variable: {v.FieldId}");
                continue;
            }
            var allowed = FieldTypeCategoryHelper.GetAllowedSummarizeByFunctions(source.TypeCode, SummaryReportConfigValidator.GetFormulaResultType(source));
            if (!allowed.Contains(v.Function, StringComparer.OrdinalIgnoreCase))
                CommonReportValidationHelpers.AddError(errors, key,
                    $"'{v.Function}' is not a valid Summarize-By option for field '{source.Name}' ({source.TypeCode}). Allowed: {string.Join(", ", allowed)}");
        }

        var variableFields = SummaryFormulaFields.BuildVariableFields(variables, tableFields, tableFields.FirstOrDefault()?.AppTableId ?? 0);
        var usedIds = new HashSet<long>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < formulas.Count; i++)
        {
            var f = formulas[i];
            var key = $"summaryFormulas[{i}]";

            if (!SummaryFormulaFields.IsSummaryFormulaFid(f.Id) || !usedIds.Add(f.Id))
                CommonReportValidationHelpers.AddError(errors, key, $"Summary formula id must be a unique value from {SummaryFormulaFields.FirstId} to {SummaryFormulaFields.FirstId + SummaryFormulaFields.MaxFormulas - 1}.");

            var name = f.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
                CommonReportValidationHelpers.AddError(errors, key, "Summary formula name is required.");
            else if (name.Length > MaxNameLength)
                CommonReportValidationHelpers.AddError(errors, key, $"Summary formula name must be at most {MaxNameLength} characters.");
            else if (!usedNames.Add(name))
                CommonReportValidationHelpers.AddError(errors, key, $"Summary formula name '{name}' is used more than once.");

            if (!SummaryFormulaFields.IsAllowedTypeCode(f.TypeCode, reportType))
            {
                CommonReportValidationHelpers.AddError(errors, key,
                    string.Equals(reportType, "Chart", StringComparison.OrdinalIgnoreCase)
                        ? $"Summary formula type '{f.TypeCode}' can't be plotted. A chart's summary formula must return Numeric, Duration or Checkbox."
                        : $"Unknown summary formula type '{f.TypeCode}'.");
                continue;
            }
            if (!SummaryFormulaFields.IsAllowedDisplayAs(f.DisplayAs))
                CommonReportValidationHelpers.AddError(errors, key, $"Unknown numeric display '{f.DisplayAs}'.");
            if (f.DecimalPlaces is < 0 or > 10)
                CommonReportValidationHelpers.AddError(errors, key, "Decimal places must be between 0 and 10.");

            if (string.IsNullOrWhiteSpace(f.Expression))
            {
                CommonReportValidationHelpers.AddError(errors, key, $"Summary formula '{name}' needs a formula.");
                continue;
            }

            if (expressionValidator is null) continue;
            var expectedType = FormulaTypeMap.ExpressionAndType(f.TypeCode, ReportFormulaFields.BuildSettings(SummaryFormulaFields.ToReportFormula(f)))?.Type;
            var problems = expressionValidator.Validate(f.Expression, variableFields, expectedType);
            if (problems.Count > 0)
                CommonReportValidationHelpers.AddError(errors, key, $"Summary formula '{name}': {problems[0]}");
        }
    }
}
