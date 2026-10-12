namespace PowerBase.Formula.Binding;

/// <summary>An app variable as a formula sees it: its stable id, its current name, and its value. A variable's
/// value is always text — a formula converts it (<c>ToNumber([Tax])</c>) like any other text.</summary>
public sealed record VariableRef(long Id, string Name, string Value);

/// <summary>
/// Resolves a <c>[Name]</c> that names an app variable. Offered by the host (built from the app's variables);
/// the engine itself stays free of PowerBase domain types, the same way <see cref="ITableAliasSchema"/> is.
/// A host with no variables to offer simply passes none, and every name resolves as a field as before.
/// </summary>
public interface IVariableSchema
{
    /// <summary>Resolve a variable by its name (case-insensitive) or by its id form (<c>var_5</c>).</summary>
    bool TryResolve(string name, out VariableRef variable);
}
