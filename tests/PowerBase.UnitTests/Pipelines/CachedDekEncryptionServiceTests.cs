using Microsoft.Extensions.Configuration;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>The DEK-caching service must read and write exactly what AesEncryptionService does: both read the same stored data.</summary>
public class CachedDekEncryptionServiceTests
{
    private static (AesEncryptionService Aes, CachedDekEncryptionService Cached) Create()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Encryption:IsEncryptionLocal"] = "true",
            ["Encryption:MasterKey"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray())
        }).Build();
        var aes = new AesEncryptionService(config);
        return (aes, new CachedDekEncryptionService(config, aes));
    }

    [Fact]
    public async Task Decrypt_ValueEncryptedByAesService_ReturnsPlaintext()
    {
        var (aes, cached) = Create();
        var dek = await aes.GenerateAndWrapDekAsync(40022, 1);
        var cipher = await aes.EncryptDataAsync("1754996", dek, 40022, 1);

        Assert.Equal("1754996", await cached.DecryptDataAsync(cipher, dek, 40022, 1));
        Assert.Equal("1754996", await cached.DecryptDataAsync(cipher, dek, 40022, 1)); // second call hits the DEK cache
    }

    [Fact]
    public async Task Encrypt_ValueIsReadableByAesService()
    {
        var (aes, cached) = Create();
        var dek = await cached.GenerateAndWrapDekAsync(40022, 1);
        var cipher = await cached.EncryptDataAsync("Order ✓ ORD000123", dek, 40022, 1);

        Assert.Equal("Order ✓ ORD000123", await aes.DecryptDataAsync(cipher, dek, 40022, 1));
    }

    [Fact]
    public async Task Decrypt_DifferentApp_DoesNotReuseAnotherAppsKey()
    {
        var (aes, cached) = Create();
        var dek = await aes.GenerateAndWrapDekAsync(40022, 1);
        var cipher = await aes.EncryptDataAsync("secret", dek, 40022, 1);
        await cached.DecryptDataAsync(cipher, dek, 40022, 1);

        await Assert.ThrowsAnyAsync<Exception>(() => cached.DecryptDataAsync(cipher, dek, 40022, 2));
    }
}
