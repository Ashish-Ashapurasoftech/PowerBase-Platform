namespace PowerBase.Formula.Binding;

/// <summary>
/// Resolves a table-alias bracket token (<c>[_DBID_FILE_TYPES]</c>) during binding to the target
/// table's id, exactly as <see cref="IFieldSchema"/> resolves an ordinary field reference. A
/// resolved alias binds as a Text literal carrying the table id, so it flows unchanged into
/// existing Text-typed parameters like <c>GetRecords</c>'s tableId argument — see
/// <see cref="Syntax.FieldRefExpr.TableAliasId"/> and <c>Evaluation.Evaluator</c>.
/// Implementations are host-supplied (built from an app's tables); the engine itself stays free
/// of PowerBase domain types. Formula callers that don't need cross-table alias resolution can
/// omit this (see <c>FormulaEngine.Compile</c>'s optional parameter) and every <c>[_DBID_*]</c>
/// token then reports as an ordinary unknown field, same as before this existed.
/// </summary>
public interface ITableAliasSchema
{
    /// <summary>Resolve an alias by its bracket text. Returns false for unknown aliases.</summary>
    bool TryResolve(string alias, out string tableId);
}
