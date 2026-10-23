using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Application.Import.Jobs;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>The fixes of docs/QBL_REIMPORT_FIX_PLAN.md (formula translation, mapping).</summary>
public class ImportFixPlanTests
{
    private readonly FormulaTranslator _sut = new(new FormulaEngine());

    private static List<AppField> Fields() =>
    [
        new() { Name = "Qty", TypeCode = "Number", Fid = 6 },
        new() { Name = "Name", TypeCode = "Text", Fid = 7 },
        new() { Name = "Date Of Birth", TypeCode = "Date", Fid = 8 },
        new() { Name = "Latest OLP Service", TypeCode = "Date", Fid = 9 },
        new() { Name = "Latest T Service", TypeCode = "Date", Fid = 10 },
        new() { Name = "Clicked A", TypeCode = "DateTime", Fid = 11 },
        new() { Name = "Clicked B", TypeCode = "DateTime", Fid = 12 },
        new() { Name = "Clicked C", TypeCode = "DateTime", Fid = 13 },
        new() { Name = "Open", TypeCode = "Number", Fid = 14 },
        new() { Name = "Status", TypeCode = "Text", Fid = 15 },
        new() { Name = "Record ID#", TypeCode = "Number", Fid = 3 },
    ];

    private void ShouldTranslate(string type, string expression, string? mustContain = null)
    {
        var result = _sut.Translate(type, expression, Fields());

        result.Status.Should().NotBe(FormulaTranslationStatus.NeedsManualReview, string.Join("; ", result.Diagnostics));
        if (mustContain is not null) result.SettingsJson.Should().Contain(mustContain);
    }

    // ---- A3: app variables ----

    [Fact]
    public void Translate_FormulaReadingAnAppVariable_FailsWithoutTheVariableSchema()
    {
        var result = _sut.Translate("Text", "[Name] & [1226]", Fields());

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
        result.Diagnostics.Should().Contain(d => d.Contains("1226"));
    }

    [Fact]
    public void Translate_FormulaReadingAnAppVariable_CompilesWhenTheImportScopeHasTheVariables()
    {
        ImportRuntime.Variables = new AppVariableSchema([new AppVariable { Id = 1, Name = "1226", Value = "3" }]);
        try
        {
            var result = _sut.Translate("Text", "[Name] & [1226]", Fields());

            result.Status.Should().Be(FormulaTranslationStatus.Clean, string.Join("; ", result.Diagnostics));
        }
        finally
        {
            ImportRuntime.Variables = null;
        }
    }
}
