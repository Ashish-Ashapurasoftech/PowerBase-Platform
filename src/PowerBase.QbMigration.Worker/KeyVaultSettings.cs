using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace PowerBase.QbMigration.Worker;

/// <summary>Reads the worker's settings from the migration's own Key Vault (<c>Migration:KeyVaultUri</c>) on the Azure host,
/// signing in as the host's managed identity. A secret named <c>QuickBase--UserToken</c> becomes <c>QuickBase:UserToken</c>.
/// Separate from <c>KeyVault:Uri</c>, which is PowerBase's own vault holding tenant connection strings.</summary>
public static class KeyVaultSettings
{
    public static async Task<IReadOnlyDictionary<string, string?>> LoadAsync(Uri vault, CancellationToken ct = default)
    {
        var client = new SecretClient(vault, new DefaultAzureCredential());
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await foreach (var secret in client.GetPropertiesOfSecretsAsync(ct))
        {
            if (secret.Enabled != true) continue;
            var value = await client.GetSecretAsync(secret.Name, cancellationToken: ct);
            settings[secret.Name.Replace("--", ":")] = value.Value.Value;
        }
        return settings;
    }
}
