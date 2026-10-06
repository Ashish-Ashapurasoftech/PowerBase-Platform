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

    public GetFieldUsageQueryHandler(
        IAppTableRepository appTableRepository,
        IAppFieldRepository appFieldRepository,
        IFormRepository formRepository,
        IFieldReferenceRepository references,
        IFieldReferenceIndexer indexer)
    {
        _appTableRepository = appTableRepository;
        _appFieldRepository = appFieldRepository;
        _formRepository = formRepository;
        _references = references;
        _indexer = indexer;
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
        // must be indexed before the answer is complete. Tables that predate meta.FieldReference are
        // backfilled here, once; afterwards this is a cheap "already indexed?" check per table.
        foreach (var appTable in await _appTableRepository.ListByAppAsync(table.AppId, cancellationToken))
            await _indexer.EnsureTableIndexedAsync(appTable.Id, cancellationToken);

        var references = await _references.ListByTargetFieldAsync(field.Id, cancellationToken);
        var allForms = await _formRepository.ListByTableAsync(request.TablePublicId, cancellationToken);
        var roles = await _appFieldRepository.GetRoleUsageAsync(field.Id, table.AppId, cancellationToken);

        var placedFormIds = references
            .Where(r => r.SourceType == FieldReferenceSourceTypes.Form)
            .Select(r => r.SourcePublicId)
            .ToHashSet();

        return new FieldUsageDto
        {
            // Every form of the table, flagged by whether it places this field itself.
            Forms = allForms
                .Select(f => new FieldUsageFormItem(f.PublicId, f.Name, placedFormIds.Contains(f.PublicId)))
                .ToList(),
            Reports = Group(references, FieldReferenceSourceTypes.Report)
                .Select(g => new FieldUsageReportItem(g.Key.SourcePublicId, g.Key.SourceName, UsedAs(g)))
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
        };
    }

    /// <summary>One group per source of that type, ordered by name (the same source can appear once per way it uses the field).</summary>
    private static IEnumerable<IGrouping<FieldReferenceItem, FieldReferenceItem>> Group(IEnumerable<FieldReferenceItem> references, string sourceType) =>
        references
            .Where(r => r.SourceType == sourceType)
            .GroupBy(r => r, SourceComparer.Instance)
            .OrderBy(g => g.Key.SourceName, StringComparer.OrdinalIgnoreCase);

    /// <summary>"group-by" → "group by"; each way listed once, in a stable order.</summary>
    private static List<string> UsedAs(IEnumerable<FieldReferenceItem> group) =>
        group.Select(r => r.Usage.Replace('-', ' ')).Distinct().Order().ToList();

    private sealed class SourceComparer : IEqualityComparer<FieldReferenceItem>
    {
        public static readonly SourceComparer Instance = new();
        public bool Equals(FieldReferenceItem? x, FieldReferenceItem? y) => x?.SourcePublicId == y?.SourcePublicId;
        public int GetHashCode(FieldReferenceItem obj) => obj.SourcePublicId.GetHashCode();
    }
}
