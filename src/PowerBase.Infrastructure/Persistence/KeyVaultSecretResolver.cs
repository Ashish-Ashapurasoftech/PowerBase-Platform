using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace PowerBase.Infrastructure.Persistence;

/// <summary>
/// Resolves and stores connection secrets in Azure Key Vault.
/// Falls back to the default connection string when no secretRef is specified (shared-server tenants).
/// </summary>
public class KeyVaultSecretResolver : ISecretResolver, ISecretStore
{
    private readonly SecretClient _client;
    private readonly string _defaultConnectionString;

    public KeyVaultSecretResolver(IConfiguration configuration)
    {
        var vaultUri = configuration["KeyVault:Uri"]
            ?? throw new InvalidOperationException("KeyVault:Uri is not configured.");
        _defaultConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string is not configured.");

        _client = new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());
    }

    public async Task<string> ResolveAsync(string? serverRef, string? secretRef, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(secretRef))
            return _defaultConnectionString;

        var response = await _client.GetSecretAsync(secretRef, cancellationToken: ct);
        return response.Value.Value;
    }

    public async Task<string> StoreAsync(string name, string secretValue, CancellationToken ct = default)
    {
        await _client.SetSecretAsync(new KeyVaultSecret(name, secretValue), ct);
        return name;
    }
}
