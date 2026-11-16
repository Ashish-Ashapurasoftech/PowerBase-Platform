// MediatR not used in this project
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Queries.GetFieldUsage;

public record GetFieldUsageQuery(Guid TablePublicId, Guid FieldPublicId);

public class GetFieldUsageQueryHandler
{
    private readonly IAppTableRepository _appTableRepository;
    private readonly IAppFieldRepository _appFieldRepository;
    private readonly IFormRepository _formRepository;
    private readonly IFieldReferenceRepository _references;
    private readonly IFieldReferenceIndexer _indexer;
    private readonly IUserRepository _users;

    public GetFieldUsageQueryHandler(
        IAppTableRepository appTableRepository,
        IAppFieldRepository appFieldRepository,
        IFormRepository formRepository,
        IFieldReferenceRepository references,
        IFieldReferenceIndexer indexer,
        IUserRepository users)
    {
        _appTableRepository = appTableRepository;
        _appFieldRepository = appFieldRepository;
        _formRepository = formRepository;
        _references = references;
        _indexer = indexer;
        _users = users;
    }

    public async Task<FieldUsageDto> HandleAsync(GetFieldUsageQuery request, CancellationToken cancellationToken = default)
    {
        var table = await _appTableRepository.GetByPublicIdAsync(request.TablePublicId, cancellationToken);
        if (table == null)
            throw new NotFoundException(nameof(table), request.TablePublicId);

        var field = await _appFieldRepository.GetByPublicIdAsync(request.FieldPublicId, cancellationToken);
        if (field == null)
            throw new NotFoundException(nameof(field), request.FieldPublicId);

        // Another table's Lookup/Summary/Report Link can read this field, so every table of the app
        // must be indexed before the answer is complete. Tables that predate meta.FieldReference (or an
        // older index version) are backfilled here, once; afterwards it is a single "which are already
        // indexed?" query for the whole app.
        var appTables = await _appTableRepository.ListByAppAsync(table.AppId, cancellationToken);
        await _indexer.EnsureTablesIndexedAsync(appTables.Select(t => t.Id).ToList(), cancellationToken);
        // PowerFlows are saved by another module, so edits are picked up by a scan; a background worker keeps it warm and
        // this makes the answer exact (a no-op query when nothing changed).
        await _indexer.SyncPipelinesAsync(cancellationToken);

        var references = await _references.ListByTargetFieldAsync(field.Id, cancellationToken);
        var allForms = await _formRepository.ListByTableAsync(request.TablePublicId, cancellationToken);
        var roles = await _appFieldRepository.GetRoleUsageAsync(field.Id, table.AppId, cancellationToken);

        var placedFormIds = references
            .Where(r => r.SourceType == FieldReferenceSourceTypes.Form)
            .Select(r => r.SourcePublicId)
            .ToHashSet();

        // Report owners' display names, in one lookup. A failed lookup only leaves the Owner column blank —
        // it must never take the whole Usage tab down.
        var ownerNames = await OwnerNamesAsync(references, cancellationToken);

        return new FieldUsageDto
        {
            // Every form of the table, flagged by whether it places this field itself.
            Forms = allForms
                .Select(f => new FieldUsageFormItem(f.PublicId, f.Name, placedFormIds.Contains(f.PublicId)))
                .ToList(),
            Reports = Group(references, FieldReferenceSourceTypes.Report)
                .Select(g => new FieldUsageReportItem(
                    g.Key.SourcePublicId, g.Key.SourceName, UsedAs(g),
                    g.Key.OwnerId is { } ownerId && ownerNames.TryGetValue(ownerId, out var ownerName) ? ownerName : null,
                    string.Equals(g.Key.Visibility, PersonalVisibility, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
            FormRules = Group(references, FieldReferenceSourceTypes.FormRule)
                .Select(g => new FieldUsageFormRuleItem(
                    g.Key.SourcePublicId, g.Key.SourceName,
                    g.Key.ParentPublicId ?? Guid.Empty, g.Key.ParentName ?? string.Empty, UsedAs(g)))
                .ToList(),
            Fields = Group(references, FieldReferenceSourceTypes.Field)
                .Select(g => new FieldUsageFieldItem(
                    g.Key.SourcePublicId, g.Key.SourceName, g.Key.SourceTablePublicId, g.Key.SourceTableName, UsedAs(g)))
                .ToList(),
            Roles = roles.ToList(),
            OtherItems = OtherItemsOf(references, ownerNames),
        };
    }

    private static List<FieldUsageOtherItem> OtherItemsOf(IReadOnlyList<FieldReferenceItem> references, IReadOnlyDictionary<long, string> ownerNames)
    {
        var items = new List<FieldUsageOtherItem>();
        foreach (var (sourceType, kind) in new[]
        {
            (FieldReferenceSourceTypes.Relationship, FieldUsageKinds.Relationship),
            (FieldReferenceSourceTypes.Dashboard, FieldUsageKinds.Dashboard),
            (FieldReferenceSourceTypes.CustomRule, FieldUsageKinds.CustomRule),
            (FieldReferenceSourceTypes.PowerFlow, FieldUsageKinds.PowerFlow),
            (FieldReferenceSourceTypes.Import, FieldUsageKinds.Import),
        })
        {
            items.AddRange(Group(references, sourceType).Select(g => new FieldUsageOtherItem(
                kind, g.Key.SourcePublicId, g.Key.SourceName,
                g.Key.OwnerId is { } ownerId && ownerNames.TryGetValue(ownerId, out var owner) ? owner : null,
                UsedAs(g), g.Key.SourceTablePublicId, g.Key.SourceTableName)));
        }
        return items;
    }

    /// <summary>meta.Report.Visibility for a report only its owner can see.</summary>
    private const string PersonalVisibility = "Personal";

    private async Task<IReadOnlyDictionary<long, string>> OwnerNamesAsync(IEnumerable<FieldReferenceItem> references, CancellationToken ct)
    {
        var ids = references
            .Where(r => r.OwnerId.HasValue) // only reports and dashboards carry an owner
            .Select(r => r.OwnerId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return new Dictionary<long, string>();

        try
        {
            return await _users.GetNamesByIdsAsync(ids, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Dictionary<long, string>();
        }
    }

    /// <summary>One group per source of that type, ordered by name (the same source can appear once per way it uses the field).</summary>
    private static IEnumerable<IGrouping<FieldReferenceItem, FieldReferenceItem>> Group(IEnumerable<FieldReferenceItem> references, string sourceType) =>
        references
            .Where(r => r.SourceType == sourceType)
            .GroupBy(r => r, SourceComparer.Instance)
            .OrderBy(g => g.Key.SourceName, StringComparer.OrdinalIgnoreCase);

    /// <summary>"group-by" → "group by"; each way listed once, in a stable order.</summary>
    private static List<string> UsedAs(IEnumerable<FieldReferenceItem> group) =>
        // A PowerFlow's "usage" is already a step label ("Create Record - Orders"), so it is shown as written.
        group.Select(r => r.SourceType == FieldReferenceSourceTypes.PowerFlow ? r.Usage : r.Usage.Replace('-', ' ')).Distinct().Order().ToList();

    private sealed class SourceComparer : IEqualityComparer<FieldReferenceItem>
    {
        public static readonly SourceComparer Instance = new();
        public bool Equals(FieldReferenceItem? x, FieldReferenceItem? y) => x?.SourcePublicId == y?.SourcePublicId;
        public int GetHashCode(FieldReferenceItem obj) => obj.SourcePublicId.GetHashCode();
    }
}
