namespace PowerBase.Application.Common.Interfaces;

public interface IEncryptionService
{
    /// <summary>
    /// Generates a new Data Encryption Key (DEK), encrypts it using a Tenant/App derived key, and returns the ciphertext Base64.
    /// </summary>
    Task<string> GenerateAndWrapDekAsync(long tenantId, long appId, CancellationToken ct = default);

    /// <summary>
    /// Encrypts plaintext data using the App's unwrapped Data Encryption Key (DEK).
    /// </summary>
    Task<string> EncryptDataAsync(string plaintext, string wrappedDek, long tenantId, long appId, CancellationToken ct = default);

    /// <summary>
    /// Decrypts ciphertext data using the App's unwrapped Data Encryption Key (DEK).
    /// </summary>
    Task<string> DecryptDataAsync(string ciphertext, string wrappedDek, long tenantId, long appId, CancellationToken ct = default);
}
