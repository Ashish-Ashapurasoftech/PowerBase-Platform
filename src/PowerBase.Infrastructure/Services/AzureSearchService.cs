using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Search;

namespace PowerBase.Infrastructure.Services;

public class AzureSearchService : IAzureSearchService
{
    private readonly SearchIndexClient? _searchIndexClient;
    private readonly ConcurrentDictionary<string, SearchClient> _searchClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _knownFieldsPerIndex = new(StringComparer.OrdinalIgnoreCase);
    // Index names that exist on the service (for the wide-table index budget); refreshed at most once a minute.
    private IReadOnlyList<string> _indexNamesCache = [];
    private DateTime _indexNamesLoadedUtc = DateTime.MinValue;
    private readonly ConcurrentDictionary<long, DateTime> _wideBudgetWarned = new();
    private readonly SemaphoreSlim _indexCreationLock = new(1, 1);
    
    private readonly string _endpoint;
    private readonly AzureKeyCredential? _credential;
    private readonly TokenCredential? _tokenCredential;
    private readonly bool _useManagedIdentity;
    private readonly SearchResourceEncryptionKey? _encryptionKey;
    private readonly string _defaultIndexName;
    private readonly bool _isMultipleIndex;
    private readonly bool _isEnabled;
    private readonly HashSet<long> _dedicatedTenantIndexes = new();
    private readonly int _shardCount;
    private readonly int _wideIndexBudget;
    private readonly ILogger<AzureSearchService>? _logger;
    private const int MaxThrottleRetries = 3;

    public AzureSearchService(IConfiguration configuration, ILogger<AzureSearchService>? logger = null)
    {
        _logger = logger;
        _endpoint = configuration["AzureAiSearch:Endpoint"] ?? string.Empty;
        var apiKey = configuration["AzureAiSearch:ApiKey"] ?? string.Empty;
        _defaultIndexName = configuration["AzureAiSearch:IndexName"] ?? "powerbase-records-index";

        var isMultiStr = configuration["AzureAiSearch:IsMultipleIndex"];
        _isMultipleIndex = bool.TryParse(isMultiStr, out var multi) && multi;

        var tenantRoutes = configuration["AzureAiSearch:DedicatedTenantIds"] ?? "";
        if (!string.IsNullOrWhiteSpace(tenantRoutes))
        {
            foreach (var tId in tenantRoutes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(tId, out var id)) _dedicatedTenantIndexes.Add(id);
            }
        }

        // Tenant -> index is a pure function of configuration (see SearchIndexRouting), so every instance and every restart agrees.
        // 1 (default) keeps everything in the configured index; N > 1 spreads tenants over pb-shard-001..N by tenant id. A tenant's data is
        // never split across indexes, so a query never pages across indexes. Changing N later moves tenants and needs a full reindex.
        _shardCount = int.TryParse(configuration["AzureAiSearch:ShardCount"], out var shards) && shards > 1 ? shards : 1;

        // How many tables may have their own "pb-tbl-{id}" index. Each one counts against the service's index limit (15 Basic, 50 S1).
        _wideIndexBudget = int.TryParse(configuration["AzureAiSearch:WideTableIndexBudget"], out var budget) && budget >= 0 ? budget : 10;

        // AzureAiSearch:UseManagedIdentity=true signs in as the app's identity (roles "Search Service Contributor" and "Search Index Data
        // Contributor"), so no admin key is stored or can leak. The key stays the default.
        _useManagedIdentity = bool.TryParse(configuration["AzureAiSearch:UseManagedIdentity"], out var mi) && mi;

        // Optional customer-managed key for index encryption (AzureAiSearch:EncryptionKey:VaultUri / KeyName / KeyVersion). The search service
        // reaches the vault with its own managed identity. Azure applies it only when an index is CREATED; an existing index must be rebuilt.
        var vaultUri = configuration["AzureAiSearch:EncryptionKey:VaultUri"];
        var keyName = configuration["AzureAiSearch:EncryptionKey:KeyName"];
        var keyVersion = configuration["AzureAiSearch:EncryptionKey:KeyVersion"];
        if (!string.IsNullOrWhiteSpace(vaultUri) && !vaultUri.StartsWith("<") && !string.IsNullOrWhiteSpace(keyName) && !string.IsNullOrWhiteSpace(keyVersion))
            _encryptionKey = new SearchResourceEncryptionKey(new Uri(vaultUri), keyName, keyVersion);

        _isEnabled = !string.IsNullOrEmpty(_endpoint) && (!string.IsNullOrEmpty(apiKey) || _useManagedIdentity) && !string.IsNullOrEmpty(_defaultIndexName);

        if (_isEnabled)
        {
            _credential = _useManagedIdentity ? null : new AzureKeyCredential(apiKey);
            _tokenCredential = _useManagedIdentity ? new Azure.Identity.DefaultAzureCredential() : null;
            var clientOptions = new SearchClientOptions
            {
                Retry =
                {
                    Mode = RetryMode.Exponential,
                    MaxRetries = 5,
                    Delay = TimeSpan.FromMilliseconds(500)
                }
            };
            _searchIndexClient = _tokenCredential != null
                ? new SearchIndexClient(new Uri(_endpoint), _tokenCredential, clientOptions)
                : new SearchIndexClient(new Uri(_endpoint), _credential!, clientOptions);
        }
        else
        {
            _credential = null;
            _tokenCredential = null;
            _searchIndexClient = null;
        }
        
