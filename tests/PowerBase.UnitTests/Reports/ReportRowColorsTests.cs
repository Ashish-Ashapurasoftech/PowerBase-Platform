using FluentAssertions;
using PowerBase.Application.Reports;
using Xunit;

namespace PowerBase.UnitTests.Reports;

public class ReportRowColorsTests
{
    private static readonly HashSet<long> Fields = new() { 7, 8, 12 };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"mode\":\"none\"}")]
    public void NoColouring_IsFine_AndIsNotStored(string? json)
    {
        ReportRowColors.Validate(json, Fields).Should().BeNull();
        ReportRowColors.Normalize(json).Should().BeNull();
    }

    [Fact]
    public void ByChoice_WithAColourPerChoice_IsAccepted_AndKept()
    {
        const string json = "{\"mode\":\"choice\",\"fieldId\":7,\"colors\":{\"Open\":\"#ffe08a\",\"Closed\":\"#B7E4C7\"}}";
        ReportRowColors.Validate(json, Fields).Should().BeNull();
        ReportRowColors.Normalize($"  {json} ").Should().Be(json);
    }

    [Fact]
    public void ByColumn_NamingATableField_IsAccepted()
    {
        ReportRowColors.Validate("{\"mode\":\"column\",\"fieldId\":12}", Fields).Should().BeNull();
    }

    [Fact]
    public void ByColumn_MayNameAReportFormulaColumn()
    {
        var formulaId = ReportFormulaFields.FirstId;
        ReportRowColors.Validate($"{{\"mode\":\"column\",\"fieldId\":{formulaId}}}", Fields).Should().BeNull();
        // ...but a report formula is not a choice field, so "choice" cannot use one.
        ReportRowColors.Validate($"{{\"mode\":\"choice\",\"fieldId\":{formulaId},\"colors\":{{}}}}", Fields).Should().Contain("unknown field");
    }

    [Theory]
    [InlineData("not json", "valid JSON")]
    [InlineData("[]", "need a mode")]
    [InlineData("{\"mode\":3}", "need a mode")]
    [InlineData("{\"mode\":\"stripes\"}", "must be none, choice or column")]
    [InlineData("{\"mode\":\"column\"}", "field to colour by")]
    [InlineData("{\"mode\":\"column\",\"fieldId\":0}", "field to colour by")]
    [InlineData("{\"mode\":\"column\",\"fieldId\":99}", "unknown field")]
    [InlineData("{\"mode\":\"choice\",\"fieldId\":7}", "colour for each choice")]
    [InlineData("{\"mode\":\"choice\",\"fieldId\":7,\"colors\":{\"Open\":\"red\"}}", "#rrggbb")]
    [InlineData("{\"mode\":\"choice\",\"fieldId\":7,\"colors\":{\"Open\":\"#fff\"}}", "#rrggbb")]
    [InlineData("{\"mode\":\"choice\",\"fieldId\":7,\"colors\":{\"Open\":5}}", "#rrggbb")]
    public void AnythingElse_IsRefusedWithAReason(string json, string reasonContains)
    {
        ReportRowColors.Validate(json, Fields).Should().Contain(reasonContains);
    }

    [Fact]
    public void TooLongOrTooManyChoices_AreRefused()
    {
        ReportRowColors.Validate("{\"mode\":\"column\",\"fieldId\":7,\"pad\":\"" + new string('x', ReportRowColors.MaxJsonLength) + "\"}", Fields)
            .Should().Contain("too long");

        var many = string.Join(",", Enumerable.Range(0, ReportRowColors.MaxChoiceColors + 1).Select(i => $"\"c{i}\":\"#aabbcc\""));
        ReportRowColors.Validate($"{{\"mode\":\"choice\",\"fieldId\":7,\"colors\":{{{many}}}}}", Fields).Should().Contain("at most");
    }
}
