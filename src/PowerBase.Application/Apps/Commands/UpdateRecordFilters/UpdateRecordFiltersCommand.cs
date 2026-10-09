using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Apps.Commands.UpdateRecordFilters;

/// <param name="Conditions">Legacy flat shape — still accepted so older clients keep working.</param>
/// <param name="Group">Nested ALL/ANY tree (fields by Fid). When it holds at least one condition it
/// takes precedence over <paramref name="Conditions"/>.</param>
public record RecordFilterInput(
    Guid TablePublicId,
    string Conjunction,
    IReadOnlyList<RoleRecordFilterCondition> Conditions,
    FilterGroup? Group = null);

public record UpdateRecordFiltersCommand(
    Guid RolePublicId,
    IReadOnlyList<RecordFilterInput> Filters);
