namespace PowerBase.Application.Import.Pbl;

/// <summary>
/// A 1:M relationship between two PBL tables. Cross-table, so it lives as a top-level list on
/// <see cref="PblDocument"/> (like <see cref="PblApp"/>) rather than nested under one table the
/// way <see cref="PblReport"/> is. One <see cref="PblRelationship"/> maps 1:1 onto a single
/// <c>CreateRelationshipCommand</c> call, which creates the Reference field on the child,
/// every Lookup/Summary field, and an auto-generated ReportLink field on the parent, all in one
/// orchestrated step — see <c>CreateRelationshipCommandHandler</c>.
/// </summary>
public sealed class PblRelationship
{
    /// <summary>Logical ref, e.g. "$Relationship_to_Folders".</summary>
    public string LogicalRef { get; set; } = string.Empty;

    /// <summary>A <see cref="PblTable.LogicalRef"/> — the "one" side.</summary>
    public string ParentTableRef { get; set; } = string.Empty;

    /// <summary>A <see cref="PblTable.LogicalRef"/> — the "many" side; the Reference field is
    /// created here.</summary>
    public string ChildTableRef { get; set; } = string.Empty;

    public string ReferenceFieldName { get; set; } = string.Empty;
    public string? ReferenceFieldLabel { get; set; }
    public bool IsReferenceRequired { get; set; }

    public List<PblLookupField> Lookups { get; set; } = [];
    public List<PblSummaryField> Summaries { get; set; } = [];

    public string? AliasHint { get; set; }
}

/// <summary>A parent field pulled down onto the child table.</summary>
public sealed class PblLookupField
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }

    /// <summary>A <see cref="PblField.Name"/> on the relationship's parent table.</summary>
    public string SourceFieldName { get; set; } = string.Empty;

    /// <summary>When <see cref="SourceFieldName"/> is a composite Address field, the JSON
    /// sub-key (see <see cref="PowerBase.Domain.FieldSettings.AddressSubFields"/>) to pull
    /// instead of the whole value — e.g. a QBL export's own "just the city" shadow field.</summary>
    public string? SourceSubField { get; set; }
}

/// <summary>A child-table aggregate rolled up onto the parent table.</summary>
public sealed class PblSummaryField
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }

    /// <summary>One of PowerBase's SummaryFunctions: Count, Exists, Sum, Avg, Min, Max.</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>A <see cref="PblField.Name"/> on the relationship's child table; null for Count/Exists.</summary>
    public string? TargetFieldName { get; set; }

    /// <summary>When <see cref="TargetFieldName"/> is a composite Address field, the JSON
    /// sub-key to aggregate instead of the whole value. Only meaningful with Count/Exists/Min/Max.</summary>
    public string? TargetSubField { get; set; }
}
