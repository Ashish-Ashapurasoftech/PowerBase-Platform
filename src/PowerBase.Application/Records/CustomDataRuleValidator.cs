using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Records;

/// <summary>
/// Evaluates a table's optional Custom Data Rule formula as a save-time gate — the formula
/// counterpart to <see cref="RecordConstraintValidator"/>'s Required/Unique checks, run
/// immediately after it from the same call sites (<c>CreateRecordCommandHandler</c>,
/// <see cref="RecordWriteService"/>) so every Add/Update goes through both. Per the feature's
/// scope, this never runs on Delete, mass-update, or any write to a table other than the one
/// being saved — it only answers "is this record allowed to be saved?" against the row's own
/// effective values, with read-only access to other tables via GetRecords/[_DBID_*].
/// </summary>
public static class CustomDataRuleValidator
{
    public static async Task ValidateAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        FormulaEngine engine,
        CancellationToken ct)
    {
        // No-op unless the "Turn custom data rules on?" toggle is on — merely having rule text
        // stored doesn't activate enforcement; the toggle does.
        if (!table.IsCustomDataRuleEnabled || string.IsNullOrWhiteSpace(table.CustomDataRule)) return;

        var schema = new AppFieldSchema(fields);
        var aliasSchema = await AppTableAliasSchema.BuildAsync(tableRepo, table.AppId, ct);
        var compiled = engine.Compile(table.CustomDataRule, schema, FormulaType.Text, aliasSchema);
        if (compiled.HasErrors)
        {
            // The rule was validated at save time (UpdateTableCommandHandler) and shouldn't be
            // able to get here broken — fail safe rather than block every write on a table whose
            // rule somehow went stale (e.g. a field it referenced was since deleted).
            return;
        }

        var crossTable = new CrossTableQueryContext(tableRepo, fieldRepo, recordRepo, table);
        var context = new CrossTableRecordContext(new ValuesRecordContext(effectiveValues), crossTable);
        var options = new EvaluationOptions { TableId = table.PublicId.ToString() };

        FormulaValue result;
        try { result = engine.Evaluate(compiled, context, options); }
        catch (FormulaEvaluationException) { return; } // same fail-safe as a stale compile above

        var message = result.AsText();
        if (!string.IsNullOrWhiteSpace(message))
            throw new ValidationException(new Dictionary<string, string[]> { ["customDataRule"] = [message] });
    }
}
