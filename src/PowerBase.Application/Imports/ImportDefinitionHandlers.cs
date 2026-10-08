using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

public sealed class SaveImportDefinitionHandler(
    ImportPlanBuilder planBuilder, IAppTableRepository tables, IImportDefinitionRepository definitions, IQueryContext user)
{
    /// <summary>Creates (<paramref name="definitionId"/> null) or updates a definition and returns its id. The full
    /// configuration is validated exactly as a run would validate it, so a saved import is always runnable.
    /// Saving re-binds the run identity to the current user, so editing someone else's import never lets it run
    /// with their access.</summary>
    public async Task<Guid> HandleAsync(Guid destinationTableId, Guid? definitionId, ImportDefinitionConfig cfg, CancellationToken ct)
    {
        // A file import has no source table: the file's columns, as saved with the import, stand in for one while it is checked.
        ImportFileSource? file = null;
        if (cfg.SourceKind == ImportSourceKinds.File)
        {
            cfg.File = ImportFileValidation.Validate(cfg.File);
            if (cfg.Schedule is not null)
                throw new ValidationException(new Dictionary<string, string[]> { ["Schedule"] = ["An import from a file runs when you upload a file; it cannot run on a schedule."] });
            file = ImportFileValidation.SavedSource(cfg.File);
        }
        else if (cfg.SourceKind != ImportSourceKinds.Table)
            throw new ValidationException(new Dictionary<string, string[]> { ["Import"] = ["The import must read from a table or a file."] });
        else cfg.File = null;

        // Every table the import fills is planned and permission-checked; an import into one table is exactly BuildAsync as before.
        var plans = await planBuilder.BuildAllAsync(cfg, destinationTableId, ct, file);
        var plan = plans[0];
        ImportDefinition entity;
        if (definitionId is null)
            entity = new ImportDefinition { AppId = plan.Destination.AppId, DestinationTableId = plan.Destination.Id, CreatedBy = user.UserId };
        else
        {
            entity = await definitions.GetByPublicIdAsync(definitionId.Value, ct) ?? throw new NotFoundException("ImportDefinition", definitionId.Value);
            if (entity.DestinationTableId != plan.Destination.Id) throw new NotFoundException("ImportDefinition", definitionId.Value);
        }

        // The Record ID# is assigned by the system: new imports cannot write it or match on it. An import saved before this rule keeps its own.
        var legacy = definitionId is null ? [] : ImportRecordIdRule.Used(ImportConfigMapper.ToConfig(entity, Guid.Empty), plan.Destination.PublicId);
        if (ImportRecordIdRule.Problem(plans, legacy) is { } recordIdProblem) throw new ValidationException(new Dictionary<string, string[]> { ["Import"] = [recordIdProblem] });

        ImportConfigMapper.Apply(entity, cfg);
        var (schedule, nextRunOn) = ImportScheduling.Prepare(cfg.Schedule, entity.ScheduleJson, DateTime.UtcNow);
        entity.ScheduleJson = schedule is null ? null : ImportJson.Serialize(schedule);
        entity.NextRunOn = nextRunOn;
        entity.SourceTableId = file is null ? plan.Source.Id : null;
        entity.RunAsUserId = user.UserId;
        entity.NeedsAttention = false;
        entity.AttentionReason = null;
        entity.ModifiedBy = user.UserId;

        if (definitionId is null) await definitions.CreateAsync(entity, ct);
        else await definitions.UpdateAsync(entity, ct);
        return entity.PublicId;
    }
}

public sealed class ListImportDefinitionsHandler(
    IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions, IImportDefinitionChecker checker)
{
    public async Task<IReadOnlyList<ImportDefinitionListItem>> HandleAsync(Guid destinationTableId, CancellationToken ct)
    {
        await access.RequireMembershipByTablePublicIdAsync(destinationTableId, ct);
        var table = await tables.GetByPublicIdAsync(destinationTableId, ct);
        // Fields may have changed since an import was saved: bring each import's "needs attention" flag up to date before listing.
        await checker.RefreshAsync(await definitions.ListEntitiesByDestinationAsync(table.Id, ct), ct);
        return await definitions.ListByDestinationAsync(table.Id, ct);
    }
}

/// <summary>All the imports of an app, for the app's Imports screen. Any member may see the list; what each import may touch is
/// still checked when it is opened, edited or run.</summary>
public sealed class ListAppImportDefinitionsHandler(
    IAppRepository apps, IAppAccessService access, IImportDefinitionRepository definitions, IImportDefinitionChecker checker)
{
    public async Task<IReadOnlyList<ImportDefinitionListItem>> HandleAsync(Guid appId, CancellationToken ct)
    {
        await access.RequireMembershipByAppPublicIdAsync(appId, ct);
        var id = await apps.GetIdByPublicIdAsync(appId, ct);
        await checker.RefreshAsync(await definitions.ListEntitiesByAppAsync(id, ct), ct);
        return await definitions.ListByAppAsync(id, ct);
    }
}

