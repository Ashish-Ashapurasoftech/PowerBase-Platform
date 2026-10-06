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
        var plan = (await planBuilder.BuildAllAsync(cfg, destinationTableId, ct, file))[0];
        ImportDefinition entity;
        if (definitionId is null)
            entity = new ImportDefinition { AppId = plan.Destination.AppId, DestinationTableId = plan.Destination.Id, CreatedBy = user.UserId };
        else
        {
            entity = await definitions.GetByPublicIdAsync(definitionId.Value, ct) ?? throw new NotFoundException("ImportDefinition", definitionId.Value);
            if (entity.DestinationTableId != plan.Destination.Id) throw new NotFoundException("ImportDefinition", definitionId.Value);
        }

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
            cfg.AdditionalTargets.Count == 0 ? null : cfg.AdditionalTargets);
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
