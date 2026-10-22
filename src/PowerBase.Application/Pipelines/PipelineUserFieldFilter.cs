using System.Globalization;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>A person a User / User List field points at, as pipeline filters and tokens read them. ScreenName is the name the
/// app shows for them (core.[User].Name); Name is the older "Name" detail — first + last name, else the email.</summary>
public sealed record PipelineUserDetails(long Id, string FirstName, string LastName, string Email, string ScreenName, string Name)
{
    public static PipelineUserDetails From(User user)
    {
        var first = user.FirstName?.Trim() ?? string.Empty;
        var last = user.LastName?.Trim() ?? string.Empty;
        var fullName = $"{first} {last}".Trim();
        var name = fullName.Length > 0 ? fullName : user.Email;
        var screenName = string.IsNullOrWhiteSpace(user.Name) ? name : user.Name.Trim();
        return new PipelineUserDetails(user.Id, first, last, user.Email, screenName, name);
    }
}

/// <summary>
/// Filtering on a detail of the person a User / User List field holds — "Record Owner → Email is x" — as Quickbase's filter field
/// picker offers it (Id, First name, Last name, Email, Screen name). A filter rule names it as <c>fid_N.detail</c>.
/// <para>A User field passes when its person passes. A User List passes when any of its people passes; a negated operator ("is not",
/// "does not contain", "is empty", …) when none of them passes the positive one. A record without a user is read as an empty value.</para>
/// </summary>
public static class PipelineUserFieldFilter
{
    /// <summary>The details a rule can filter on (Name is the older detail, kept so saved filters still run).</summary>
    public static readonly IReadOnlyList<string> Details = new[] { "id", "first_name", "last_name", "email", "screen_name", "name" };

    /// <summary>
    /// How many user ids the Search Records SQL may carry for user-detail rules. Each id is one SQL parameter and SQL Server allows
    /// about 2,100 per query, shared with the rest of the search. A rule needing more is not cut short: its field is checked in
    /// memory on each page of the search instead (the path encrypted and formula fields use), so any number of users and records
    /// is searched.
    /// </summary>
    public const int SqlIdBudget = 1_000;

    /// <summary>A Search Records condition for a user-detail rule and how many user ids (SQL parameters) it carries.</summary>
    public sealed record DbFilter(FilterNode? Node, int IdCount);

    private static readonly Dictionary<string, string> NegatedToPositive = new(StringComparer.OrdinalIgnoreCase)
    {
        ["is-not"] = "is", ["is_not"] = "is", ["not-equals"] = "is", ["not_equals"] = "is", ["<>"] = "is", ["!="] = "is",
        ["not-contains"] = "contains", ["not_contains"] = "contains",
        ["not-starts-with"] = "starts-with", ["not_starts_with"] = "starts-with",
        ["not-ends-with"] = "ends-with", ["not_ends_with"] = "ends-with",
        ["is-empty"] = "is-not-empty", ["is_empty"] = "is-not-empty", ["is-blank"] = "is-not-empty", ["is_blank"] = "is-not-empty",
        ["is-null"] = "is-not-empty",
        ["not-wildcard"] = "wildcard", ["not_wildcard"] = "wildcard"
    };

    /// <summary>A rule on the User / User List field itself with one of these operators matches its person by name or email, as the
    /// report filter's "wildcard match" on a user field does — the field itself stores only the user id.</summary>
    public const string PersonDetail = "person";
    private static readonly HashSet<string> PersonOperators = new(StringComparer.OrdinalIgnoreCase) { "wildcard", "not-wildcard", "not_wildcard" };

    /// <summary>A User or User List field with a physical column (Record Owner and Last Modified By are User fields).</summary>
    public static bool IsUserField(AppField? field) =>
        field != null && (field.TypeCode.Equals("User", StringComparison.OrdinalIgnoreCase) || IsUserList(field));

    public static bool IsUserList(AppField? field) => field != null && field.TypeCode.Equals("MultiUser", StringComparison.OrdinalIgnoreCase);

