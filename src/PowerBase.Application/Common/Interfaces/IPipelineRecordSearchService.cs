using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Domain.Entities;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineRecordSearchService
{
    bool SupportsKeysetPaging => false;

    IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCopySnapshotAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree,
        CancellationToken ct = default);

    /// <summary>
    /// <paramref name="page"/> (1-based) lets a caller page through results larger than one
    /// <paramref name="maxResults"/>-sized fetch — e.g. to pull every matching row without a
    /// fixed cap — while still routing through this service's own (possibly cross-tenant)
    /// connection, unlike IRecordRepository.ListAsync which is always the current tenant.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        int? maxResults = null,
        FilterGroup? filterTree = null,
        CancellationToken ct = default,
        int page = 1);

}

public interface IKeysetPipelineRecordSearchService
{
    Task<long> GetMaxRecordIdAsync(AppTable table, CancellationToken ct = default);

    /// <summary>Reads bounded pages using the stable record Id as a continuation cursor.</summary>
    IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchPagesAsync(
        AppTable table, IReadOnlyList<AppField> fields, int pageSize,
        FilterGroup? filterTree = null, long afterId = 0, long maxId = long.MaxValue,
        CancellationToken ct = default);
}
