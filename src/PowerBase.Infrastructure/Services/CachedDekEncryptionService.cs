using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Same cipher format and keys as <see cref="AesEncryptionService"/> (nonce | tag | ciphertext, DEK wrapped under an
/// HKDF-derived KEK), but the unwrapped DEK is cached. <see cref="AesEncryptionService"/> re-derives the KEK and re-decrypts
/// the wrapped DEK for every value it touches; reading millions of encrypted rows spends most of its time there.
/// Used where many values are decrypted in a row (pipeline record search). The existing service is left as it is, and
/// DEK generation is delegated to it so there is one place that creates keys.
/// </summary>
public sealed class CachedDekEncryptionService : IEncryptionService
{
    private readonly IConfiguration _configuration;
    private readonly IEncryptionService _inner;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private byte[]? _masterKey;
    private readonly ConcurrentDictionary<(long TenantId, long AppId, string WrappedDek), byte[]> _deks = new();

    public CachedDekEncryptionService(IConfiguration configuration, IEncryptionService inner)
    {
        _configuration = configuration;
        _inner = inner;
    }

    public Task<string> GenerateAndWrapDekAsync(long tenantId, long appId, CancellationToken ct = default)
        => _inner.GenerateAndWrapDekAsync(tenantId, appId, ct);

    public async Task<string> EncryptDataAsync(string plaintext, string wrappedDek, long tenantId, long appId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var dek = await GetDekAsync(wrappedDek, tenantId, appId, ct);
        return Convert.ToBase64String(EncryptAesGcm(Encoding.UTF8.GetBytes(plaintext), dek));
    }

    public async Task<string> DecryptDataAsync(string ciphertext, string wrappedDek, long tenantId, long appId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext;
        var dek = await GetDekAsync(wrappedDek, tenantId, appId, ct);
        return Encoding.UTF8.GetString(DecryptAesGcm(Convert.FromBase64String(ciphertext), dek));
    }

    private async ValueTask<byte[]> GetDekAsync(string wrappedDek, long tenantId, long appId, CancellationToken ct)
    {
        var key = (tenantId, appId, wrappedDek);
        if (_deks.TryGetValue(key, out var cached)) return cached;
        var masterKey = await GetMasterKeyAsync(ct);
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, BitConverter.GetBytes(tenantId), BitConverter.GetBytes(appId));
        return _deks.GetOrAdd(key, _ => DecryptAesGcm(Convert.FromBase64String(wrappedDek), kek));
    }

    private async Task<byte[]> GetMasterKeyAsync(CancellationToken ct)
    {
        if (_masterKey != null) return _masterKey;
        await _keyLock.WaitAsync(ct);
        try
        {
            if (_masterKey != null) return _masterKey;

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
                var client = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());
                masterKeyBase64 = (await client.GetSecretAsync(keyName, cancellationToken: ct)).Value.Value;
            }

            var keyBytes = Convert.FromBase64String(masterKeyBase64);
            if (keyBytes.Length != 32)
                throw new InvalidOperationException("Master Key must be a valid 256-bit Base64 string.");
            return _masterKey = keyBytes;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static byte[] EncryptAesGcm(byte[] plaintext, byte[] key)
    {
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
        return result;
    }

    private static byte[] DecryptAesGcm(byte[] combined, byte[] key)
    {
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        var nonce = combined.AsSpan(0, 12);
        var tag = combined.AsSpan(12, 16);
        var ciphertext = combined.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
