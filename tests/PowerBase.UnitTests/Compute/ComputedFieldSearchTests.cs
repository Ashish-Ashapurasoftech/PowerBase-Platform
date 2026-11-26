using FluentAssertions;
using PowerBase.Domain.Constants;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Decision Q4: every Lookup / Summary / Formula field is always Searchable and Filterable; nothing else is touched.</summary>
public class ComputedFieldSearchTests
{
    [Theory]
    [InlineData("Lookup")]
    [InlineData("Summary")]
    [InlineData("Formula")]
    [InlineData("Formula_Number")]
    [InlineData("Formula_Text")]
    [InlineData("Formula_Date")]
    [InlineData("Formula_Url")]
    public void Apply_ComputedField_IsForcedOn(string typeCode)
    {
        ComputedFieldSearch.IsLocked(typeCode).Should().BeTrue();
        ComputedFieldSearch.Apply(false, typeCode).Should().BeTrue("an admin unticking it is ignored");
        ComputedFieldSearch.Apply(true, typeCode).Should().BeTrue();
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Number")]
    [InlineData("Reference")]
    [InlineData("ReportLink")]
    [InlineData("ActionButton")]
    public void Apply_OtherField_KeepsWhatWasAsked(string typeCode)
    {
        ComputedFieldSearch.IsLocked(typeCode).Should().BeFalse();
        ComputedFieldSearch.Apply(false, typeCode).Should().BeFalse();
        ComputedFieldSearch.Apply(true, typeCode).Should().BeTrue();
    }

    [Fact]
    public void Apply_HiddenHelperField_KeepsWhatWasAsked() =>
        ComputedFieldSearch.Apply(false, "Lookup", isHidden: true).Should().BeFalse("hidden helper fields stay out of every list");

    [Fact]
    public void Apply_SystemField_KeepsWhatWasAsked() =>
        ComputedFieldSearch.Apply(false, "Formula_Number", isSystem: true).Should().BeFalse();

    [Fact]
    public void Apply_UnknownType_KeepsWhatWasAsked() =>
        ComputedFieldSearch.Apply(false, null).Should().BeFalse();
}