    /// <summary>A computed field holding a user (a bare user id) or a list of users (their ids): a formula returning one, or a Lookup of a
    /// parent's user field. No column of its own: its value is worked out when read.</summary>
    public static bool IsComputedUser(AppField? field) =>
        field != null && (field.TypeCode is "Formula_User" or "Formula_ListUser"
            || (field.TypeCode == "Lookup" && PowerBase.Application.Records.RecordResult.UserLookupSourceType(field) != null));

    /// <summary>Of those, the ones holding a list of users.</summary>
    public static bool IsComputedUserList(AppField? field) =>
        field != null && (field.TypeCode == "Formula_ListUser"
            || (field.TypeCode == "Lookup" && PowerBase.Application.Records.RecordResult.UserLookupSourceType(field) is "MultiUser" or "Formula_ListUser"));

    /// <summary>A rule field like <c>fid_4.email</c> (or <c>Record Owner.email</c>) → the User / User List field and the detail.</summary>
    public static bool TryResolve(string? fieldRef, IReadOnlyList<AppField> fields, out AppField field, out string detail)
    {
        field = null!;
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(fieldRef)) return false;
        var dot = fieldRef.LastIndexOf('.');
        if (dot <= 0 || dot == fieldRef.Length - 1) return false;
        var candidate = fieldRef[(dot + 1)..].Trim().ToLowerInvariant();
        if (!Details.Contains(candidate)) return false;
        var baseRef = fieldRef[..dot].Trim();
        var resolved = fields.FirstOrDefault(f => f.Fid.HasValue && $"fid_{f.Fid}".Equals(baseRef, StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => !string.IsNullOrEmpty(f.Label) && f.Label.Equals(baseRef, StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => f.Name.Equals(baseRef, StringComparison.OrdinalIgnoreCase));
        if (!IsUserField(resolved)) return false;
        field = resolved!;
        detail = candidate;
        return true;
    }

    /// <summary>The User / User List field and detail a rule is checked on: a named detail (<c>fid_4.email</c>), or the field itself
    /// with a person operator ("wildcard match": its person's name or email, <see cref="PersonDetail"/>).</summary>
    public static bool TryResolveRule(TriggerFilterRule? rule, IReadOnlyList<AppField> fields, out AppField field, out string detail)
    {
        if (TryResolve(rule?.Field, fields, out field, out detail)) return true;
        if (rule?.Field is not { } fieldRef || string.IsNullOrWhiteSpace(fieldRef) || rule.Operator is not { } op || !PersonOperators.Contains(op.Trim()))
            return false;
        var name = fieldRef.Trim();
        var resolved = fields.FirstOrDefault(f => f.Fid.HasValue && $"fid_{f.Fid}".Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => !string.IsNullOrEmpty(f.Label) && f.Label.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        // A formula or lookup user field holds user ids too, so its person is matched the same way.
        if (!IsUserField(resolved) && !IsComputedUser(resolved)) return false;
        field = resolved!;
        detail = PersonDetail;
        return true;
    }

    /// <summary>The user ids a User / User List value holds: <c>40021</c>, <c>40021,40022</c> or <c>["40021","40022"]</c>.</summary>
    public static List<long> ParseUserIds(string? raw)
    {
        var ids = new List<long>();
        if (string.IsNullOrWhiteSpace(raw)) return ids;
        foreach (var part in raw.Trim().TrimStart('[').TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (long.TryParse(part.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>The detail of one person; empty when the person could not be loaded (except the id, which the field itself holds).</summary>
    public static string DetailOf(long id, PipelineUserDetails? user, string detail) => detail switch
    {
        "id" => id.ToString(CultureInfo.InvariantCulture),
        _ when user == null => string.Empty,
        "first_name" => user.FirstName,
        "last_name" => user.LastName,
        "email" => user.Email,
        "screen_name" => user.ScreenName,
        _ => user.Name
    };

    /// <summary>The operator to test each person with, and whether the rule passes when none of them does.</summary>
    public static (string Operator, bool Negated) Split(string? op)
    {
        var trimmed = string.IsNullOrWhiteSpace(op) ? "is" : op.Trim();
        return NegatedToPositive.TryGetValue(trimmed, out var positive) ? (positive, true) : (trimmed, false);
    }

    private static bool Holds(string left, string op, string value) =>
        PipelineFilterEvaluator.EvaluateConditionOperator(left, op, value, "TEXT");

    /// <summary>Whether one person passes: their detail, or for <see cref="PersonDetail"/> any of their screen name, name or email.</summary>
    private static bool PersonHolds(long id, PipelineUserDetails? user, string detail, string op, string value) =>
        detail == PersonDetail
            ? user != null && new[] { user.ScreenName, user.Name, user.Email }.Any(text => Holds(text, op, value))
            : Holds(DetailOf(id, user, detail), op, value);

    /// <summary>Whether a record whose field holds <paramref name="ids"/> passes the rule (trigger filters, evaluated in memory).</summary>
    public static bool Matches(IReadOnlyList<long> ids, IReadOnlyDictionary<long, PipelineUserDetails>? users, string detail, string? op, string? value)
    {
        var (positive, negated) = Split(op);
        var right = value ?? string.Empty;
        var any = ids.Count == 0
            ? Holds(string.Empty, positive, right)
            : ids.Any(id => PersonHolds(id, users?.GetValueOrDefault(id), detail, positive, right));
        return negated ? !any : any;
    }

    /// <summary>
    /// The Search Records condition for a user-detail rule, over the people the column holds (<paramref name="candidateIds"/>): the
    /// records whose user (any of whose users, for a list) passes — or, for a negated operator, none of whose users passes the
    /// positive one. Null when every record passes.
    /// </summary>
    public static FilterNode? BuildDbNode(AppField field, string detail, string? op, string? value,
        IReadOnlyCollection<long> candidateIds, IReadOnlyDictionary<long, PipelineUserDetails> users) =>
        BuildDbFilter(field, detail, op, value, candidateIds, users).Node;

    /// <summary><see cref="BuildDbNode"/>, with how many user ids the condition carries. <paramref name="candidateIds"/> must be every
    /// person the column holds (no limit), so "not one of these" is exact.</summary>
    public static DbFilter BuildDbFilter(AppField field, string detail, string? op, string? value,
        IReadOnlyCollection<long> candidateIds, IReadOnlyDictionary<long, PipelineUserDetails> users)
    {
        var fid = field.Fid!.Value;
        var (positive, negated) = Split(op);
        var right = value ?? string.Empty;
        var matched = candidateIds.Where(id => PersonHolds(id, users.GetValueOrDefault(id), detail, positive, right)).ToList();
        var emptyHolds = Holds(string.Empty, positive, right);
        var withoutUserPasses = negated ? !emptyHolds : emptyHolds;

        FilterNode Condition(string dbOp, string? v) => new() { Condition = new FilterCondition { FieldId = fid, Operator = dbOp, Value = v } };
        FilterNode Group(string logic, List<FilterNode> nodes) => nodes.Count == 1 ? nodes[0] : new FilterNode { Group = new FilterGroup { Logic = logic, Nodes = nodes } };
        string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
        string IdList(IEnumerable<long> ids) => System.Text.Json.JsonSerializer.Serialize(ids.Select(Id));

        if (!IsUserList(field))
        {
            // One person per record: the rule passes for a set of people. Name that set — or, when it is the larger side, the
            // people it leaves out — as one IN / NOT IN list, so the SQL carries as few ids as possible.
            var matchedSet = matched.ToHashSet();
            var passing = negated ? candidateIds.Where(id => !matchedSet.Contains(id)).ToList() : matched;
            var failing = negated ? matched : candidateIds.Where(id => !matchedSet.Contains(id)).ToList();

            FilterNode? withUser;
            var idCount = 0;
            if (passing.Count == 0) withUser = null;
            else if (failing.Count == 0) withUser = Condition("isNotEmpty", null);
            else if (passing.Count <= failing.Count) { withUser = Condition("in", IdList(passing)); idCount = passing.Count; }
            else
            {
                // NOT IN never keeps a row without a user, so those rows are left out explicitly (and added back below when they pass).
                withUser = Group("and", new List<FilterNode> { Condition("isNotEmpty", null), Condition("notIn", IdList(failing)) });
                idCount = failing.Count;
            }

            if (!withoutUserPasses) return new DbFilter(withUser ?? Condition("eq", "-1"), idCount); // nobody passes: no record does
            if (withUser == null) return new DbFilter(Condition("isEmpty", null), 0);
            if (failing.Count == 0) return new DbFilter(null, 0);                                       // every record passes
            return new DbFilter(Group("or", new List<FilterNode> { withUser, Condition("isEmpty", null) }), idCount);
        }

        // A list holds several people: positive — any of them passes; negated — none of them passes the positive operator.
        if (!negated)
        {
            var any = matched.Select(id => Condition("includes", Id(id))).ToList();
            if (withoutUserPasses) any.Add(Condition("isEmpty", null));
            return new DbFilter(any.Count == 0 ? Condition("eq", "-1") : Group("or", any), matched.Count);
        }

        if (matched.Count == 0) return new DbFilter(withoutUserPasses ? null : Condition("isNotEmpty", null), 0);
        var none = Group("and", matched.Select(id => Condition("notIncludes", Id(id))).ToList());
        // NOT LIKE never keeps a row without a user, so those rows are added back (or explicitly left out).
        return new DbFilter(withoutUserPasses
            ? Group("or", new List<FilterNode> { none, Condition("isEmpty", null) })
            : Group("and", new List<FilterNode> { Condition("isNotEmpty", null), none }), matched.Count);
    }

    /// <summary>Every rule (nested ones included) that filters on a user detail.</summary>
    public static IEnumerable<TriggerFilterRule> DetailRules(IEnumerable<TriggerFilterGroup>? groups, IReadOnlyList<AppField> fields) =>
        (groups ?? Enumerable.Empty<TriggerFilterGroup>()).SelectMany(group => DetailRules(group?.Rules, fields));

    public static IEnumerable<TriggerFilterRule> DetailRules(IEnumerable<TriggerFilterRule>? rules, IReadOnlyList<AppField> fields)
    {
        foreach (var rule in rules ?? Enumerable.Empty<TriggerFilterRule>())
        {
            if (rule == null) continue;
            if (rule.Type == "nested")
            {
                foreach (var nested in DetailRules(rule.Groups, fields)) yield return nested;
            }
            else if (TryResolveRule(rule, fields, out _, out _))
            {
                yield return rule;
            }
        }
    }

    /// <summary>How many people are read at once (each read is its own short query).</summary>
    private const int LoadParallelism = 8;

    /// <summary>Loads the people behind <paramref name="ids"/> that <paramref name="known"/> does not hold yet — every one of them,
    /// several at a time.</summary>
    public static async Task LoadAsync(IUserRepository? users, IEnumerable<long> ids, IDictionary<long, PipelineUserDetails> known, ILogger? logger, CancellationToken ct)
    {
        if (users == null) return;
        var missing = ids.Distinct().Where(id => !known.ContainsKey(id)).ToList();
        if (missing.Count == 0) return;
        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<long, PipelineUserDetails>();
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = LoadParallelism, CancellationToken = ct }, async (id, token) =>
        {
            try
            {
                loaded[id] = PipelineUserDetails.From(await users.GetByIdAsync(id, token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning("User {UserId} referenced by a pipeline step could not be loaded: {Message}", id, ex.Message);
            }
        });
        foreach (var (id, details) in loaded) known[id] = details;
    }
}
