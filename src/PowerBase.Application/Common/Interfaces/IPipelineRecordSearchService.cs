using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Domain.Entities;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineRecordSearchService
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        int? maxResults = null,
        FilterGroup? filterTree = null,
        CancellationToken ct = default);
}
