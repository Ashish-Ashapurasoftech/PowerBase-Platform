namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Loose AST for a parsed QBL YAML document. Node <c>Properties</c>/children shapes vary too
/// widely by <see cref="QblResourceNode.Type"/> to hand-model every one as a strict C# class —
/// <see cref="QblToPblConverter"/> dispatches on <c>Type</c> and reads whatever properties that
/// specific type needs out of the generic <see cref="Properties"/> bag.
/// </summary>
public sealed class QblDocument
{
    public string Version { get; init; } = string.Empty;

    /// <summary>Parameter key → resolved value, for <c>!Var</c> references. Quickbase exports
    /// literal values as named parameters; a <c>!Var</c> tag resolves back to one by name.</summary>
    public IReadOnlyDictionary<string, object?> ParameterDefinitions { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Top-level Resources map (logical ref → node). In practice most of what an
    /// importer cares about (Fields, Relationships, Reports, Forms) is nested inside each
    /// Table's own children, not listed flatly here — see <see cref="QblResourceNode.Children"/>.</summary>
    public IReadOnlyDictionary<string, QblResourceNode> Resources { get; init; } =
        new Dictionary<string, QblResourceNode>();
}

/// <summary>One QBL node: <c>Type: &lt;namespaced type&gt;</c> + <c>Properties: &lt;map&gt;</c> +
/// type-specific children (e.g. a Table's <c>Fields</c>/<c>Relationships</c>/<c>Reports</c>/<c>Forms</c>).</summary>
public sealed class QblResourceNode
{
    public string Type { get; init; } = string.Empty;

    /// <summary>Raw property values. May contain nested <see cref="Dictionary{TKey,TValue}"/>,
    /// <see cref="List{T}"/>, scalars, or <see cref="QblRef"/>/<see cref="QblBadRef"/>/
    /// <see cref="QblVarRef"/> markers produced by <see cref="QblTagNodeDeserializer"/>.</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Everything under this node that isn't <c>Type</c>/<c>Properties</c> — e.g. a
    /// Table's <c>Fields</c>/<c>Relationships</c>/<c>Reports</c>/<c>Forms</c> maps, or a
    /// Section's <c>Columns</c>. Each value is itself a map of logical ref → child node (or, for
    /// a few QBL shapes, a plain list — callers know which shape to expect for a given key).</summary>
    public IReadOnlyDictionary<string, object?> Children { get; init; } =
        new Dictionary<string, object?>();

    public string? PropertyString(string name) => Properties.TryGetValue(name, out var v) ? v?.ToString() : null;

    public bool PropertyBool(string name, bool defaultValue = false)
    {
        if (!Properties.TryGetValue(name, out var v) || v is null)
            return defaultValue;
        if (v is bool b)
            return b;
        return bool.TryParse(v.ToString(), out var parsed) ? parsed : defaultValue;
    }

    public QblRef? PropertyRef(string name) => Properties.TryGetValue(name, out var v) ? v as QblRef : null;

    /// <summary>Reads a child map (e.g. <c>Fields</c>) as logical-ref → node pairs. QBL nests
    /// some maps one level deeper under a <c>Resources</c>/<c>Properties</c> wrapper (Forms do
    /// this — see <c>QBL_COMPATIBILITY_MATRIX.md</c> notes); callers pass the already-unwrapped
    /// map when that applies.</summary>
    public IReadOnlyDictionary<string, QblResourceNode> ChildMap(string name)
    {
        if (!Children.TryGetValue(name, out var raw) || raw is not IReadOnlyDictionary<string, object?> map)
            return new Dictionary<string, QblResourceNode>();

        var result = new Dictionary<string, QblResourceNode>();
        foreach (var (key, value) in map)
        {
            if (value is QblResourceNode node)
                result[key] = node;
        }
        return result;
    }
}

/// <summary>A resolved <c>!Ref</c> cross-reference. Real QBL refs use varying key combinations
/// depending on what's targeted (<c>{Table, Field}</c>, <c>{Relationship}</c>, <c>{Role}</c>,
/// <c>{FormV2Page, FormV2Section}</c>, ...) — modeled as a flexible bag, not fixed properties.</summary>
public sealed class QblRef
{
    private readonly IReadOnlyDictionary<string, string> _scopes;

    public QblRef(IReadOnlyDictionary<string, string> scopes) => _scopes = scopes;

    public string? this[string scope] => _scopes.TryGetValue(scope, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> Scopes => _scopes;
}

/// <summary>An unresolvable reference in the source export (Quickbase's own escape hatch for a
/// deleted/broken reference). Always a <see cref="Warning"/>-severity issue where encountered —
/// never blocks the rest of the document.</summary>
public sealed class QblBadRef
{
    public string Message { get; }
    public QblBadRef(string message) => Message = message;
}

/// <summary>A <c>!Var</c> reference — resolves to a value in the document's
/// <see cref="QblDocument.ParameterDefinitions"/> by parameter name.</summary>
public sealed class QblVarRef
{
    public string ParameterName { get; }
    public QblVarRef(string parameterName) => ParameterName = parameterName;
}
