using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Search;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using System.Linq;

namespace PowerBase.Application.Records.Queries.SearchGlobalRecords;

/// <summary>
/// Search across every table of the tenant. The index only proposes candidates, best first; each is then checked against the database
/// before it is shown: the record still exists, the user may see its table and the record itself, and the search matches in fields the
/// user may see (so a hidden field's content can never be found by typing it). Records with a change the index has not confirmed yet are
/// checked in SQL as well, so a record saved a moment ago is found and an edited one is not found by its old text. Only records the user
/// may see are counted, and the display text comes from the database, from fields the user may see.
/// </summary>
public class SearchGlobalRecordsQueryHandler
{
    /// <summary>Candidates asked of the index per round.</summary>
    internal const int CandidateBatch = 100;
    /// <summary>The most candidates one search looks at (the index's window when it merges several indexes): a page past what they hold is empty.</summary>
    internal const int MaxCandidates = 1000;
    /// <summary>Records with an unconfirmed change that are checked in SQL on every search.</summary>
    internal const int PendingLimit = 200;
    public const int MaxPageSize = 50;

    private readonly IAzureSearchService _searchService;
    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly ISearchOutbox? _outbox;

    public SearchGlobalRecordsQueryHandler(
        IAzureSearchService searchService,
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        ISearchOutbox? outbox = null)
    {
        _searchService = searchService;
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _outbox = outbox;
    }

    /// <summary>What one table needs to check a record: null when the table or its app is gone (deleted or archived).</summary>
    private sealed record TableContext(AppTable Table, App App, IReadOnlyList<AppField> Fields, TableAccessContext Access);

    public async Task<SearchGlobalRecordsResult> HandleAsync(SearchGlobalRecordsQuery request, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);
        if (string.IsNullOrWhiteSpace(request.SearchText)) return new SearchGlobalRecordsResult([], 0, page, pageSize);

        var skip = (page - 1) * pageSize;
        var wanted = skip + pageSize + 1; // one more than the page: tells whether another page follows
        var tables = new Dictionary<long, TableContext?>();
        var found = new List<SearchGlobalRecordsResultItem>();
        var seen = new HashSet<Guid>();

        // 1. Records with a change the index has not confirmed: it may not hold them yet, or hold an older copy. SQL decides them (one read
        // per table), newest first.
        var pending = await PendingAsync(request.AppId, cancellationToken);
        var pendingVerified = new Dictionary<Guid, SearchGlobalRecordsResultItem>();
        foreach (var group in pending.GroupBy(p => p.TableId))
            foreach (var item in await VerifyAsync(group.Key, group.Select(p => p.RecordPublicId).ToList(), request.SearchText, tables, cancellationToken))
                pendingVerified[item.RecordId] = item;
        foreach (var p in pending)
            if (seen.Add(p.RecordPublicId) && pendingVerified.TryGetValue(p.RecordPublicId, out var item))
                found.Add(item);

        // 2. The index's matches, best first, each checked against the database, until the page is full or the index has no more.
        for (var round = 1; found.Count < wanted && (round - 1) * CandidateBatch < MaxCandidates; round++)
        {
            var raw = await _searchService.SearchGlobalAsync(_queryContext.TenantId, request.SearchText, request.AppId, round, CandidateBatch, cancellationToken);
            var candidates = raw.Items.Where(r => seen.Add(r.PublicId)).ToList();

            var verified = new Dictionary<Guid, SearchGlobalRecordsResultItem>();
            foreach (var group in candidates.GroupBy(c => c.TableId))
                foreach (var item in await VerifyAsync(group.Key, group.Select(c => c.PublicId).ToList(), request.SearchText, tables, cancellationToken))
                    verified[item.RecordId] = item;
            found.AddRange(candidates.Where(c => verified.ContainsKey(c.PublicId)).Select(c => verified[c.PublicId]));

            var looked = (round - 1) * CandidateBatch + raw.Items.Count;
            if (raw.Items.Count < CandidateBatch || (raw.TotalCount is long total && looked >= total)) break;
        }

