using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>One saved import as a PowerFlow lists it: just enough to show it in a dropdown and to start it afterwards
/// (<c>POST apps/{appId}/imports/{templateId}/run</c>).</summary>
/// <param name="TemplateId">The import's id: what the run endpoint takes.</param>
/// <param name="SourceKind">"table" (copies from another table) or "file" (an uploaded CSV / Excel file).</param>
/// <param name="TableId">The first table the import fills (where it is saved).</param>
/// <param name="TableIds">Every table the import fills, the first one included (an import can fill up to 5).</param>
/// <param name="NeedsAttention">As last saved or checked: a field the import uses changed, so it cannot run until it is opened and saved.</param>
public sealed record PowerFlowImportTemplate(
    Guid TemplateId, string Name, string ImportType, string SourceKind, Guid TableId, string TableName,
    IReadOnlyList<Guid> TableIds, bool NeedsAttention, string? AttentionReason);

/// <summary>The imports of an app for a PowerFlow to pick from: the person chooses an app, then a table, and gets the imports that fill that table (or
/// one import by its id, or only those that read a file or a table). A read of what is saved and nothing else: it does not recheck, change or run
/// anything, and it is its own code, so the import screen and the run API are untouched by it.
/// <para>Every filter is optional and they are combined with AND. The caller must be a member of the app; the checks run in this order: who may ask,
/// then what was asked, then the data, so a stranger learns nothing from a validation message. A table that is not in the app is a 404, never an
/// empty list; a template id that is no import of the app (or of another app) is simply an empty list, which tells nothing about other apps.</para></summary>
public sealed class ListPowerFlowImportTemplatesHandler(
    IAppRepository apps, IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions)
{
    public const string SourceKindFile = "file";
    public const string SourceKindTable = "table";

    public async Task<IReadOnlyList<PowerFlowImportTemplate>> HandleAsync(
        Guid appId, Guid? tableId, Guid? templateId, string? sourceKind, CancellationToken ct)
    {
        if (appId == Guid.Empty) throw Invalid("appId", "appId must be a valid id.");
        await access.RequireMembershipByAppPublicIdAsync(appId, ct);

        // What was asked. A filter that is present but means nothing is a mistake, and is refused rather than ignored (an ignored filter would list too much).
        if (tableId == Guid.Empty) throw Invalid("tableId", "tableId must be a valid id.");
        if (templateId == Guid.Empty) throw Invalid("templateId", "templateId must be a valid id.");
        var kind = NormalizeSourceKind(sourceKind);

        var app = await apps.GetIdByPublicIdAsync(appId, ct);

        // A table of another app is not a filter, it is a mistake: say so rather than return an empty list.
        if (tableId is { } wanted && (await tables.GetByPublicIdAsync(wanted, ct)).AppId != app)
            throw new NotFoundException("Table", wanted);

        IEnumerable<ImportDefinitionListItem> items = await definitions.ListByAppAsync(app, ct);
        if (templateId is { } id) items = items.Where(i => i.PublicId == id);
        if (kind is not null) items = items.Where(i => string.Equals(i.SourceKind, kind, StringComparison.OrdinalIgnoreCase));
        var matching = items.ToList();
        if (matching.Count == 0) return [];

        // The list rows know only the first table an import fills; the others are in its saved options.
        var others = (await definitions.ListEntitiesByAppAsync(app, ct)).GroupBy(d => d.PublicId).ToDictionary(g => g.Key, g => OtherTables(g.First()));

        var result = new List<PowerFlowImportTemplate>(matching.Count);
        foreach (var item in matching)
        {
            var tableIds = new List<Guid> { item.DestinationTableId };
            if (others.TryGetValue(item.PublicId, out var extra)) tableIds.AddRange(extra.Where(t => t != Guid.Empty && !tableIds.Contains(t)));
            if (tableId is { } table && !tableIds.Contains(table)) continue;
            result.Add(new PowerFlowImportTemplate(item.PublicId, item.Name, item.ImportType, item.SourceKind, item.DestinationTableId,
                item.DestinationTableName, tableIds, item.NeedsAttention, item.AttentionReason));
        }
        return result;
    }

    /// <summary>The kinds of source an import can have, as the caller wrote it (case and surrounding spaces do not matter); null when none was asked for.</summary>
    private static string? NormalizeSourceKind(string? sourceKind)
    {
        if (string.IsNullOrWhiteSpace(sourceKind)) return null;
        var kind = sourceKind.Trim().ToLowerInvariant();
        if (kind is SourceKindFile or SourceKindTable) return kind;
        throw Invalid("sourceKind", "sourceKind must be 'file' or 'table'.");
    }

    /// <summary>The other tables an import fills, from its saved options. An import whose options cannot be read still lists, with its first table only:
    /// one damaged saved import must not take the whole list down.</summary>
    private static List<Guid> OtherTables(PowerBase.Domain.Entities.ImportDefinition definition)
    {
        try { return ImportConfigMapper.ToConfig(definition, Guid.Empty).AdditionalTargets.Select(t => t.DestinationTableId).ToList(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NullReferenceException) { return []; }
    }

    private static ValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
