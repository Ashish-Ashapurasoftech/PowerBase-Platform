using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Domain.Entities;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineRecordSearchService
{
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
