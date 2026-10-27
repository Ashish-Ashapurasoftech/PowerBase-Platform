using FluentAssertions;
using PowerBase.Application.Fields.TypeConversion;

namespace PowerBase.UnitTests.Fields.TypeConversion;

public class SelectChoiceBuilderTests
{
    [Fact]
    public void Add_DistinctNonBlankInFirstSeenOrder()
    {
        var builder = new SelectChoiceBuilder();
        foreach (var v in new[] { "b", "a", "b", null, " ", "c" }) builder.Add(v);

        builder.Choices.Should().Equal("b", "a", "c");
        builder.Overflowed.Should().BeFalse();
    }

    [Fact]
    public void Add_PastCap_CountsOverflow()
    {
        var builder = new SelectChoiceBuilder();
        for (var i = 0; i < SelectChoiceBuilder.MaxChoices + 3; i++) builder.Add($"v{i}");

        builder.Choices.Should().HaveCount(SelectChoiceBuilder.MaxChoices);
        builder.OverflowCount.Should().Be(3);
    }

    [Fact]
    public void Ctor_ExistingChoicesKeptFirst()
    {
        var builder = new SelectChoiceBuilder(["x"]);
        builder.Add("y");
        builder.Add("x");

        builder.Choices.Should().Equal("x", "y");
    }
}
