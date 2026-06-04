namespace PowerBase.API.Models.Apps;

// ── Table-level ───────────────────────────────────────────────────────────────

public class TablePermissionDto
{
    public Guid TablePublicId { get; init; }
    public string? TableName { get; init; }
    public string ViewScope { get; init; } = "AllRecords";
    public string ModifyScope { get; init; } = "None";
    public bool CanAdd { get; init; }
    public bool CanDelete { get; init; }
    public bool CanSaveSharedReports { get; init; }
    public bool CanEditFieldProperties { get; init; }
    public string FieldAccessLevel { get; init; } = "FullAccess";
}

public class TablePermissionsResponse
{
    public Guid RolePublicId { get; init; }
    public IReadOnlyList<TablePermissionDto> Tables { get; init; } = Array.Empty<TablePermissionDto>();
}

public record UpdateTablePermissionsRequest(IReadOnlyList<TablePermissionDto> Tables);

// ── Field-level ───────────────────────────────────────────────────────────────

public class FieldPermissionDto
{
    public Guid FieldPublicId { get; init; }
    public string? FieldName { get; init; }
    public string Access { get; init; } = "Modify";
}

public class FieldPermissionsResponse
{
    public Guid RolePublicId { get; init; }
    public Guid TablePublicId { get; init; }
    public IReadOnlyList<FieldPermissionDto> Fields { get; init; } = Array.Empty<FieldPermissionDto>();
}

public record UpdateFieldPermissionsRequest(IReadOnlyList<FieldPermissionDto> Fields);

// ── Record-level row filters ──────────────────────────────────────────────────

public class RecordFilterConditionDto
{
    public Guid FieldPublicId { get; init; }
    public string Operator { get; init; } = "eq";
    public string? Value { get; init; }
    public bool UseCurrentUser { get; init; }
}

public class RecordLevelFilterDto
{
    public Guid TablePublicId { get; init; }
    public string Conjunction { get; init; } = "AND";
    public IReadOnlyList<RecordFilterConditionDto> Conditions { get; init; } = Array.Empty<RecordFilterConditionDto>();
}

public class RecordLevelFiltersResponse
{
    public Guid RolePublicId { get; init; }
    public IReadOnlyList<RecordLevelFilterDto> Filters { get; init; } = Array.Empty<RecordLevelFilterDto>();
}

public record UpdateRecordLevelFiltersRequest(IReadOnlyList<RecordLevelFilterDto> Filters);
