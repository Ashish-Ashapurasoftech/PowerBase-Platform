using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Apps.Commands.UpdateRecordFilters;

/// <param name="Conditions">Legacy flat shape — still accepted so older clients keep working.</param>
/// <param name="Group">Nested ALL/ANY tree (fields by Fid). When it holds at least one condition it
/// takes precedence over <paramref name="Conditions"/>.</param>
/// <param name="Kind">"View" (default; also what older clients send) or "Modify" — the Custom Rules that
/// decide which records the role may edit / delete. Modify rules are group-shaped only.</param>
public record RecordFilterInput(
    Guid TablePublicId,
    string Conjunction,
    IReadOnlyList<RoleRecordFilterCondition> Conditions,
    FilterGroup? Group = null,
    string Kind = "View");

public record UpdateRecordFiltersCommand(
    Guid RolePublicId,
    IReadOnlyList<RecordFilterInput> Filters);
