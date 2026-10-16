using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>Where a schedule stands after it was switched on or off from the list of imports.</summary>
public sealed record ImportScheduleState(bool Enabled, string? Summary, DateTime? NextRunOn);

/// <summary>Switches an import's schedule on or off without opening the import: the list's own switch. Only the on/off flag changes; the schedule's settings,
/// the import's mapping and everything else stay as saved, so the import's own screen (which still edits the whole schedule) is not affected.
/// <para>Switching it on works out the next run from now and, as saving the import does, runs it from then on as the person who switched it on (with their
/// access, never more). An import that needs attention is not switched on: it would only fail. Switching it off needs no more than the right to change the
/// import and takes effect at once: a run already started is not stopped.</para></summary>
public sealed class SetImportScheduleEnabledHandler(
    IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions, IQueryContext user)
{
    public async Task<ImportScheduleState> HandleAsync(Guid definitionId, bool enabled, CancellationToken ct)
    {
        var def = await definitions.GetByPublicIdAsync(definitionId, ct) ?? throw new NotFoundException("ImportDefinition", definitionId);
        var destination = await tables.GetByIdAsync(def.DestinationTableId, ct);
        await access.RequirePermissionByTablePublicIdAsync(destination.PublicId, PermissionCodes.RecordsCreate, ct);

        var schedule = ImportJson.Deserialize<ImportSchedule>(def.ScheduleJson)
            ?? throw Invalid("This import has no schedule. Open it to set one up.");
        if (schedule.Enabled == enabled) return State(schedule, def.NextRunOn); // already as asked: nothing to change

        if (enabled && def.NeedsAttention)
            throw Invalid($"This import needs attention before its schedule can be switched on: {def.AttentionReason ?? "a mapped field changed"}. Open it, fix it and save.");

        schedule.Enabled = enabled;
        var (prepared, nextRunOn) = ImportScheduling.Prepare(schedule, def.ScheduleJson, DateTime.UtcNow);
        def.ScheduleJson = ImportJson.Serialize(prepared);
        def.NextRunOn = nextRunOn;
        if (enabled) def.RunAsUserId = user.UserId;
        def.ModifiedBy = user.UserId;
        await definitions.UpdateAsync(def, ct);
        return State(prepared!, nextRunOn);
    }

    private static ImportScheduleState State(ImportSchedule schedule, DateTime? nextRunOn) =>
        new(schedule.Enabled, ImportScheduling.Describe(schedule) + (schedule.Enabled ? "" : " (paused)"), nextRunOn);

    private static ValidationException Invalid(string message) => new(new Dictionary<string, string[]> { ["Schedule"] = [message] });
}
