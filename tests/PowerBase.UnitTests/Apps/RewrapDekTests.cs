using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Apps;

/// <summary>Copy App with data copies ciphertext as it is, so the copy must hold the original's data key, wrapped under its
/// own app key (RewrapDekAsync), never a new key.</summary>
public class RewrapDekTests
{
    private const long TENANT_ID = 44;
    private const long SOURCE_APP_ID = 34;
    private const long COPY_APP_ID = 139;

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
    public async Task RewrapDek_CopiedCiphertext_DecryptsInTheCopy()
    {
        var (aes, _) = Create();
        var sourceDek = await aes.GenerateAndWrapDekAsync(TENANT_ID, SOURCE_APP_ID);
        var cipher = await aes.EncryptDataAsync("Department Ω 42", sourceDek, TENANT_ID, SOURCE_APP_ID);

        var copyDek = await aes.RewrapDekAsync(sourceDek, TENANT_ID, SOURCE_APP_ID, COPY_APP_ID);

        (await aes.DecryptDataAsync(cipher, copyDek, TENANT_ID, COPY_APP_ID)).Should().Be("Department Ω 42");
    }

    [Fact]
    public async Task RewrapDek_IsWrappedForTheCopyOnly()
    {
        var (aes, _) = Create();
        var sourceDek = await aes.GenerateAndWrapDekAsync(TENANT_ID, SOURCE_APP_ID);
        var cipher = await aes.EncryptDataAsync("secret", sourceDek, TENANT_ID, SOURCE_APP_ID);

        var copyDek = await aes.RewrapDekAsync(sourceDek, TENANT_ID, SOURCE_APP_ID, COPY_APP_ID);

        copyDek.Should().NotBe(sourceDek);
        // The copy's wrapped key does not open under the original app's key, nor under another tenant's.
        await aes.Invoking(a => a.DecryptDataAsync(cipher, copyDek, TENANT_ID, SOURCE_APP_ID)).Should().ThrowAsync<Exception>();
        await aes.Invoking(a => a.DecryptDataAsync(cipher, copyDek, TENANT_ID + 1, COPY_APP_ID)).Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task RewrapDek_NewRecordsOfTheCopy_AreReadableByBothServices()
    {
        var (aes, cached) = Create();
        var sourceDek = await aes.GenerateAndWrapDekAsync(TENANT_ID, SOURCE_APP_ID);

        var copyDek = await cached.RewrapDekAsync(sourceDek, TENANT_ID, SOURCE_APP_ID, COPY_APP_ID);
        var cipher = await cached.EncryptDataAsync("new in the copy", copyDek, TENANT_ID, COPY_APP_ID);

        (await aes.DecryptDataAsync(cipher, copyDek, TENANT_ID, COPY_APP_ID)).Should().Be("new in the copy");
        // Same data key: what the copy writes, the original's key reads too.
        (await aes.DecryptDataAsync(cipher, sourceDek, TENANT_ID, SOURCE_APP_ID)).Should().Be("new in the copy");
    }

    [Fact]
    public async Task RewrapDek_WrongSourceApp_Throws()
    {
        var (aes, _) = Create();
        var sourceDek = await aes.GenerateAndWrapDekAsync(TENANT_ID, SOURCE_APP_ID);

        await aes.Invoking(a => a.RewrapDekAsync(sourceDek, TENANT_ID, SOURCE_APP_ID + 1, COPY_APP_ID)).Should().ThrowAsync<Exception>();
    }
}
