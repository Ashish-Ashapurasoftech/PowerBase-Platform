using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

public partial class PipelineEngine
{
    /// <summary>A PowerFlow run without a message id cannot be released to wait and resumed, so it waits here, up to this long.</summary>
    private static readonly TimeSpan ImportWithCsvMaxDirectWait = TimeSpan.FromHours(6);

    /// <summary>
    /// Import with CSV: starts the selected import with the file and waits until the import has finished, like the Pause step
    /// waits — inside the job while it is short, otherwise the run is released as Waiting and resumes to check again. The
    /// started import run is kept in its own receipt, so a resume or retry follows that same run and never starts a second
    /// import (the import's client token guards the start itself). Earlier steps replay from their receipts as usual.
    /// </summary>
    private async Task<string> ExecuteImportWithCsvAsync(PipelineStep step, string payloadJson, List<PipelineStep> allSteps,
        Dictionary<string, object> contextDict, string executionPath, PipelineStepRun stepRun, IServiceProvider services, CancellationToken ct)
    {
        var config = ImportWithCsvDefinition.Read(step.ConfigJson);
        config.Validate();
        var appId = config.AppGuid;
        var templateId = config.TemplateGuid;
        var messageId = contextDict.TryGetValue("_MessageId", out var messageObj) && messageObj is Guid id ? id : Guid.Empty;
        var receiptPath = executionPath + ImportWithCsvExecutor.RunReceiptSuffix;
        var receiptHash = ComputeSha256Hash(receiptPath);
        var executor = new ImportWithCsvExecutor(services, _fileStorageService,
            (uri, token) => _httpClientFactory.CreateClient().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token));

        var savedReceipt = messageId == Guid.Empty ? null
            : await _idempotencyRepo.GetByExecutionKeyAsync(messageId, step.PublicId, receiptHash, null, ct);
        if (!ImportWithCsvExecutor.TryReadRunReceipt(savedReceipt, out var receipt))
        {
            var source = config.HasUpload ? null
                : EvaluateTokens(UploadFileStepContract.NormalizeFileSourceToken(config.FileSource!), payloadJson, executionPath, allSteps);
            var notifyEmails = string.IsNullOrWhiteSpace(config.NotifyEmails) ? null
                : EvaluateTokens(config.NotifyEmails, payloadJson, executionPath, allSteps);
            await using var file = await executor.OpenFileAsync(config, source, ct);
            var token = messageId == Guid.Empty ? null : ImportWithCsvExecutor.ClientToken(messageId, step.PublicId, executionPath);
            var baseUrl = services.GetService<IConfiguration>()?["Frontend:BaseUrl"];
            var started = await executor.StartAsync(appId, templateId, file, notifyEmails, token, baseUrl, ct);
            receipt = new ImportWithCsvRunReceipt(started.RunId, file.FileName, file.Length);
            if (messageId != Guid.Empty)
            {
                try
                {
                    await _idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                    {
                        MessageId = messageId,
                        StepPublicId = step.PublicId,
                        ExecutionPathHash = receiptHash,
                        ExecutionPath = receiptPath,
                        OutputJson = ImportWithCsvExecutor.RunReceipt(receipt)
                    }, null, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The client token still returns this same run if the step starts again.
                    _logger.LogWarning(ex, "Could not keep the started import run {ImportRunId} for step {StepId}.", receipt.ImportRunId, step.Id);
                }
            }
        }

        stepRun.InputContext = SerializeAndSanitizeAudit(new
        {
            AppId = appId,
            ImportTemplateId = templateId,
            receipt.FileName,
            receipt.FileSize,
            ImportRunId = receipt.ImportRunId
        });

        var waitedDirectly = TimeSpan.Zero;
        while (true)
        {
            var status = await executor.GetStatusAsync(appId, receipt.ImportRunId, ct);
            if (ImportWithCsvExecutor.IsFinished(status.Status))
            {
                ImportWithCsvExecutor.EnsureSucceeded(status);
                return ImportWithCsvExecutor.Output(status);
            }

            if (messageId == Guid.Empty)
            {
                if (waitedDirectly >= ImportWithCsvMaxDirectWait)
                    throw new PipelineNonRetryableException($"The import (run {receipt.ImportRunId}) has not finished after {ImportWithCsvMaxDirectWait.TotalHours:0} hours.");
                await Task.Delay(ImportWithCsvExecutor.PollInterval, ct);
                waitedDirectly += ImportWithCsvExecutor.PollInterval;
                continue;
            }
            if (await TryWaitOutPauseInlineAsync(DateTime.UtcNow.Add(ImportWithCsvExecutor.PollInterval), ct)) continue;
            throw new PipelineWaitException(DateTime.UtcNow.Add(ImportWithCsvExecutor.ResumeInterval));
        }
    }
}
