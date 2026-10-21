using PowerBase.Application.Reports;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Search Records maps "is not" / "does not contain"… (and "is false" on a checkbox) to the condition OR the field being blank, so a blank
/// record passes as it does in the trigger filters (SQL's NOT on a NULL is unknown). These read the condition back out of that shape.
/// </summary>
internal static class SearchFilterNodes
{
    /// <summary>True when the node is <c>(condition OR the field is blank)</c>.</summary>
    public static bool IsOrBlank(FilterNode node) =>
        node.Group is { Logic: "or", Nodes.Count: 2 } g && g.Nodes[1].Condition is { Operator: "isEmpty" };

    /// <summary>The condition of a <c>(condition OR blank)</c> node; any other node as it is.</summary>
    public static FilterNode WithoutBlank(FilterNode node) => IsOrBlank(node) ? node.Group!.Nodes[0] : node;

    /// <summary>Every condition of the tree, the added blank checks left out.</summary>
    public static IEnumerable<FilterCondition> Conditions(FilterGroup group) =>
        group.Nodes.Select(WithoutBlank).SelectMany(n => n.Condition != null ? new[] { n.Condition } : Conditions(n.Group!));
}
