using System.Reflection;
using System.Text.Json;
using PowerBase.Application.Pipelines;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.UnitTests.Pipelines;

public class RecordLimitConfigTests
{
    [Theory]
    [InlineData(typeof(PipelineStepValidator))]
    [InlineData(typeof(PipelineRepository))]
    [InlineData(typeof(PipelineTriggerInterceptor))]
    public void LegacyTextLimitIsPreservedInValidationSubscriptionAndExecution(Type owner)
    {
        var configType = owner.GetNestedType("NewEventStepConfig", BindingFlags.NonPublic)!;
        var config = JsonSerializer.Deserialize("{\"limitRecords\":true,\"maxRecords\":\"10\"}", configType,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(10, configType.GetProperty("MaxRecords")!.GetValue(config));
        Assert.True((bool)configType.GetProperty("LimitRecords")!.GetValue(config)!);
    }
}
