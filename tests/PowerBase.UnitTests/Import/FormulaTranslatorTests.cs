using FluentAssertions;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Import;

public class FormulaTranslatorTests
{
    private readonly FormulaTranslator _sut = new(new FormulaEngine());

    private static List<AppField> TableFields() =>
    [
        new() { Name = "Qty", TypeCode = "Number", Fid = 6 },
        new() { Name = "Price", TypeCode = "Currency", Fid = 7 },
        new() { Name = "Name", TypeCode = "Text", Fid = 8 },
    ];

    [Fact]
    public void Translate_ConcatWithNonTextOperand_IsRewrittenAndImportedAsAdjusted()
    {
        // Quickbase's '&' coerces; PowerBase's deliberately doesn't (there are engine tests
        // asserting that). The translator bridges the two by wrapping the offending operands, and
        // persists the rewritten text so the stored formula stays valid under the same rules the
        // authoring UI enforces.
        var result = _sut.Translate("Text", "[Name] & [Qty]", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.Adjusted);
        result.SettingsJson.Should().Contain("ToText([Qty])");
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Translate_ConcatAlreadyWrapped_IsLeftAlone()
    {
        var result = _sut.Translate("Text", "[Name] & ToText([Qty])", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.Clean);
        result.SettingsJson.Should().NotContain("ToText(ToText(");
    }

    [Fact]
    public void Translate_ConcatRewriteCannotFixOtherErrors_StillReportsOriginalDiagnostics()
    {
        // The retry only earns a pass when it actually compiles; an unrelated error must still
        // surface, and against the author's original text rather than the rewritten form.
        var result = _sut.Translate("Text", "[Name] & [Qty] & [Missing]", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
        result.Diagnostics.Should().Contain(d => d.Contains("Missing"));
    }

    [Fact]
    public void Translate_TwoArgIf_CompilesCleanly()
    {
        // Quickbase allows the else branch to be omitted, meaning "blank when false".
        var result = _sut.Translate("Number", "If([Qty] > 1, [Price])", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.Clean);
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Translate_ValidExpression_ReturnsCleanWithSettingsJson()
    {
        var result = _sut.Translate("Number", "[Qty] * [Price]", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.Clean);
        result.SettingsJson.Should().NotBeNullOrEmpty();
        result.SettingsJson.Should().Contain("Number");
        result.SettingsJson.Should().Contain("[Qty] * [Price]");
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Translate_ReferencesUnknownField_ReturnsNeedsManualReview()
    {
        var result = _sut.Translate("Number", "[Qty] * [DoesNotExist]", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
        result.SettingsJson.Should().BeNull();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Fact]
    public void Translate_ResultTypeMismatch_ReturnsNeedsManualReview()
    {
        var result = _sut.Translate("Bool", "[Qty] * [Price]", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
    }

    [Fact]
    public void Translate_EmptyExpression_ReturnsNeedsManualReview()
    {
        var result = _sut.Translate("Number", "", TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
        result.Diagnostics.Should().Contain(d => d.Contains("empty"));
    }

    [Fact]
    public void Translate_NullExpression_ReturnsNeedsManualReview()
    {
        var result = _sut.Translate("Number", null, TableFields());

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
    }

    [Fact]
    public void Translate_FieldWithoutFid_IsNotResolvable()
    {
        var fieldsMissingFid = new List<AppField> { new() { Name = "Qty", TypeCode = "Number", Fid = null } };

        var result = _sut.Translate("Number", "[Qty] * 2", fieldsMissingFid);

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
    }
}
