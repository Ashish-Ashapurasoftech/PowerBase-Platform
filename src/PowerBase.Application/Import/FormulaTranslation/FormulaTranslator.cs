using System.Text.Json;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Import.FormulaTranslation;

public enum FormulaTranslationStatus
{
    /// <summary>Compiled without errors; ready to persist as-is.</summary>
    Clean,
    /// <summary>Reserved for the QBL→PBL conversion phase, where the raw source expression is
    /// auto-rewritten (e.g. renamed functions) before compiling. Importing PBL directly never
    /// produces this — a PBL formula either compiles clean or needs manual review.</summary>
    Adjusted,
    /// <summary>Did not compile against the table's fields; skipped rather than imported broken.</summary>
    NeedsManualReview,
}

public sealed class FormulaTranslationResult
{
    public FormulaTranslationStatus Status { get; init; }
    /// <summary>JSON settings (<c>{ "expression": ... }</c>) for the <see cref="TypeCode"/> variant, ready for
    /// <c>AppField.Settings</c>. Null unless Status is Clean/Adjusted.</summary>
    public string? SettingsJson { get; init; }

    /// <summary>The <c>Formula_{X}</c> field type to create. Tenant databases only carry the per-result-type
    /// variants (Formula_Text, Formula_Number, …) — the bare "Formula" type does not exist there.</summary>
    public string? TypeCode { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>
/// Validates a PBL formula field's expression against a table's already-created fields, using
/// the same <see cref="FormulaEngine"/> the live authoring UI compiles against (see
/// <c>ValidateFormulaQueryHandler</c>). A formula field can only be created once the scalar
/// fields it may reference exist and have Fids assigned — see the two-pass field creation in
/// <c>ImportAppFromPblCommandHandler</c>.
/// </summary>
public class FormulaTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const int MAX_REWRITE_PASSES = 4;

    private readonly FormulaEngine _engine;

    public FormulaTranslator(FormulaEngine engine)
    {
        _engine = engine;
    }

    /// <param name="aliasSchema">Resolves <c>[_DBID_*]</c> table tokens (used by Quickbase "add record" URL formulas). Null = none resolve.</param>
    public FormulaTranslationResult Translate(string? resultType, string? expression, IReadOnlyList<AppField> tableFields, ITableAliasSchema? aliasSchema = null, IVariableSchema? variableSchema = null)
    {
        variableSchema ??= Jobs.ImportRuntime.Variables;
        if (string.IsNullOrWhiteSpace(expression))
            return new FormulaTranslationResult
            {
                Status = FormulaTranslationStatus.NeedsManualReview,
                Diagnostics = ["Formula expression is empty."],
            };

        var schema = new AppFieldSchema(tableFields);
        var expectedType = FormulaTypeMap.ParseExpected(resultType);
        var compiled = _engine.Compile(expression, schema, expectedType, aliasSchema, variableSchema);

        // "var" declared inside an If branch (or any call argument) is Quickbase-only: move it to the top and compile again.
        if (compiled.HasErrors && compiled.Diagnostics.Any(d => d.Message.StartsWith("Unexpected 'var'", StringComparison.Ordinal))
            && QuickbaseDialectRewriter.HoistNestedVariables(expression) is { } hoisted)
        {
            expression = hoisted;
            compiled = _engine.Compile(expression, schema, expectedType, aliasSchema, variableSchema);
            if (!compiled.HasErrors)
                return Success(FormulaTranslationStatus.Adjusted, compiled.ResultType.ToString(), expression);
        }

        if (compiled.HasErrors)
        {
            // Before giving up, see whether the only thing wrong is a place where Quickbase's
            // dialect is looser than PowerBase's, and retry the rewritten form. The rewrite is
            // what gets persisted, so the stored formula stays valid under the same rules the
            // authoring UI applies.
            // One formula can need several fixes (a text/number concatenation AND a comparison AND a multi-branch If), and fixing one
            // can expose the next, so rewrite and re-check a few times.
            var current = expression;
            var currentDiagnostics = compiled.Diagnostics;
            for (var pass = 0; pass < MAX_REWRITE_PASSES; pass++)
            {
                var rewritten = QuickbaseDialectRewriter.TryRewrite(current, currentDiagnostics);
                if (rewritten is null || rewritten == current)
                    break;

                var recompiled = _engine.Compile(rewritten, schema, expectedType, aliasSchema, variableSchema);
                if (!recompiled.HasErrors)
                    return Success(FormulaTranslationStatus.Adjusted, recompiled.ResultType.ToString(), rewritten);

                current = rewritten;
                currentDiagnostics = recompiled.Diagnostics;
            }

            // The rewrite didn't get there — report against the original so the diagnostics
            // point at what the author actually wrote.

            return new FormulaTranslationResult
            {
                Status = FormulaTranslationStatus.NeedsManualReview,
                Diagnostics = compiled.Diagnostics.Select(d => d.Message).ToList(),
            };
        }

        return Success(FormulaTranslationStatus.Clean, compiled.ResultType.ToString(), expression);
    }

    private static FormulaTranslationResult Success(FormulaTranslationStatus status, string resultType, string expression) =>
        new()
        {
            Status = status,
            TypeCode = VariantTypeCode(resultType),
            SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["expression"] = expression }, JsonOptions),
        };

    /// <summary>Maps the compiled result type to its <c>Formula_{X}</c> field type code.</summary>
    public static string VariantTypeCode(string resultType) => resultType switch
    {
        nameof(FormulaType.Number) => "Formula_Number",
        nameof(FormulaType.Date) => "Formula_Date",
        nameof(FormulaType.DateTime) => "Formula_DateTime",
        nameof(FormulaType.Time) => "Formula_Time",
        nameof(FormulaType.Duration) => "Formula_Duration",
        nameof(FormulaType.Bool) => "Formula_Bool",
        nameof(FormulaType.User) => "Formula_User",
        nameof(FormulaType.UserList) => "Formula_ListUser",
        nameof(FormulaType.TextList) => "Formula_MultiSelect",
        _ => "Formula_Text",
    };
}
