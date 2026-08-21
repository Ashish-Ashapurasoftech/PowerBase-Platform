using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Queries.ListPipelines;

public record PipelineListResult(IReadOnlyList<PipelineListItem> Items, int TotalCount, int Page, int PageSize);
