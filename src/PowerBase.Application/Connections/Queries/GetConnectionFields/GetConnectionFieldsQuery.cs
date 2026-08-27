using System;

namespace PowerBase.Application.Connections.Queries.GetConnectionFields;

/// <summary>Fields of a table reached through a saved account.</summary>
public class GetConnectionFieldsQuery
{
    public Guid ConnectionPublicId { get; }
    public Guid TablePublicId { get; }

    public GetConnectionFieldsQuery(Guid connectionPublicId, Guid tablePublicId)
    {
        ConnectionPublicId = connectionPublicId;
        TablePublicId = tablePublicId;
    }
}
