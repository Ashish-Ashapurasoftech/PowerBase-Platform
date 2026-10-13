using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Binding;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Converts formula text between what the user types/sees and what is stored, so a field rename
/// never breaks a formula.
///
/// <list type="bullet">
/// <item><c>[Customer Name]</c> (typed by name) is stored as <c>[fid_14|Customer Name]</c>. The part
///   before <c>|</c> is what resolves; the part after is a snapshot of the name, used only to
///   recognise "this was typed by name" and as the fallback text if the field is later deleted.
///   It is shown back as <c>[current label]</c>, so a rename follows through automatically.</item>
/// <item><c>[fid_14]</c> (typed by ID) is stored and shown as <c>[fid_14]</c>, untouched.</item>
/// </list>
///
/// App variables work the same way: <c>[Tax]</c> typed by name is stored as <c>[var_5|Tax]</c> and shown as
/// <c>[Tax]</c>. A variable wins over a field of the same name (Quickbase's rule), so a field with such a name
/// is shown by its id, <c>[fid_14]</c> — the only spelling that still reaches the field and survives a re-save.
///
/// Both directions are idempotent, and anything that doesn't resolve (unknown names, <c>[_DBID_*]</c>
/// aliases, malformed text) is left exactly as typed.
/// </summary>
public sealed class FormulaCanonicalizer
{
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Settings properties that hold formula text, across every formula field type
    /// (Formula_* use Expression, Formula_Url uses Template).</summary>
    private static readonly string[] FormulaProperties = ["expression", "template"];

    // Same visibility rules as AppFieldSchema: name resolution only sees fields a formula can read.
    private readonly Dictionary<string, AppField> _referenceableByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, AppField> _byFid = new();
    private readonly Dictionary<string, AppVariable> _variablesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, AppVariable> _variablesById = new();

    /// <param name="fields">The table's fields.</param>
    /// <param name="extraNames">Extra name → Fid aliases, e.g. a field's new label while it is being renamed
    /// in the same save as the formula that references it. Never overrides a name that already resolves.</param>
    /// <param name="variables">The app's variables, when formulas may reference them. Omit for callers that don't.</param>
    public FormulaCanonicalizer(IReadOnlyList<AppField> fields, IReadOnlyDictionary<string, long>? extraNames = null,
        IEnumerable<AppVariable>? variables = null)
    {
        foreach (var v in variables ?? [])
        {
            _variablesById[v.Id] = v;
            _variablesByName[v.Name.Trim()] = v;
        }

        foreach (var f in fields)
        {
            if (!f.Fid.HasValue) continue;
            _byFid[f.Fid.Value] = f;

            if (FormulaTypeMap.FieldType(f.TypeCode, f.Settings) is null) continue;
            _referenceableByName[f.Name] = f;
            if (!string.IsNullOrWhiteSpace(f.Label) && !string.Equals(f.Label, f.Name, StringComparison.OrdinalIgnoreCase))
                _referenceableByName[f.Label] = f;
        }

        if (extraNames is null) return;
        foreach (var (name, fid) in extraNames)
            if (!_referenceableByName.ContainsKey(name) && _byFid.TryGetValue(fid, out var field)
                && FormulaTypeMap.FieldType(field.TypeCode, field.Settings) is not null)
                _referenceableByName[name] = field;
    }

    /// <summary>Typed form → stored form: <c>[Name]</c> becomes <c>[var_N|Name]</c> for an app variable (which wins)
    /// or <c>[fid_N|Name]</c> for a field; <c>[fid_N]</c> and <c>[var_N]</c> are kept.</summary>
    public string ToStored(string? expression) => FieldReferenceRewriter.Rewrite(expression, name =>
    {
        if (_variablesByName.TryGetValue(name.Trim(), out var variable))
            return $"{VariableIdReference.Prefix}{variable.Id}{VariableIdReference.SnapshotSeparator}{variable.Name}";

        return _referenceableByName.TryGetValue(name, out var field)
            ? $"{FieldIdReference.Prefix}{field.Fid}{FieldIdReference.SnapshotSeparator}{DisplayName(field)}"
            : null;
    });

    /// <summary>Stored form → displayed form: <c>[var_N|Name]</c> / <c>[fid_N|Name]</c> become <c>[current name]</c> (or
    /// the snapshot name if the target no longer exists); <c>[fid_N]</c> and <c>[var_N]</c> are kept. A field whose
    /// label is also an app variable's name is shown as <c>[fid_N]</c>, because the bare name would now mean the variable.</summary>
    public string ToDisplay(string? expression) => FieldReferenceRewriter.Rewrite(expression, name =>
    {
        var separator = name.IndexOf(FieldIdReference.SnapshotSeparator);

        if (VariableIdReference.TryParse(name, out var variableId))
        {
            if (separator < 0) return null; // typed by ID — stays an ID
            if (_variablesById.TryGetValue(variableId, out var variable)) return variable.Name;
            var variableSnapshot = name[(separator + 1)..].Trim();
            return variableSnapshot.Length > 0 ? variableSnapshot : null;
        }

        if (!FieldIdReference.TryParse(name, out var fid)) return null;
        if (separator < 0) return null; // typed by ID — stays an ID

        if (_byFid.TryGetValue(fid, out var field))
        {
            var label = DisplayName(field);
            return _variablesByName.ContainsKey(label.Trim()) ? $"{FieldIdReference.Prefix}{fid}" : label;
        }

        var snapshot = name[(separator + 1)..].Trim();
        return snapshot.Length > 0 ? snapshot : null;
    });

    /// <summary>Applies <see cref="ToStored"/> to the formula text inside a formula field's Settings JSON.
    /// Returns the input untouched for non-formula types, unparseable JSON, or when nothing changes.</summary>
    public string? ToStoredSettings(string typeCode, string? settingsJson) => Transform(typeCode, settingsJson, ToStored);

    /// <summary>Applies <see cref="ToDisplay"/> to the formula text inside a formula field's Settings JSON.</summary>
    public string? ToDisplaySettings(string typeCode, string? settingsJson) => Transform(typeCode, settingsJson, ToDisplay);

    private static string DisplayName(AppField field) => string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;

    private static string? Transform(string typeCode, string? settingsJson, Func<string, string> convert)
    {
        if (string.IsNullOrWhiteSpace(settingsJson) || !FormulaTypeMap.IsFormulaComputed(typeCode, settingsJson))
            return settingsJson;

        JsonObject? root;
        try { root = JsonNode.Parse(settingsJson) as JsonObject; }
        catch (JsonException) { return settingsJson; }
        if (root is null) return settingsJson;

        var changed = false;
        foreach (var property in root.ToList())
        {
            if (!FormulaProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;

            var converted = convert(text);
            if (string.Equals(converted, text, StringComparison.Ordinal)) continue;

            root[property.Key] = converted;
            changed = true;
        }

        return changed ? root.ToJsonString(WriteOptions) : settingsJson;
    }
}
