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

    // Lookups and Summaries have no column to aggregate — rejected for every function.
    [Theory]
    [InlineData("DistinctCount", "Lookup")]
    [InlineData("Max", "Summary")]
    public void Validate_CalculatedTarget_ThrowsValidation(string function, string typeCode) =>
        Run(function, Field(typeCode, "{\"resultType\":\"Number\"}")).Should().Throw<ValidationException>()
            .Which.Errors["targetFid"].Single().Should().Contain("calculated");

    // A formula is summarized with the functions its result type supports.
    [Theory]
    [InlineData("Formula_Bool", "DistinctCount")]
    [InlineData("Formula_Date", "Min")]
    [InlineData("Formula_Date", "Max")]
    [InlineData("Formula_Date", "DistinctCount")]
    [InlineData("Formula_DateTime", "Min")]
    [InlineData("Formula_DateTime", "Max")]
    [InlineData("Formula_DateTime", "DistinctCount")]
    [InlineData("Formula_Duration", "Sum")]
    [InlineData("Formula_Duration", "Avg")]
    [InlineData("Formula_Duration", "Min")]
    [InlineData("Formula_Duration", "Max")]
    [InlineData("Formula_Email", "CombinedText")]
    [InlineData("Formula_Email", "DistinctCount")]
    [InlineData("Formula_Number", "Sum")]
    [InlineData("Formula_Number", "Avg")]
    [InlineData("Formula_Number", "Min")]
    [InlineData("Formula_Number", "Max")]
    [InlineData("Formula_Number", "DistinctCount")]
    [InlineData("Formula_Phone", "CombinedText")]
    [InlineData("Formula_Phone", "DistinctCount")]
    [InlineData("Formula_Text", "CombinedText")]
    [InlineData("Formula_Text", "DistinctCount")]
    public void Validate_FormulaTargetWithSupportedFunction_Passes(string typeCode, string function) =>
        Run(function, Field(typeCode)).Should().NotThrow();

    [Theory]
    [InlineData("Formula_Bool", "Sum")]
    [InlineData("Formula_Bool", "CombinedText")]
    [InlineData("Formula_Date", "Sum")]
    [InlineData("Formula_Date", "CombinedText")]
    [InlineData("Formula_Duration", "DistinctCount")]
    [InlineData("Formula_Duration", "CombinedText")]
    [InlineData("Formula_Email", "Max")]
    [InlineData("Formula_Number", "CombinedText")]
    [InlineData("Formula_Phone", "Sum")]
    [InlineData("Formula_Text", "Min")]
    [InlineData("Formula_Time", "DistinctCount")]
    [InlineData("Formula_User", "DistinctCount")]
    [InlineData("Formula_Url", "CombinedText")]
    public void Validate_FormulaTargetWithUnsupportedFunction_ThrowsValidation(string typeCode, string function) =>
        Run(function, Field(typeCode)).Should().Throw<ValidationException>();

    // A child's own summary field (Project › Tasks › Time Entries) is summarized by what it produces.
    [Theory]
    [InlineData("{\"function\":\"Sum\",\"targetTypeCode\":\"Currency\"}", "Sum", true)]
    [InlineData("{\"function\":\"Count\"}", "Max", true)]
    [InlineData("{\"function\":\"Count\"}", "CombinedText", false)]
    [InlineData("{\"function\":\"Exists\"}", "DistinctCount", true)]
    [InlineData("{\"function\":\"Exists\"}", "Sum", false)]
    [InlineData("{\"function\":\"CombinedText\"}", "CombinedText", true)]
    [InlineData("{\"function\":\"Max\",\"targetTypeCode\":\"Date\"}", "Min", true)]
    [InlineData("{\"function\":\"Max\",\"targetTypeCode\":\"Date\"}", "Sum", false)]
    [InlineData("{\"function\":\"Min\",\"targetTypeCode\":\"Text\"}", "DistinctCount", false)]
    public void Validate_ChildSummaryTarget_FollowsWhatItProduces(string settings, string function, bool valid)
    {
        var run = Run(function, Field("Summary", settings));
        if (valid) run.Should().NotThrow(); else run.Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData("Number", "Sum")]
    [InlineData("Text", "CombinedText")]
    [InlineData("Date", "Min")]
    public void Validate_GenericFormulaTarget_FollowsItsResultType(string resultType, string function) =>
        Run(function, Field("Formula", $"{{\"resultType\":\"{resultType}\"}}")).Should().NotThrow();

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

    // System fields are read from their own columns (Id, CreatedOn, …) — summarizable like Quickbase.
    [Theory]
    [InlineData("DistinctCount", 3, "Number", "Id")]
    [InlineData("Max", 3, "Number", "Id")]
    [InlineData("Min", 1, "DateTime", "CreatedOn")]
    [InlineData("DistinctCount", 4, "User", "CreatedBy")]
    public void Validate_SystemFieldTarget_Passes(string function, int fid, string typeCode, string column) =>
        Run(function, new AppField { Id = fid, Fid = fid, Name = "System", TypeCode = typeCode, IsSystem = true, PhysicalColumnName = column })
            .Should().NotThrow();

    // ── Lookup targets: judged by the parent field they pull down ──

    private static AppField Lookup(int fid = 20, string? subField = null) => new()
    {
        Id = fid, Fid = fid, Name = "Dept Budget", TypeCode = "Lookup",
        Settings = $"{{\"relationshipId\":1,\"referenceFid\":10,\"sourceTableId\":7,\"sourceFid\":8{(subField is null ? "" : $",\"sourceSubField\":\"{subField}\"")}}}",
    };

    private static Dictionary<long, AppField> Sources(string sourceType) =>
        new() { [20] = new AppField { Id = 8, Fid = 8, Name = "Budget", TypeCode = sourceType } };

    [Theory]
    [InlineData("Sum", "Currency")]
    [InlineData("Max", "Date")]
    [InlineData("DistinctCount", "Text")]
    [InlineData("CombinedText", "Text")]
    public void Validate_LookupOfStoredField_UsesSourceType_Passes(string function, string sourceType) =>
        FluentActions.Invoking(() => SummaryTargetValidator.Validate(function, 20, Lookup(), null, lookupSources: Sources(sourceType)))
            .Should().NotThrow();

    [Theory]
    [InlineData("Sum", "Text")]
    [InlineData("CombinedText", "User")]
    public void Validate_LookupOfWrongType_ThrowsValidation(string function, string sourceType) =>
        FluentActions.Invoking(() => SummaryTargetValidator.Validate(function, 20, Lookup(), null, lookupSources: Sources(sourceType)))
            .Should().Throw<ValidationException>();

    [Fact]
    public void Validate_LookupOfAddressPart_IsText() =>
        FluentActions.Invoking(() => SummaryTargetValidator.Validate("Min", 20, Lookup(subField: "city"), null, lookupSources: Sources("Address")))
            .Should().Throw<ValidationException>();

    [Theory]
    [InlineData("Formula_Number")]
    [InlineData("Summary")]
    [InlineData("Lookup")]
    public void Validate_LookupOfCalculatedField_ThrowsValidation(string sourceType) =>
        FluentActions.Invoking(() => SummaryTargetValidator.Validate("DistinctCount", 20, Lookup(), null, lookupSources: Sources(sourceType)))
            .Should().Throw<ValidationException>().Which.Errors["targetFid"].Single().Should().Contain("calculated");

    [Fact]
    public void Validate_LookupWhoseSourceIsGone_ThrowsValidation() =>
        FluentActions.Invoking(() => SummaryTargetValidator.Validate("DistinctCount", 20, Lookup(), null, lookupSources: new Dictionary<long, AppField>()))
            .Should().Throw<ValidationException>();

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

    [Fact]
    public void ValidateCombinedTextOptions_FormulaSortField_Passes() =>   // computed per record, sorted in memory
        SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(", ", 7, false, false), ChildFields)
            .Should().NotBeNull();

    [Fact]
    public void ValidateCombinedTextOptions_UnsortableField_ThrowsValidation() =>   // a formula that returns a Time can't be compared
        FluentActions.Invoking(() => SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(", ", 90, false, false),
                [.. ChildFields, new AppField { Id = 90, Fid = 90, Name = "T", TypeCode = "Formula_Time" }]))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainKey("sortFid");

    [Fact]
    public void ValidateCombinedTextOptions_SystemSortField_Passes() =>   // Date Created, read from CreatedOn
        SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(", ", 1, false, false), ChildFields)
            .Should().NotBeNull();

    [Fact]
    public void ValidateCombinedTextOptions_LookupSortField_PassesWithItsSource() =>
        SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(", ", 20, false, false),
                [.. ChildFields, Lookup()], Sources("Date"))
            .Should().NotBeNull();

    [Fact]
    public void ValidateCombinedTextOptions_UnknownSortField_ThrowsNotFound() =>
        FluentActions.Invoking(() => SummaryTargetValidator.ValidateCombinedTextOptions("CombinedText", new CombinedTextOptions(", ", 999, false, false), ChildFields))
            .Should().Throw<NotFoundException>();

    [Fact]
    public void Validate_UnknownTargetFid_ThrowsNotFound() =>
        FluentActions.Invoking(() => SummaryTargetValidator.Validate("Sum", 999, null, null))
            .Should().Throw<NotFoundException>();
}
