namespace PowerBase.Infrastructure.Persistence;

/// <summary>
/// Used in local development when KeyVault:Uri is not configured.
/// Throws a clear error if a BYO-server tenant creation is attempted.
/// </summary>
public class NoOpSecretStore : ISecretStore
{
    public Task<string> StoreAsync(string name, string secretValue, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "BYO server configuration requires Azure Key Vault. Set KeyVault:Uri in configuration.");
}
