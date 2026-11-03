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

    /// <summary>The source Reference field's numeric <c>Id:</c>; kept as the PowerBase Fid on a new app.</summary>
    public int? ReferenceFieldSourceId { get; set; }

    public List<PblLookupField> Lookups { get; set; } = [];
    public List<PblSummaryField> Summaries { get; set; } = [];

    public string? AliasHint { get; set; }

    /// <summary>Dependent dropdown (Quickbase "conditional values"): the field on the child table whose value narrows the choices …</summary>
    public string? ReferenceFilterFormFieldName { get; set; }

    /// <summary>… and the field on the parent table that must equal it. Only parent records where it matches are offered.</summary>
    public string? ReferenceFilterParentFieldName { get; set; }

    /// <summary>The source Reference field's comment (Quickbase "Comments"). Applied to the field once it exists.</summary>
    public string? ReferenceFieldDescription { get; set; }

    /// <summary>True when the document says exactly which link fields the parent table has (a Quickbase file does): PowerBase then creates the
    /// parent's report link only when <see cref="ReportLink"/> is set, on its own number, and never adds its own "Add {child}" link — the file's
    /// own "Add …" formula is imported as a normal field. False (a PowerBase export, the wizard): both links are generated as before.</summary>
    public bool ExplicitLinks { get; set; }

    /// <summary>The parent's source report link for this relationship (Quickbase's "{child} records"); only read when <see cref="ExplicitLinks"/>.</summary>
    public PblReportLinkField? ReportLink { get; set; }
}

/// <summary>A source report link that PowerBase creates as the relationship's own report link, keeping the source label and number.</summary>
public sealed class PblReportLinkField
{
    public string LogicalRef { get; set; } = string.Empty;
    public string? Label { get; set; }

    /// <summary>The hyperlink text; null = PowerBase's default.</summary>
    public string? LinkText { get; set; }

    /// <summary>The source field's numeric <c>Id:</c>; kept as the PowerBase Fid on a new app.</summary>
    public int? SourceId { get; set; }

    public string? Description { get; set; }
}

/// <summary>A source report link that is not a relationship's own: it opens a filtered view of <see cref="TargetTableRef"/> by matching
/// <see cref="TargetFieldName"/> against this table's <see cref="SourceFieldName"/> (null = the record's own id).</summary>
public sealed class PblStandaloneReportLink
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? LinkText { get; set; }
    public int? SourceId { get; set; }
    public string? Description { get; set; }

    /// <summary>A <see cref="PblTable.LogicalRef"/>.</summary>
    public string TargetTableRef { get; set; } = string.Empty;

    /// <summary>A <see cref="PblField.Name"/> (or reference/lookup/summary name) on the target table.</summary>
    public string TargetFieldName { get; set; } = string.Empty;

    /// <summary>A field name on this table; null = the record's own id.</summary>
    public string? SourceFieldName { get; set; }
}

/// <summary>A parent field pulled down onto the child table.</summary>
public sealed class PblLookupField
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }

    /// <summary>A <see cref="PblField.Name"/> on the relationship's parent table.</summary>
    public string SourceFieldName { get; set; } = string.Empty;

    /// <summary>The source field's comment, applied as the field's description.</summary>
    public string? Description { get; set; }

    /// <summary>When <see cref="SourceFieldName"/> is a composite Address field, the JSON
    /// sub-key (see <see cref="PowerBase.Domain.FieldSettings.AddressSubFields"/>) to pull
    /// instead of the whole value — e.g. a QBL export's own "just the city" shadow field.</summary>
    public string? SourceSubField { get; set; }

    /// <summary>The source field's numeric <c>Id:</c>; kept as the PowerBase Fid on a new app.</summary>
    public int? SourceId { get; set; }
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

    /// <summary>The source field's comment, applied as the field's description.</summary>
    public string? Description { get; set; }

    /// <summary>When <see cref="TargetFieldName"/> is a composite Address field, the JSON
    /// sub-key to aggregate instead of the whole value. Only meaningful with Count/Exists/Min/Max.</summary>
    public string? TargetSubField { get; set; }

    /// <summary>The source's "only summarize records where…" query, verbatim (Quickbase syntax). The converter translates it into
    /// <see cref="Filter"/>; both are kept so a failed translation can be reported with the original text.</summary>
    public string? QueryCriteria { get; set; }

    /// <summary>Child-table conditions limiting which records are summarized. Null = all child records.</summary>
    public PblFilterGroup? Filter { get; set; }

    /// <summary>When set, the importer does not create this summary and reports the reason. The field stays in the document so everything
    /// that refers to it (lookups, report columns, form elements) still validates and is skipped individually, with its own message.</summary>
    public string? SkipReason { get; set; }

    /// <summary>The source field's numeric <c>Id:</c>; kept as the PowerBase Fid on a new app.</summary>
    public int? SourceId { get; set; }
}
