using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Compute;

public enum MaterializeOutcome
{
    /// <summary>The worker can build and keep c_{fid} with set-based SQL; a plan is attached.</summary>
    Allowed,
    /// <summary>Can never be stored (MaterializeMode = 2): reads keep today's dynamic path.</summary>
    NotAllowed,
    /// <summary>Could be stored, but not by this version of the worker (formulas arrive in Phase 5). Left as it is.</summary>
    NotYetSupported
}

/// <summary>What the writer does for one stored field.</summary>
public abstract record MaterializePlan(long TableId, int Fid, long FieldId)
{
    public FieldKey Key => new(TableId, Fid);
}

/// <summary>child.c_L = parent.f_Source through child.f_Ref (a deleted parent gives NULL), exactly as LookupColumnExpr reads it.</summary>
public sealed record LookupPlan(long ChildTableId, int LookupFid, long FieldId, int ReferenceFid, long ParentTableId, int SourceFid, string SourceColumn)
    : MaterializePlan(ChildTableId, LookupFid, FieldId);

/// <summary>parent.c_S = aggregate over live children (child.f_Ref = parent.Id) matching the criteria, exactly as AggregateByReferenceAsync.
/// <paramref name="TargetIsStored"/>: the target is itself a stored calculated field (a chain): its c_ column is aggregated.</summary>
public sealed record SummaryPlan(long ParentTableId, int SummaryFid, long FieldId, long ChildTableId, int ReferenceFid, string Function,
    int? TargetFid, string? TargetSubField, FilterGroup? Filter, bool TargetIsStored = false)
    : MaterializePlan(ParentTableId, SummaryFid, FieldId);

/// <summary>row.c_F = the formula evaluated in C# (the API's formula engine) from the row's own fields and the stored values of the calculated
/// fields it reads (<paramref name="StoredReads"/>). <paramref name="IsUnique"/>: enforced by a filtered UNIQUE index and computed in the save.</summary>
public sealed record FormulaPlan(long FormulaTableId, int FormulaFid, long FieldId, string Expression, PowerBase.Formula.Types.FormulaType ResultType,
    IReadOnlyList<int> PhysicalReads, IReadOnlyList<int> StoredReads, bool IsUnique)
    : MaterializePlan(FormulaTableId, FormulaFid, FieldId);

public sealed record MaterializeDecision(MaterializeOutcome Outcome, string? Reason, MaterializePlan? Plan)
{
    public static MaterializeDecision Allow(MaterializePlan plan) => new(MaterializeOutcome.Allowed, null, plan);
    public static MaterializeDecision Deny(string reason) => new(MaterializeOutcome.NotAllowed, reason, null);
    public static MaterializeDecision Later(string reason) => new(MaterializeOutcome.NotYetSupported, reason, null);
}

/// <summary>
/// Which fields may keep a stored column (design section 8.1). Deliberately conservative: a field is stored only when the set-based SQL
/// produces exactly the value today's dynamic path shows. Anything in doubt is refused and simply stays on the dynamic path, which is
/// always correct. Never stored: Today()/Now()/User()/UserRoles()/GetRecords-family formulas, current-user or relative-date criteria,
/// fields reading encrypted data, fields in a dependency cycle, and (in this version) fields reading other calculated fields.
/// </summary>
public static class MaterializeRules
{
    /// <summary>Formula functions whose value moves without any data change, differs per viewer, or reads tables the graph can't see.</summary>
    public static readonly string[] VolatileFormulaFunctions =
        ["Today", "Now", "User", "UserRoles", "GetRecords", "GetRecord", "GetRecordByUniqueField", "SumValues", "GetFieldValues",
         // the deployment's address, the viewer, and the user directory (a name changes without any record changing)
         "URLRoot", "UserID", "UserToID", "UserToName", "UserToEmail"];

    /// <summary>Value types a chained input (a stored calculated field read by another one) may have: exact in its c_ column. Text is not
    /// (it is cut to a 400-character sort key), so a calculated field reading stored text stays dynamic.</summary>
    private static readonly HashSet<string> ChainValueTypes = new(StringComparer.Ordinal)
    {
        "Number", "Currency", "Percent", "Rating", "Duration", "Date", "DateTime", "Boolean"
    };

    /// <summary>Physical field types a stored formula may read: the value the formula sees is the column's own (no key shown instead, no
    /// encryption, no file).</summary>
    private static readonly HashSet<string> FormulaReadableTypes = new(StringComparer.Ordinal)
    {
        "Text", "TextMultiLine", "Email", "Phone", "Url", "SingleSelect", "MultiSelect",
        "Number", "Currency", "Percent", "Rating", "Duration", "Date", "DateTime", "Time", "Boolean", "Address"
    };

