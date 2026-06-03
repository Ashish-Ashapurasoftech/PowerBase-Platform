namespace PowerBase.Infrastructure.Persistence;

public interface ISecretResolver
{
    Task<string> ResolveAsync(string? serverRef, string? secretRef, CancellationToken ct = default);
}
