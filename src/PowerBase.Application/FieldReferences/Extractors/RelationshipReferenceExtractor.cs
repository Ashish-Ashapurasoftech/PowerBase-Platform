using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>The fields one relationship stands on: the child's reference field (the foreign key), the parent field
/// the pickers and grids show for it, and the child lookup used as its label. All rows are filed under the CHILD
/// table, which is the table the relationship belongs to. The Lookups and Summaries built on a relationship are
/// separate Field sources and are indexed already.</summary>
public static class RelationshipReferenceExtractor
{
    /// <param name="parentKeyFieldId">The parent table's own key field (<c>AppTable.KeyFieldId</c>), or null when the
    /// key is the default Record ID#. Used only when the relationship has no display-key override of its own.</param>
    public static void Extract(RelationshipSource relationship, long? parentKeyFieldId, FieldReferenceCollector collector)
    {
        collector.Add(relationship.ReferenceFieldId, FieldReferenceUsages.RelationshipReference);

        // The override wins; otherwise the parent's global key is what the relationship matches on.
        if (relationship.DisplayKeyFieldId is { } displayKey)
            collector.Add(displayKey, FieldReferenceUsages.RelationshipDisplayKey);
        else
            collector.Add(parentKeyFieldId, FieldReferenceUsages.RelationshipKey);

        collector.Add(relationship.ProxyFieldId, FieldReferenceUsages.RelationshipDisplayLabel);
    }
}
