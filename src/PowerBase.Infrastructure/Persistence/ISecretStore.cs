namespace PowerBase.Infrastructure.Persistence;

public interface ISecretStore
{
    /// <summary>
    /// Stores a secret value and returns its reference (name/identifier) for later retrieval.
    /// </summary>
    Task<string> StoreAsync(string name, string secretValue, CancellationToken ct = default);
}
