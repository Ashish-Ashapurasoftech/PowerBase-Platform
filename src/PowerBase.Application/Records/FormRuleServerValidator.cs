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
        IUserRepository userRepo,
        FormulaEngine engine,
        CancellationToken ct)
    {
        var violations = await CollectViolationsAsync(
            table, fields, effectiveValues, oldValuesByFid, currentUserRole, currentUserId,
            ruleRepo, formRepo, tableRepo, fieldRepo, recordRepo, userRepo, engine, ct);
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
        IUserRepository userRepo,
        FormulaEngine engine,
        CancellationToken ct,
        Guid recordId = default)
    {
        var violations = new List<RecordConstraintViolation>();

        var rules = await ruleRepo.ListActiveByTableIdAsync(table.Id, ct);
        if (rules.Count == 0) return violations;

        var fieldsByFid = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
        var isCreate = oldValuesByFid is null;
        // A User/MultiUser field's raw value is a numeric user id (or comma-joined ids) — 'contains'
        // is a free-text name/email search, so every id any rule's 'contains' condition might need
        // is resolved to a display name once, up front, rather than per-condition (mirrors the
        // frontend's resolveUserDisplayNames, called from form-renderer.component.ts's conditionsMet).
        var userNames = await ResolveContainsUserNamesAsync(rules, fieldsByFid, effectiveValues, userRepo, ct);

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
                met = ConditionsMet(rule, effectiveValues, oldValuesByFid, currentUserRole, currentUserId, fieldsByFid, userNames);
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
                    string? message = null;
                    if (action.IsExpressionValue && !string.IsNullOrWhiteSpace(action.ActionValue))
                        message = await EvaluateExpressionActionValueAsync(action.ActionValue, table, fields, effectiveValues, tableRepo, fieldRepo, recordRepo, engine, ct);
                    else if (!string.IsNullOrWhiteSpace(action.ActionValue))
                        message = action.ActionValue;
                    message ??= $"This change is not allowed ('{rule.Name}').";
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
        long currentUserId,
        IReadOnlyDictionary<long, AppField> fieldsByFid,
        IReadOnlyDictionary<long, string> userNames)
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

            // DateRange/NumericRange: eq/ne compares the field's WHOLE {start,end} range against a
            // literal start/end pair (rule-condition-row.component.ts's rangeValue JSON encoding);
            // contains/notContains checks whether a single literal point falls inside the field's
            // range. Mirrors form-renderer.component.ts's identical range block — neither fits
            // EvalOp's (current, ruleValue) contract, so it's handled entirely here, same as
            // during/notDuring above.
            var rangeTypeCode = c.AppFieldId is { } rtf && fieldsByFid.TryGetValue(rtf, out var rangeField) ? rangeField.TypeCode : null;
            if (rangeTypeCode is "DateRange" or "NumericRange")
            {
                if (c.AppFieldId is not { } rfid) return false;
                var range = ParseRangeValue(effectiveValues.TryGetValue(rfid, out var rraw) ? rraw : null);
                if (c.Operator == "isEmpty") return range is null || (range.Value.Start is null && range.Value.End is null);
                if (c.Operator == "isNotEmpty") return range is not null && (range.Value.Start is not null || range.Value.End is not null);
                if (c.Operator is "eq" or "ne")
                {
                    var ruleRange = ParseRangeValue(c.Value);
                    var equal = RangesEqual(range, ruleRange, rangeTypeCode);
                    return c.Operator == "eq" ? equal : !equal;
                }
                var within = PointWithinRange(range, c.Value, rangeTypeCode);
                return c.Operator == "contains" ? within : !within;
            }

            var fieldVal = c.AppFieldId is { } f && effectiveValues.TryGetValue(f, out var v) ? v : null;
            var typeCode = rangeTypeCode;
            object? normalizedVal = NormalizeForCompare(typeCode, fieldVal);
            // Only 'contains' — eq/ne/includes/notIncludes must keep comparing raw user ids, which
            // NormalizeForCompare already leaves untouched (it only special-cases File/Address/
            // DateTime), mirroring the frontend's own contains-only gating in conditionsMet.
            if ((typeCode == "User" || typeCode == "MultiUser") && c.Operator == "contains")
                normalizedVal = ResolveUserDisplayNames(fieldVal, userNames);
            var ruleVal = ResolveConditionValue(c, effectiveValues, currentUserId, fieldsByFid);
            return EvalOp(c.Operator, normalizedVal, ruleVal);
        }).ToList();

        return rule.ConditionLogic == "all" ? results.All(r => r) : results.Any(r => r);
    }

    /// <summary>A DateRange/NumericRange field's raw value is a JSON "{start,end}" blob (or,
    /// depending on the repository's own deserialization, an already-parsed IDictionary) — mirrors
    /// the frontend's parseRangeValue. Each part is null when absent; both null collapses to a
    /// null range (an open-ended single-sided range is meaningfully different from empty).</summary>
    private static (string? Start, string? End)? ParseRangeValue(object? raw)
    {
        if (raw is null) return null;
        string? start, end;
        if (raw is System.Collections.IDictionary dict)
        {
            start = dict["start"]?.ToString();
            end = dict["end"]?.ToString();
        }
        else
        {
            var json = raw as string;
            if (json is null) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                start = GetRangePart(doc.RootElement, "start");
                end = GetRangePart(doc.RootElement, "end");
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
        if (string.IsNullOrEmpty(start)) start = null;
        if (string.IsNullOrEmpty(end)) end = null;
        return start is null && end is null ? null : (start, end);
    }

    /// <summary>Reads a "start"/"end" property as text regardless of whether it was serialized as a
    /// JSON string or a JSON number (a NumericRange's parts may be either, depending on what wrote
    /// them) — unlike JsonElement.GetString(), which throws for a Number-kind value.</summary>
    private static string? GetRangePart(System.Text.Json.JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => v.GetString(),
            System.Text.Json.JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    /// <summary>eq/ne's whole-range comparison — mirrors the frontend's rangesEqual: a
    /// NumericRange compares both parts numerically ("5" and "5.0" match), a DateRange compares
    /// calendar days only (strips any time component). Two null ranges (both blank) count as
    /// equal.</summary>
    private static bool RangesEqual((string? Start, string? End)? a, (string? Start, string? End)? b, string typeCode)
    {
        string? Norm(string? v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            return typeCode == "NumericRange"
                ? (double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : v)
                : v.Split('T')[0];
        }
        return Norm(a?.Start) == Norm(b?.Start) && Norm(a?.End) == Norm(b?.End);
    }

    /// <summary>contains/notContains — mirrors the frontend's pointWithinRange: is this single
    /// literal point (a date, or a number) inside the field's [start, end] range, inclusive on
    /// both ends? A missing start/end means that side is open-ended, not "never matches".</summary>
    private static bool PointWithinRange((string? Start, string? End)? range, string? ruleValue, string typeCode)
    {
        if (range is null || string.IsNullOrEmpty(ruleValue)) return false;
        if (typeCode == "NumericRange")
        {
            if (!double.TryParse(ruleValue, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
            var s = range.Value.Start != null && double.TryParse(range.Value.Start, System.Globalization.CultureInfo.InvariantCulture, out var sv) ? sv : double.NegativeInfinity;
            var e = range.Value.End != null && double.TryParse(range.Value.End, System.Globalization.CultureInfo.InvariantCulture, out var ev) ? ev : double.PositiveInfinity;
            return v >= s && v <= e;
        }
        var vDate = ruleValue.Split('T')[0];
        if (range.Value.Start != null && string.CompareOrdinal(vDate, range.Value.Start.Split('T')[0]) < 0) return false;
        if (range.Value.End != null && string.CompareOrdinal(vDate, range.Value.End.Split('T')[0]) > 0) return false;
        return true;
    }

    /// <summary>Server-side mirror of the frontend's normalizeFieldValueForCompare
    /// (form-renderer.component.ts) — File's raw JSON blob becomes a comma-joined filename list,
    /// Address's raw JSON blob becomes a comma-joined non-empty-parts text, and a DateTime field's
    /// value gets minute precision ("yyyy-MM-ddTHH:mm") instead of EvalOp's own date-only
    /// formatting (a plain Date field is deliberately left as a raw DateTime — EvalOp already
    /// formats those as date-only, which is correct for Date; only DateTime needs the extra
    /// time-of-day component). Anything else (typeCode null/unrecognized) passes through
    /// unchanged.</summary>
    private static object? NormalizeForCompare(string? typeCode, object? raw)
    {
        if (raw is null) return raw;
        if (typeCode == "File") return ExtractFileNames(raw);
        if (typeCode == "Address") return ExtractAddressText(raw);
        if (typeCode == "DateTime" && raw is DateTime dt) return dt.ToString("yyyy-MM-ddTHH:mm");
        return raw;
    }

    /// <summary>Mirrors the frontend's extractFileNames — a File field's raw value is a JSON blob
    /// (a single {name,...} object, or an array of them), never a plain string worth comparing
    /// verbatim.</summary>
    private static string ExtractFileNames(object? raw)
    {
        var json = raw as string;
        if (json is null) return Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var names = new List<string>();
            void AddName(System.Text.Json.JsonElement item)
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object
                    && item.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                    names.Add(n.GetString()!);
            }
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in root.EnumerateArray()) AddName(item);
            else
                AddName(root);
            return string.Join(", ", names);
        }
        catch (System.Text.Json.JsonException) { return json; }
    }

    /// <summary>Mirrors the frontend's extractAddressText — an Address field's raw value is a JSON
    /// blob ({street1, street2, city, state, zip, country}), flattened to its non-empty parts
    /// comma-joined, the same shape ExtractFileNames produces for a filename list.</summary>
    private static string ExtractAddressText(object? raw)
    {
        var json = raw as string;
        if (json is null) return Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
            string[] keys = ["street1", "street2", "city", "state", "zip", "country"];
            var parts = new List<string>();
            foreach (var k in keys)
                if (doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                    parts.Add(v.GetString()!);
            return string.Join(", ", parts);
        }
        catch (System.Text.Json.JsonException) { return json; }
    }

    /// <summary>Gathers every user id any active rule's 'contains' condition on a User/MultiUser
    /// field might need a display name for, and resolves them all in one batched call — mirrors
    /// the frontend's resolveUserDisplayNames, just done once up front here rather than lazily per
    /// condition (this runs before the per-rule Conditions loop, so it has to look across every
    /// rule's conditions at once instead of one rule's).</summary>
    private static async Task<IReadOnlyDictionary<long, string>> ResolveContainsUserNamesAsync(
        IReadOnlyList<FormRule> rules,
        IReadOnlyDictionary<long, AppField> fieldsByFid,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IUserRepository userRepo,
        CancellationToken ct)
    {
        var ids = new HashSet<long>();
        foreach (var rule in rules)
        {
            if (rule.IsExpressionMode) continue;
            foreach (var c in rule.Conditions)
            {
                if (c.Operator != "contains" || c.AppFieldId is not { } fid) continue;
                if (!fieldsByFid.TryGetValue(fid, out var field)) continue;
                if (field.TypeCode != "User" && field.TypeCode != "MultiUser") continue;
                if (!effectiveValues.TryGetValue(fid, out var raw) || raw is null) continue;
                var raws = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                foreach (var part in raws.Split(','))
                    if (long.TryParse(part.Trim(), out var uid)) ids.Add(uid);
            }
        }
        if (ids.Count == 0) return new Dictionary<long, string>();
        return await userRepo.GetNamesByIdsAsync(ids, ct);
    }

    /// <summary>Mirrors the frontend's resolveUserDisplayNames — a User/MultiUser field's raw value
    /// is a numeric user id (or comma-joined ids), never a display name, so 'contains' (a free-text
    /// name/email search) needs each id resolved via the userNames map built by
    /// ResolveContainsUserNamesAsync first. An id with no resolvable name (deleted user, bad data)
    /// falls back to the raw id text rather than being dropped.</summary>
    private static string ResolveUserDisplayNames(object? raw, IReadOnlyDictionary<long, string> userNames)
    {
        if (raw is null) return "";
        var raws = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return string.Join(", ", raws.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
            .Select(id => long.TryParse(id, out var uid) && userNames.TryGetValue(uid, out var name) ? name : id));
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
    private static string? ResolveConditionValue(FormRuleCondition c, IReadOnlyDictionary<long, object?> effectiveValues, long currentUserId, IReadOnlyDictionary<long, AppField> fieldsByFid)
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
                {
                    // A referenced Date/DateTime field's raw value is still a live DateTime here —
                    // Convert.ToString(DateTime) produces its default culture-formatted text (e.g.
                    // "01/06/2026 00:00:00"), never an ISO date, so a "the value in the field"
                    // comparison against another Date field silently never matched. Fmt() covers it
                    // the same way the 'today' branches above already do; NormalizeForCompare covers
                    // File/Address/DateTime-precision the same as the condition's OWN field.
                    var refTypeCode = fieldsByFid.TryGetValue(vf, out var refField) ? refField.TypeCode : null;
                    var normalizedRef = NormalizeForCompare(refTypeCode, refVal);
                    if (normalizedRef is DateTime refDt) return Fmt(refDt);
                    return Convert.ToString(normalizedRef, System.Globalization.CultureInfo.InvariantCulture);
                }
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

    /// <summary>Server-side mirror of resolving a formula-mode PreventSave message (ActionValue
    /// is a formula expression when FormRuleAction.IsExpressionValue is true — see migration
    /// 060_formruleaction_add_is_expression_value.sql). Only PreventSave needs this here: Require/
    /// PreventSave are the only two actions this validator enforces at all (see the class doc
    /// comment) — ChangeLabel/ChangeValue/DisplayMessage's own formula-mode values are resolved
    /// entirely client-side (FormRendererComponent.evaluateRules), same as every other UI-only
    /// action. No expectedType constraint (null) — a PreventSave message could reasonably be built
    /// from Text, Number, Date, etc. concatenation. Fail-open like EvaluateExpressionConditionAsync
    /// above: a compile/evaluate failure returns null, and the caller falls back to the rule's
    /// generic default message rather than surfacing a broken one.</summary>
    private static async Task<string?> EvaluateExpressionActionValueAsync(
        string expressionText,
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        FormulaEngine engine,
        CancellationToken ct)
    {
        var schema = new AppFieldSchema(fields);
        var aliasSchema = await AppTableAliasSchema.BuildAsync(tableRepo, table.AppId, ct);
        var compiled = engine.Compile(expressionText, schema, null, aliasSchema);
        if (compiled.HasErrors) return null;

        var crossTable = new CrossTableQueryContext(tableRepo, fieldRepo, recordRepo, table);
        var context = new CrossTableRecordContext(new ValuesRecordContext(effectiveValues), crossTable);
        var options = new EvaluationOptions { TableId = table.PublicId.ToString() };

        try
        {
            var raw = FormulaRawValue.ToRaw(engine.Evaluate(compiled, context, options));
            return raw is null ? null : Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormulaEvaluationException) { return null; }
    }
}
