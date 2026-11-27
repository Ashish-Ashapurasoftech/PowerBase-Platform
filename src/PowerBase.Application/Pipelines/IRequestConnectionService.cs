namespace PowerBase.Application.Pipelines;

public interface IRequestConnectionService
{
    Task<MakeRequestDefinition> ResolveAsync(Guid connectionId, long pipelineId, long ownerId, CancellationToken ct);

    /// <summary>An HTTP account without authentication for <paramref name="baseUrl"/> in the PowerFlow (reused when one with the
    /// same name and address exists). Used by the import for Quickbase Webhooks-channel requests, which have no account.</summary>
    Task<Guid> EnsureNoAuthenticationAccountAsync(long pipelineId, string name, string baseUrl, long ownerId, CancellationToken ct)
        => throw new NotSupportedException();
}
