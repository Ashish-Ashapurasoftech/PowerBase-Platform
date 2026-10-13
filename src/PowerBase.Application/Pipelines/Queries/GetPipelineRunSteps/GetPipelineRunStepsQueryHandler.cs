using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineRunSteps;

public class GetPipelineRunStepsQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IPipelineAuditFormatter? _auditFormatter;

    public GetPipelineRunStepsQueryHandler(IPipelineRepository pipelineRepo, IPipelineAuditFormatter? auditFormatter = null)
    {
        _pipelineRepo = pipelineRepo;
        _auditFormatter = auditFormatter;
    }

    /// <summary>The engine turns a step's raw input/output into the readable history format once the whole run is over (doing it per
    /// step would slow every record of a big loop). A run that is still going would therefore show raw keys (fid_3, CreatedBy…),
    /// so its finished steps are formatted here, when they are read. Formatted contexts are recognised by their shape and left alone.</summary>
    private static bool IsFormatted(string? contextJson, string requiredKey)
    {
        if (string.IsNullOrWhiteSpace(contextJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(contextJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(requiredKey, out _)
                && doc.RootElement.TryGetProperty("TechnicalDetails", out _);
        }
        catch (JsonException) { return false; }
    }

    private async Task<bool> TryInitializeFormatterAsync(PowerBase.Domain.Entities.PipelineRun run, CancellationToken ct)
    {
        if (_auditFormatter == null) return false;
        try { await _auditFormatter.InitializeAsync(run.PipelineId, run.TriggeredBy, ct); return true; }
        catch (Exception) { return false; }
    }

    public async Task<PipelineStepRunsResultDto> HandleAsync(GetPipelineRunStepsQuery query, CancellationToken ct = default)
    {
        var run = await _pipelineRepo.GetRunByPublicIdAsync(query.RunPublicId, ct);
        if (run == null)
            throw new NotFoundException("PowerFlowRun", query.RunPublicId);

        var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(run.PipelineId, ct);
        var stepsMap = steps.ToDictionary(s => s.Id);

        var page = query.Page > 0 ? query.Page : 1;
        var pageSize = query.PageSize > 0 ? query.PageSize : 50;
        if (pageSize > 200) pageSize = 200; // Enforce MaxPageSize limit

        var totalCount = await _pipelineRepo.CountStepRunsByRunIdAsync(run.Id, ct);
        var stepRuns = await _pipelineRepo.GetStepRunsByRunIdAsync(run.Id, page, pageSize, ct);

        // Only while the run is going: a finished run's steps were formatted by the engine.
        var formatLive = run.CompletedOn == null && stepRuns.Any(sr => sr.Status != "Running" && !IsFormatted(sr.OutputContext, "Output"))
            && await TryInitializeFormatterAsync(run, ct);

        var items = stepRuns.Select(sr => {
            var stepExists = stepsMap.TryGetValue(sr.StepId, out var step);
            if (formatLive && stepExists && sr.Status != "Running" && sr.StepSubtypeSnapshot != "loop-batch"
                && !IsFormatted(sr.OutputContext, "Output"))
            {
                try
                {
                    var (formattedInput, formattedOutput, logMessage) = _auditFormatter!.FormatStepRun(
                        step!, sr.InputContext, sr.OutputContext, sr.Status, run.MessageId?.ToString() ?? string.Empty, sr.StartedOn, sr.CompletedOn);
                    sr.InputContext = IsFormatted(sr.InputContext, "Input") ? sr.InputContext : formattedInput;
                    sr.OutputContext = formattedOutput;
                    if (!string.IsNullOrEmpty(logMessage)) sr.LogMessage = logMessage;
                }
                catch (Exception)
                {
                    // Keep the raw values: a formatting problem must never hide the step.
                }
            }
            return new PipelineStepRunDto(
                sr.Id,
                sr.StepPublicIdSnapshot.HasValue && sr.StepPublicIdSnapshot.Value != Guid.Empty ? sr.StepPublicIdSnapshot.Value : stepExists ? step!.PublicId : Guid.Empty,
                !string.IsNullOrWhiteSpace(sr.StepRefIdSnapshot) ? sr.StepRefIdSnapshot : stepExists ? step!.RefId : string.Empty,
                !string.IsNullOrWhiteSpace(sr.StepLabelSnapshot) ? sr.StepLabelSnapshot : stepExists ? (!string.IsNullOrWhiteSpace(step!.Label) ? step!.Label : step!.RefId) : string.Empty,
                !string.IsNullOrWhiteSpace(sr.StepTypeSnapshot) ? sr.StepTypeSnapshot : stepExists ? step!.Type : string.Empty,
                !string.IsNullOrWhiteSpace(sr.StepSubtypeSnapshot) ? sr.StepSubtypeSnapshot : stepExists ? (step!.Subtype ?? string.Empty) : string.Empty,
                sr.Status,
                sr.StartedOn,
                sr.CompletedOn,
                sr.InputContext,
                sr.OutputContext,
                sr.LogMessage,
                sr.PipelineRunAttemptId,
                sr.ExecutionPath,
                sr.SequenceNumber,
                sr.TransactionOutcome,
                sr.ErrorType,
                sr.CompletedOn.HasValue ? Math.Max(0L, (long)(sr.CompletedOn.Value - sr.StartedOn).TotalMilliseconds) : null
            );
        }).ToList();

        return new PipelineStepRunsResultDto(items, totalCount, page, pageSize);
    }
}
