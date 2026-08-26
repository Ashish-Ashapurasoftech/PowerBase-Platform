using System;
using System.Collections.Generic;

namespace PowerBase.Application.Connections.Queries.GetConnections;

/// <summary>
/// Lists the saved PowerFlows accounts belonging to the logged-in user in the current tenant.
/// </summary>
public class GetConnectionsQuery
{
}

public class GetConnectionsResult
{
    public IReadOnlyList<Common.PipelineAccountDto> Items { get; init; } = Array.Empty<Common.PipelineAccountDto>();
}
