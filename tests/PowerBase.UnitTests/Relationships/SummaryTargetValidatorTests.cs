using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

public class SummaryTargetValidatorTests
{
    private static AppField Field(string typeCode, string? settings = null) =>
        new() { Id = 5, Fid = 5, Name = "Target", TypeCode = typeCode, Settings = settings };

    private static Action Run(string function, AppField? target, string? subField = null) =>
        () => SummaryTargetValidator.Validate(function, target?.Fid, target, subField);

    [Theory]
    [InlineData("Count")]
    [InlineData("Exists")]
    public void Validate_CountOrExists_NeedsNoTarget(string function) =>
        Run(function, null).Should().NotThrow();

    [Theory]
    [InlineData("Sum", "Number")]
    [InlineData("Sum", "Currency")]
    [InlineData("Avg", "Percent")]
    [InlineData("Avg", "Duration")]
    [InlineData("Min", "Number")]
    [InlineData("Max", "Rating")]
    [InlineData("Min", "Date")]
    [InlineData("Max", "DateTime")]
    [InlineData("DistinctCount", "Text")]
    [InlineData("DistinctCount", "Number")]
    [InlineData("DistinctCount", "Date")]
    [InlineData("DistinctCount", "Boolean")]
    [InlineData("DistinctCount", "SingleSelect")]
    [InlineData("DistinctCount", "Email")]
    [InlineData("CombinedText", "Text")]
    [InlineData("CombinedText", "Number")]
    [InlineData("CombinedText", "Currency")]
    [InlineData("CombinedText", "Date")]
    [InlineData("CombinedText", "SingleSelect")]
    public void Validate_ValidFunctionForType_Passes(string function, string typeCode) =>
        Run(function, Field(typeCode)).Should().NotThrow();

    [Theory]
    [InlineData("Sum", "Text")]
    [InlineData("Avg", "Text")]
    [InlineData("Sum", "Date")]
    [InlineData("Avg", "DateTime")]
    [InlineData("Min", "Text")]
    [InlineData("Max", "Text")]
    [InlineData("Min", "Email")]
    [InlineData("Sum", "Boolean")]
    [InlineData("Max", "Boolean")]
    [InlineData("Min", "Lookup")]
    public void Validate_InvalidFunctionForType_ThrowsValidation(string function, string typeCode) =>
        Run(function, Field(typeCode)).Should().Throw<ValidationException>();

    // Calculated fields have no physical column to aggregate — rejected for every function.
    [Theory]
    [InlineData("Sum", "Formula_Number")]
    [InlineData("Max", "Formula_Date")]
    [InlineData("DistinctCount", "Formula_Text")]
    [InlineData("Sum", "Formula")]
    [InlineData("DistinctCount", "Lookup")]
    [InlineData("Max", "Summary")]
    [InlineData("CombinedText", "Formula_Text")]
    public void Validate_CalculatedTarget_ThrowsValidation(string function, string typeCode) =>
        Run(function, Field(typeCode, "{\"resultType\":\"Number\"}")).Should().Throw<ValidationException>()
            .Which.Errors["targetFid"].Single().Should().Contain("calculated");

    [Theory]
    [InlineData("User")]
    [InlineData("MultiUser")]
    [InlineData("File")]
    [InlineData("RichText")]
    [InlineData("Reference")]
    [InlineData("DateRange")]
    [InlineData("NumericRange")]
    public void Validate_CombinedTextOnTypeWithNoTextForm_ThrowsValidation_ButDistinctCountPasses(string typeCode)
    {
        Run("CombinedText", Field(typeCode)).Should().Throw<ValidationException>()
            .Which.Errors["targetFid"].Single().Should().Contain("Distinct Count instead");
        Run("DistinctCount", Field(typeCode)).Should().NotThrow();
    }

    [Theory]
    [InlineData("Address")]
    [InlineData("Phone")]
    [InlineData("MultiSelect")]
    [InlineData("Email")]
    [InlineData("Url")]
    [InlineData("Time")]
    public void Validate_CombinedTextOnTypesWithTextForm_Passes(string typeCode) =>
        Run("CombinedText", Field(typeCode)).Should().NotThrow();

