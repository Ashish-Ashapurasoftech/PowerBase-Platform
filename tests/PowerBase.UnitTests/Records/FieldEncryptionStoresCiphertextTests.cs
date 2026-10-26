using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// Which fields hold ciphertext in their column. The search-index backfill (RecordRepository.GetFieldBackfillBatchAsync) reads a
/// column raw and decrypts exactly these before indexing: it used to decrypt only fields flagged IsEncrypted, so a field of an
/// encrypted App went into Azure AI Search as ciphertext and no search could ever match it.
/// </summary>
public class FieldEncryptionStoresCiphertextTests
{
    private static AppField Field(string typeCode = "Text", bool encrypted = false, bool system = false) =>
        new() { Fid = 6, Name = "Name", TypeCode = typeCode, IsEncrypted = encrypted, IsSystem = system };

    [Fact]
    public void FieldOfAnEncryptedApp_NotFlaggedItself_HoldsCiphertext() =>
        Assert.True(FieldEncryptionContext.StoresCiphertext(Field(), appEncrypted: true));

    [Fact]
    public void FieldFlaggedEncrypted_InAPlainApp_HoldsCiphertext() =>
        Assert.True(FieldEncryptionContext.StoresCiphertext(Field(encrypted: true), appEncrypted: false));

    [Fact]
    public void UnflaggedFieldOfAPlainApp_HoldsPlainText() =>
        Assert.False(FieldEncryptionContext.StoresCiphertext(Field(), appEncrypted: false));

    [Theory]
    [InlineData("Number")]
    [InlineData("Date")]
    [InlineData("Boolean")]
    [InlineData("Reference")]
    [InlineData("User")]
    public void ExemptType_IsNeverEncrypted_EvenInAnEncryptedApp(string typeCode) =>
        Assert.False(FieldEncryptionContext.StoresCiphertext(Field(typeCode, encrypted: true), appEncrypted: true));

    [Fact]
    public void SystemField_IsNeverEncrypted() =>
        Assert.False(FieldEncryptionContext.StoresCiphertext(Field(system: true), appEncrypted: true));
}
