using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace PowerBase.Infrastructure.Services;

public class AesEncryptionService : IEncryptionService
{
    private readonly IConfiguration _configuration;
    private byte[]? _cachedMasterKey;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    public AesEncryptionService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    private async Task<byte[]> GetMasterKeyAsync(CancellationToken ct)
    {
        if (_cachedMasterKey != null) return _cachedMasterKey;

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_cachedMasterKey != null) return _cachedMasterKey;

            var isLocalStr = _configuration["Encryption:IsEncryptionLocal"];
            var isLocal = !string.IsNullOrEmpty(isLocalStr) && bool.Parse(isLocalStr);
            string masterKeyBase64;

            if (isLocal)
            {
                masterKeyBase64 = _configuration["Encryption:MasterKey"] 
                    ?? throw new InvalidOperationException("Encryption:MasterKey is not configured in appsettings.");
            }
            else
            {
                var kvUrl = _configuration["Encryption:KeyVaultUrl"] 
                    ?? throw new InvalidOperationException("Encryption:KeyVaultUrl is not configured.");
                var keyName = _configuration["Encryption:MasterKeyName"] 
                    ?? throw new InvalidOperationException("Encryption:MasterKeyName is not configured.");

                var client = new SecretClient(new Uri(kvUrl), new Azure.Identity.DefaultAzureCredential());
                var secret = await client.GetSecretAsync(keyName, cancellationToken: ct);
                masterKeyBase64 = secret.Value.Value;
            }

            var keyBytes = Convert.FromBase64String(masterKeyBase64);
            if (keyBytes.Length != 32)
            {
                throw new InvalidOperationException("Master Key must be a valid 256-bit Base64 string.");
            }

            _cachedMasterKey = keyBytes;
            return _cachedMasterKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    public async Task<string> GenerateAndWrapDekAsync(long tenantId, long appId, CancellationToken ct = default)
    {
        var masterKey = await GetMasterKeyAsync(ct);
        var kek = DeriveKeyEncryptionKey(masterKey, tenantId, appId);

        // 1. Generate a new Data Encryption Key (DEK)
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);

        // 2. Encrypt the DEK using the derived KEK
        var wrappedDek = EncryptAesGcm(dek, kek);
        
        return Convert.ToBase64String(wrappedDek);
    }

    public async Task<string> EncryptDataAsync(string plaintext, string wrappedDekBase64, long tenantId, long appId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var masterKey = await GetMasterKeyAsync(ct);

        // 1. Unwrap the DEK
        var dek = UnwrapDek(masterKey, wrappedDekBase64, tenantId, appId);

        // 2. Encrypt the plaintext using the DEK
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertextBytes = EncryptAesGcm(plaintextBytes, dek);

        return Convert.ToBase64String(ciphertextBytes);
    }

    public async Task<string> DecryptDataAsync(string ciphertextBase64, string wrappedDekBase64, long tenantId, long appId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ciphertextBase64)) return ciphertextBase64;

        var masterKey = await GetMasterKeyAsync(ct);

        // 1. Unwrap the DEK
        var dek = UnwrapDek(masterKey, wrappedDekBase64, tenantId, appId);

        // 2. Decrypt the ciphertext using the DEK
        var ciphertextBytes = Convert.FromBase64String(ciphertextBase64);
        var plaintextBytes = DecryptAesGcm(ciphertextBytes, dek);

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private byte[] UnwrapDek(byte[] masterKey, string wrappedDekBase64, long tenantId, long appId)
    {
        var kek = DeriveKeyEncryptionKey(masterKey, tenantId, appId);
        var wrappedDek = Convert.FromBase64String(wrappedDekBase64);
        return DecryptAesGcm(wrappedDek, kek);
    }

    private byte[] DeriveKeyEncryptionKey(byte[] masterKey, long tenantId, long appId)
    {
        // Use HKDF to mathematically mix the Master Key with the Tenant and App IDs
        var salt = BitConverter.GetBytes(tenantId);
        var info = BitConverter.GetBytes(appId);
        
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt, info);
    }

    private static byte[] EncryptAesGcm(byte[] plaintext, byte[] key)
    {
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        var nonce = new byte[12]; // Standard nonce size for GCM
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Combine Nonce + Tag + Ciphertext into a single array for easy storage
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

        return result;
    }

    private static byte[] DecryptAesGcm(byte[] combinedData, byte[] key)
    {
        using var aes = new AesGcm(key, tagSizeInBytes: 16);

        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[combinedData.Length - nonce.Length - tag.Length];

        Buffer.BlockCopy(combinedData, 0, nonce, 0, nonce.Length);
        Buffer.BlockCopy(combinedData, nonce.Length, tag, 0, tag.Length);
        Buffer.BlockCopy(combinedData, nonce.Length + tag.Length, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