        IsGridSearchEnabled = (bool.TryParse(configuration["UseAzureAiForGridSearch"], out var b1) && b1)
                           || (bool.TryParse(configuration["AzureAiSearch:UseAzureAiForGridSearch"], out var b2) && b2);
    }

    public bool IsEnabled => _isEnabled;
    public bool IsGridSearchEnabled { get; }

    private DateTime _lastHealthCheckTime = DateTime.MinValue;
    private bool _cachedHealthState = false;
    private readonly SemaphoreSlim _healthLock = new(1, 1);

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (!_isEnabled || _searchIndexClient == null) return false;

        if ((DateTime.UtcNow - _lastHealthCheckTime).TotalSeconds < 30)
        {
            return _cachedHealthState;
        }

        await _healthLock.WaitAsync(ct);
        try
        {
            if ((DateTime.UtcNow - _lastHealthCheckTime).TotalSeconds < 30)
            {
                return _cachedHealthState;
            }

            var stats = await _searchIndexClient.GetServiceStatisticsAsync(ct);
            _cachedHealthState = stats?.Value != null;
            _lastHealthCheckTime = DateTime.UtcNow;
            return _cachedHealthState;
        }
        catch
        {
            _cachedHealthState = false;
            _lastHealthCheckTime = DateTime.UtcNow;
            return false;
        }
        finally
        {
            _healthLock.Release();
        }
    }

    private string TenantIndex(long tenantId) =>
        SearchIndexRouting.TenantIndex(tenantId, _defaultIndexName, _shardCount, _isMultipleIndex, _dedicatedTenantIndexes);

    private string GetWriteIndexNameForDocument(SearchIndexDocument doc) =>
        !string.IsNullOrWhiteSpace(doc.CustomIndexName) ? doc.CustomIndexName : TenantIndex(doc.TenantId);

    private string GetWriteIndexNameForTenant(long tenantId) => TenantIndex(tenantId);

    /// <summary>The (single) index holding all of a tenant's shared-index documents.</summary>
    private IReadOnlyList<string> GetShardsForTenant(long tenantId) => [TenantIndex(tenantId)];

    /// <summary>The index to query for a table: the one the caller names (a table with its own index), otherwise the tenant's. Always exactly
    /// one, so results never need merging or paging across indexes.</summary>
    private IReadOnlyList<string> GetShardsForTable(long tenantId, long tableId, string? overrideIndex = null) =>
        !string.IsNullOrWhiteSpace(overrideIndex) ? [overrideIndex] : GetShardsForTenant(tenantId);

    private async Task<IReadOnlyList<string>> GetIndexNamesAsync(CancellationToken ct)
    {
        if (_searchIndexClient == null) return [];
        if (DateTime.UtcNow - _indexNamesLoadedUtc < TimeSpan.FromMinutes(1)) return _indexNamesCache;

        var names = new List<string>();
        await foreach (var name in _searchIndexClient.GetIndexNamesAsync(ct)) names.Add(name);
        _indexNamesCache = names;
        _indexNamesLoadedUtc = DateTime.UtcNow;
        return names;
    }

    public async Task<bool> CanUseWideIndexAsync(long tableId, CancellationToken ct = default)
    {
        if (!_isEnabled) return false;
        return SearchIndexRouting.WithinWideIndexBudget(await GetIndexNamesAsync(ct), SearchSlotResolver.GetTableIndexName(tableId), _wideIndexBudget);
    }

    private SearchClient GetSearchClient(string indexName)
    {
        return _searchClients.GetOrAdd(indexName, name =>
        {
            var options = new SearchClientOptions
            {
                Retry =
                {
                    Mode = RetryMode.Exponential,
                    MaxRetries = 5,
                    Delay = TimeSpan.FromMilliseconds(500)
                }
            };
            return _tokenCredential != null
                ? new SearchClient(new Uri(_endpoint), name, _tokenCredential, options)
                : new SearchClient(new Uri(_endpoint), name, _credential!, options);
        });
    }

    /// <summary>
    /// Creates the complete typed-slot shard index schema.
    /// Includes system fields, unified full-text search_content, and typed slots
    /// (num_01..60, date_01..40, str_01..60, bool_01..20).
    /// </summary>
    private static List<SearchField> CreateShardTemplateFields()
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("tenantId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("appId", SearchFieldDataType.Int64) { IsFilterable = true },
            new SimpleField("tableId", SearchFieldDataType.Int64) { IsFilterable = true },
            new SimpleField("publicId", SearchFieldDataType.String) { IsFilterable = true },
            // The source version last applied to the record (see SearchIndexMessage.Version): lets the indexer recognise a stale message.
            new SimpleField("src_version", SearchFieldDataType.Int64),
            new SearchableField("search_content") { IsFilterable = false }
        };

        for (int i = 1; i <= SearchSlotResolver.MaxNumericSlots; i++)
        {
            fields.Add(new SimpleField($"num_{i:D2}", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true, IsFacetable = true });
        }

        for (int i = 1; i <= SearchSlotResolver.MaxDateSlots; i++)
        {
            fields.Add(new SimpleField($"date_{i:D2}", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true, IsFacetable = true });
        }

        for (int i = 1; i <= SearchSlotResolver.MaxStringSlots; i++)
        {
            fields.Add(new SearchableField($"str_{i:D2}") { IsFilterable = true, IsSortable = true, IsFacetable = true });
        }

        for (int i = 1; i <= SearchSlotResolver.MaxBooleanSlots; i++)
        {
            fields.Add(new SimpleField($"bool_{i:D2}", SearchFieldDataType.Boolean) { IsFilterable = true, IsSortable = true, IsFacetable = true });
        }

        return fields;
    }

    private async Task EnsureIndexAndFieldsExistAsync(string indexName, IEnumerable<string> fieldNames, CancellationToken ct)
    {
        if (!_isEnabled || _searchIndexClient == null) return;
        
        var requiredFields = fieldNames.Where(f => f.StartsWith("f_")).Distinct().ToList();
        var knownFields = _knownFieldsPerIndex.GetOrAdd(indexName, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Fast path: if all required fields are already known, avoid locking and remote calls
        if (knownFields.Count > 0 && requiredFields.All(f => knownFields.Contains(f)))
        {
            return;
        }

        await _indexCreationLock.WaitAsync(ct);
        try
        {
            SearchIndex index;
            try
            {
                var indexResponse = await _searchIndexClient.GetIndexAsync(indexName, ct);
                index = indexResponse.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                index = new SearchIndex(indexName) { EncryptionKey = _encryptionKey };
                foreach (var baseField in CreateShardTemplateFields())
                    index.Fields.Add(baseField);
                await _searchIndexClient.CreateIndexAsync(index, ct);
            }

            foreach (var existingField in index.Fields)
            {
                knownFields.Add(existingField.Name);
            }

            var updated = false;

            // Ensure all template fields exist in index
            foreach (var baseField in CreateShardTemplateFields())
            {
                if (!knownFields.Contains(baseField.Name))
                {
                    index.Fields.Add(baseField);
                    knownFields.Add(baseField.Name);
                    updated = true;
                }
            }

            // Ensure any legacy f_{fid} fields requested are present
            var newlyAddedFields = new List<string>();
            foreach (var fieldName in requiredFields)
            {
                if (!knownFields.Contains(fieldName))
                {
                    if (!index.Fields.Any(x => x.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
                    {
                        index.Fields.Add(new SearchableField(fieldName)
                        {
                            IsFilterable = true,
                            IsSortable = true,
                            IsFacetable = true
                        });
                        newlyAddedFields.Add(fieldName);
                        updated = true;
                    }
                    else
                    {
                        knownFields.Add(fieldName);
                    }
                }
            }

            if (updated)
            {
                await _searchIndexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
                foreach (var nf in newlyAddedFields)
                {
                    knownFields.Add(nf);
                }
            }
        }
        finally
        {
            _indexCreationLock.Release();
        }
    }

    private async Task EnsureWideTableIndexAsync(string indexName, IReadOnlyList<SearchIndexDocument> docs, CancellationToken ct)
    {
        if (!_isEnabled || _searchIndexClient == null) return;

        var knownFields = _knownFieldsPerIndex.GetOrAdd(indexName, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // D21 Fast path: if all required slot fields are already known, avoid locking and remote calls
        var neededFields = docs.SelectMany(d => d.SlotValues?.Keys ?? Enumerable.Empty<string>())
            .Concat(docs.Any(d => d.SourceVersion.HasValue) ? new[] { "src_version" } : Array.Empty<string>())
            .Distinct().ToList();
        if (knownFields.Count > 0 && neededFields.All(f => knownFields.Contains(f)))
        {
            return;
        }

        await _indexCreationLock.WaitAsync(ct);
        try
        {
            SearchIndex index;
            try
            {
                var resp = await _searchIndexClient.GetIndexAsync(indexName, ct);
                index = resp.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                index = new SearchIndex(indexName) { EncryptionKey = _encryptionKey };
                index.Fields.Add(new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true });
                index.Fields.Add(new SimpleField("tenantId", SearchFieldDataType.String) { IsFilterable = true });
                index.Fields.Add(new SimpleField("appId", SearchFieldDataType.Int64) { IsFilterable = true });
                index.Fields.Add(new SimpleField("tableId", SearchFieldDataType.Int64) { IsFilterable = true });
                index.Fields.Add(new SimpleField("publicId", SearchFieldDataType.String) { IsFilterable = true });
                index.Fields.Add(new SearchableField("search_content") { IsFilterable = false });
                index.Fields.Add(new SimpleField("src_version", SearchFieldDataType.Int64));
                await _searchIndexClient.CreateIndexAsync(index, ct);
            }

            foreach (var f in index.Fields)
            {
                knownFields.Add(f.Name);
            }

            var updated = false;
            var newlyAddedWideFields = new List<string>();
            if (!knownFields.Contains("src_version") && !index.Fields.Any(f => f.Name.Equals("src_version", StringComparison.OrdinalIgnoreCase)))
            {
                index.Fields.Add(new SimpleField("src_version", SearchFieldDataType.Int64));
                newlyAddedWideFields.Add("src_version");
                updated = true;
            }
            foreach (var doc in docs)
            {
                if (doc.SlotValues != null)
                {
                    foreach (var kvp in doc.SlotValues)
                    {
                        var fieldName = kvp.Key;
                        if (!knownFields.Contains(fieldName) && !newlyAddedWideFields.Contains(fieldName) && !index.Fields.Any(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var val = kvp.Value;
                            if (val == null) continue; // the column's type is taken from a real value; a null cannot decide it
                            SearchField newField = val switch
                            {
                                double or float or decimal or int or long =>
                                    new SimpleField(fieldName, SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true, IsFacetable = true },
                                DateTimeOffset or DateTime =>
                                    new SimpleField(fieldName, SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true, IsFacetable = true },
                                bool =>
                                    new SimpleField(fieldName, SearchFieldDataType.Boolean) { IsFilterable = true, IsFacetable = true },
                                _ =>
                                    new SearchableField(fieldName) { IsFilterable = true, IsSortable = true, IsFacetable = true }
                            };

                            index.Fields.Add(newField);
                            newlyAddedWideFields.Add(fieldName);
                            updated = true;
                        }
                    }
                }
            }

            if (updated)
            {
                await _searchIndexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
                foreach (var nf in newlyAddedWideFields)
                {
                    knownFields.Add(nf);
                }
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
        
        var doc = new SearchIndexDocument(tenantId, appId, tableId, publicId, values);
        await BulkIndexRecordsAsync([doc], ct);
    }

    public async Task BulkIndexRecordsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default)
    {
        // Callers that do not track individual documents keep their behavior (a request-level failure throws); documents
        // the service rejected are no longer silent: they are logged with their key and status.
        var failures = await BulkIndexRecordsWithResultsAsync(documents, ct);
        foreach (var f in failures)
            _logger?.LogError("Azure AI Search did not index document {DocumentKey}: status {Status}, retryable {Retryable}, error {Error}", f.Key, f.Status, f.Retryable, f.Error);
    }

    public async Task<IReadOnlyList<SearchIndexFailure>> BulkIndexRecordsWithResultsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default)
    {
        var allFailures = new List<SearchIndexFailure>();
        if (!_isEnabled) return allFailures;

        var docList = documents.ToList();
        if (docList.Count == 0) return allFailures;

        var groups = docList.GroupBy(GetWriteIndexNameForDocument);

        foreach (var group in groups)
        {
            var indexName = group.Key;
            var groupDocs = group.ToList();

            // A table's own index is only used while the service has room for it (see WideTableIndexBudget). Over budget, its documents
            // stay in the shared index (which the table's fields still use) instead of creating one more index.
            if (SearchIndexRouting.IsWideIndexName(indexName)
                && !await CanUseWideIndexAsync(groupDocs[0].TableId, ct))
            {
                var tableId = groupDocs[0].TableId;
                if (!_wideBudgetWarned.TryGetValue(tableId, out var warned) || DateTime.UtcNow - warned > TimeSpan.FromMinutes(10))
                {
                    _wideBudgetWarned[tableId] = DateTime.UtcNow;
                    _logger?.LogError("Table {TableId} cannot get its own search index {Index}: AzureAiSearch:WideTableIndexBudget ({Budget}) is used up. Its documents stay in the shared index.", tableId, indexName, _wideIndexBudget);
                }
                continue;
            }

            if (indexName.StartsWith("pb-tbl-", StringComparison.OrdinalIgnoreCase))
            {
                await EnsureWideTableIndexAsync(indexName, groupDocs, ct);
            }
            else
            {
                var dynamicFields = groupDocs
                    .Where(d => d.SlotValues != null)
                    .SelectMany(d => d.SlotValues!.Keys)
                    // Legacy f_{fid} columns only for documents sent without slot values; a slotted document must not
                    // add one column per field to the shared index.
                    .Concat(groupDocs.Where(d => d.SlotValues == null).SelectMany(d => d.Values.Keys.Select(k => $"f_{k}")))
                    .Where(f => f.StartsWith("f_"))
                    .Distinct();
                await EnsureIndexAndFieldsExistAsync(indexName, dynamicFields, ct);
            }

            var searchDocs = groupDocs.Select(doc => 
            {
                var searchDoc = new Dictionary<string, object>
                {
                    { "id", doc.PublicId.ToString() },
                    { "tenantId", doc.TenantId.ToString() },
                    { "appId", doc.AppId },
                    { "tableId", doc.TableId },
                    { "publicId", doc.PublicId.ToString() }
                };

                var textParts = new List<string>();

                // Only into an index that has the column (an index created before it existed gets it added by the ensure step above).
                if (doc.SourceVersion.HasValue
                    && _knownFieldsPerIndex.TryGetValue(indexName, out var knownForVersion) && knownForVersion.Contains("src_version"))
                {
                    searchDoc["src_version"] = doc.SourceVersion.Value;
                }

                // Populate typed slot values if provided (for both standard shard slots and wide table native fields)
                if (doc.SlotValues != null)
                {
                    _knownFieldsPerIndex.TryGetValue(indexName, out var knownInIndex);
                    var isWideIndex = indexName.StartsWith("pb-tbl-", StringComparison.OrdinalIgnoreCase);
                    foreach (var kvp in doc.SlotValues)
                    {
                        var isKnown = knownInIndex != null && knownInIndex.Contains(kvp.Key);

                        // In standard shard, guard against phantom fields not in index
                        if (!isWideIndex && kvp.Key.StartsWith("f_") && !isKnown) continue;

                        if (kvp.Value == null)
                        {
                            // A cleared value must reach the column (an explicit null in a merge clears it). Without this a
                            // number/date/boolean that is emptied, or switched off by NullifyField, keeps its old value in
                            // the index. Only for columns the index really has: naming an unknown one fails the request.
                            if (isKnown) searchDoc[kvp.Key] = null!;
                            continue;
                        }

                        searchDoc[kvp.Key] = kvp.Value;
                        // Only text the field owner marked searchable goes into the free-text field; a filterable-only
                        // field keeps its value for filters but must not become findable by typing it.
                        if ((kvp.Key.StartsWith("str_") || kvp.Key.StartsWith("f_"))
                            && (doc.SearchTextSlots == null || doc.SearchTextSlots.Contains(kvp.Key)))
                        {
                            var s = kvp.Value.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) textParts.Add(s);
                        }
                    }
                }
                else
                {
                    // Legacy fallback when slot values not precomputed
                    foreach (var kvp in doc.Values)
                    {
                        var fieldName = $"f_{kvp.Key}";
                        searchDoc[fieldName] = FormatIndexValue(kvp.Value);
                        var strVal = kvp.Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(strVal)) textParts.Add(strVal);
                    }
                }

                if (textParts.Count > 0)
                {
                    searchDoc["search_content"] = string.Join(" ", textParts);
                }

                return searchDoc;
            }).ToList();

            // D19: Chunk by count (max 500) and byte size (max 8 MB) to stay under the 16 MB request limit
            var batches = ChunkByCountAndBytes(searchDocs, maxCount: 500, maxBytes: 8 * 1024 * 1024);
            var searchClient = GetSearchClient(indexName);

            try
            {
                foreach (var batchList in batches)
                {
                    allFailures.AddRange(await UploadBatchWithRetryAsync(searchClient, indexName, batchList, ct));
                }
            }
            catch (RequestFailedException ex)
            {
                throw new InvalidOperationException($"Failed to bulk index records in Azure AI Search (Index: {indexName}).", ex);
            }
        }

        return allFailures;
    }

    /// <summary>
    /// Uploads one batch and reads the per-document answers. Throttled documents (429/503) are retried a few times with
    /// growing pauses; whatever still has not been accepted is returned so the caller can dead-letter or redeliver it.
    /// </summary>
    private async Task<IReadOnlyList<SearchIndexFailure>> UploadBatchWithRetryAsync(
        SearchClient searchClient, string indexName, List<Dictionary<string, object>> docs, CancellationToken ct)
    {
        var pending = docs;
        var permanent = new List<SearchIndexFailure>();
        IReadOnlyList<SearchIndexFailure> lastFailures = [];

        for (var attempt = 0; attempt <= MaxThrottleRetries; attempt++)
        {
            var response = await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.MergeOrUpload(pending), cancellationToken: ct);
            lastFailures = IndexingResultClassifier.Classify(response.Value.Results);
            if (lastFailures.Count == 0) return permanent;

            var throttled = lastFailures.Where(f => IndexingResultClassifier.IsThrottle(f.Status)).Select(f => f.Key).ToHashSet();
            permanent.AddRange(lastFailures.Where(f => !IndexingResultClassifier.IsThrottle(f.Status)));
            if (throttled.Count == 0) return permanent;
            if (attempt == MaxThrottleRetries) break;

            pending = pending.Where(d => throttled.Contains(d["id"].ToString()!)).ToList();
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
            _logger?.LogWarning("Azure AI Search throttled {Count} documents in index {Index}; retrying in {Delay} (attempt {Attempt}/{Max}).", throttled.Count, indexName, delay, attempt + 1, MaxThrottleRetries);
            await Task.Delay(delay, ct);
        }

        // Still throttled after the last attempt: report them as outstanding.
        permanent.AddRange(lastFailures.Where(f => IndexingResultClassifier.IsThrottle(f.Status)));
        return permanent;
    }

    private static List<List<Dictionary<string, object>>> ChunkByCountAndBytes(
        List<Dictionary<string, object>> docs, int maxCount, int maxBytes)
    {
        var result = new List<List<Dictionary<string, object>>>();
        var currentBatch = new List<Dictionary<string, object>>();
        long currentBytes = 0;

        foreach (var doc in docs)
        {
            long docSize = 200; // estimated overhead
            foreach (var kvp in doc)
            {
                docSize += kvp.Key.Length + 4;
                if (kvp.Value is string s) docSize += s.Length * 2;
                else docSize += 16;
            }

            if (currentBatch.Count >= maxCount || (currentBytes + docSize > maxBytes && currentBatch.Count > 0))
            {
                result.Add(currentBatch);
                currentBatch = new List<Dictionary<string, object>>();
                currentBytes = 0;
            }

            currentBatch.Add(doc);
            currentBytes += docSize;
        }

        if (currentBatch.Count > 0)
        {
            result.Add(currentBatch);
        }

        return result;
    }

    public async Task BulkDeleteRecordsAsync(long tenantId, long tableId, IReadOnlyList<Guid> publicIds, CancellationToken ct = default)
    {
        if (!_isEnabled || publicIds.Count == 0) return;
        
        var shardNames = GetShardsForTable(tenantId, tableId).ToList();
        var dedicated = SearchSlotResolver.GetTableIndexName(tableId);
        if (!shardNames.Contains(dedicated, StringComparer.OrdinalIgnoreCase)) shardNames.Add(dedicated); // 404 below if the table has none
        var idList = publicIds.Select(id => id.ToString()).ToList();

        foreach (var indexName in shardNames)
        {
            var batch = IndexDocumentsBatch.Delete("id", idList);
            var searchClient = GetSearchClient(indexName);
            try
            {
                await searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Shard does not exist yet
            }
        }
    }

    public async Task PurgeTableRecordsAsync(long tenantId, long tableId, CancellationToken ct = default)
    {
        if (!_isEnabled) return;

        // D13: If table had a dedicated index, delete that entire index
        var dedicatedName = $"pb-tbl-{tableId}";
        if (_searchIndexClient != null)
        {
            try
            {
                await _searchIndexClient.DeleteIndexAsync(dedicatedName, ct);
                _indexNamesLoadedUtc = DateTime.MinValue; // the budget count changed
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        // Also purge any documents for this table from shared tenant / shard indexes
        var shardNames = GetShardsForTenant(tenantId);
        foreach (var indexName in shardNames)
        {
            var searchClient = GetSearchClient(indexName);
            while (!ct.IsCancellationRequested)
            {
                var options = new SearchOptions
                {
                    Filter = $"tenantId eq '{tenantId}' and tableId eq {tableId}",
                    Size = 500
                };
                options.Select.Add("id");

                var idsToDelete = new List<string>();
                try
                {
                    var resp = await searchClient.SearchAsync<SearchDocument>("*", options, ct);
                    await foreach (var item in resp.Value.GetResultsAsync().WithCancellation(ct))
                    {
                        var idStr = item.Document["id"]?.ToString();
                        if (!string.IsNullOrEmpty(idStr)) idsToDelete.Add(idStr);
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    break;
                }

                if (idsToDelete.Count == 0) break;

                var batch = IndexDocumentsBatch.Delete("id", idsToDelete);
                await searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);

                if (idsToDelete.Count < 500) break;
            }
        }
    }

    public async Task PurgeAppRecordsAsync(long tenantId, long appId, CancellationToken ct = default)
    {
        if (!_isEnabled) return;

        var shardNames = GetShardsForTenant(tenantId);
        foreach (var indexName in shardNames)
        {
            var searchClient = GetSearchClient(indexName);
            while (!ct.IsCancellationRequested)
            {
                var options = new SearchOptions
                {
                    Filter = $"tenantId eq '{tenantId}' and appId eq {appId}",
                    Size = 500
                };
                options.Select.Add("id");

                var idsToDelete = new List<string>();
                try
                {
                    var resp = await searchClient.SearchAsync<SearchDocument>("*", options, ct);
                    await foreach (var item in resp.Value.GetResultsAsync().WithCancellation(ct))
                    {
                        var idStr = item.Document["id"]?.ToString();
                        if (!string.IsNullOrEmpty(idStr)) idsToDelete.Add(idStr);
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    break;
                }

                if (idsToDelete.Count == 0) break;

                var batch = IndexDocumentsBatch.Delete("id", idsToDelete);
                await searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);

                if (idsToDelete.Count < 500) break;
            }
        }
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetSourceVersionsAsync(
        long tenantId, long tableId, string? indexName, IReadOnlyList<Guid> publicIds, CancellationToken ct = default)
    {
        var versions = new Dictionary<Guid, long>();
        if (!_isEnabled || publicIds.Count == 0) return versions;

        foreach (var idx in GetShardsForTable(tenantId, tableId, indexName))
        {
            var searchClient = GetSearchClient(idx);
            foreach (var chunk in publicIds.Distinct().Chunk(500))
            {
                var options = new SearchOptions
                {
                    Filter = $"tenantId eq '{tenantId}' and search.in(id, '{string.Join(",", chunk)}', ',')",
                    Size = chunk.Length
                };
                options.Select.Add("id");
                options.Select.Add("src_version");
                try
                {
                    var response = await searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken: ct);
                    await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct))
                    {
                        if (!Guid.TryParse(result.Document["id"]?.ToString(), out var id)) continue;
                        if (result.Document.TryGetValue("src_version", out var v) && v != null)
                            versions[id] = Math.Max(versions.TryGetValue(id, out var cur) ? cur : long.MinValue, Convert.ToInt64(v));
                    }
                }
                catch (RequestFailedException ex) when (ex.Status is 404 or 400)
                {
                    // 404: the index does not exist yet. 400: it predates the src_version column. Either way nothing is known, so
                    // the indexer reads the database instead of trusting a message.
                    break;
                }
            }
        }
        return versions;
    }

    public async Task PurgeTableFromSharedIndexesAsync(long tenantId, long tableId, CancellationToken ct = default)
    {
        if (!_isEnabled) return;
        var dedicated = SearchSlotResolver.GetTableIndexName(tableId);
        foreach (var indexName in GetShardsForTenant(tenantId).Where(i => !i.Equals(dedicated, StringComparison.OrdinalIgnoreCase)))
        {
            var searchClient = GetSearchClient(indexName);
            while (!ct.IsCancellationRequested)
            {
                var options = new SearchOptions { Filter = $"tenantId eq '{tenantId}' and tableId eq {tableId}", Size = 500 };
                options.Select.Add("id");

                var idsToDelete = new List<string>();
                try
                {
                    var resp = await searchClient.SearchAsync<SearchDocument>("*", options, ct);
                    await foreach (var item in resp.Value.GetResultsAsync().WithCancellation(ct))
                    {
                        var idStr = item.Document["id"]?.ToString();
                        if (!string.IsNullOrEmpty(idStr)) idsToDelete.Add(idStr);
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    break;
                }

                if (idsToDelete.Count == 0) break;
                await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Delete("id", idsToDelete), cancellationToken: ct);
                if (idsToDelete.Count < 500) break;
            }
        }
    }

    public async Task<bool> CreateIndexAsync(string? indexName = null, bool force = false, CancellationToken ct = default)
    {
        if (!_isEnabled || _searchIndexClient == null) return false;

        var targetIndex = !string.IsNullOrWhiteSpace(indexName) ? indexName : _defaultIndexName;

        bool exists;
        try
        {
            var resp = await _searchIndexClient.GetIndexAsync(targetIndex, ct);
            exists = resp?.Value != null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            exists = false;
        }

        if (exists && !force)
        {
            return false;
        }

        if (exists && force)
        {
            try
            {
                await _searchIndexClient.DeleteIndexAsync(targetIndex, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        _knownFieldsPerIndex.TryRemove(targetIndex, out _);
        _searchClients.TryRemove(targetIndex, out _);

        var newIndex = new SearchIndex(targetIndex) { EncryptionKey = _encryptionKey };
        foreach (var baseField in CreateShardTemplateFields())
        {
            newIndex.Fields.Add(baseField);
        }
        await _searchIndexClient.CreateIndexAsync(newIndex, ct);

        var knownFields = _knownFieldsPerIndex.GetOrAdd(targetIndex, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var f in newIndex.Fields)
        {
            knownFields.Add(f.Name);
        }

        return true;
    }

    public Task ResetIndexAsync(string? indexName = null, CancellationToken ct = default)
        => CreateIndexAsync(indexName, force: true, ct);

    public Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tenantId, long tableId, string searchText, CancellationToken ct = default)
        => SearchRecordsAsync(tenantId, tableId, searchText, null, ct);

    public async Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tenantId, long tableId, string searchText, string? indexName, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(searchText)) return [];

        var shardNames = GetShardsForTable(tenantId, tableId, indexName);
        var query = FormatSearchQuery(searchText);
        var results = new List<Guid>();

        foreach (var idx in shardNames)
        {
            var searchClient = GetSearchClient(idx);
            var options = new SearchOptions
            {
                Filter = $"tenantId eq '{tenantId}' and tableId eq {tableId}",
                Size = 1000,
                QueryType = SearchQueryType.Full
            };
            options.Select.Add("id");
            options.SearchFields.Add("search_content");

            try
            {
                var response = await searchClient.SearchAsync<SearchDocument>(query, options, cancellationToken: ct);
                await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct))
                {
                    if (Guid.TryParse(result.Document["id"]?.ToString(), out var id))
                    {
                        results.Add(id);
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                continue;
            }
            catch (RequestFailedException ex)
            {
                throw new InvalidOperationException($"Failed to search records for table {tableId} in Azure AI Search (Index: {idx}).", ex);
            }
        }

        return results;
    }

    public Task<(IReadOnlyList<Guid> Ids, long TotalCount)> SearchRecordsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, int page, int pageSize, CancellationToken ct = default)
        => SearchRecordsPagedAsync(tenantId, tableId, null, odataFilter, page, pageSize, null, null, ct);

    public Task<(IReadOnlyList<Guid> Ids, long TotalCount)> SearchRecordsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, int page, int pageSize, string? indexName, CancellationToken ct = default)
        => SearchRecordsPagedAsync(tenantId, tableId, null, odataFilter, page, pageSize, null, indexName, ct);

    public async Task<(IReadOnlyList<Guid> Ids, long TotalCount)> SearchRecordsPagedAsync(
        long tenantId, long tableId, string? searchText, string? odataFilter, int page, int pageSize,
        IReadOnlyList<string>? orderBy = null, string? indexName = null, CancellationToken ct = default)
    {
        if (!_isEnabled) return ([], 0);

        var shardNames = GetShardsForTable(tenantId, tableId, indexName);
        var allIds = new List<Guid>();
        long totalCount = 0;

        var baseFilter = $"tenantId eq '{tenantId}' and tableId eq {tableId}";
        var combinedFilter = string.IsNullOrWhiteSpace(odataFilter)
            ? baseFilter
            : $"{baseFilter} and ({odataFilter})";

        var query = !string.IsNullOrWhiteSpace(searchText) ? FormatSearchQuery(searchText) : "*";

        foreach (var idx in shardNames)
        {
            var searchClient = GetSearchClient(idx);
            var options = new SearchOptions
            {
                Filter = combinedFilter,
                Skip = Math.Max(0, (page - 1) * pageSize),
                Size = pageSize,
                IncludeTotalCount = true,
                QueryType = !string.IsNullOrWhiteSpace(searchText) ? SearchQueryType.Full : SearchQueryType.Simple
            };
            options.Select.Add("id");

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                options.SearchFields.Add("search_content");
            }

            if (orderBy != null && orderBy.Count > 0)
            {
                foreach (var ob in orderBy)
                {
                    if (!string.IsNullOrWhiteSpace(ob))
                        options.OrderBy.Add(ob);
                }
            }

            try
            {
                var response = await searchClient.SearchAsync<SearchDocument>(query, options, cancellationToken: ct);
                if (response.Value.TotalCount.HasValue)
                    totalCount += response.Value.TotalCount.Value;

                await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct))
                {
                    if (Guid.TryParse(result.Document["id"]?.ToString(), out var id))
                    {
                        allIds.Add(id);
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                continue;
            }
            catch (RequestFailedException ex)
            {
                throw new InvalidOperationException($"Failed to search records paged for table {tableId} in Azure AI Search (Index: {idx}).", ex);
            }
        }

        return (allIds.Take(pageSize).ToList(), totalCount);
    }

    public Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tenantId, long tableId, string odataFilter, CancellationToken ct = default)
        => SearchRecordsByFilterAsync(tenantId, tableId, odataFilter, null, ct);

    public async Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tenantId, long tableId, string odataFilter, string? indexName, CancellationToken ct = default)
    {
        // Paged search capped at 2,000 prevents downloading 50,000 IDs and avoids SQL parameter explosion
        var (ids, _) = await SearchRecordsByFilterPagedAsync(tenantId, tableId, odataFilter, 1, 2000, indexName, ct);
        return ids;
    }

    public IAsyncEnumerable<AiSearchIdPage> SearchRecordIdsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, string? afterCursor = null, CancellationToken ct = default)
        => SearchRecordIdsByFilterPagedAsync(tenantId, tableId, odataFilter, afterCursor, null, ct);

    public async IAsyncEnumerable<AiSearchIdPage> SearchRecordIdsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, string? afterCursor, string? indexName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(odataFilter)) yield break;

        var shardNames = GetShardsForTable(tenantId, tableId, indexName);
        var baseFilter = $"tenantId eq '{tenantId}' and tableId eq {tableId} and ({odataFilter})";

        foreach (var idx in shardNames)
        {
            var searchClient = GetSearchClient(idx);

            async Task<(IReadOnlyList<Guid> Ids, long Total)> QueryAsync(string filter, int size, CancellationToken token)
            {
                var options = new SearchOptions
                {
                    Filter = filter,
                    Size = size,
                    IncludeTotalCount = true,
                    QueryType = SearchQueryType.Full
                };
                options.Select.Add("id");
                try
                {
                    var response = await searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken: token);
                    var ids = new List<Guid>();
                    await foreach (var result in response.Value.GetResultsAsync().WithCancellation(token))
                    {
                        if (Guid.TryParse(result.Document["id"]?.ToString(), out var id)) ids.Add(id);
                    }
                    return (ids, response.Value.TotalCount ?? -1);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return ([], 0);
                }
                catch (RequestFailedException ex)
                {
                    throw new InvalidOperationException($"Failed to search record ids by filter for table {tableId} in Azure AI Search (Index: {idx}).", ex);
                }
            }

            await foreach (var page in AiIdRangePager.PageAsync(baseFilter, afterCursor, QueryAsync, ct))
                yield return page;
        }
    }

    public async Task<(IReadOnlyList<GlobalSearchResult> Items, long? TotalCount)> SearchGlobalAsync(long tenantId, string searchText, long? appId = null, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(searchText)) return ([], 0);

        var query = FormatSearchQuery(searchText);
        var filter = $"tenantId eq '{tenantId}'";
        if (appId.HasValue)
        {
            filter += $" and appId eq {appId.Value}";
        }

        // The tenant's shared index, plus every table-owned index (a table that outgrew the shared index lives in its own; each holds
        // documents of any tenant, so the tenant filter above is what keeps results to this tenant).
        var indexes = new List<string>(GetShardsForTenant(tenantId));
        indexes.AddRange((await GetIndexNamesAsync(ct)).Where(SearchIndexRouting.IsWideIndexName));

        if (indexes.Count == 1)
        {
            var (items, total) = await SearchGlobalInIndexAsync(indexes[0], query, filter, tenantId, (page - 1) * pageSize, pageSize, ct);
            return (items.Select(i => i.Item).ToList(), total);
        }

        // Several indexes: take the best (skip + size) of each, merge by relevance, then cut the requested page. The service pages one index
        // at a time, so a page cannot be asked of each index separately (deep pages are capped at the service's 1,000-result window).
        var window = Math.Min(1000, Math.Max(1, page * pageSize));
        var perIndex = new List<IReadOnlyList<(double Score, GlobalSearchResult Item)>>();
        long grandTotal = 0;
        foreach (var indexName in indexes)
        {
            var (items, total) = await SearchGlobalInIndexAsync(indexName, query, filter, tenantId, 0, window, ct);
            perIndex.Add(items);
            grandTotal += total ?? 0;
        }
        return (SearchResultMerger.MergeByScore(perIndex, (page - 1) * pageSize, pageSize), grandTotal);
    }

    private async Task<(IReadOnlyList<(double Score, GlobalSearchResult Item)> Items, long? Total)> SearchGlobalInIndexAsync(
        string indexName, string query, string filter, long tenantId, int skip, int size, CancellationToken ct)
    {
        var results = new List<(double, GlobalSearchResult)>();
        var options = new SearchOptions
        {
            Filter = filter,
            Skip = skip,
            Size = size,
            IncludeTotalCount = true,
            QueryType = SearchQueryType.Full
        };
        // Only the free-text field: a filterable-only text field has its value in a searchable column too, and must not be findable by typing it.
        options.SearchFields.Add("search_content");
        options.Select.Add("id");
        options.Select.Add("appId");
        options.Select.Add("tableId");
        options.Select.Add("publicId");
        options.Select.Add("search_content");

        try
        {
            var response = await GetSearchClient(indexName).SearchAsync<SearchDocument>(query, options, cancellationToken: ct);
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                if (!Guid.TryParse(result.Document["id"]?.ToString(), out var id)) continue;
                var docAppId = Convert.ToInt64(result.Document["appId"]);
                var docTableId = Convert.ToInt64(result.Document["tableId"]);

                var fields = new Dictionary<string, string>();
                foreach (var kvp in result.Document)
                {
                    if ((kvp.Key.StartsWith("f_") || kvp.Key == "search_content") && kvp.Value != null)
                        fields[kvp.Key] = kvp.Value.ToString() ?? string.Empty;
                }
                results.Add((result.Score ?? 0, new GlobalSearchResult(id, docAppId, docTableId, fields)));
            }
            return (results, response.Value.TotalCount);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (results, 0);
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to search global records for tenant {tenantId} in Azure AI Search (Index: {indexName}).", ex);
        }
    }

    public async Task PurgeTenantRecordsAsync(long tenantId, CancellationToken ct = default)
    {
        if (!_isEnabled) return;
        // The tenant's shared index, and the tenant's documents inside every table-owned index (those hold other tenants' tables too).
        var indexes = new List<string>(GetShardsForTenant(tenantId));
        indexes.AddRange((await GetIndexNamesAsync(ct)).Where(SearchIndexRouting.IsWideIndexName));
        foreach (var indexName in indexes.Distinct(StringComparer.OrdinalIgnoreCase))
            await PurgeByFilterAsync(indexName, $"tenantId eq '{tenantId}'", ct);
    }

    private async Task PurgeByFilterAsync(string indexName, string filter, CancellationToken ct)
    {
        var searchClient = GetSearchClient(indexName);
        while (!ct.IsCancellationRequested)
        {
            var options = new SearchOptions { Filter = filter, Size = 500 };
            options.Select.Add("id");

            var idsToDelete = new List<string>();
            try
            {
                var resp = await searchClient.SearchAsync<SearchDocument>("*", options, ct);
                await foreach (var item in resp.Value.GetResultsAsync().WithCancellation(ct))
                {
                    var idStr = item.Document["id"]?.ToString();
                    if (!string.IsNullOrEmpty(idStr)) idsToDelete.Add(idStr);
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return;
            }

            if (idsToDelete.Count == 0) return;
            await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Delete("id", idsToDelete), cancellationToken: ct);
            if (idsToDelete.Count < 500) return;
        }
    }

    private static string FormatSearchQuery(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return "*";

        var trimmed = searchText.Trim();
        if (trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
            return trimmed;

        var sanitized = System.Text.RegularExpressions.Regex.Replace(trimmed, @"[\+\-\&\|\!\(\)\{\}\[\]\^\""\~\*\?\:\\\/]", " ").Trim();
        if (string.IsNullOrWhiteSpace(sanitized)) return "*";

        var terms = sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return "*";

        // D25: Guard against prefix wildcard explosion (Azure AI Search caps wildcard expansions at 1,000 terms).
        // Only expand terms with length >= 3; shorter terms match exactly.
        var formattedTerms = terms.Select(t => t.Length >= 3 ? $"({t}* OR {t})" : t);

        return string.Join(" AND ", formattedTerms);
    }

    public async Task EnsureTableSchemaAsync(long tenantId, long tableId, IEnumerable<(int Fid, bool IsSearchable, bool IsFilterable)> fields, CancellationToken ct = default)
    {
        if (!_isEnabled) return;
        var indexName = GetWriteIndexNameForTenant(tenantId);
        await EnsureIndexAndFieldsExistAsync(indexName, fields.Select(f => $"f_{f.Fid}"), ct);
    }

    private static object FormatIndexValue(object? val)
    {
        if (val is System.Text.Json.JsonElement je)
        {
            return (je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : je.GetRawText()) ?? string.Empty;
        }

        return val switch
        {
            null => string.Empty,
            decimal d => d.ToString("0.####################", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            _ => val.ToString() ?? string.Empty,
        };
    }
}
