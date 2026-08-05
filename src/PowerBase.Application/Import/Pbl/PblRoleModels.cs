namespace PowerBase.Application.Import.Pbl;

/// <summary>
/// A custom app role. Cross-cutting, so lives as a top-level list on <see cref="PblDocument"/>,
/// matching <see cref="PblRelationship"/>/<see cref="PblForm"/>. Default roles
/// (Administrator/Participant/Viewer) aren't modeled here — <c>CreateAppCommandHandler</c>
/// already seeds those for every new app; this only covers additional custom roles a QBL
/// export defines.
/// </summary>
public sealed class PblRole
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }

    public List<PblTablePermission> TablePermissions { get; set; } = [];
}

public sealed class PblTablePermission
{
    /// <summary>A <see cref="PblTable.LogicalRef"/>.</summary>
    public string TableRef { get; set; } = string.Empty;

    /// <summary>One of PowerBase's RecordScopes: None, OwnRecords, AllRecords.</summary>
    public string ViewScope { get; set; } = "AllRecords";
    public string ModifyScope { get; set; } = "None";
    public bool CanAdd { get; set; }
    public bool CanDelete { get; set; }
    public bool CanSaveSharedReports { get; set; }
    public bool CanEditFieldProperties { get; set; }

    /// <summary>FullAccess or CustomAccess — gates whether FieldPermissions is consulted.</summary>
    public string FieldAccessLevel { get; set; } = "FullAccess";

    /// <summary>Real QBL exports carry no field-level permission data (confirmed against a
    /// real sample — every RolePermissions block found was on a Table or Report, never a
    /// Field); this stays empty for QBL-sourced imports and exists for hand-authored PBL.</summary>
    public List<PblFieldPermission> FieldPermissions { get; set; } = [];
}

public sealed class PblFieldPermission
{
    /// <summary>A <see cref="PblField.Name"/> on the permission's table.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>View, Modify, or None.</summary>
    public string Access { get; set; } = "Modify";
}