public sealed class GetImportDefinitionHandler(
    IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions, IImportDefinitionChecker checker)
{
    public async Task<ImportDefinitionDetail> HandleAsync(Guid definitionId, CancellationToken ct)
    {
        var def = await definitions.GetByPublicIdAsync(definitionId, ct) ?? throw new NotFoundException("ImportDefinition", definitionId);
        var destination = await tables.GetByIdAsync(def.DestinationTableId, ct);
        await access.RequireMembershipByTablePublicIdAsync(destination.PublicId, ct);
        await checker.RefreshAsync(def, ct);
        var source = def.SourceKind == ImportSourceKinds.File ? null : await tables.GetByIdAsync(def.SourceTableId ?? 0, ct);
        var cfg = ImportConfigMapper.ToConfig(def, source?.PublicId ?? Guid.Empty);
        return new ImportDefinitionDetail(def.PublicId, cfg.Name, destination.PublicId, source?.PublicId ?? Guid.Empty, cfg.ImportType, cfg.MergeKeyFid,
            cfg.Conditions, cfg.Mappings, cfg.ColumnRules, cfg.ConstraintPolicy, cfg.NotifyEmails, def.NeedsAttention, def.AttentionReason,
            ImportJson.Deserialize<ImportSchedule>(def.ScheduleJson), def.NextRunOn, def.SourceKind, cfg.File,
            cfg.AdditionalTargets.Count == 0 ? null : cfg.AdditionalTargets, cfg.VirtualColumns.Count == 0 ? null : cfg.VirtualColumns,
            cfg.TableConditions is { Nodes.Count: > 0 } ? cfg.TableConditions : null);
    }
}

public sealed class DeleteImportDefinitionHandler(
    IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions, IQueryContext user)
{
    public async Task HandleAsync(Guid definitionId, CancellationToken ct)
    {
        var def = await definitions.GetByPublicIdAsync(definitionId, ct) ?? throw new NotFoundException("ImportDefinition", definitionId);
        var destination = await tables.GetByIdAsync(def.DestinationTableId, ct);
        await access.RequirePermissionByTablePublicIdAsync(destination.PublicId, PowerBase.Domain.Constants.PermissionCodes.RecordsCreate, ct);
        await definitions.DeleteAsync(def.Id, user.UserId, ct);
    }
}

/// <summary>The Record ID# is assigned by the system, so a new import cannot write to it or match records on it. An import that already did
/// when this rule came in keeps working and can be saved again as it is; the rule only stops it being added to a new import or to another table.</summary>
/// <summary>Deletes several imports of an app at once (the list's bulk delete). All or nothing: every import must be the caller's to delete,
/// by the same rule as deleting one (the right to add records to its first table), or none is deleted. Only imports of this app are
/// considered, so an id from another app is simply not found.</summary>
public sealed class DeleteImportDefinitionsHandler(
    IAppRepository apps, IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions, IQueryContext user)
{
    public const int MaxAtOnce = 200;

    /// <returns>How many imports were deleted.</returns>
    public async Task<int> HandleAsync(Guid appId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await access.RequireMembershipByAppPublicIdAsync(appId, ct);
        var wanted = ids.Distinct().ToHashSet();
        if (wanted.Count == 0) return 0;
        if (wanted.Count > MaxAtOnce)
            throw new ValidationException(new Dictionary<string, string[]> { ["Imports"] = [$"Delete at most {MaxAtOnce} imports at a time."] });

        var inApp = await definitions.ListEntitiesByAppAsync(await apps.GetIdByPublicIdAsync(appId, ct), ct);
        var found = inApp.Where(d => wanted.Contains(d.PublicId)).ToList();
        foreach (var tableId in found.Select(d => d.DestinationTableId).Distinct())
        {
            var table = await tables.GetByIdAsync(tableId, ct);
            await access.RequirePermissionByTablePublicIdAsync(table.PublicId, PowerBase.Domain.Constants.PermissionCodes.RecordsCreate, ct);
        }
        await definitions.DeleteManyAsync(found.Select(d => d.Id).ToList(), user.UserId, ct);
        return found.Count;
    }
}

public static class ImportRecordIdRule
{
    /// <summary>The (table, field) pairs a stored configuration writes to, for the tables it fills.</summary>
    public static HashSet<(Guid Table, int Fid)> Used(ImportDefinitionConfig stored, Guid homeTable)
    {
        var used = stored.Mappings.Where(m => !m.DoNotImport).Select(m => (homeTable, m.DestFid)).ToHashSet();
        foreach (var t in stored.AdditionalTargets)
            foreach (var m in t.Mappings.Where(m => !m.DoNotImport)) used.Add((t.DestinationTableId, m.DestFid));
        return used;
    }

    public static string? Problem(IReadOnlyList<ImportPlan> plans, IReadOnlySet<(Guid Table, int Fid)> legacy)
    {
        foreach (var plan in plans)
            foreach (var m in plan.Mappings)
                if (ImportTypeCompatibility.IsRecordId(m.Destination) && !legacy.Contains((plan.Destination.PublicId, m.Destination.Fid ?? 0)))
                    return $"'{plan.Destination.Name}': the Record ID# is assigned by the system. It cannot be imported into or used to match records.";
        return null;
    }
}
