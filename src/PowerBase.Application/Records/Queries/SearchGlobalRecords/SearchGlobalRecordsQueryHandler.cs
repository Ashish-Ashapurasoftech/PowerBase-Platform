using PowerBase.Application.Common.Interfaces;
using System.Collections.Concurrent;
using System.Linq;

namespace PowerBase.Application.Records.Queries.SearchGlobalRecords;

public class SearchGlobalRecordsQueryHandler
{
    private readonly IAzureSearchService _searchService;
    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;

    public SearchGlobalRecordsQueryHandler(
        IAzureSearchService searchService, 
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer)
    {
        _searchService = searchService;
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
    }

    public async Task<SearchGlobalRecordsResult> HandleAsync(SearchGlobalRecordsQuery request, CancellationToken cancellationToken = default)
    {
        var rawResults = await _searchService.SearchGlobalAsync(_queryContext.TenantId, request.SearchText, request.AppId, request.Page, request.PageSize, cancellationToken);
        if (rawResults.Items.Count == 0) return new SearchGlobalRecordsResult([], rawResults.TotalCount ?? 0, request.Page, request.PageSize);

        var appCache = new ConcurrentDictionary<long, Domain.Entities.App>();
        var tableCache = new ConcurrentDictionary<long, Domain.Entities.AppTable>();
        var fieldsCache = new ConcurrentDictionary<long, IReadOnlyList<Domain.Entities.AppField>>();
        var accessCache = new ConcurrentDictionary<long, TableAccessContext>();
        
        var finalResults = new List<SearchGlobalRecordsResultItem>();

        foreach (var r in rawResults.Items)
        {
            if (!tableCache.TryGetValue(r.TableId, out var table))
            {
                try
                {
                    table = await _tableRepo.GetByIdAsync(r.TableId, cancellationToken);
                    tableCache[r.TableId] = table;
                }
                catch { continue; } // Deleted table
            }

            if (!fieldsCache.TryGetValue(r.TableId, out var fields))
            {
                try
                {
                    fields = await _fieldRepo.ListByTableAsync(table.Id, cancellationToken);
                    fieldsCache[r.TableId] = fields;
                }
                catch { continue; }
            }

            // D02: Enforce table-level view permission
            if (!accessCache.TryGetValue(r.TableId, out var access))
            {
                access = await _enforcer.GetTableAccessAsync(table, fields, cancellationToken);
                accessCache[r.TableId] = access;
            }

            if (!access.CanView)
            {
                continue; // User is not permitted to view records from this table
            }

            // D02: Enforce record-level scope (OwnRecords, Role ViewFilter)
            if (!access.Unrestricted && (access.ViewFilter != null || access.RestrictToCreatedBy != null))
            {
                var isVisible = await _recordRepo.ExistsWithViewFilterAsync(
                    table,
                    fields,
                    r.PublicId,
                    viewFilter: access.ViewFilter,
                    restrictToCreatedBy: access.RestrictToCreatedBy,
                    ct: cancellationToken);

                if (!isVisible)
                {
                    continue; // Record fails row-level security / view filter
                }
            }

            if (!appCache.TryGetValue(r.AppId, out var app))
            {
                try
                {
                    app = await _appRepo.GetByIdAsync(r.AppId, cancellationToken);
                    appCache[r.AppId] = app;
                }
                catch { continue; } // Deleted app
            }

            // D02: Safe display text resolution - only use fields the user has permission to see
            string primaryText = $"Record ID: {r.PublicId.ToString()[..8]}";
            var visibleFids = access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();

            if (table.DisplayFieldId.HasValue && visibleFids.Contains(table.DisplayFieldId.Value))
            {
                if (r.Fields.TryGetValue($"f_{table.DisplayFieldId.Value}", out var txt) && !string.IsNullOrWhiteSpace(txt))
                {
                    primaryText = txt;
                }
            }
            else
            {
                // Only inspect fields that are explicitly in the user's VisibleFields list
                foreach (var visibleField in access.VisibleFields)
                {
                    if (visibleField.Fid.HasValue && r.Fields.TryGetValue($"f_{visibleField.Fid.Value}", out var val) && !string.IsNullOrWhiteSpace(val))
                    {
                        primaryText = val;
                        break;
                    }
                }
            }

            finalResults.Add(new SearchGlobalRecordsResultItem(
                r.PublicId, 
                app.PublicId, 
                app.Name, 
                table.PublicId, 
                table.SingularLabel ?? table.Name ?? "Record", 
                table.Icon,
                primaryText));
        }

        return new SearchGlobalRecordsResult(finalResults, rawResults.TotalCount ?? 0, request.Page, request.PageSize);
    }
}