    [Theory]
    [InlineData("Email", "'Min' can only summarize numeric or date fields; 'Target' is an Email field.")]
    [InlineData("Address", "'Min' can only summarize numeric or date fields; 'Target' is an Address field.")]
    [InlineData("Text", "'Min' can only summarize numeric or date fields; 'Target' is a Text field.")]
    [InlineData("Url", "'Min' can only summarize numeric or date fields; 'Target' is a Url field.")]
    public void Validate_ErrorMessage_UsesCorrectArticle(string typeCode, string expected) =>
        Run("Min", Field(typeCode)).Should().Throw<ValidationException>()
            .Which.Errors["targetFid"].Single().Should().Be(expected);

    [Theory]
    [InlineData("sum", "Sum")]
    [InlineData(" Max ", "Max")]
    [InlineData("combinedtext", "CombinedText")]
    [InlineData("Bogus", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SummaryFunctions_Normalize_ReturnsCanonicalSpellingOrNull(string? input, string? expected) =>
        PowerBase.Domain.FieldSettings.SummaryFunctions.Normalize(input).Should().Be(expected);

    [Fact]
    public void Validate_SystemFieldTarget_ThrowsValidation()
    {
        var recordId = new AppField { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" };
        Run("DistinctCount", recordId).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Validate_DistinctCountOnAddressSubField_Passes() =>
        Run("DistinctCount", Field("Address"), "city").Should().NotThrow();

    [Theory]
    [InlineData("DistinctCount")]
    [InlineData("CombinedText")]
    public void Validate_AnyTypeFunctionWithoutTarget_ThrowsValidation(string function) =>
        Run(function, null).Should().Throw<ValidationException>();

    [Theory]
    [InlineData("Sum")]
    [InlineData("Min")]
    [InlineData("Max")]
    public void Validate_AddressSubField_IsTextAndRejected(string function) =>
        Run(function, Field("Address"), "city").Should().Throw<ValidationException>();

    [Fact]
    public void Validate_AggregateWithoutTargetFid_ThrowsValidation() =>
        Run("Sum", null).Should().Throw<ValidationException>();

    // ── Combined Text options ──

    private static readonly List<AppField> ChildFields =
    [
        new() { Id = 5, Fid = 5, Name = "Title", TypeCode = "Text" },
        new() { Id = 6, Fid = 6, Name = "Due Date", TypeCode = "Date" },
        new() { Id = 7, Fid = 7, Name = "Days Left", TypeCode = "Formula_Number" },
        new() { Id = 1, Fid = 1, Name = "Date Created", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn" },
    ];

    [Fact]
    public void ValidateCombinedTextOptions_OtherFunction_ReturnsNull() =>
        SummaryTargetValidator.ValidateCombinedTextOptions("Sum", new CombinedTextOptions(" | ", 6, true, true), ChildFields)
            .Should().BeNull();

    [Fact]
    public void ValidateCombinedTextOptions_NoOptions_ReturnsDefault() =>
        SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", null, ChildFields)
            .Should().Be(CombinedTextOptions.Default);

    [Theory]
    [InlineData("\n")]
    [InlineData(" | ")]
    [InlineData(";")]
    [InlineData(" -- ")]
    public void ValidateCombinedTextOptions_ValidDelimiterAndSort_Passes(string delimiter) =>
        SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(delimiter, 6, true, true), ChildFields)
            .Should().Be(new CombinedTextOptions(delimiter, 6, true, true));

    [Theory]
    [InlineData("")]
    [InlineData("12345678901")]   // 11 chars — over the 10-char limit
    public void ValidateCombinedTextOptions_BadDelimiter_ThrowsValidation(string delimiter) =>
        FluentActions.Invoking(() => SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(delimiter, null, false, false), ChildFields))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainKey("delimiter");

    [Theory]
    [InlineData(7)]   // formula — no physical column
    [InlineData(1)]   // system field — lives in its own column
    public void ValidateCombinedTextOptions_UnsortableField_ThrowsValidation(int sortFid) =>
        FluentActions.Invoking(() => SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(", ", sortFid, false, false), ChildFields))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainKey("sortFid");

    [Fact]
    public void ValidateCombinedTextOptions_UnknownSortField_ThrowsNotFound() =>
        FluentActions.Invoking(() => SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(", ", 999, false, false), ChildFields))
            .Should().Throw<NotFoundException>();

    [Fact]
    public void Validate_UnknownTargetFid_ThrowsNotFound() =>
        FluentActions.Invoking(() => SummaryTargetValidator.Validate("Sum", 999, null, null))
            .Should().Throw<NotFoundException>();
}
