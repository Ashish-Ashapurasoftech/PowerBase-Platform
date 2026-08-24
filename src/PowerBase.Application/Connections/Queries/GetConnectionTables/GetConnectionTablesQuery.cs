using System;

namespace PowerBase.Application.Connections.Queries.GetConnectionTables;

/// <summary>Tables of an app reached through a saved account.</summary>
public class GetConnectionTablesQuery
{
    public Guid ConnectionPublicId { get; }
    public Guid AppPublicId { get; }

    public GetConnectionTablesQuery(Guid connectionPublicId, Guid appPublicId)
    {
        ConnectionPublicId = connectionPublicId;
        AppPublicId = appPublicId;
    }
}
