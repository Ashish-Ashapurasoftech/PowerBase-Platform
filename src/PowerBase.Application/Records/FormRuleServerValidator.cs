using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Records;

/// <summary>
/// Server-side mirror of FormRendererComponent's client-side rule evaluation
/// (form-renderer.component.ts's evaluateRules/conditionsMet) — enforces the two rule actions
/// that actually mean "reject this write", Require and PreventSave, against every active Form
/// Rule on every form attached to the table being written, so a client that bypasses the
/// Add/Edit Record UI entirely (a direct API call) can't skip a rule the UI would have blocked
/// on. Every other action type (Show/Hide/Enable/Disable/ChangeLabel/ChangeValue/SetColor/
/// DisplayMessage) is a UI-presentation concern with no server-side meaning and is silently
/// skipped — see FormRuleActionType's doc comment for the full list.
///
/// Scope is table-wide, not form-wide: a direct API write carries no "which form" context, so
/// unlike the client's single-form evaluation, this enforces the union of every active rule
/// across every form on the table (IFormRuleRepository.ListActiveByTableIdAsync).
///
/// Mirrors RecordConstraintValidator's dual-method shape (throwing ValidateAsync for
/// single-record callers, non-throwing CollectViolationsAsync for mass-update's aggregate-then-
/// throw-once pattern) and CustomDataRuleValidator's fail-open posture for anything that can't be
/// evaluated cleanly (a rule referencing a since-deleted field, or an Expression Mode formula
/// that fails to compile/evaluate) — that rule is skipped, not treated as a blocking failure,
/// since it was valid when authored and shouldn't be able to jam every future write on the table.
/// </summary>
public static class FormRuleServerValidator
{
    public static async Task ValidateAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IReadOnlyDictionary<long, object?>? oldValuesByFid,
        string? currentUserRole,
        long currentUserId,
        IFormRuleRepository ruleRepo,
        IFormRepository formRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        FormulaEngine engine,
        CancellationToken ct)
    {
        var violations = await CollectViolationsAsync(
            table, fields, effectiveValues, oldValuesByFid, currentUserRole, currentUserId,
            ruleRepo, formRepo, tableRepo, fieldRepo, recordRepo, engine, ct);
        if (violations.Count > 0)
            // Grouped, not a plain ToDictionary — more than one rule can flag the same field
            // (e.g. two separate Require rules), which a flat ToDictionary can't hold.
            throw new ValidationException(
                violations.GroupBy(v => v.FieldId.ToString())
                    .ToDictionary(g => g.Key, g => g.Select(v => v.Message).ToArray()));
    }

    /// <summary>Same rules as <see cref="ValidateAsync"/>, but collects every violation instead
    /// of throwing on the first — used by mass-update's all-or-nothing pre-flight, alongside
    /// RecordConstraintValidator.CollectViolationsAsync.</summary>
    /// <param name="oldValuesByFid">The record's values before this write, keyed by Fid — null on
    /// create (there is no "before"). 'changed'/'notChanged' conditions never match when null.</param>
    /// <param name="currentUserRole">The evaluating user's role name (IQueryContext.TenantRole),
    /// compared directly against a role condition's Value — same convention the frontend's
    /// AppPermissionService.roleName comparison uses.</param>
    /// <param name="currentUserId">The evaluating user's numeric id (IQueryContext.UserId) —
    /// resolves a User/MultiUser field condition's "the current user" option
    /// (FormRuleCondition.Value containing the '__current_user__' sentinel). This is the numeric
    /// space a User field's value lives in server-side (UserFieldValueResolver has already turned
    /// any userPublicId into this same long id by the time effectiveValues reaches here) — NOT
    /// the GUID the frontend's own resolution uses, which compares against the field's live
    /// pre-save value instead (see AppPermissionsResult.CurrentUserPublicId).</param>
    /// <param name="recordId">Echoed back on each violation for the caller's own reporting; not
    /// used for lookup. Defaults to Guid.Empty for single-record callers that don't need it.</param>
    public static async Task<List<RecordConstraintViolation>> CollectViolationsAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IReadOnlyDictionary<long, object?>? oldValuesByFid,
        string? currentUserRole,
        long currentUserId,
        IFormRuleRepository ruleRepo,
        IFormRepository formRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        FormulaEngine engine,
        CancellationToken ct,
        Guid recordId = default)
    {
        var violations = new List<RecordConstraintViolation>();

        var rules = await ruleRepo.ListActiveByTableIdAsync(table.Id, ct);
        if (rules.Count == 0) return violations;

        var fieldsByFid = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
        var isCreate = oldValuesByFid is null;

        // A rule's Require/PreventSave actions target a form-layout element (TargetElementId),
        // not an AppField directly — resolved to AppFieldId via that rule's own form layout,
        // cached per FormId since several rules across the table can share one form.
        var elementFieldCache = new Dictionary<long, Dictionary<long, long?>>();

        foreach (var rule in rules)
        {
            bool met;
            if (rule.IsExpressionMode)
            {
                met = await EvaluateExpressionConditionAsync(rule, table, fields, effectiveValues, tableRepo, fieldRepo, recordRepo, engine, ct);
            }
            else
            {
                met = ConditionsMet(rule, effectiveValues, oldValuesByFid, currentUserRole, currentUserId);
            }
            if (!met) continue;

            if (!elementFieldCache.TryGetValue(rule.FormId, out var elementFieldMap))
            {
                var sections = await formRepo.GetLayoutAsync(rule.FormId, ct);
                elementFieldMap = sections
                    .SelectMany(s => s.Blocks)
                    .SelectMany(b => b.Elements)
                    .ToDictionary(e => e.Id, e => e.AppFieldId);
                elementFieldCache[rule.FormId] = elementFieldMap;
            }

            foreach (var action in rule.Actions)
            {
                if (action.ActionType == "PreventSave")
                {
                    var message = string.IsNullOrWhiteSpace(action.ActionValue)
                        ? $"This change is not allowed ('{rule.Name}')."
                        : action.ActionValue;
                    violations.Add(new RecordConstraintViolation(recordId, 0, "FormRule", message));
                    continue;
                }

                if (action.ActionType != "Require") continue;
                if (action.TargetElementId is not { } elementId) continue;
                if (!elementFieldMap.TryGetValue(elementId, out var fid) || fid is not { } fidVal) continue;
                if (!fieldsByFid.TryGetValue(fidVal, out var field)) continue;

                var hasValue = effectiveValues.TryGetValue(fidVal, out var value);
                // Update: a field this write never touched isn't checked (mirrors
                // RecordConstraintValidator's own Required check for the same reason — nothing
                // about this write is responsible for that field's existing, already-saved value).
                if (!hasValue && !isCreate) continue;
                var isBlank = !hasValue || PhysicalNaming.IsRequiredMissing(field.TypeCode, value);
                if (!isBlank) continue;

                var label = !string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name;
                violations.Add(new RecordConstraintViolation(recordId, fidVal, "FormRule", $"'{label}' is required."));
            }
        }

        return violations;
    }

    private static bool ConditionsMet(
        FormRule rule,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IReadOnlyDictionary<long, object?>? oldValuesByFid,
        string? currentUserRole,
        long currentUserId)
    {
        if (rule.Conditions.Count == 0) return true;

        var results = rule.Conditions.Select(c =>
        {
            if (c.Operator is "changed" or "notChanged")
            {
                if (c.AppFieldId is not { } fid) return false;
                var oldVal = oldValuesByFid != null && oldValuesByFid.TryGetValue(fid, out var ov) ? ov : null;
                var newVal = effectiveValues.TryGetValue(fid, out var nv) ? nv : oldVal;
                // No "before" at all (create) — a field can't have "changed" from nothing it was
                // ever loaded with, so 'changed' never matches and 'notChanged' always does.
                var changed = oldValuesByFid != null && !ValuesEqual(oldVal, newVal);
                return c.Operator == "changed" ? changed : !changed;
            }

            if (c.ConditionKind == "role")
                return EvalOp(c.Operator, currentUserRole, c.Value);

            // during/notDuring check range CONTAINMENT (is this date inside a computed
            // {start,end} window), not a single-value comparison — doesn't fit EvalOp's
            // (current, ruleValue) contract, so it's handled here the same way changed/notChanged
            // is above. Mirrors the frontend's computeDuringRange (form-renderer.component.ts),
            // but stays on UTC (DateTime.UtcNow.Date) rather than that method's local-date
            // anchoring — same accepted, narrow client/server split 'today'/'yesterday' already
            // has here, only reachable via a direct API call bypassing the form UI entirely.
            if (c.Operator is "during" or "notDuring")
            {
                if (c.AppFieldId is not { } dfid || !effectiveValues.TryGetValue(dfid, out var dval) || dval is null)
                    return false;
                var dateStr = dval switch
                {
                    DateTime dt => dt.ToString("yyyy-MM-dd"),
                    DateOnly d => d.ToString("yyyy-MM-dd"),
                    _ => Convert.ToString(dval, System.Globalization.CultureInfo.InvariantCulture)?.Split('T')[0] ?? "",
                };
                var (count, unit) = ParseDuringValue(c.Value);
                var direction = c.ValueType ?? "duringCurrent";
                var (start, end) = ComputeDuringRange(direction, count, unit);
                var inRange = string.CompareOrdinal(dateStr, start) >= 0 && string.CompareOrdinal(dateStr, end) <= 0;
                return c.Operator == "during" ? inRange : !inRange;
            }

            var fieldVal = c.AppFieldId is { } f && effectiveValues.TryGetValue(f, out var v) ? v : null;
            var ruleVal = ResolveConditionValue(c, effectiveValues, currentUserId);
            return EvalOp(c.Operator, fieldVal, ruleVal);
        }).ToList();

        return rule.ConditionLogic == "all" ? results.All(r => r) : results.Any(r => r);
    }

    /// <summary>Normalized equality for 'changed'/'notChanged' — mirrors the frontend's
    /// valuesEqual: blank/null all count as the same "nothing", and everything else compares as
    /// a trimmed string so type/format noise (e.g. a decimal vs its string form) doesn't register
    /// as a change.</summary>
    private static bool ValuesEqual(object? a, object? b)
    {
        static string Norm(object? v)
        {
            if (v is null) return "";
            if (v is string s) return s.Trim();
            if (v is DateTime dt) return dt.ToString("yyyy-MM-dd");
            if (v is DateOnly d) return d.ToString("yyyy-MM-dd");
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
        }
        return Norm(a) == Norm(b);
    }

    /// <summary>Resolves a condition's relative-date valueType ('today'/'yesterday'/'tomorrow'/
    /// 'pastDays'/'futureDays'/'field') into a plain comparable string, mirroring the frontend's
    /// resolveConditionValue. Falls back to the condition's literal Value for everything else —
    /// substituting the '__current_user__' sentinel (comma-joined alongside literal userIds for
    /// includes/notIncludes, or standalone for eq/ne) with the evaluating user's own id first.</summary>
    private static string? ResolveConditionValue(FormRuleCondition c, IReadOnlyDictionary<long, object?> effectiveValues, long currentUserId)
    {
        var today = DateTime.UtcNow.Date;
        string Fmt(DateTime d) => d.ToString("yyyy-MM-dd");

        switch (c.ValueType)
        {
            case "today": return Fmt(today);
            case "yesterday": return Fmt(today.AddDays(-1));
            case "tomorrow": return Fmt(today.AddDays(1));
            case "pastDays": return Fmt(today.AddDays(-(int.TryParse(c.Value, out var pd) ? pd : 0)));
            case "futureDays": return Fmt(today.AddDays(int.TryParse(c.Value, out var fd) ? fd : 0));
            case "field":
                if (c.ValueFieldId is { } vf && effectiveValues.TryGetValue(vf, out var refVal) && refVal != null)
                    return Convert.ToString(refVal, System.Globalization.CultureInfo.InvariantCulture);
                return null;
            default:
                if (c.Value is not null && c.Value.Contains("__current_user__"))
                    return string.Join(',', c.Value.Split(',').Select(s => s.Trim() == "__current_user__" ? currentUserId.ToString() : s.Trim()));
                return c.Value;
        }
    }

    private static readonly string[] DuringUnits = ["day", "week", "month", "quarter", "year"];

    /// <summary>"count:unit" (e.g. "2:month") — same encoding the frontend's parseDuringValue
    /// (both rule-condition-row.component.ts's UI copy and form-renderer.component.ts's runtime
    /// copy) and the report engine's ParseDuringValue (filter-condition-operators.ts) use.
    /// Malformed/missing input falls back to 1 week.</summary>
    private static (int Count, string Unit) ParseDuringValue(string? value)
    {
        var parts = (value ?? "").Split(':');
        var count = parts.Length > 0 && int.TryParse(parts[0], out var c) && c > 0 ? c : 1;
        var unit = parts.Length > 1 && DuringUnits.Contains(parts[1]) ? parts[1] : "week";
        return (count, unit);
    }

    /// <summary>Port of RunReportQueryHandler's ResolveDuringCondition/ComputeDuringRange (the
    /// report Static Filters' "is during" resolution) — same period-boundary math (Monday-aligned
    /// weeks, calendar month/quarter/year starts, "previous" excludes the current in-progress
    /// period, "next" starts the day after the current period ends), anchored to
    /// DateTime.UtcNow.Date like every other date resolution in THIS file (see
    /// ResolveConditionValue's 'today') — see ConditionsMet's call site for why this stays UTC
    /// rather than matching the frontend's local-date version.</summary>
    private static (string Start, string End) ComputeDuringRange(string direction, int count, string unit)
    {
        var today = DateTime.UtcNow.Date;
        string Fmt(DateTime d) => d.ToString("yyyy-MM-dd");

        DateTime StartOfPeriod(DateTime d) => unit switch
        {
            "day" => d.Date,
            "month" => new DateTime(d.Year, d.Month, 1),
            "quarter" => new DateTime(d.Year, ((d.Month - 1) / 3) * 3 + 1, 1),
            "year" => new DateTime(d.Year, 1, 1),
            _ => d.Date.AddDays(d.DayOfWeek == DayOfWeek.Sunday ? -6 : 1 - (int)d.DayOfWeek), // week, Monday-aligned
        };

        DateTime AddUnits(DateTime d, int amount) => unit switch
        {
            "day" => d.AddDays(amount),
            "month" => d.AddMonths(amount),
            "quarter" => d.AddMonths(amount * 3),
            "year" => d.AddYears(amount),
            _ => d.AddDays(amount * 7), // week
        };

        DateTime EndOfPeriod(DateTime periodStart) => AddUnits(periodStart, 1).AddDays(-1);

        var curStart = StartOfPeriod(today);
        var curEnd = EndOfPeriod(curStart);

        if (direction == "duringPrevious")
            return (Fmt(AddUnits(curStart, -count)), Fmt(curStart.AddDays(-1)));

        if (direction == "duringNext")
            return (Fmt(curEnd.AddDays(1)), Fmt(EndOfPeriod(AddUnits(curStart, count))));

        // duringCurrent (and any unrecognized ValueType — same fail-open default the frontend and
        // report engine both use)
        return (Fmt(curStart), Fmt(curEnd));
    }

    private static bool EvalOp(string op, object? formVal, string? ruleVal)
    {
        var r = ruleVal ?? "";
        var rawV = formVal switch
        {
            null => "",
            DateTime dt => dt.ToString("yyyy-MM-dd"),
            DateOnly d => d.ToString("yyyy-MM-dd"),
            _ => Convert.ToString(formVal, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
        // For date comparisons: strip time component so "2026-06-01T10:30:00" == "2026-06-01".
        var isDateRule = System.Text.RegularExpressions.Regex.IsMatch(r, @"^\d{4}-\d{2}-\d{2}$");
        var v = (isDateRule && rawV.Contains('T')) ? rawV.Split('T')[0] : rawV;

        switch (op)
        {
            case "eq": return v == r;
            case "ne": return v != r;
            case "contains": return v.Contains(r, StringComparison.OrdinalIgnoreCase);
            case "notContains": return !v.Contains(r, StringComparison.OrdinalIgnoreCase);
            case "startsWith": return v.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            case "notStartsWith": return !v.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            case "isEmpty": return v == "";
            case "isNotEmpty": return v != "";
            case "includes":
                return r.Split(',').Select(s => s.Trim())
                    .Any(id => v.Split(',').Select(s => s.Trim()).Contains(id));
            case "notIncludes":
                return !r.Split(',').Select(s => s.Trim())
                    .Any(id => v.Split(',').Select(s => s.Trim()).Contains(id));
            // Falls through Date -> Number -> ordinal string comparison — the last tier is what
            // makes gt/lt/gte/lte meaningful for a genuine text field (alphabetical "is after"/
            // "is before"), now that the client's text group offers these operators too;
            // previously anything that was neither a parseable date nor number returned false
            // unconditionally, silently disagreeing with the client-side mirror in evalOp
            // (form-renderer.component.ts) once that grew the same string fallback.
            case "gt": case "lt": case "gte": case "lte":
            {
                double? vd = DateTime.TryParse(v, out var vdt) ? vdt.Ticks : (double.TryParse(v, out var vn) ? vn : null);
                double? rd = DateTime.TryParse(r, out var rdt) ? rdt.Ticks : (double.TryParse(r, out var rn) ? rn : null);
                if (vd is not null && rd is not null)
                {
                    return op switch
                    {
                        "gt" => vd > rd,
                        "lt" => vd < rd,
                        "gte" => vd >= rd,
                        _ => vd <= rd,
                    };
                }
                var cmp = string.CompareOrdinal(v, r);
                return op switch
                {
                    "gt" => cmp > 0,
                    "lt" => cmp < 0,
                    "gte" => cmp >= 0,
                    _ => cmp <= 0,
                };
            }
            default: return false;
        }
    }

    /// <summary>Expression Mode rules use their ExpressionText as the condition itself (must
    /// compile to Bool), evaluated the same way CustomDataRuleValidator evaluates a table's
    /// Custom Data Rule — in-process via FormulaEngine, no HTTP round trip. Fail-open: a stale or
    /// erroring formula is treated as "condition not met" (this one rule contributes no
    /// violations) rather than as a match or a thrown error.</summary>
    private static async Task<bool> EvaluateExpressionConditionAsync(
        FormRule rule,
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        FormulaEngine engine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rule.ExpressionText)) return false;

        var schema = new AppFieldSchema(fields);
        var aliasSchema = await AppTableAliasSchema.BuildAsync(tableRepo, table.AppId, ct);
        var compiled = engine.Compile(rule.ExpressionText, schema, FormulaType.Bool, aliasSchema);
        if (compiled.HasErrors) return false;

        var crossTable = new CrossTableQueryContext(tableRepo, fieldRepo, recordRepo, table);
        var context = new CrossTableRecordContext(new ValuesRecordContext(effectiveValues), crossTable);
        var options = new EvaluationOptions { TableId = table.PublicId.ToString() };

        try { return engine.Evaluate(compiled, context, options).AsBool(); }
        catch (FormulaEvaluationException) { return false; }
    }
}
