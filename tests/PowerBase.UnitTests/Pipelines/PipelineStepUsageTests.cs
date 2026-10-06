using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineStepUsageTests
{
    [Theory]
    [InlineData("create-record", "{}", false)]
    [InlineData("search-records", "{}", false)]
    [InlineData("send-email-outlook", "{}", true)]
    [InlineData("make-request", "{\"url\":\"https://api.example.com\"}", true)]
    [InlineData("make-request", "{\"url\":\"http://api.vendor.example\"}", true)]
    [InlineData("make-request", "{\"url\":\"/records\"}", false)]
    [InlineData("make-request", "{\"url\":\"file:///records\"}", false)]
    [InlineData("make-request", "{\"baseUrl\":\"https://external.example\",\"url\":\"/records\"}", true)]
    [InlineData("make-request", "bad json", false)]
    public void ClassifiesInternalAndExternalSteps(string subtype, string config, bool expected)
    {
        Assert.Equal(expected, PipelineStepUsage.IsBillable(new PipelineStep { Subtype = subtype, ConfigJson = config }));
    }
}
