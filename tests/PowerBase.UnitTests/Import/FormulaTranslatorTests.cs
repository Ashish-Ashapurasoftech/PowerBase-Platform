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
    ];

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
