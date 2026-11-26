using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PowerBase.Formula;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Process-wide cache of compiled formulas (FRM-01). Compiling (lex, parse, bind, type-check) used to run for every formula on every
/// report page and record read. A <see cref="CompiledFormula"/> is immutable, so one compile can serve every later read with the same
/// inputs. The key holds everything the binder reads: the expression, the expected type, the exact field list the formula is bound
/// against (names, labels, types — <see cref="AppFieldSchema.Signature"/>) and the app variables, whose values are baked in at bind
/// time (<see cref="AppVariableSchema.Signature"/>). A field rename, type change or variable edit therefore gives a new key: a stale
/// compile is never used. Bounded: cleared when it holds more than <see cref="MaxEntries"/> compiles.
/// </summary>
public static class CompiledFormulaCache
{
    public const int MaxEntries = 20_000;
    private static readonly ConcurrentDictionary<string, CompiledFormula> Cache = new(StringComparer.Ordinal);

    public static int Count => Cache.Count;

    public static CompiledFormula GetOrCompile(FormulaEngine engine, string expression, AppFieldSchema schema, FormulaType? expectedType,
        AppVariableSchema? variables = null)
    {
        // The engine instance too: another engine may carry other functions (one engine per process in the application).
        var key = string.Concat(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "\u0001", expectedType?.ToString() ?? "-", "\u0001", schema.Signature, "\u0001", variables?.Signature ?? "-", "\u0001", expression);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var compiled = engine.Compile(expression, schema, expectedType, variableSchema: variables);
        if (Cache.Count >= MaxEntries) Cache.Clear();
        Cache[key] = compiled;
        return compiled;
    }

    /// <summary>A short, stable hash of a schema's description (SHA-256, hex) — keeps keys small for tables with hundreds of fields.</summary>
    internal static string Hash(StringBuilder description) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description.ToString())));
}
