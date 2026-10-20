using FluentAssertions;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Binding;

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
        // Tenant databases only carry Formula_{X} variants, so the result type picks the field type.
        result.TypeCode.Should().Be("Formula_Number");
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

    private sealed class Aliases : ITableAliasSchema
    {
        public bool TryResolve(string alias, out string tableId)
        {
            tableId = "11111111-1111-1111-1111-111111111111";
            return string.Equals(alias, "_DBID_CLIENTS", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Translate_AddRecordUrlWithTableAliasAndNumberArgument_ResolvesAliasAndWrapsToText()
    {
        const string formula = "\"db/\" & [_DBID_CLIENTS] & \"?x=\" & URLEncode([Qty])";

        _sut.Translate("Text", formula, TableFields()).Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);

        var result = _sut.Translate("Text", formula, TableFields(), new Aliases());
        result.Status.Should().Be(FormulaTranslationStatus.Adjusted);
        result.SettingsJson.Should().Contain("ToText([Qty])");
    }

    [Fact]
    public void Translate_LeftAndRightWithTextDelimiter_CompileCleanly()
    {
        _sut.Translate("Text", "Left([Name], \";\")", TableFields()).Status.Should().Be(FormulaTranslationStatus.Clean);
        _sut.Translate("Text", "If(Contains([Name], \";\"), Right([Name], \";\"), [Name])", TableFields()).Status.Should().Be(FormulaTranslationStatus.Clean);
    }

    [Fact]
    public void Translate_LeftWithCount_IsLeftAlone()
    {
        _sut.Translate("Text", "Left([Name], 3)", TableFields()).Status.Should().Be(FormulaTranslationStatus.Clean);
    }

    [Fact]
    public void Translate_DateTimeVariableTypeAndTwoArgumentToTimestamp_Compile()
    {
        const string formula = "var dateTime stamp = ToTimestamp(ToDate(\"2026-06-11\"), ToTimeOfDay(\"1:30 PM\")); $stamp";

        var result = _sut.Translate("DateTime", formula, TableFields());

        result.Status.Should().NotBe(FormulaTranslationStatus.NeedsManualReview, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void Translate_FormulaReadingReferenceField_Compiles()
    {
        var fields = TableFields();
        fields.Add(new AppField { Name = "Related Client", TypeCode = "Reference", Fid = 9 });

        var result = _sut.Translate("Text", "[Name] & \"/\" & [Related Client]", fields);

        result.Status.Should().Be(FormulaTranslationStatus.Clean, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void Translate_ReferenceCompareToNumber_NeedsToNumber()
    {
        var fields = TableFields();
        fields.Add(new AppField { Name = "Related Client", TypeCode = "Reference", Fid = 9 });

        _sut.Translate("Bool", "ToNumber([Related Client]) = [Qty]", fields).Status.Should().Be(FormulaTranslationStatus.Clean);
    }
}
