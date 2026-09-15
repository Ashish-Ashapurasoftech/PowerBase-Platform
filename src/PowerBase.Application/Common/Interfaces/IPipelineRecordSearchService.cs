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

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        int? maxResults = null,
        FilterGroup? filterTree = null,
        CancellationToken ct = default);
}
