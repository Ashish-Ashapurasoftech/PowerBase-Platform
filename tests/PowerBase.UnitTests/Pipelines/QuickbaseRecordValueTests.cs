using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class QuickbaseRecordValueTests
{
    private static AppField Field(string type, string name = "Amount", int fid = 7) =>
        new() { Name = name, Label = name, TypeCode = type, Fid = fid };

    [Theory]
    [InlineData("1234.5", 1234.5)]
    [InlineData(" -3 ", -3)]
    [InlineData("1e3", 1000)]
    public void Read_Number_AsWritten_IsTheNumber(string value, double expected)
    {
        var (handled, result) = QuickbaseRecordValue.Read(value, Field("Currency"), "Amount");

        Assert.True(handled);
        Assert.Equal((decimal)expected, result);
    }

    [Theory]
    [InlineData("1,234.50")]
    [InlineData("$1,234.50")]
    [InlineData("abc")]
    public void Read_NumberWithCommaOrSymbol_FailsAsQuickbase(string value)
    {
        var error = Assert.Throws<PipelineMappingException>(() => QuickbaseRecordValue.Read(value, Field("Numeric"), "Amount"));

        Assert.Equal("Validation error: in field **amount**: not a valid number", error.Message);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("1", true)]
    [InlineData("TRUE", true)]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    [InlineData("no", false)]
    [InlineData("maybe", false)]
    [InlineData("on", false)]
    [InlineData("2", false)]
    [InlineData("checked", false)]
    public void Read_Checkbox_UnknownTextIsUnchecked_AsQuickbase(string value, bool expected)
    {
        var (handled, result) = QuickbaseRecordValue.Read(value, Field("Checkbox", "Active", 9), "Active");

        Assert.True(handled);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Read_BlankOrOtherTypes_AreLeftToTheStep()
    {
        Assert.False(QuickbaseRecordValue.Read("", Field("Numeric"), "Amount").Handled);
        Assert.False(QuickbaseRecordValue.Read("x", Field("Text", "Name", 6), "Name").Handled);
    }

    [Fact]
    public void InvalidDate_IsWordedAsQuickbase()
    {
        Assert.Equal("Validation error: in field **start_date**: Validation error: Invalid date/time value: 13/13/2025",
            QuickbaseRecordValue.InvalidDate("Start Date", " 13/13/2025 "));
    }

    [Fact]
    public void AsQuickbaseUnique_NamesTheFieldAndItsId()
    {
        var error = new ValidationException(new Dictionary<string, string[]>
        {
            ["12"] = ["'Key' must be unique — this value is already in use."]
        });

        var result = QuickbaseRecordValue.AsQuickbaseUnique(error, [Field("Text", "Key", 12)]);

        Assert.NotNull(result);
        Assert.Matches("^Quickbase reported an error: Trying to add a non-unique value to a field marked unique; " +
            "The field named \"Key\" with field id 12 requires a unique value[.]; Error Code: 51; Transaction ID: [0-9a-f-]{36}$", result!.Message);
    }

    [Fact]
    public void AsQuickbaseUnique_OtherErrors_AreLeftAsTheyAre()
    {
        var error = new ValidationException(new Dictionary<string, string[]> { ["6"] = ["'Name' is required."] });

        Assert.Null(QuickbaseRecordValue.AsQuickbaseUnique(error, [Field("Text", "Name", 6)]));
    }
}
