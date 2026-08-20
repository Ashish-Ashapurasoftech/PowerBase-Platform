using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public class AzureSearchService : IAzureSearchService
{
    private readonly SearchClient _searchClient;
    private readonly bool _isEnabled;

    public AzureSearchService(IConfiguration configuration)
    {
        var endpoint = configuration["AzureAiSearch:Endpoint"];
        var apiKey = configuration["AzureAiSearch:ApiKey"];
        var indexName = configuration["AzureAiSearch:IndexName"];

        _isEnabled = !string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(indexName);

        if (_isEnabled)
        {
            var credential = new AzureKeyCredential(apiKey!);
            _searchClient = new SearchClient(new Uri(endpoint!), indexName, credential);
        }
        else
        {
            // Fallback for development if Azure AI Search is not yet configured
            _searchClient = null!;
        }
    }

    public async Task IndexRecordAsync(long tableId, Guid publicId, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        if (!_isEnabled) return;

        // Map the dictionary values into a dynamic object format for Azure Search
        // Format: { "id": "...", "tableId": 123, "f_1": "value", "f_2": 45 }
        var document = new Dictionary<string, object>
        {
            { "id", publicId.ToString() }, // Azure AI Search requires an 'id' field
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

    public async Task DeleteRecordAsync(long tableId, Guid publicId, CancellationToken ct = default)
    {
        if (!_isEnabled) return;

        var batch = IndexDocumentsBatch.Delete("id", new[] { publicId.ToString() });
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
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
}
