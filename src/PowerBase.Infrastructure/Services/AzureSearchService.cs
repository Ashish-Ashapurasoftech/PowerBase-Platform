using System.Collections.Concurrent;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public class AzureSearchService : IAzureSearchService
{
    private readonly SearchClient _searchClient;
    private readonly SearchIndexClient _searchIndexClient;
    private readonly HashSet<string> _knownFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _indexCreationLock = new(1, 1);
    
    private readonly string _endpoint;
    private readonly AzureKeyCredential _credential;
    private readonly string _indexName;
    private readonly bool _isEnabled;

    public AzureSearchService(IConfiguration configuration)
    {
        _endpoint = configuration["AzureAiSearch:Endpoint"] ?? string.Empty;
        var apiKey = configuration["AzureAiSearch:ApiKey"] ?? string.Empty;
        _indexName = configuration["AzureAiSearch:IndexName"] ?? "powerbase-records-index";

        _isEnabled = !string.IsNullOrEmpty(_endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(_indexName);

        if (_isEnabled)
        {
            _credential = new AzureKeyCredential(apiKey);
            _searchClient = new SearchClient(new Uri(_endpoint), _indexName, _credential);
            _searchIndexClient = new SearchIndexClient(new Uri(_endpoint), _credential);
        }
        else
        {
            _credential = null!;
            _searchClient = null!;
            _searchIndexClient = null!;
        }
        
        IsGridSearchEnabled = bool.TryParse(configuration["UseAzureAiForGridSearch"], out var b) && b;
    }

    public bool IsGridSearchEnabled { get; }

    private async Task EnsureIndexAndFieldsExistAsync(IEnumerable<string> fieldNames, CancellationToken ct)
    {
        if (!_isEnabled) return;
        
        var requiredFields = fieldNames.Where(f => f.StartsWith("f_")).Distinct().ToList();

        await _indexCreationLock.WaitAsync(ct);
        try
        {
            SearchIndex index;
            try
            {
                var indexResponse = await _searchIndexClient.GetIndexAsync(_indexName, ct);
                index = indexResponse.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                index = new SearchIndex(_indexName)
                {
                    Fields = {
                        new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                        new SimpleField("tenantId", SearchFieldDataType.String) { IsFilterable = true },
                        new SimpleField("appId", SearchFieldDataType.Int64) { IsFilterable = true },
                        new SimpleField("tableId", SearchFieldDataType.Int64) { IsFilterable = true }
                    }
                };
                await _searchIndexClient.CreateIndexAsync(index, ct);
            }

            foreach (var existingField in index.Fields)
            {
                _knownFields.Add(existingField.Name);
            }

            var updated = false;
            foreach (var fieldName in requiredFields)
            {
                if (!_knownFields.Contains(fieldName))
                {
                    if (!index.Fields.Any(x => x.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
                    {
                        index.Fields.Add(new SearchableField(fieldName)
                        {
                            IsFilterable = true,
                            IsSortable = true,
                            IsFacetable = true
                        });
                        updated = true;
                    }
                    _knownFields.Add(fieldName);
                }
            }

            if (updated)
            {
                await _searchIndexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
            }
        }
        finally
        {
            _indexCreationLock.Release();
        }
    }

    public async Task IndexRecordAsync(long tenantId, long appId, long tableId, Guid publicId, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        if (!_isEnabled) return;
        
        await EnsureIndexAndFieldsExistAsync(values.Keys.Select(k => $"f_{k}"), ct);

        var document = new Dictionary<string, object>
        {
            { "id", publicId.ToString() },
            { "tenantId", tenantId.ToString() },
            { "appId", appId },
            { "tableId", tableId }
        };

        foreach (var kvp in values)
        {
            var fieldName = $"f_{kvp.Key}";
            document[fieldName] = ConvertValueToString(kvp.Value);
        }

        var batch = IndexDocumentsBatch.MergeOrUpload(new[] { document });
        
        try
        {
            await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to index record {publicId} in Azure AI Search.", ex);
        }
    }

    public async Task BulkIndexRecordsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default)
    {
        if (!_isEnabled) return;

        var allFields = documents.SelectMany(d => d.Values.Keys).Select(k => $"f_{k}");
        await EnsureIndexAndFieldsExistAsync(allFields, ct);

        var searchDocs = documents.Select(doc => 
        {
            var searchDoc = new Dictionary<string, object>
            {
                { "id", doc.PublicId.ToString() },
                { "tenantId", doc.TenantId.ToString() },
                { "appId", doc.AppId },
                { "tableId", doc.TableId }
            };
            foreach (var kvp in doc.Values)
            {
                var fieldName = $"f_{kvp.Key}";
                searchDoc[fieldName] = ConvertValueToString(kvp.Value);
            }
            return searchDoc;
        });

        var batches = searchDocs.Chunk(1000);
        
        try
        {
            foreach (var batchDocs in batches)
            {
                var batch = IndexDocumentsBatch.MergeOrUpload(batchDocs);
                await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
            }
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException("Failed to bulk index records in Azure AI Search.", ex);
        }
    }

    public async Task BulkDeleteRecordsAsync(long tenantId, long tableId, IReadOnlyList<Guid> publicIds, CancellationToken ct = default)
    {
        if (!_isEnabled || publicIds.Count == 0) return;
        
        var batch = IndexDocumentsBatch.Delete("id", publicIds.Select(id => id.ToString()));
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tenantId, long tableId, string searchText, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(searchText)) return [];

        var options = new SearchOptions
        {
            Filter = $"tenantId eq '{tenantId}' and tableId eq {tableId}",
            Size = 1000
        };
        options.Select.Add("id");

        try
        {
            var response = await _searchClient.SearchAsync<SearchDocument>(searchText, options, cancellationToken: ct);
            var results = new List<Guid>();
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (Guid.TryParse(result.Document["id"].ToString(), out var id))
                {
                    results.Add(id);
                }
            }
            return results;
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to search records for table {tableId} in Azure AI Search.", ex);
        }
    }

    public async Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tenantId, long tableId, string odataFilter, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(odataFilter)) return [];

        var options = new SearchOptions
        {
            Filter = $"tenantId eq '{tenantId}' and tableId eq {tableId} and ({odataFilter})",
            Size = 50000,
            QueryType = SearchQueryType.Full
        };
        options.Select.Add("id");

        try
        {
            var response = await _searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken: ct);
            var results = new List<Guid>();
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (Guid.TryParse(result.Document["id"].ToString(), out var id))
                {
                    results.Add(id);
                }
            }
            return results;
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to search records by filter for table {tableId} in Azure AI Search.", ex);
        }
    }

    public async Task<(IReadOnlyList<GlobalSearchResult> Items, long? TotalCount)> SearchGlobalAsync(long tenantId, string searchText, long? appId = null, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(searchText)) return ([], 0);

        var filter = $"tenantId eq '{tenantId}'";
        if (appId.HasValue)
        {
            filter += $" and appId eq {appId.Value}";
        }

        var options = new SearchOptions
        {
            Filter = filter,
            Skip = (page - 1) * pageSize,
            Size = pageSize,
            IncludeTotalCount = true
        };

        try
        {
            var response = await _searchClient.SearchAsync<SearchDocument>(searchText, options, cancellationToken: ct);
            var results = new List<GlobalSearchResult>();
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (Guid.TryParse(result.Document["id"]?.ToString(), out var id))
                {
                    var docAppId = Convert.ToInt64(result.Document["appId"]);
                    var docTableId = Convert.ToInt64(result.Document["tableId"]);
                    
                    var fields = new Dictionary<string, string>();
                    foreach (var kvp in result.Document)
                    {
                        if (kvp.Key.StartsWith("f_") && kvp.Value != null)
                        {
                            fields[kvp.Key] = kvp.Value.ToString() ?? string.Empty;
                        }
                    }
                    
                    results.Add(new GlobalSearchResult(id, docAppId, docTableId, fields));
                }
            }
            return (results, response.Value.TotalCount);
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to search global records for tenant {tenantId} in Azure AI Search.", ex);
        }
    }

    public async Task EnsureTableSchemaAsync(long tenantId, long tableId, IEnumerable<(int Fid, bool IsSearchable, bool IsFilterable)> fields, CancellationToken ct = default)
    {
        if (!_isEnabled) return;
        await EnsureIndexAndFieldsExistAsync(fields.Select(f => $"f_{f.Fid}"), ct);
    }

    private static string ConvertValueToString(object? val)
    {
        if (val is null) return string.Empty;
        if (val is System.Text.Json.JsonElement je)
        {
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() ?? string.Empty : je.GetRawText();
        }
        return val.ToString() ?? string.Empty;
    }
}
