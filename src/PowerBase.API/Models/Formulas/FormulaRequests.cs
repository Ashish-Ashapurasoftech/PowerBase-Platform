using System.Text.Json;

namespace PowerBase.API.Models.Formulas;

public sealed class ValidateFormulaRequest
{
    public string Expression { get; set; } = string.Empty;

    /// <summary>Optional expected result type (e.g. a formula field's declared type, or "Bool" for rule conditions).</summary>
    public string? ExpectedType { get; set; }

    /// <summary>Set when authoring a report's summary formula: the formula is then checked against these named aggregates instead
    /// of the table's fields (an empty list means "no variables defined yet", so every [reference] is unknown).</summary>
    public List<PowerBase.API.Models.Reports.SummaryVariableRequest>? SummaryVariables { get; set; }
}

public sealed class EvaluateFormulaRequest
{
    public string Expression { get; set; } = string.Empty;

    public string? ExpectedType { get; set; }

    /// <summary>In-flight field values keyed by field id (as JSON), used to evaluate the expression.</summary>
    public Dictionary<string, JsonElement>? Values { get; set; }
}
