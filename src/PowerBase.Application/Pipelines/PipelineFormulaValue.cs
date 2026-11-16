using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// A PowerFlow step value written as a formula — the same formula language as a table's formula fields — instead of text with
/// references. Stored in the value itself, so every place that keeps a value keeps a formula unchanged:
/// <c>@formula|{"expression":"\"CST\" &amp; ToText([{{steps.ref_aa.fid_3}}])","fields":[{"token":"{{steps.ref_aa.fid_3}}","type":"Number"}]}</c>.
/// The expression names each earlier step's field by its reference token in brackets (a step or field renamed later still
/// resolves); <c>fields</c> carries each token's type, so the formula is checked and run with the right types. A value without
/// the prefix is an ordinary value and is untouched.
/// </summary>
public static class PipelineFormulaValue
{
    public const string Prefix = "@formula|";
    public const int MaxExpressionLength = 4000;

    public sealed record Field(string Token, string? Type);

    public sealed record Definition(string Expression, IReadOnlyList<Field> Fields);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, (CompiledFormula Compiled, IReadOnlyList<Field> Fields, IReadOnlyList<AppField> Schema)> Cache = new(StringComparer.Ordinal);

    public static bool IsFormula(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static Definition? Parse(string? value)
    {
        if (!IsFormula(value))
            return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredDefinition>(value![Prefix.Length..], Json);
            if (stored?.Expression is null)
                return null;
            var fields = (stored.Fields ?? [])
                .Where(f => !string.IsNullOrWhiteSpace(f.Token))
                .GroupBy(f => f.Token!, StringComparer.Ordinal)
                .Select(g => new Field(g.Key, g.First().Type))
                .ToList();
            return new Definition(stored.Expression, fields);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Create(Definition definition) =>
        Prefix + JsonSerializer.Serialize(new StoredDefinition
        {
            Expression = definition.Expression,
            Fields = definition.Fields.Select(f => new StoredField { Token = f.Token, Type = f.Type }).ToList(),
        });

    /// <summary>Compiles the formula against its fields (each a virtual field named f1, f2…), or returns the problems.</summary>
    public static (CompiledFormula? Compiled, IReadOnlyList<Field> Fields, IReadOnlyList<FormulaDiagnostic> Diagnostics) Compile(Definition definition, FormulaEngine engine)
    {
        if (definition.Expression.Length > MaxExpressionLength)
            return (null, definition.Fields, [Problem($"The formula is too long (at most {MaxExpressionLength} characters).")]);
        var compiled = engine.Compile(definition.Expression, new AppFieldSchema(SchemaFields(definition)));
        return compiled.HasErrors ? (null, definition.Fields, compiled.Diagnostics) : (compiled, definition.Fields, compiled.Diagnostics);
    }

    /// <summary>Runs a formula value with the app's formula engine: each field token is resolved by <paramref name="resolveToken"/> (as
    /// any reference is) and read as its type, and the result is returned as text the way a value is used after references are replaced.
    /// <paramref name="optionsFor"/> builds the evaluation options as every other formula gets them, given the formula's fields.</summary>
    public static string Evaluate(string value, Func<string, string?> resolveToken, FormulaEngine engine, Func<IReadOnlyList<AppField>, EvaluationOptions> optionsFor)
    {
        var entry = Cache.TryGetValue(value, out var cached) ? cached : CompileForRun(value, engine);
        var values = new Dictionary<long, object?>();
        for (var i = 0; i < entry.Fields.Count; i++)
            values[i + 1] = Read(resolveToken(entry.Fields[i].Token), entry.Fields[i].Type);
        var result = engine.Evaluate(entry.Compiled, new ValuesContext(values), optionsFor(entry.Schema));
        return ToText(result);
    }

    private static (CompiledFormula Compiled, IReadOnlyList<Field> Fields, IReadOnlyList<AppField> Schema) CompileForRun(string value, FormulaEngine engine)
    {
        var definition = Parse(value) ?? throw new PipelineStepException("The formula value could not be read.");
        var (compiled, fields, diagnostics) = Compile(definition, engine);
        if (compiled is null)
            throw new PipelineStepException("The formula has an error: " + string.Join(" ", diagnostics.Where(d => d.Severity == FormulaSeverity.Error).Select(d => d.Message)));
        if (Cache.Count > 2000)
            Cache.Clear();
        var entry = (compiled, fields, SchemaFields(definition));
        Cache[value] = entry;
        return entry;
    }

    /// <summary>The formula's fields as virtual fields (Fid 1, 2…), each named by its token (or, while the formula is being typed, by the
    /// name the editor shows), so the expression is compiled as written and its error positions match the text.</summary>
    public static IReadOnlyList<AppField> SchemaFields(Definition definition) =>
        definition.Fields.Select((f, i) => new AppField
        {
            Id = i + 1, Fid = i + 1, Name = f.Token, Label = f.Token, TypeCode = TypeCode(f.Type),
        }).ToList();

    /// <summary>A field type as the step editors name it (text / number / date / boolean …) or as a field TypeCode.</summary>
    public static string TypeCode(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "number" or "numeric" or "currency" or "percent" or "rating" or "recordid" => "Number",
        "date" => "Date",
        "datetime" or "date_time" or "timestamp" => "DateTime",
        "boolean" or "bool" or "checkbox" => "Boolean",
        _ => "Text",
    };

    private static object? Read(string? raw, string? type)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim();
        switch (TypeCode(type))
        {
            case "Number":
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : text;
            case "Date":
            case "DateTime":
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : text;
            case "Boolean":
                return text.ToLowerInvariant() switch { "true" or "1" or "yes" or "y" => true, "false" or "0" or "no" or "n" => false, _ => text };
            default:
                return text;
        }
    }

    private static string ToText(FormulaValue value)
    {
        if (value.IsNull)
            return string.Empty;
        return value.Type switch
        {
            FormulaType.Number => value.AsNumber().ToString(CultureInfo.InvariantCulture),
            FormulaType.Bool => value.AsBool() ? "true" : "false",
            FormulaType.Date => value.AsDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FormulaType.DateTime => value.AsDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            _ => FormulaRawValue.ToRaw(value) switch
            {
                null => string.Empty,
                IEnumerable<string> list => string.Join(", ", list),
                var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty,
            },
        };
    }

    private static FormulaDiagnostic Problem(string message) => new(FormulaErrorCode.FormulaTooComplex, message, default);

    private sealed class ValuesContext(IReadOnlyDictionary<long, object?> values) : IRecordContext
    {
        public object? GetValue(long fid) => values.TryGetValue(fid, out var value) ? value : null;
    }

    private sealed class StoredDefinition
    {
        public string? Expression { get; set; }
        public List<StoredField>? Fields { get; set; }
    }

    private sealed class StoredField
    {
        public string? Token { get; set; }
        public string? Type { get; set; }
    }

    /// <summary>Every formula value in a step's settings must compile; a step marked complete with a broken one is refused when it is
    /// saved, with the formula's own error. A draft step is saved as it is, like any other incomplete setting.</summary>
    public static void ValidateStepConfig(string? configJson, string? stepLabel, FormulaEngine engine)
    {
        if (string.IsNullOrEmpty(configJson) || !configJson.Contains(Prefix, StringComparison.Ordinal))
            return;
        JsonDocument document;
        try { document = JsonDocument.Parse(configJson); }
        catch (JsonException) { return; }
        using (document)
        {
            var problems = new List<string>();
            Walk(document.RootElement, value =>
            {
                var definition = Parse(value);
                var diagnostics = definition is null ? null : Compile(definition, engine).Diagnostics;
                if (definition is null)
                    problems.Add("A formula could not be read.");
                else if (diagnostics!.Any(d => d.Severity == FormulaSeverity.Error))
                    problems.Add(string.Join(" ", diagnostics!.Where(d => d.Severity == FormulaSeverity.Error).Select(d => d.Message)));
            });
            if (problems.Count > 0)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["Formula"] = problems.Select(p => $"Step '{stepLabel}': the formula has an error: {p}").ToArray(),
                });
        }
    }

    private static void Walk(JsonElement element, Action<string> onFormula)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) Walk(property.Value, onFormula);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, onFormula);
                break;
            case JsonValueKind.String when IsFormula(element.GetString()):
                onFormula(element.GetString()!);
                break;
        }
    }

    /// <summary>The field tokens a formula value reads — for the checks that look at which steps a value refers to.</summary>
    public static IReadOnlyList<string> Tokens(string? value) => Parse(value)?.Fields.Select(f => f.Token).ToList() ?? [];

    internal static readonly Regex TokenPattern = new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled);
}
