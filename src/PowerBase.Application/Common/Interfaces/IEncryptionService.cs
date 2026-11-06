namespace PowerBase.Application.Common.Interfaces;

public interface IEncryptionService
{
    /// <summary>
    /// Generates a new Data Encryption Key (DEK), encrypts it using a Tenant/App derived key, and returns the ciphertext Base64.
    /// </summary>
    Task<string> GenerateAndWrapDekAsync(long tenantId, long appId, CancellationToken ct = default);

    /// <summary>
    /// Wraps the SAME Data Encryption Key under another app's derived key: unwraps <paramref name="wrappedDek"/> with
    /// <paramref name="fromAppId"/>'s key and wraps the result with <paramref name="toAppId"/>'s. Used by Copy App with data,
    /// whose copied ciphertext must stay readable in the new app without being decrypted.
    /// </summary>
    Task<string> RewrapDekAsync(string wrappedDek, long tenantId, long fromAppId, long toAppId, CancellationToken ct = default);

    /// <summary>
    /// Encrypts plaintext data using the App's unwrapped Data Encryption Key (DEK).
    /// </summary>
    Task<string> EncryptDataAsync(string plaintext, string wrappedDek, long tenantId, long appId, CancellationToken ct = default);

    /// <summary>
    /// Decrypts ciphertext data using the App's unwrapped Data Encryption Key (DEK).
    /// </summary>
    Task<string> DecryptDataAsync(string ciphertext, string wrappedDek, long tenantId, long appId, CancellationToken ct = default);
}
