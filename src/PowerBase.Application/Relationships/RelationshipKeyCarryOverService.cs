using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// For one relationship, carries a field that's about to lose its "effective key" role forward as
/// a real Lookup on the child (so its value stays visible), and removes a Lookup an earlier
/// switch-away left behind for a field that's regaining that role (now redundant, since the
/// reference itself shows it again). Shared by both places the effective key can change:
/// <see cref="PowerBase.Application.Relationships.Commands.UpdateDisplayKey.UpdateDisplayKeyCommandHandler"/>
/// (one relationship's own override) and <see cref="PowerBase.Application.Fields.Commands.SetKey.SetKeyCommandHandler"/>
/// (the parent table's global key, which cascades to every relationship still on Standard).
/// </summary>
public sealed class RelationshipKeyCarryOverService
{
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly RelationshipFieldFactory _fieldFactory;

    public RelationshipKeyCarryOverService(
        IAppFieldRepository fieldRepo, IRelationshipRepository relRepo, RelationshipFieldFactory fieldFactory)
    {
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
        _fieldFactory = fieldFactory;
    }

    /// <summary>Labels of the lookup created/removed (for audit messages), or null if nothing happened.</summary>
    public readonly record struct Result(string? CarriedOverLabel, string? RemovedLabel);

    /// <param name="oldKeyField">The field that was the effective key before this change — resolve
    /// via KeyFieldResolver.ResolveDisplayKey (or table.KeyFieldId for the Set Key path), falling
    /// back to the Record ID# AppField when the result is null, so Standard key is a normal field
    /// here too, not a special case.</param>
    /// <param name="newKeyField">Same resolution, for the state after this change.</param>
    public async Task<Result> ApplyAsync(
        Relationship rel, AppTable parent, AppTable child,
        AppField? oldKeyField, AppField? newKeyField, CancellationToken ct)
    {
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);

        string? carriedOverLabel = null;
        if (oldKeyField?.Fid is int oldKeyFid && oldKeyField.Id != newKeyField?.Id)
        {
            var alreadyLookedUp = childFields.Any(f => f.TypeCode == "Lookup"
                && FormulaTypeMap.ParseLookupSettings(f.Settings) is { } ls
                && ls.RelationshipId == rel.Id && ls.SourceFid == oldKeyFid);

            // Every table has its own native "Record ID#" field, so a bare "Record ID#" label would
            // always collide with the CHILD's own — qualify it with the parent's name so the
            // carry-over isn't silently skipped by the collision check below every single time.
            var label = oldKeyField.Fid == 3 ? $"{parent.Name} Record ID#" : (oldKeyField.Label ?? oldKeyField.Name);
            if (!alreadyLookedUp && !await _fieldRepo.LabelExistsInTableAsync(child.Id, label, ct: ct))
            {
                var lookup = await _fieldFactory.CreateAsync(child, nameof(Domain.Enums.FieldTypeCode.Lookup), label, false,
                    new LookupSettings
                    {
                        RelationshipId = rel.Id,
                        ReferenceFid = rel.ReferenceFid,
                        SourceTableId = parent.Id,
                        SourceFid = oldKeyFid,
                        SourceTypeCode = oldKeyField.TypeCode,
                    }, ct);

                if (rel.ProxyFieldId is null)
                    await _relRepo.UpdateProxyFieldAsync(rel.Id, lookup.Id, ct);

                await _fieldFactory.AppendToAutoAddFormsAsync(child.PublicId, new List<int> { lookup.Fid!.Value }, ct);
                carriedOverLabel = label;
            }
        }

        string? removedLabel = null;
        if (newKeyField?.Fid is int newKeyFid && newKeyField.Id != oldKeyField?.Id)
        {
            var staleLookup = childFields.FirstOrDefault(f => f.TypeCode == "Lookup"
                && FormulaTypeMap.ParseLookupSettings(f.Settings) is { } ls
                && ls.RelationshipId == rel.Id && ls.SourceFid == newKeyFid);

            if (staleLookup is not null)
            {
                if (rel.ProxyFieldId == staleLookup.Id)
                {
                    var nextProxy = childFields.FirstOrDefault(f => f.TypeCode == "Lookup" && f.Id != staleLookup.Id
                        && FormulaTypeMap.ParseLookupSettings(f.Settings)?.RelationshipId == rel.Id);
                    await _relRepo.UpdateProxyFieldAsync(rel.Id, nextProxy?.Id, ct);
                }
                await _fieldRepo.DeleteAsync(staleLookup.PublicId, staleLookup.AppTableId, ct);
                removedLabel = staleLookup.Label ?? staleLookup.Name;
            }
        }

        return new Result(carriedOverLabel, removedLabel);
    }
}
