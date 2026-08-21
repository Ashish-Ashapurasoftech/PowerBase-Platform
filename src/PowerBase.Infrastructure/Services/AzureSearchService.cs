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
    private readonly string _indexName;
    private readonly bool _isEnabled;

    public AzureSearchService(IConfiguration configuration)
    {
        var endpoint = configuration["AzureAiSearch:Endpoint"];
        var apiKey = configuration["AzureAiSearch:ApiKey"];
        _indexName = configuration["AzureAiSearch:IndexName"] ?? string.Empty;

        _isEnabled = !string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(_indexName);

        if (_isEnabled)
        {
            var credential = new AzureKeyCredential(apiKey!);
            _searchClient = new SearchClient(new Uri(endpoint!), _indexName, credential);
            _searchIndexClient = new SearchIndexClient(new Uri(endpoint!), credential);
        }
        else
        {
            // Fallback for development if Azure AI Search is not yet configured
            _searchClient = null!;
            _searchIndexClient = null!;
        }
        
        IsGridSearchEnabled = bool.TryParse(configuration["UseAzureAiForGridSearch"], out var b) && b;
    }

    public bool IsGridSearchEnabled { get; }

    public async Task IndexRecordAsync(long tenantId, long appId, long tableId, Guid publicId, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        if (!_isEnabled) return;

        // Map the dictionary values into a dynamic object format for Azure Search
        // Format: { "id": "...", "tenantId": "...", "appId": 123, "tableId": 123, "f_1": "value" }
        var document = new Dictionary<string, object>
        {
            { "id", publicId.ToString() }, // Azure AI Search requires an 'id' field
            { "tenantId", tenantId.ToString() },
            { "appId", appId },
            { "tableId", tableId }
        };

        foreach (var kvp in values)
        {
            // Convert field ids to f_X properties to match the search index schema
            var fieldName = $"f_{kvp.Key}";
            document[fieldName] = kvp.Value ?? string.Empty; // Avoid nulls if possible depending on schema
        }

        var batch = IndexDocumentsBatch.MergeOrUpload(new[] { document });
        
        try
        {
            await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
        }
        catch (RequestFailedException ex)
        {
            // Log the exception in a real application
            // _logger.LogError(ex, "Failed to index record {PublicId}", publicId);
            throw new InvalidOperationException($"Failed to index record {publicId} in Azure AI Search.", ex);
        }
    }

    public async Task BulkIndexRecordsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default)
    {
        if (!_isEnabled) return;

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
                searchDoc[fieldName] = kvp.Value ?? string.Empty;
            }
            return searchDoc;
        });

        // Split into batches of 1000 to respect Azure AI Search limits
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
            throw new InvalidOperationException($"Failed to bulk index records in Azure AI Search.", ex);
        }
    }



    public async Task BulkDeleteRecordsAsync(long tableId, IReadOnlyList<Guid> publicIds, CancellationToken ct = default)
    {
        if (!_isEnabled || publicIds.Count == 0) return;

        var batch = IndexDocumentsBatch.Delete("id", publicIds.Select(id => id.ToString()));
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tableId, string searchText, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(searchText)) return [];

        var options = new SearchOptions
        {
            Filter = $"tableId eq {tableId}",
            Size = 1000 // Limit for safety
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

    public async Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tableId, string odataFilter, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(odataFilter)) return [];

        var options = new SearchOptions
        {
            Filter = $"tableId eq {tableId} and ({odataFilter})",
            Size = 50000, // Large limit to return all possible matches for filtering
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
    public async Task<IReadOnlyList<GlobalSearchResult>> SearchGlobalAsync(long tenantId, string searchText, long? appId = null, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(searchText)) return [];

        var filter = $"tenantId eq '{tenantId}'";
        if (appId.HasValue)
        {
            filter += $" and appId eq {appId.Value}";
        }

        var options = new SearchOptions
        {
            Filter = filter,
            Size = 50 // Global search limits results across tables
        };
        options.Select.Add("id");
        options.Select.Add("appId");
        options.Select.Add("tableId");

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
                    results.Add(new GlobalSearchResult(id, docAppId, docTableId));
                }
            }
            return results;
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to search global records for tenant {tenantId} in Azure AI Search.", ex);
        }
    }

    public async Task EnsureTableSchemaAsync(long tableId, IEnumerable<(int Fid, bool IsSearchable, bool IsFilterable)> fields, CancellationToken ct = default)
    {
        if (!_isEnabled) return;
        
        try
        {
            var index = await _searchIndexClient.GetIndexAsync(_indexName, ct);
            var updated = false;

            foreach (var f in fields)
            {
                var fieldName = $"f_{f.Fid}";
                if (!index.Value.Fields.Any(x => x.Name == fieldName))
                {
                    index.Value.Fields.Add(new SearchableField(fieldName)
                    {
                        IsFilterable = f.IsFilterable,
                        IsSortable = f.IsFilterable, // Assuming filterable fields can be sorted
                        IsFacetable = f.IsFilterable
                    });
                    updated = true;
                }
            }

            if (updated)
            {
                await _searchIndexClient.CreateOrUpdateIndexAsync(index.Value, cancellationToken: ct);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // If index does not exist, it must be created through some other admin process or here.
            // For safety, assuming the index 'powerbase-ai-search' is managed globally.
            // But we can throw or handle it.
        }
    }
}
