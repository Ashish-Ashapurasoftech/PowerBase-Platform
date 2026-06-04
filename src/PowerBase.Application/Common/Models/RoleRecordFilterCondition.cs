namespace PowerBase.Application.Common.Models;

/// <summary>
/// Persisted shape of a single record-level filter condition (stored as JSON in
/// meta.AppRoleRecordFilter.FilterJson). FieldPublicId is stable across schema edits;
/// it is resolved to a numeric field id only at enforcement time.
/// </summary>
public record RoleRecordFilterCondition(
    Guid FieldPublicId,
    string Operator,
    string? Value,
    bool UseCurrentUser);
