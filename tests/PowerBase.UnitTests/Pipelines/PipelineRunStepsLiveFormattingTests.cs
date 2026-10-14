using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Queries.GetPipelineRunSteps;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>While a run is still going, its finished steps must already read as the friendly history (not raw fid_N / CreatedBy keys).</summary>
public class PipelineRunStepsLiveFormattingTests
{
    private const string RawOutput = "{\"CreatedRecordPublicId\":\"a04b64d9\",\"Id\":25033,\"fid_3\":25033,\"CreatedBy\":40021}";
    private const string FormattedOutput = "{\"Output\":{\"Record ID\":25033},\"TechnicalDetails\":{\"StepId\":7}}";
    private const string FormattedInput = "{\"Header\":{},\"Input\":{},\"Metadata\":{},\"TechnicalDetails\":{}}";

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineAuditFormatter _formatter = Substitute.For<IPipelineAuditFormatter>();
    private readonly Guid _runId = Guid.NewGuid();

    private GetPipelineRunStepsQueryHandler Handler(DateTime? runCompletedOn, params PipelineStepRun[] stepRuns)
    {
        var run = new PipelineRun { Id = 10, PipelineId = 20, PublicId = _runId, TriggeredBy = 5, CompletedOn = runCompletedOn, MessageId = Guid.NewGuid() };
        _repo.GetRunByPublicIdAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _repo.GetStepsByPipelineIdAsync(20, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep> { new() { Id = 7, PipelineId = 20, RefId = "ref_3", Type = "action", Subtype = "create-record" } });
        _repo.CountStepRunsByRunIdAsync(10, Arg.Any<CancellationToken>()).Returns(stepRuns.Length);
        _repo.GetStepRunsByRunIdAsync(10, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(stepRuns.ToList());
        _formatter.FormatStepRun(Arg.Any<PipelineStep>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>())
            .Returns((FormattedInput, FormattedOutput, "Record created."));
        return new GetPipelineRunStepsQueryHandler(_repo, _formatter);
    }

    private static PipelineStepRun StepRun(string status, string? output, string? input = "{\"TableId\":\"x\"}", string subtype = "create-record") => new()
    {
        Id = 1, PipelineRunId = 10, StepId = 7, Status = status, StartedOn = DateTime.UtcNow.AddSeconds(-2),
        CompletedOn = status == "Running" ? null : DateTime.UtcNow, InputContext = input, OutputContext = output, StepSubtypeSnapshot = subtype
    };

    [Fact]
    public async Task RunningRun_FinishedStepWithRawOutput_IsReturnedFormatted()
    {
        var handler = Handler(null, StepRun("Success", RawOutput));

        var result = await handler.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        var item = result.Items.Single();
        item.OutputContext.Should().Be(FormattedOutput);
        item.InputContext.Should().Be(FormattedInput);
        item.LogMessage.Should().Be("Record created.");
        await _formatter.Received(1).InitializeAsync(20, 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletedRun_IsReturnedAsStored_WithoutFormatting()
    {
        var handler = Handler(DateTime.UtcNow, StepRun("Success", RawOutput));

        var result = await handler.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        result.Items.Single().OutputContext.Should().Be(RawOutput);
        _formatter.DidNotReceiveWithAnyArgs().FormatStepRun(default!, default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task RunningRun_StepStillRunning_IsLeftAlone()
    {
        var handler = Handler(null, StepRun("Running", null));

        var result = await handler.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        result.Items.Single().OutputContext.Should().BeNull();
        _formatter.DidNotReceiveWithAnyArgs().FormatStepRun(default!, default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task RunningRun_AlreadyFormattedStep_IsNotFormattedAgain()
    {
        var handler = Handler(null, StepRun("Success", FormattedOutput, FormattedInput));

        var result = await handler.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        result.Items.Single().OutputContext.Should().Be(FormattedOutput);
        _formatter.DidNotReceiveWithAnyArgs().FormatStepRun(default!, default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task RunningRun_LoopBatchRow_IsNotFormatted()
    {
        var handler = Handler(null, StepRun("Success", "{\"Batch\":1,\"RecordCount\":100}", "{\"Batch\":1}", "loop-batch"));

        var result = await handler.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        result.Items.Single().OutputContext.Should().Contain("\"Batch\":1");
        _formatter.DidNotReceiveWithAnyArgs().FormatStepRun(default!, default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task RunningRun_WhenTheFormatterFails_TheRawStepIsStillReturned()
    {
        var handler = Handler(null, StepRun("Success", RawOutput));
        _formatter.FormatStepRun(Arg.Any<PipelineStep>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>())
            .Throws(new InvalidOperationException("boom"));

        var result = await handler.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        result.Items.Single().OutputContext.Should().Be(RawOutput);
    }

    [Fact]
    public async Task RunningRun_WhenTheFormatterCannotBeInitialised_TheRawStepIsStillReturned()
    {
        var handler = Handler(null, StepRun("Success", RawOutput));
        _formatter.InitializeAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("boom"));

        var result = await handler.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        result.Items.Single().OutputContext.Should().Be(RawOutput);
    }

    [Fact]
    public async Task HandlerWithoutAFormatter_StillReturnsStoredValues()
    {
        var handler = Handler(null, StepRun("Success", RawOutput));
        var plain = new GetPipelineRunStepsQueryHandler(_repo);

        var result = await plain.HandleAsync(new GetPipelineRunStepsQuery(_runId, 1, 50));

        result.Items.Single().OutputContext.Should().Be(RawOutput);
    }
}
