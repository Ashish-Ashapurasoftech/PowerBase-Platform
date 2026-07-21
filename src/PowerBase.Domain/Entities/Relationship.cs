namespace PowerBase.Domain.Entities;

/// <summary>
/// A one-to-many table-to-table relationship: one <see cref="ParentTableId"/> record
/// has many <see cref="ChildTableId"/> records. The link is stored on the child via the
/// <see cref="ReferenceFieldId"/> field (a physical BIGINT foreign-key column holding the
/// parent row Id). Lookup fields (on the child) and Summary fields (on the parent) are
/// computed at read time from this link by the relationship projector.
/// </summary>
public class Relationship
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }

    /// <summary>The "one" side.</summary>
    public long ParentTableId { get; set; }

    /// <summary>The "many" side; owns the reference field.</summary>
    public long ChildTableId { get; set; }

    /// <summary>The child <see cref="AppField.Id"/> that stores the parent row Id (the foreign key).</summary>
    public long ReferenceFieldId { get; set; }

    /// <summary>The reference field's Fid (its physical column is <c>f_{ReferenceFid}</c> on the child
    /// data table). Denormalized here so projection and delete-restrict avoid an extra field lookup.</summary>
    public int ReferenceFid { get; set; }

    /// <summary>
    /// The child Lookup <see cref="AppField.Id"/> used as the reference's display label
    /// (the "reference proxy"; the first lookup created). Null falls back to the parent
    /// table's <see cref="AppTable.DisplayFieldId"/>.
    /// </summary>
    public long? ProxyFieldId { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }

    /// <summary>
    /// True when the reference field was an existing child Number field repurposed for this
    /// relationship. On deletion the field's type will be restored instead of being deleted.
    /// False (default) means the field was newly created and should be deleted with the relationship.
    /// </summary>
    public bool ReferenceFieldIsExisting { get; set; }
}