        // Only records this user may see are counted, never the index's own total (which counts records hidden from them). Exact once every
        // match was looked at; otherwise how many were found so far, which is more than this page whenever another page follows.
        return new SearchGlobalRecordsResult(found.Skip(skip).Take(pageSize).ToList(), found.Count, page, pageSize);
    }

    private async Task<IReadOnlyList<SearchOutboxPendingRecord>> PendingAsync(long? appId, CancellationToken ct)
    {
        if (_outbox == null) return [];
        var pending = await _outbox.ListPendingRecordsAsync(PendingLimit, ct) ?? [];
        return appId.HasValue ? pending.Where(p => p.AppId == appId.Value).ToList() : pending;
    }

    private async Task<TableContext?> TableAsync(long tableId, Dictionary<long, TableContext?> tables, CancellationToken ct)
    {
        if (tables.TryGetValue(tableId, out var known)) return known;
        TableContext? context = null;
        try
        {
            var table = await _tableRepo.GetByIdAsync(tableId, ct);
            var app = await _appRepo.GetByIdAsync(table.AppId, ct);
            var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            context = new TableContext(table, app, fields, await _enforcer.GetTableAccessAsync(table, fields, ct));
        }
        catch (NotFoundException)
        {
            // The table or its app was deleted: its records are not shown (any other failure is a real error and is not hidden).
        }
        return tables[tableId] = context;
    }

    /// <summary>The records of one table that this user may see and that match the search in fields this user may see, in the order asked.</summary>
    private async Task<IReadOnlyList<SearchGlobalRecordsResultItem>> VerifyAsync(
        long tableId, IReadOnlyList<Guid> recordIds, string search, Dictionary<long, TableContext?> tables, CancellationToken ct)
    {
        var context = await TableAsync(tableId, tables, ct);
        if (context == null || !context.Access.CanView) return [];

        // An unrestricted user sees every field.
        var visible = (context.Access.Unrestricted ? context.Fields : context.Access.VisibleFields)
            .Where(f => f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        // The fields the index searches, cut down to those this user may see.
        var searchable = visible.Where(f => f.IsSearchable && SearchSlotResolver.IsIndexableType(f.TypeCode)).ToList();
        if (searchable.Count == 0) return [];

        // Live records only, decrypted, in the order asked.
        var rows = await _recordRepo.ListByPublicIdsAsync(context.Table, visible, recordIds, ct);
        var result = new List<SearchGlobalRecordsResultItem>(rows.Count);
        foreach (var row in rows)
        {
            if (row["PublicId"] is not Guid id) continue;
            if (!SearchText.Matches(search, searchable.Select(f => SearchText.Format(f.TypeCode, ValueOf(row, f))))) continue;

            // D02: Enforce record-level scope (OwnRecords, Role ViewFilter)
            if (!context.Access.Unrestricted && (context.Access.ViewFilter != null || context.Access.RestrictToCreatedBy != null)
                && !await _recordRepo.ExistsWithViewFilterAsync(context.Table, context.Fields, id,
                    viewFilter: context.Access.ViewFilter, restrictToCreatedBy: context.Access.RestrictToCreatedBy, ct: ct))
                continue;

            result.Add(new SearchGlobalRecordsResultItem(
                id,
                context.App.PublicId,
                context.App.Name,
                context.Table.PublicId,
                context.Table.SingularLabel ?? context.Table.Name ?? "Record",
                context.Table.Icon,
                PrimaryText(context, visible, searchable, row, id)));
        }
        return result;
    }

    /// <summary>D02: what the result shows, from fields this user may see: the table's display field, else the first searchable field with
    /// text, else the first other field with text, else the record's id.</summary>
    private static string PrimaryText(TableContext context, IReadOnlyList<AppField> visible, IReadOnlyList<AppField> searchable,
        IReadOnlyDictionary<string, object?> row, Guid id)
    {
        if (context.Table.DisplayFieldId is long displayFid
            && visible.FirstOrDefault(f => f.Fid == displayFid) is { } displayField
            && SearchText.Format(displayField.TypeCode, ValueOf(row, displayField)) is { } displayText)
            return displayText;

        foreach (var field in searchable.Concat(visible.Where(f => !f.IsSystem)))
            if (SearchText.Format(field.TypeCode, ValueOf(row, field)) is { } text)
                return text;

        return $"Record ID: {id.ToString()[..8]}";
    }

    private static object? ValueOf(IReadOnlyDictionary<string, object?> row, AppField field)
    {
        var column = field.IsSystem && !string.IsNullOrWhiteSpace(field.PhysicalColumnName)
            ? field.PhysicalColumnName!
            : PhysicalNaming.ColumnName(field.Fid!.Value);
        return row.TryGetValue(column, out var value) ? value : null;
    }
}
