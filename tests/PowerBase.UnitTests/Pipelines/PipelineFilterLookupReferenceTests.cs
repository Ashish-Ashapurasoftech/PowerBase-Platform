using FluentAssertions;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineFilterLookupReferenceTests
{
    [Fact]
    public void GetTypeCategory_LookupOfAReference_IsNumber() =>
        PipelineFilterEvaluator.GetTypeCategory(new AppField { TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"Reference\",\"resultTypeCode\":\"Reference\"}" }).Should().Be("NUMBER");

    [Fact]
    public void GetTypeCategory_LookupOfText_StaysText() =>
        PipelineFilterEvaluator.GetTypeCategory(new AppField { TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"Text\",\"resultTypeCode\":\"Text\"}" }).Should().Be("TEXT");
}
