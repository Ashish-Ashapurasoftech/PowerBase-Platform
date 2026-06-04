using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Apps.Commands.UpdateRecordFilters;

public record RecordFilterInput(
    Guid TablePublicId,
    string Conjunction,
    IReadOnlyList<RoleRecordFilterCondition> Conditions);

public record UpdateRecordFiltersCommand(
    Guid RolePublicId,
    IReadOnlyList<RecordFilterInput> Filters);
