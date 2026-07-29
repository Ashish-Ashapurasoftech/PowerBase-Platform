using System.Text.Json;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;

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
    /// <summary>JSON-serialized <see cref="FormulaSettings"/>, ready for <c>AppField.Settings</c>. Null unless Status is Clean.</summary>
    public string? SettingsJson { get; init; }
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

    private readonly FormulaEngine _engine;

    public FormulaTranslator(FormulaEngine engine)
    {
        _engine = engine;
    }

    public FormulaTranslationResult Translate(string? resultType, string? expression, IReadOnlyList<AppField> tableFields)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return new FormulaTranslationResult
            {
                Status = FormulaTranslationStatus.NeedsManualReview,
                Diagnostics = ["Formula expression is empty."],
            };

        var schema = new AppFieldSchema(tableFields);
        var expectedType = FormulaTypeMap.ParseExpected(resultType);
        var compiled = _engine.Compile(expression, schema, expectedType);

        if (compiled.HasErrors)
        {
            // Before giving up, see whether the only thing wrong is a place where Quickbase's
            // dialect is looser than PowerBase's, and retry the rewritten form. The rewrite is
            // what gets persisted, so the stored formula stays valid under the same rules the
            // authoring UI applies.
            var rewritten = QuickbaseDialectRewriter.TryRewrite(expression, compiled.Diagnostics);
            if (rewritten is not null)
            {
                var recompiled = _engine.Compile(rewritten, schema, expectedType);
                if (!recompiled.HasErrors)
                    return Success(FormulaTranslationStatus.Adjusted, recompiled.ResultType.ToString(), rewritten);

                // The rewrite didn't get there — report against the original so the diagnostics
                // point at what the author actually wrote.
            }

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
            SettingsJson = JsonSerializer.Serialize(
                new FormulaSettings { ResultType = resultType, Expression = expression }, JsonOptions),
        };
}
