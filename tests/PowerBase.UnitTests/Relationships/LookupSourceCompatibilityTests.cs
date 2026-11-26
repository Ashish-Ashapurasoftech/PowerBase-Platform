using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class LookupSourceCompatibilityTests
{
    [Theory]
    // Text lookups: plain-text fields only (Formula-Text resolves to "Text") — the "App Name" case.
    [InlineData("Text", "Text", true)]
    [InlineData("Text", "TextMultiLine", true)]
    [InlineData("Text", "SingleSelect", true)]
    [InlineData("Text", "Url", false)]       // a URL / Formula-URL field (e.g. "Add Children") is not text
    [InlineData("Text", "Email", false)]
    [InlineData("Text", "RichText", false)]
    [InlineData("Text", "Number", false)]
    [InlineData("Url", "Url", true)]
    [InlineData("Url", "Text", false)]
    // Numeric lookups: numbers only — the "App FID" case.
    [InlineData("Number", "Number", true)]
    [InlineData("Number", "Currency", false)]
    [InlineData("Number", "Text", false)]
    [InlineData("Date", "DateTime", false)]
    [InlineData("Boolean", "Boolean", true)]
    [InlineData("Text", "File", false)]
    [InlineData("File", "File", true)]
    [InlineData("MultiUser", "User", false)]
    // A lookup whose type can't be told accepts any source.
    [InlineData(null, "Number", true)]
    public void IsCompatible(string? current, string? candidate, bool expected) =>
        LookupSourceCompatibility.IsCompatible(current, candidate).Should().Be(expected);

    [Theory]
    [InlineData("Text", true)]
    [InlineData("Formula_Text", true)]
    [InlineData("Lookup", true)]
    [InlineData("ReportLink", false)]
    public void CanBeSource(string typeCode, bool expected) =>
        LookupSourceCompatibility.CanBeSource(new AppField { Id = 1, Fid = 6, Name = "F", TypeCode = typeCode })
            .Should().Be(expected);

    [Theory]
    [InlineData("Formula_Text", "Text")]
    [InlineData("Formula_Url", "Url")]
    [InlineData("Formula_Number", "Number")]
    public void FormulaFields_MatchTheTypeTheyReturn(string typeCode, string expectedKind) =>
        LookupSourceCompatibility.KindOf(FieldValueTypes.Of(new AppField { Id = 1, Fid = 6, Name = "F", TypeCode = typeCode }))
            .Should().Be(expectedKind);

    [Fact]
    public void CanBeSource_DeletedField_IsFalse() =>
        LookupSourceCompatibility.CanBeSource(new AppField { Id = 1, Fid = 6, Name = "F", TypeCode = "Text", IsDeleted = true })
            .Should().BeFalse();
}
