namespace PowerBase.Application.Pipelines;

public interface IRequestConnectionService
{
    Task<MakeRequestDefinition> ResolveAsync(Guid connectionId, long pipelineId, long ownerId, CancellationToken ct);
}
