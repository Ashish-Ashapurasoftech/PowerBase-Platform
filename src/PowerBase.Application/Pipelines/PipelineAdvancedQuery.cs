using Microsoft.Extensions.Logging;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// The Advanced Query ({6.XEX.'x'}) as Search Records / Copy Records and the trigger filters read it. The parsed tree goes to SQL as is
/// otherwise, which answered differently from a trigger (PipelineFilterSqlParityTests): "is not" dropped blank records, a blank value
/// dropped the condition (the whole table), a checkbox's blank was not "false", a range compared its start alone. Each condition is worded
/// the way the filter editor's rules are (PipelineEngine.MapCondition).
/// </summary>
public static class PipelineAdvancedQuery
{
    /// <summary>The tree for Search Records / Copy Records: SQL and its in-memory half.</summary>
    public static FilterGroup ForSearch(FilterGroup tree, IReadOnlyList<AppField> fields, ILogger? logger = null)
    {
        var byFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        return new FilterGroup { Logic = tree.Logic, Nodes = tree.Nodes.Select(Node).Where(n => n != null).Select(n => n!).ToList() };

        FilterNode? Node(FilterNode node)
        {
            if (node.Group != null) return new FilterNode { Group = ForSearch(node.Group, fields, logger) };
            var c = node.Condition;
            // "The value in another field" and anything not on a known field keep their SQL as it was.
            if (c == null || string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) || !byFid.TryGetValue(c.FieldId, out var field)
                || !field.Fid.HasValue)
                return node;
            var op = PipelineFilterEvaluator.GetTypeCategory(field) == "DATE" ? c.Operator switch
            {
                "eq" => "date_eq", "ne" => "date_ne", "gt" => "date_gt", "gte" => "date_gte", "lt" => "date_lt", "lte" => "date_lte",
                _ => c.Operator,
            } : c.Operator;
            // Null: the condition holds for every record — an empty AND group.
            return PipelineEngine.MapCondition(field, c.SubField, op, c.Value, valueIsLikeEscaped: true, logger)
                ?? new FilterNode { Group = new FilterGroup { Logic = "and", Nodes = new List<FilterNode>() } };
        }
    }

    /// <summary>The tree for a trigger's in-memory filter: CT / SW values are escaped for SQL LIKE by the parser ("a_b" → "a[_]b"), which
    /// compared as text never matched "a_b".</summary>
    public static FilterGroup ForTrigger(FilterGroup tree) => new()
    {
        Logic = tree.Logic,
        Nodes = tree.Nodes.Select(n => n.Group != null
            ? new FilterNode { Group = ForTrigger(n.Group) }
            : n.Condition is { Operator: "contains" or "notContains" or "startsWith" or "notStartsWith" } c && c.Value != null
                ? new FilterNode { Condition = new FilterCondition
                {
                    FieldId = c.FieldId, Operator = c.Operator, Value = PipelineComputedFilter.UnescapeLike(c.Value), SubField = c.SubField,
                    ValueMode = c.ValueMode, ValueFieldId = c.ValueFieldId,
                } }
                : n).ToList()
    };
}
