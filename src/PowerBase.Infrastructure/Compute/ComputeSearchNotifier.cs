using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Decision Q7: tells the search index when a stored value of a Searchable / Filterable field changed. A few records: one upsert message
/// each, recorded in meta.SearchOutbox first (the indexer reads the record's current state, c_ value included, and the search relay
/// re-sends what is lost). A whole field (first build, or too many changes to list): one field re-index job, the path a field switched on
/// for search already takes. Never fails the compute work: indexing is not a condition of it.
/// </summary>
public sealed class ComputeSearchNotifier : TenantRepositoryBase
{
    private const int MaxRecordMessages = 5000;
    private readonly IMessagePublisher? _publisher;
    private readonly ISearchOutbox? _outbox;
    private readonly ISearchFieldJobs? _jobs;
    private readonly ILogger<ComputeSearchNotifier>? _logger;

    public ComputeSearchNotifier(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IMessagePublisher? publisher = null,
        ISearchOutbox? outbox = null, ISearchFieldJobs? jobs = null, ILogger<ComputeSearchNotifier>? logger = null)
        : base(connectionFactory, queryContext)
    {
        _publisher = publisher;
        _outbox = outbox;
        _jobs = jobs;
        _logger = logger;
    }

    internal static bool IsIndexed(AppField field) => field.IsSearchable || field.IsFilterable;

    /// <summary>The field's stored values changed for these rows (or for too many to list).</summary>
    public async Task ValuesChangedAsync(AppField field, IReadOnlyCollection<long> rowIds, bool tooMany, CancellationToken ct)
    {
        if (_publisher is null || !IsIndexed(field) || (!tooMany && rowIds.Count == 0)) return;
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            var appId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT AppId FROM meta.AppTable WHERE Id = @tableId AND IsDeleted = 0", new { tableId = field.AppTableId }, cancellationToken: ct));
            if (appId is not long app) return;
            var table = new AppTable { Id = field.AppTableId, AppId = app };

            if (tooMany || rowIds.Count > MaxRecordMessages)
            {
                await SearchFieldReindex.RequestAsync(_publisher, QueryContext, table, field, ct, _jobs);
                return;
            }

            var publicIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
                $"SELECT PublicId FROM {PhysicalNaming.FullTableName(field.AppTableId)} WHERE IsDeleted = 0 AND Id IN (SELECT k FROM OPENJSON(@ids) WITH (k BIGINT '$'))",
                new { ids = JsonSerializer.Serialize(rowIds) }, cancellationToken: ct))).AsList();
            var messages = publicIds.Select(id => new SearchIndexMessage
            {
                Action = IndexAction.Upsert,
                TenantId = QueryContext.TenantId,
                AppId = app,
                TableId = field.AppTableId,
                RecordPublicId = id
            }).ToList();
            if (messages.Count == 0) return;
            if (_outbox is not null) await _outbox.AddAsync(messages, null, ct);
            await _publisher.PublishBatchAsync(messages, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Compute: could not tell the search index about changed values of field {FieldId}; the search relay / nightly sweep catch up.", field.Id);
        }
    }
}
