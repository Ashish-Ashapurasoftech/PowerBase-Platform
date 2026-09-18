using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Records;

namespace PowerBase.UnitTests.Pipelines;

public class CallablePipelineTests
{
    private static PipelineEngine CreateEngine(IPipelineRepository repository, IPipelineExecutionQueue queue)
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IPipelineExecutionQueue)).Returns(queue);
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(3L);
        return new PipelineEngine(repository, Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), context, Substitute.For<IServiceScopeFactory>(), services,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    private static Task<string> ExecuteStep(PipelineEngine engine, PipelineStep step, Dictionary<string, object> context, Dictionary<string, object> outputs)
    {
        context["steps"] = outputs;
        return (Task<string>)typeof(PipelineEngine).GetMethod("ExecuteStepAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, new object[] { step, JsonSerializer.Serialize(context), context, new List<PipelineStep> { step }, outputs,
                1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "root/call", CancellationToken.None })!;
    }

    [Fact]
    public async Task EngineDispatchesReferenceAndChildExposesArgumentsToDownstreamSteps()
    {
        var repository = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var target = new Pipeline { Id = 20, PublicId = Guid.NewGuid(), CreatedBy = 7, IsActive = true };
        var trigger = new PipelineStep { Id = 21, PipelineId = 20, RefId = "child", Type = "trigger", Subtype = "pipeline-called", IsValidated = true, ConfigJson = """{"callDefinition":"f(value)"}""" };
        repository.FindCallablePipelinesAsync(7, "f(value)", Arg.Any<CancellationToken>()).Returns(new[] { target });
        repository.GetStepsByPipelineIdAsync(20, Arg.Any<CancellationToken>()).Returns(new[] { trigger });
        PipelineExecutionTask? child = null;
        queue.When(x => x.QueueTask(Arg.Any<PipelineExecutionTask>())).Do(x => child = x.Arg<PipelineExecutionTask>());
        var engine = CreateEngine(repository, queue);
        var caller = new PipelineStep { Id = 11, PublicId = Guid.NewGuid(), PipelineId = 10, RefId = "caller", Type = "action", Subtype = "call-another-pipeline",
            ConfigJson = """{"callDefinition":"f(value)","arguments":{"value":"{{steps.source.data}}"}}""" };
        var context = new Dictionary<string, object> { ["_CreatedBy"] = 7L, ["_Depth"] = 1, ["_MessageId"] = Guid.NewGuid() };
        var outputs = new Dictionary<string, object> { ["source"] = new { data = new { flag = false, count = 0 } } };
        (await ExecuteStep(engine, caller, context, outputs)).Should().Contain("Queued");
        child.Should().NotBeNull();
        var childOutputs = new Dictionary<string, object>();
        var childContext = new Dictionary<string, object> { ["trigger"] = JsonSerializer.Deserialize<JsonElement>(child!.TriggerPayloadJson) };
        await ExecuteStep(engine, trigger, childContext, childOutputs);
        // Serializing after trigger execution also catches JsonElements referencing disposed documents.
        var downstreamPayload = JsonSerializer.Serialize(childContext);
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.Instance | BindingFlags.NonPublic)!;
        evaluate.Invoke(engine, new object?[] { "{{steps.child.value.count}}", downstreamPayload, null, null }).Should().Be("0");
        childOutputs.Should().ContainKey("child");
        trigger.ConfigJson = """{"callDefinition":"f(renamed)"}""";
        FluentActions.Invoking(() => evaluate.Invoke(engine, new object?[] {
            "{{steps.child.value.count}}", downstreamPayload, null, new List<PipelineStep> { trigger }
        })).Should().Throw<TargetInvocationException>().WithInnerException<PipelineStepException>();
    }

    [Theory]
    [InlineData("myFunction(contact_id, created_at)", 2)]
    [InlineData("ping()", 0)]
    [InlineData("call_2(value_1)", 1)]
    public void ParsesDocumentedSignatures(string input, int count) =>
        CallablePipelineDefinition.Parse(input).Arguments.Should().HaveCount(count);

    [Theory]
    [InlineData("")]
    [InlineData("1call(x)")]
    [InlineData("call(x,x)")]
    [InlineData("call(x,)")]
    [InlineData("call(x y)")]
    [InlineData("call({{trigger.x}})")]
    public void RejectsInvalidSignatures(string input) =>
        FluentActions.Invoking(() => CallablePipelineDefinition.Parse(input)).Should().Throw<PipelineNonRetryableException>();

    [Fact]
    public void ExplicitFalsyAndNullValuesAreDifferentFromMissingArguments()
    {
        var config = """{"callDefinition":"f(zero,flag,empty,nothing)","arguments":{"zero":0,"flag":false,"empty":"","nothing":null}}""";
        CallablePipelineDefinition.ValidateConfig(config, true).Arguments.Should().HaveCount(4);
        FluentActions.Invoking(() => CallablePipelineDefinition.ValidateConfig("""{"callDefinition":"f(x)","arguments":{}}""", true))
            .Should().Throw<PipelineNonRetryableException>();
    }

    [Fact]
    public async Task DispatchCarriesTypedValuesAndUsesStableIdentityPerLoopIteration()
    {
        var repository = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var target = new Pipeline { Id = 20, PublicId = Guid.NewGuid(), CreatedBy = 7, IsActive = true };
        var trigger = new PipelineStep { Id = 21, PipelineId = 20, RefId = "child", Type = "trigger", Subtype = "pipeline-called", IsValidated = true, ConfigJson = """{"callDefinition":"f(value)"}""" };
        repository.FindCallablePipelinesAsync(7, "f(value)", Arg.Any<CancellationToken>()).Returns(new[] { target });
        repository.GetStepsByPipelineIdAsync(20, Arg.Any<CancellationToken>()).Returns(new[] { trigger });
        var tasks = new List<PipelineExecutionTask>();
        queue.When(x => x.QueueTask(Arg.Any<PipelineExecutionTask>())).Do(x => tasks.Add(x.Arg<PipelineExecutionTask>()));
        var dispatcher = new CallablePipelineDispatcher(repository, queue);
        var parent = Guid.NewGuid();
        var step = Guid.NewGuid();
        var args = new Dictionary<string, object?> { ["value"] = new { flag = false, count = 0 } };
        for (var index = 0; index < 3; index++)
            await dispatcher.DispatchAsync(3, 7, 10, parent, step, index < 2 ? "root/loop/0/call" : "root/loop/1/call",
                parent.ToString(), 2, CallablePipelineDefinition.Parse("f(value)"), args, default, "[1,10]");
        tasks[0].MessageId.Should().Be(tasks[1].MessageId);
        tasks[2].MessageId.Should().NotBe(tasks[0].MessageId);
        tasks[0].TenantId.Should().Be(3);
        tasks[0].TriggeredBy.Should().Be(7);
        tasks[0].Depth.Should().Be(3);
        tasks[0].PipelineChain.Should().Be("[1,10,20]");
        using var payload = JsonDocument.Parse(tasks[0].TriggerPayloadJson);
        payload.RootElement.GetProperty("Arguments").GetProperty("value").GetProperty("flag").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("TriggerStepRefId").GetString().Should().Be("child");
    }

    [Fact]
    public async Task RefusesMissingOrWrongOwnerTargetWithoutEnqueueing()
    {
        var repository = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var dispatcher = new CallablePipelineDispatcher(repository, queue);
        async Task Call() => await dispatcher.DispatchAsync(3, 7, 10, Guid.NewGuid(), Guid.NewGuid(), "root/call", null, 1,
            CallablePipelineDefinition.Parse("ping()"), new Dictionary<string, object?>(), default);
        repository.FindCallablePipelinesAsync(7, "ping()", Arg.Any<CancellationToken>()).Returns(Array.Empty<Pipeline>());
        await FluentActions.Awaiting(Call).Should().ThrowAsync<PipelineNonRetryableException>();
        repository.FindCallablePipelinesAsync(7, "ping()", Arg.Any<CancellationToken>())
            .Returns(new[] { new Pipeline { Id = 20, CreatedBy = 8, IsActive = true } });
        await FluentActions.Awaiting(Call).Should().ThrowAsync<PipelineNonRetryableException>();
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
}
