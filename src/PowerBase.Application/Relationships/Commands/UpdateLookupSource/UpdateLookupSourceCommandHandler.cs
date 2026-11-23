using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.UpdateLookupSource;

/// <summary>Lookup field options: point an existing Lookup at another Reference field (an existing relationship of its
/// table — no relationship is created) and/or another Value field on that relationship's parent table.</summary>
public record UpdateLookupSourceCommand(Guid TablePublicId, Guid FieldPublicId, Guid RelationshipPublicId, int SourceFid, string? CommitMessage = null);

/// <summary>
/// Saves a Lookup's new source. Lookups have no column (they are read through the reference at read time), so nothing
/// is copied: the next read shows the new value. The new value field must be on the chosen relationship's parent table
/// and hold the same kind of value the lookup shows now (<see cref="LookupSourceCompatibility"/>), so whatever is built
/// on the lookup keeps working. "Display as a link" and the Behavior Settings are kept. Recorded as a field version.
/// </summary>
public class UpdateLookupSourceCommandHandler
{
    private const string DefaultCommitMessage = "Lookup field options updated";

    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly FieldVersionService _versionService;
    private readonly ITenantUnitOfWork _uow;
    private readonly IAuditRepository _auditRepo;
    private readonly IFieldReferenceIndexer _refIndexer;

    public UpdateLookupSourceCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        FieldVersionService versionService,
        ITenantUnitOfWork uow,
        IAuditRepository auditRepo,
        IFieldReferenceIndexer? refIndexer = null)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _versionService = versionService;
        _uow = uow;
        _auditRepo = auditRepo;
        _refIndexer = refIndexer ?? NullFieldReferenceIndexer.Instance;
    }

    public async Task HandleAsync(UpdateLookupSourceCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var lookup = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct);
        if (lookup is null || lookup.IsDeleted || lookup.AppTableId != table.Id || !SummaryLookupSources.IsLookup(lookup))
            throw new NotFoundException("Lookup field", command.FieldPublicId);

        // Reference field: one of this table's existing relationships, with its reference field still there.
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct);
        if (rel is null || rel.ChildTableId != table.Id)
            throw Invalid("relationshipId", $"Choose a reference field on the {table.Name} table.");
        var childFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        if (!childFields.Any(f => f.Id == rel.ReferenceFieldId && !f.IsDeleted))
            throw Invalid("relationshipId", "The reference field of that relationship no longer exists.");

        // Value field: a field of that relationship's parent table that can be pulled down.
        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var source = parentFields.FirstOrDefault(f => f.Fid == command.SourceFid && !f.IsDeleted)
            ?? throw Invalid("sourceFid", $"Field {command.SourceFid} was not found on the {parent.Name} table.");
        if (!LookupSourceCompatibility.CanBeSource(source))
            throw Invalid("sourceFid", $"'{Display(source)}' has no value a lookup can show.");
        if (source.Id == lookup.Id || await LookupSourceRules.ChainReachesAsync(source, lookup.Id, _fieldRepo, ct))
            throw Invalid("sourceFid", $"'{Display(source)}' reads this lookup, so the lookup can't pull it.");

        var settings = FormulaTypeMap.ParseLookupSettings(lookup.Settings) ?? new Domain.FieldSettings.LookupSettings();
        if (settings.RelationshipId == rel.Id && settings.SourceFid == source.Fid && settings.ReferenceFid == rel.ReferenceFid
            && settings.SourceTableId == parent.Id)
            return;   // nothing changed

        // The same parent field through another relationship to that table keeps its address part, if it has one.
        var sameSource = settings.SourceTableId == parent.Id && settings.SourceFid == source.Fid;
        var keepSubField = sameSource && !string.IsNullOrWhiteSpace(settings.SourceSubField);
        var (sourceTypeCode, resultTypeCode) = await LookupChain.ResolveForNewLookupAsync(
            source, keepSubField ? settings.SourceSubField : null, _fieldRepo, ct);
        var currentType = FieldValueTypes.Of(lookup);
        if (!LookupSourceCompatibility.IsCompatible(currentType, resultTypeCode))
            throw Invalid("sourceFid",
                $"'{Display(source)}' holds {resultTypeCode ?? "an unknown type of"} values, but '{Display(lookup)}' shows {currentType} values. "
                + "Choose a field of the same type.");

        var oldRelationshipId = settings.RelationshipId;
        settings.RelationshipId = rel.Id;
        settings.ReferenceFid = rel.ReferenceFid;
        settings.SourceTableId = parent.Id;
        settings.SourceFid = source.Fid;
        settings.SourceTypeCode = sourceTypeCode;
        settings.ResultTypeCode = resultTypeCode;
        if (!keepSubField) settings.SourceSubField = null;   // an address part only means something for that address field
        var newSettings = RelationshipFieldFactory.Serialize(settings);

        var before = FieldSnapshot.From(lookup);
        var after = before with { Settings = newSettings };
        var commitMessage = string.IsNullOrWhiteSpace(command.CommitMessage) ? DefaultCommitMessage : command.CommitMessage.Trim();

        await _uow.BeginAsync(ct);
        try
        {
            var affected = await _fieldRepo.UpdateAsync(
                lookup.PublicId, table.Id,
                lookup.Label, lookup.Description,
                lookup.IsRequired, lookup.DefaultValue,
                lookup.IsSearchable, lookup.IsSortable,
                lookup.IsFilterable, lookup.IsReportable, lookup.IsAuditable,
                lookup.IsUnique, lookup.IsEncrypted, lookup.IsAutoFill, newSettings, ct, _uow.Transaction);
            if (affected == 0) throw new NotFoundException("Lookup field", command.FieldPublicId);

            await _versionService.CreateVersionIfChangedAsync(
                lookup.Id, before, after, commitMessage,
                FieldVersionChangeType.Update, restoredFromVersion: null, _uow.Transaction!, ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        // Moved to another relationship: it can no longer stand in for the old relationship's reference.
        Relationship? oldRel = null;
        if (oldRelationshipId is long oldId && oldId != rel.Id)
        {
            oldRel = await _relRepo.GetByIdAsync(oldId, ct);
            if (oldRel?.ProxyFieldId == lookup.Id)
                await _relRepo.UpdateProxyFieldAsync(oldRel.Id, null, ct);
        }

        // Lookups that pull this lookup further down show what it now shows (Text → Url keeps the kind, not the code).
        var updated = new AppField { Id = lookup.Id, Fid = lookup.Fid, AppTableId = lookup.AppTableId, TypeCode = lookup.TypeCode, Settings = newSettings };
        await FieldValueTypes.RefreshLookupsOfAsync(updated, FieldValueTypes.Of(updated), _relRepo, _fieldRepo, ct);

        await _refIndexer.ReindexFieldAsync(lookup.PublicId, ct);
        await _refIndexer.ReindexRelationshipAsync(rel.PublicId, ct);
        if (oldRel is not null) await _refIndexer.ReindexRelationshipAsync(oldRel.PublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, lookup.PublicId.ToString(),
            $"Lookup field '{Display(lookup)}' on {table.Name} now pulls {parent.Name}: {Display(source)}",
            appId: table.AppId, ct: ct);
    }

    private static ValidationException Invalid(string key, string message) =>
        new(new Dictionary<string, string[]> { [key] = [message] });

    private static string Display(AppField f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label!;
}