    private static readonly Regex VolatileCall = new(
        @"\b(" + string.Join("|", VolatileFormulaFunctions) + @")\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Source / target field types whose stored value is the value shown and sorts the same way in SQL.</summary>
    private static readonly HashSet<string> StorableValueTypes = new(StringComparer.Ordinal)
    {
        "Text", "Email", "Phone", "Url", "SingleSelect",
        "Number", "Currency", "Percent", "Rating", "Duration",
        "Date", "DateTime", "Time", "Boolean"
    };

    /// <summary>Criteria operators the SQL builder evaluates from the stored values alone (no viewer, no clock).</summary>
    private static readonly HashSet<string> StaticOperators = new(StringComparer.Ordinal)
    {
        "eq", "ne", "contains", "notContains", "startsWith", "notStartsWith", "endsWith", "notEndsWith",
        "gt", "gte", "lt", "lte", "isEmpty", "isNotEmpty", "wildcard", "notWildcard", "in", "notIn", "includes", "notIncludes"
    };

    /// <summary>System fields a lookup may pull (Fid 1..5) and the column each lives in.</summary>
    private static readonly Dictionary<int, string> SystemColumns = new()
    {
        [1] = "CreatedOn", [2] = "ModifiedOn", [3] = "Id", [4] = "CreatedBy", [5] = "ModifiedBy"
    };

    private static readonly JsonSerializerOptions FilterJson = new() { PropertyNameCaseInsensitive = true };

    /// <param name="inCycle">Fields the dependency graph found in a cycle.</param>
    /// <param name="decided">The decision already made for another calculated field (callers evaluate in topological order). A field may read a
    /// stored calculated field only when that one is allowed too; without it, any calculated input is refused.</param>
    public static MaterializeDecision Evaluate(AppField field, ComputeSchema schema, IReadOnlySet<FieldKey>? inCycle = null,
        Func<FieldKey, MaterializeDecision?>? decided = null)
    {
        _decided = decided;
        try { return EvaluateCore(field, schema, inCycle); }
        finally { _decided = null; }
    }

    [ThreadStatic] private static Func<FieldKey, MaterializeDecision?>? _decided;

    /// <summary>True when <paramref name="input"/> is a calculated field that is stored too, with a value its c_ column holds exactly.</summary>
    private static bool IsStoredChainInput(AppField input)
    {
        if (input.Fid is not int fid || _decided?.Invoke(new FieldKey(input.AppTableId, fid)) is not { Outcome: MaterializeOutcome.Allowed }) return false;
        return MaterializedReadFields.ValueTypeOf(input) is { } type && ChainValueTypes.Contains(type);
    }

    private static MaterializeDecision EvaluateCore(AppField field, ComputeSchema schema, IReadOnlySet<FieldKey>? inCycle)
    {
        if (field.Fid is not int fid) return MaterializeDecision.Deny("The field has no Fid.");
        if (!PhysicalNaming.IsMaterializableTypeCode(field.TypeCode))
            return MaterializeDecision.Deny($"{field.TypeCode} fields have no value to store.");
        if (inCycle?.Contains(new FieldKey(field.AppTableId, fid)) == true)
            return MaterializeDecision.Deny("The field is part of a dependency cycle.");
        if (schema.IsAppEncrypted(field.AppTableId) || field.IsEncrypted)
            return MaterializeDecision.Deny("The field's app is encrypted; a stored value would have to be encrypted too and could not be sorted.");

        if (field.TypeCode == "Lookup") return EvaluateLookup(field, fid, schema);
        if (field.TypeCode == "Summary") return EvaluateSummary(field, fid, schema);
        return EvaluateFormula(field, fid, schema);
    }

    private static MaterializeDecision EvaluateFormula(AppField field, int fid, ComputeSchema schema)
    {
        var expression = FormulaExpression(field.Settings) ?? FormulaTypeMap.ExpressionAndType(field.TypeCode, field.Settings)?.Expression;
        if (expression is not null && VolatileCall.Match(expression) is { Success: true } m)
            return MaterializeDecision.Deny($"The formula uses {m.Groups[1].Value}(), whose value changes without a data change or differs per viewer.");

        var tableFields = schema.FieldsOf(field.AppTableId);
        if (FormulaAnalysis.Analyze(field, tableFields) is not { } info) return MaterializeDecision.Deny("The formula has no expression.");
        if (info.UsesVariables) return MaterializeDecision.Deny("The formula uses an app variable (its value can change without any record changing).");
        if (info.HasErrors) return MaterializeDecision.Deny("The formula does not compile.");
        if (info.UsesDynamicFieldReference) return MaterializeDecision.Deny("The formula picks the field it reads per record, so what it depends on cannot be listed.");
        if (FormulaAnalysis.StoredSqlType(info.ResultType) is null)
            return MaterializeDecision.Deny($"{info.ResultType} formula results are not stored in this version.");

        var byFid = schema.FieldsByFid(field.AppTableId);
        var physical = new List<int>();
        var stored = new List<int>();
        foreach (var read in info.Reads.Distinct())
        {
            if (read == fid) continue;
            if (read is >= 1 and <= 5) { physical.Add((int)read); continue; }   // system fields (Record ID#, dates, users)
            if (!byFid.TryGetValue(read, out var input)) return MaterializeDecision.Deny("The formula reads a field that no longer exists.");
            if (input.IsEncrypted) return MaterializeDecision.Deny("The formula reads an encrypted field.");
            if (PhysicalNaming.IsComputedTypeCode(input.TypeCode))
            {
                if (!IsStoredChainInput(input))
                    return MaterializeDecision.Deny($"The formula reads the calculated field '{input.Name}', which is not stored (or is text, which is stored cut to 400 characters).");
                stored.Add((int)read);
            }
            else if (!FormulaReadableTypes.Contains(input.TypeCode))
                return MaterializeDecision.Deny($"The formula reads a {input.TypeCode} field, whose value in a report is not the stored one.");
            else physical.Add((int)read);
        }

        // Design section 5: a unique formula must be computable inside the save, from the record alone.
        if (field.IsUnique && stored.Count > 0)
            return MaterializeDecision.Deny("A unique formula may only read fields of the same record (not lookups, summaries or other formulas).");

        return MaterializeDecision.Allow(new FormulaPlan(field.AppTableId, fid, field.Id, info.Expression, info.ResultType, physical, stored, field.IsUnique));
    }

    internal static string? FormulaExpression(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "Expression", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static MaterializeDecision EvaluateLookup(AppField field, int fid, ComputeSchema schema)
    {
        var s = FormulaTypeMap.ParseLookupSettings(field.Settings);
        if (s is not { SourceTableId: long parentId, ReferenceFid: int refFid, SourceFid: int sourceFid })
            return MaterializeDecision.Deny("The lookup's settings are incomplete.");
        if (!string.IsNullOrWhiteSpace(s.SourceSubField))
            return MaterializeDecision.Deny("Lookups of an Address part are not stored in this version.");
        if (schema.Field(field.AppTableId, refFid) is not { TypeCode: "Reference" } reference || reference.IsEncrypted)
            return MaterializeDecision.Deny("The lookup's reference field is missing.");
        if (!schema.TableExists(parentId)) return MaterializeDecision.Deny("The lookup's parent table is missing.");
        if (schema.IsAppEncrypted(parentId)) return MaterializeDecision.Deny("The lookup reads an encrypted app.");

        string column;
        if (SystemColumns.TryGetValue(sourceFid, out var systemColumn))
            column = systemColumn;
        else
        {
            var source = schema.Field(parentId, sourceFid);
            if (source is null) return MaterializeDecision.Deny("The lookup's source field is missing.");
            if (source.IsEncrypted) return MaterializeDecision.Deny("The lookup reads an encrypted field.");
            if (PhysicalNaming.IsComputedTypeCode(source.TypeCode))
            {
                // A chain: the parent's own stored value, when it is stored and exact.
                if (!IsStoredChainInput(source))
                    return MaterializeDecision.Deny("The lookup reads a calculated field that is not stored (or is text, stored cut to 400 characters).");
                column = PhysicalNaming.MaterializedColumnName(sourceFid);
            }
            else
            {
                if (!StorableValueTypes.Contains(source.TypeCode))
                    return MaterializeDecision.Deny($"Lookups of {source.TypeCode} fields are not stored in this version.");
                column = PhysicalNaming.GetPhysicalColumnName(source);
            }
        }

        return MaterializeDecision.Allow(new LookupPlan(field.AppTableId, fid, field.Id, refFid, parentId, sourceFid, column));
    }

    private static MaterializeDecision EvaluateSummary(AppField field, int fid, ComputeSchema schema)
    {
        var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
        var function = SummaryFunctions.Normalize(s?.Function);
        if (s is not { ChildTableId: long childId, ReferenceFid: int refFid } || function is null)
            return MaterializeDecision.Deny("The summary's settings are incomplete.");
        if (function == SummaryFunctions.CombinedText)
            return MaterializeDecision.Deny("Combined Text summaries are not stored (long text, ordering rules).");
        if (!schema.TableExists(childId)) return MaterializeDecision.Deny("The summary's child table is missing.");
        if (schema.IsAppEncrypted(childId)) return MaterializeDecision.Deny("The summary reads an encrypted app.");
        if (schema.Field(childId, refFid) is not { TypeCode: "Reference" })
            return MaterializeDecision.Deny("The summary's reference field is missing.");

        var childFields = schema.FieldsByFid(childId);
        var targetIsStored = false;
        if (s.TargetFid is int targetFid && childFields.TryGetValue(targetFid, out var chained) && PhysicalNaming.IsComputedTypeCode(chained.TypeCode))
        {
            // A chain (e.g. Sum of the children's "Sum of Hours"): the children's own stored value, when it is stored and exact.
            if (!IsStoredChainInput(chained))
                return MaterializeDecision.Deny("The summary reads a calculated field that is not stored (or is text, stored cut to 400 characters).");
            if (!string.IsNullOrWhiteSpace(s.TargetSubField)) return MaterializeDecision.Deny("Summaries of an Address part are not stored in this version.");
            targetIsStored = true;
        }
        else if (s.TargetFid is int plainFid)
        {
            if (!childFields.TryGetValue(plainFid, out var target)) return MaterializeDecision.Deny("The summary's target field is missing.");
            if (target.IsEncrypted) return MaterializeDecision.Deny("The summary reads an encrypted field.");
            if (target.TypeCode == "Reference") return MaterializeDecision.Deny("Summaries of a Reference (shown as its key) are not stored.");
            if (!string.IsNullOrWhiteSpace(s.TargetSubField))
                return MaterializeDecision.Deny("Summaries of an Address part are not stored in this version.");
            if (!StorableValueTypes.Contains(target.TypeCode) && function is not (SummaryFunctions.Count or SummaryFunctions.Exists))
                return MaterializeDecision.Deny($"Summaries of {target.TypeCode} fields are not stored in this version.");
            if (SummaryTargetValidator.FindProblem(function, target, s.TargetSubField, null, null) is { } problem)
                return MaterializeDecision.Deny($"The summary target is not valid: {problem}");
        }
        else if (function is not (SummaryFunctions.Count or SummaryFunctions.Exists))
            return MaterializeDecision.Deny("The summary has no target field.");

        FilterGroup? filter = null;
        if (!string.IsNullOrWhiteSpace(s.FilterTree))
        {
            try { filter = JsonSerializer.Deserialize<FilterGroup>(s.FilterTree, FilterJson); }
            catch (JsonException) { return MaterializeDecision.Deny("The summary's criteria cannot be read."); }
            if (CriteriaProblem(filter, childFields) is { } problem) return MaterializeDecision.Deny(problem);
        }

        return MaterializeDecision.Allow(new SummaryPlan(field.AppTableId, fid, field.Id, childId, refFid, function, s.TargetFid,
            s.TargetSubField, FilterGroup.NullIfEmpty(filter), targetIsStored));
    }

    /// <summary>Null when every criteria condition can be evaluated from stored child values alone; otherwise why not.</summary>
    internal static string? CriteriaProblem(FilterGroup? group, IReadOnlyDictionary<long, AppField> childFields)
    {
        if (group is null) return null;
        foreach (var node in group.Nodes)
        {
            if (node.Group is { } sub && CriteriaProblem(sub, childFields) is { } inner) return inner;
            if (node.Condition is not { } c) continue;

            var mode = c.ValueMode?.Trim();
            if (!string.IsNullOrEmpty(mode) && !string.Equals(mode, "literal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "field", StringComparison.OrdinalIgnoreCase))
                return $"The criteria use \"{mode}\" (current user, a relative date, a parent field or an ask-the-user value), which is not fixed.";
            if (!StaticOperators.Contains(c.Operator))
                return $"The criteria use the \"{c.Operator}\" operator, which is not fixed (current user, a moving date range).";
            if (FieldProblem(c.FieldId, childFields) is { } problem) return problem;
            if (string.Equals(mode, "field", StringComparison.OrdinalIgnoreCase)
                && (c.ValueFieldId is not long other || FieldProblem(other, childFields) is not null))
                return "The criteria compare to a field that cannot be read in SQL.";
        }
        return null;
    }

    private static string? FieldProblem(long fid, IReadOnlyDictionary<long, AppField> childFields)
    {
        if (fid is >= 1 and <= 5) return null;   // system fields are plain columns
        if (!childFields.TryGetValue(fid, out var f)) return "The criteria name a field that no longer exists.";
        if (PhysicalNaming.IsComputedTypeCode(f.TypeCode)) return "The criteria read a calculated field.";
        if (f.IsEncrypted) return "The criteria read an encrypted field.";
        if (f.TypeCode is "Reference" or "User" or "MultiUser")
            return $"The criteria read a {f.TypeCode} field, whose value is resolved per viewer or shown as a key.";
        return null;
    }
}
