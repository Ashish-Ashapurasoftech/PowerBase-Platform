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
using Microsoft.Extensions.Configuration;

namespace PowerBase.UnitTests.Pipelines;

public class CallablePipelineTests
{
    private static PipelineEngine CreateEngine(IPipelineRepository repository, IPipelineExecutionQueue queue)
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IPipelineExecutionQueue)).Returns(queue);
        var apps = Substitute.For<IAppRepository>();
        apps.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new App { PublicId = Guid.Parse("11111111-1111-1111-1111-111111111111") });
        services.GetService(typeof(IAppRepository)).Returns(apps);
        services.GetService(typeof(IConfiguration)).Returns(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Frontend:BaseUrl"] = "https://powerbase.example" }).Build());
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
        repository.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new Pipeline {
            Id = 10, AppId = 5, Name = "Customer sync", PublicId = Guid.Parse("22222222-2222-2222-2222-222222222222") });
        var target = new Pipeline { Id = 20, PublicId = Guid.NewGuid(), CreatedBy = 7, IsActive = true };
        var trigger = new PipelineStep { Id = 21, PipelineId = 20, RefId = "child", Type = "trigger", Subtype = "pipeline-called", IsValidated = true, ConfigJson = """{"callDefinition":"f(value)"}""" };
        repository.FindCallablePipelinesAsync(7, "f(value)", Arg.Any<CancellationToken>()).Returns(new[] { target });
        repository.GetStepsByPipelineIdAsync(20, Arg.Any<CancellationToken>()).Returns(new[] { trigger });
        PipelineExecutionTask? child = null;
        queue.When(x => x.QueueTask(Arg.Any<PipelineExecutionTask>())).Do(x => child = x.Arg<PipelineExecutionTask>());
        var engine = CreateEngine(repository, queue);
        var caller = new PipelineStep { Id = 11, PublicId = Guid.NewGuid(), PipelineId = 10, RefId = "caller", Type = "action", Subtype = "call-another-pipeline",
            ConfigJson = """{"callDefinition":"f(value)","arguments":{"value":"{{steps.source.data}}"}}""" };
        var runId = Guid.NewGuid();
        var started = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        var context = new Dictionary<string, object> { ["_CreatedBy"] = 7L, ["_Depth"] = 1, ["_MessageId"] = Guid.NewGuid(),
            ["_RunPublicId"] = runId, ["_RunStartedOn"] = started };
        var outputs = new Dictionary<string, object> { ["source"] = new { data = new { flag = false, count = 0 } } };
        var callerOutput = await ExecuteStep(engine, caller, context, outputs);
        outputs[caller.RefId] = JsonSerializer.Deserialize<JsonElement>(callerOutput);
        using var callerDocument = JsonDocument.Parse(callerOutput);
        callerDocument.RootElement.GetProperty("Status").GetString().Should().Be("Ok");
        var calling = callerDocument.RootElement.GetProperty("calling_pipeline");
        calling.GetProperty("id").GetInt64().Should().Be(10);
        calling.GetProperty("name").GetString().Should().Be("Customer sync");
        calling.GetProperty("url").GetString().Should().Be("https://powerbase.example/app/11111111-1111-1111-1111-111111111111/pipelines/22222222-2222-2222-2222-222222222222");
        calling.GetProperty("run").GetString().Should().Be(runId.ToString());
        calling.GetProperty("activity_url").GetString().Should().EndWith($"?runId={runId}");
        calling.GetProperty("triggered_at").GetDateTime().Should().Be(started);
        callerDocument.RootElement.GetProperty("value").GetProperty("flag").GetBoolean().Should().BeFalse();
        child.Should().NotBeNull();
        var childOutputs = new Dictionary<string, object>();
        var childContext = new Dictionary<string, object> { ["trigger"] = JsonSerializer.Deserialize<JsonElement>(child!.TriggerPayloadJson) };
        await ExecuteStep(engine, trigger, childContext, childOutputs);
        // Serializing after trigger execution also catches JsonElements referencing disposed documents.
        var downstreamPayload = JsonSerializer.Serialize(childContext);
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (reference, payload, steps) in new[] {
            ("caller", JsonSerializer.Serialize(context), new List<PipelineStep> { caller }),
            ("child", downstreamPayload, new List<PipelineStep> { trigger }) })
        {
            evaluate.Invoke(engine, new object?[] { $"{{{{steps.{reference}.calling_pipeline.name}}}}", payload, null, steps }).Should().Be("Customer sync");
            evaluate.Invoke(engine, new object?[] { $"{{{{steps.{reference}._metadata.call_status}}}}", payload, null, steps }).Should().Be("Ok");
            evaluate.Invoke(engine, new object?[] { $"{{{{steps.{reference}.value.count}}}}", payload, null, steps }).Should().Be("0");
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewRunRetainsDatabaseGeneratedPublicId(bool queued)
    {
        var repository = Substitute.For<IPipelineRepository>();
        var publicId = Guid.NewGuid();
        PipelineRun? created = null;
        repository.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns(call => {
            created = call.Arg<PipelineRun>();
            return (publicId, 123L);
        });
        repository.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 10, IsActive = true });
        repository.GetStepsByPipelineIdAsync(10, Arg.Any<CancellationToken>()).Returns(Array.Empty<PipelineStep>());
        await CreateEngine(repository, Substitute.For<IPipelineExecutionQueue>()).ExecuteAsync(
            new PipelineExecutionTask { PipelineId = 10, TenantId = 3, TriggerEvent = "manual", TriggerPayloadJson = "{}",
                MessageId = queued ? Guid.NewGuid().ToString() : null }, default);
        created.Should().NotBeNull();
        created!.PublicId.Should().Be(publicId);
        created.Id.Should().Be(123);
    }

    [Fact]
    public void HttpMetadataIsNotShadowedByAResponseBodyMetadataProperty()
    {
        var engine = CreateEngine(Substitute.For<IPipelineRepository>(), Substitute.For<IPipelineExecutionQueue>());
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var payload = """{"steps":{"http":{"_metadata":{"status_code":999}}},"request_metadata":{"http":{"status_code":200}}}""";
        evaluate.Invoke(engine, new object?[] { "{{steps.http._metadata.status_code}}", payload, null, null }).Should().Be("200");
    }

    [Fact]
    public void RejectsArgumentsThatWouldOverwriteCallerDetails()
    {
        FluentActions.Invoking(() => CallablePipelineDefinition.ValidateConfig("""{"callDefinition":"f(calling_pipeline)"}""", false))
            .Should().Throw<PipelineNonRetryableException>().WithMessage("*reserved*");
    }

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
    public async Task MissingTargetReturnsNoDispatchAndWrongOwnerIsStillRejected()
    {
        var repository = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var dispatcher = new CallablePipelineDispatcher(repository, queue);
        async Task Call() => await dispatcher.DispatchAsync(3, 7, 10, Guid.NewGuid(), Guid.NewGuid(), "root/call", null, 1,
            CallablePipelineDefinition.Parse("ping()"), new Dictionary<string, object?>(), default);
        repository.FindCallablePipelinesAsync(7, "ping()", Arg.Any<CancellationToken>()).Returns(Array.Empty<Pipeline>());
        var missing = await dispatcher.DispatchAsync(3, 7, 10, Guid.NewGuid(), Guid.NewGuid(), "root/call", null, 1,
            CallablePipelineDefinition.Parse("ping()"), new Dictionary<string, object?>(), default);
        missing.Should().BeEmpty();
        repository.FindCallablePipelinesAsync(7, "ping()", Arg.Any<CancellationToken>())
            .Returns(new[] { new Pipeline { Id = 20, CreatedBy = 8, IsActive = true } });
        await FluentActions.Awaiting(Call).Should().ThrowAsync<PipelineNonRetryableException>();
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }

    [Theory]
    [InlineData("f(id)", """{"id":5}""")]
    [InlineData("Test(id)", """{"id":1}""")]
    [InlineData("test(id)", """{"id":1}""")]
    [InlineData("missing()", "{}")]
    public async Task UnmatchedDefinitionExposesNotFoundWithoutCallingAChild(string definition, string arguments)
    {
        var repository = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        repository.FindCallablePipelinesAsync(7, definition, Arg.Any<CancellationToken>()).Returns(Array.Empty<Pipeline>());
        var step = new PipelineStep { Id = 11, PipelineId = 10, PublicId = Guid.NewGuid(), RefId = "caller",
            Type = "action", Subtype = "call-another-pipeline",
            ConfigJson = "{\"callDefinition\":\"" + definition + "\",\"arguments\":" + arguments + "}" };
        var context = new Dictionary<string, object> { ["_CreatedBy"] = 7L, ["_MessageId"] = Guid.NewGuid() };
        var engine = CreateEngine(repository, queue);
        var outputs = new Dictionary<string, object>();
        var output = await ExecuteStep(engine, step, context, outputs);
        using var result = JsonDocument.Parse(output);
        result.RootElement.GetProperty("Status").GetString().Should().Be("Not Found");
        outputs[step.RefId] = JsonSerializer.Deserialize<JsonElement>(output);
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.Instance | BindingFlags.NonPublic)!;
        evaluate.Invoke(engine, new object?[] { "{{steps.caller._metadata.call_status}}", JsonSerializer.Serialize(context), null, new List<PipelineStep> { step } })
            .Should().Be("Not Found");
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
}
