using FluentAssertions;
using PowerBase.Domain.Constants;

namespace PowerBase.UnitTests.Fields;

public class FieldNamingTests
{
    [Fact]
    public void GenerateBaseName_CustomField_UsesCPrefixAndCamelCase()
    {
        FieldNaming.GenerateBaseName("Full Name", isSystem: false).Should().Be("C_fullName");
    }

    [Fact]
    public void GenerateBaseName_SystemField_UsesSPrefix()
    {
        FieldNaming.GenerateBaseName("Record ID#", isSystem: true).Should().Be("S_recordId");
    }

    [Fact]
    public void GenerateBaseName_StripsPunctuation()
    {
        FieldNaming.GenerateBaseName("E-mail Address!", isSystem: false).Should().Be("C_eMailAddress");
    }

    [Fact]
    public void GenerateBaseName_LeadingDigit_PrefixesWithF()
    {
        FieldNaming.GenerateBaseName("2024 Revenue", isSystem: false).Should().Be("C_f2024Revenue");
    }

    [Fact]
    public void GenerateBaseName_NoAlphanumericChars_FallsBackToField()
    {
        FieldNaming.GenerateBaseName("***", isSystem: false).Should().Be("C_field");
    }

    [Fact]
    public void GenerateBaseName_SingleWord_LowercasesEntirely()
    {
        FieldNaming.GenerateBaseName("STATUS", isSystem: false).Should().Be("C_status");
    }

    [Fact]
    public void GenerateBaseName_VeryLongLabel_TruncatesToFitColumn()
    {
        var longLabel = string.Concat(Enumerable.Repeat("Word ", 60));
        var name = FieldNaming.GenerateBaseName(longLabel, isSystem: false);
        name.Length.Should().BeLessThanOrEqualTo(200);
    }
}
