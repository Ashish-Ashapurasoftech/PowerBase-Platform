using System;

namespace PowerBase.Application.Connections.Queries.GetConnectionApps;

/// <summary>Apps visible to a saved account, resolved with the account's own credentials.</summary>
public class GetConnectionAppsQuery
{
    public Guid ConnectionPublicId { get; }

    public GetConnectionAppsQuery(Guid connectionPublicId)
    {
        ConnectionPublicId = connectionPublicId;
    }
}
