using PowerBase.Application.Common.Interfaces;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

public partial class PipelineEngine
{
    public const string CsvImportSubtype = "csv-import";
    private static readonly TimeSpan CsvImportMaxDirectWait = TimeSpan.FromHours(6);

    /// <summary>
    /// Import with CSV (the Quickbase step): fetches the CSV from its URL (or a file from an earlier step), reads it with the step's separator,
    /// quote character and header settings, and imports the mapped columns into the table through the table's own import (the one its Import
    /// screen runs), matching records on the merge field. It waits until the import has finished. The run started for each part is kept in a
    /// receipt, so a resumed or retried PowerFlow follows those runs instead of importing again. Output: records_processed / added / updated / unchanged.
    /// </summary>
    private async Task<string> ExecuteCsvImportAsync(PipelineStep step, string payloadJson, List<PipelineStep> allSteps,
        Dictionary<string, object> contextDict, string executionPath, PipelineStepRun stepRun, IServiceProvider services, CancellationToken ct)
    {
        var config = CsvImportDefinition.Read(step.ConfigJson);
        config.Validate();
        var appId = config.AppGuid;
        var tableId = config.TableGuid;
        var messageId = contextDict.TryGetValue("_MessageId", out var messageObj) && messageObj is Guid id ? id : Guid.Empty;
        var header = CsvImportDefinition.HeaderNames(EvaluateTokens(config.HeaderRow, payloadJson, executionPath, allSteps), config.Delimiter);

        string ReceiptPath(int part) => executionPath + ImportWithCsvExecutor.RunReceiptSuffix + (part == 0 ? "" : "#" + part);
        async Task<CsvImportReceipt?> ReadReceiptAsync(int part) => messageId == Guid.Empty ? null
            : CsvImportReceipt.TryRead(await _idempotencyRepo.GetByExecutionKeyAsync(messageId, step.PublicId, ComputeSha256Hash(ReceiptPath(part)), null, ct));
        async Task<IReadOnlyList<CsvImportPart>> ReadPartsAsync(CsvImportRows rows) =>
            await Task.FromResult(CsvImportFile.BuildParts(config, header, config.FieldsByColumn(header), rows.Rows));
        async Task<CsvImportRows> ReadRowsAsync()
        {
            var source = EvaluateTokens(UploadFileStepContract.NormalizeFileSourceToken(config.CsvUrl!), payloadJson, executionPath, allSteps)?.Trim() ?? "";
            var prepared = CsvImportFile.Prepare(await FetchCsvTextAsync(source, services, ct), config);
            var fields = await CsvImportFieldsAsync(tableId, services, ct);
            return prepared with { Rows = CsvImportFile.Clean(prepared.Rows, config.FieldsByColumn(header), fields), Fields = fields };
        }

        var first = await ReadReceiptAsync(0);
        IReadOnlyList<CsvImportPart>? parts = null;
        int totalRows;
        int partCount;
        if (first is null)
        {
            var rows = await ReadRowsAsync();
            totalRows = rows.TotalRows;
            stepRun.InputContext = SerializeAndSanitizeAudit(new { AppId = appId, TableId = tableId, config.MergeField, Header = header, Rows = totalRows });
            if (rows.Rows.Count == 0) return CsvImportCounts.None.Output();
            if (config.FieldsByColumn(header).Count == 0)
                throw CsvImportCounts.None.Fail("Invalid input; While importing by merge field you must provide input columns list.; Error Code: 2");
            CsvImportFile.CheckBeforeImport(config, config.FieldsByColumn(header), rows.Rows, rows.Fields ?? new Dictionary<int, CsvImportFile.FieldInfo>());
            parts = await ReadPartsAsync(rows);
            if (parts.Count == 0) return CsvImportCounts.None.Output();
            partCount = parts.Count;
        }
        else
        {
            totalRows = first.TotalRows;
            partCount = Math.Max(1, first.Parts);
        }

        var statuses = new List<ImportRunStatusResponse>();
        var runIds = new List<Guid>();
        for (var part = 0; part < partCount; part++)
        {
            var receipt = part == 0 && first is not null ? first : await ReadReceiptAsync(part);
            if (receipt is null)
            {
                // Resumed after an earlier part: the CSV is read again for the parts not started yet.
                parts ??= await ReadPartsAsync(await ReadRowsAsync());
                if (part >= parts.Count) break;
                var started = await StartCsvImportPartAsync(parts[part], tableId, services, CountsOf(statuses), ct);
                receipt = new CsvImportReceipt(started.RunId, totalRows, partCount);
                if (messageId != Guid.Empty)
                {
                    try
                    {
                        await _idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                        {
                            MessageId = messageId,
                            StepPublicId = step.PublicId,
                            ExecutionPathHash = ComputeSha256Hash(ReceiptPath(part)),
                            ExecutionPath = ReceiptPath(part),
                            OutputJson = receipt.Write()
                        }, null, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not keep the started import run {ImportRunId} for step {StepId}.", receipt.ImportRunId, step.Id);
                    }
                }
            }
            runIds.Add(receipt.ImportRunId);
            stepRun.InputContext = SerializeAndSanitizeAudit(new { AppId = appId, TableId = tableId, config.MergeField, Header = header, Rows = totalRows, ImportRunIds = runIds });
            var status = await WaitForCsvImportAsync(appId, receipt.ImportRunId, messageId, services, CountsOf(statuses), ct);
            statuses.Add(status);
            // A part that did not go in cleanly ends the step: what was imported stays, as in Quickbase.
            if (!IsCsvImportClean(status))
            {
                string? reason = null;
                try
                {
                    var detail = await services.GetRequiredService<GetImportRunHandler>().HandleAsync(receipt.ImportRunId, ct);
                    reason = detail.Issues?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Message))?.Message;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not read the issues of import run {ImportRunId}.", receipt.ImportRunId);
                }
                if (reason?.StartsWith("No record has the Record ID#", StringComparison.OrdinalIgnoreCase) == true)
                    throw CountsOf(statuses).Fail("Unknown error; Some record IDs specified were not found in the table.; Error Code: 1");
                return CsvImportOutcome(statuses, totalRows, reason);
            }
        }
        return CsvImportOutcome(statuses, totalRows);
    }

    /// <summary>The table's fields by field id (type, label, unique); empty when they cannot be read, so the import itself then reports any problem.</summary>
    private static async Task<IReadOnlyDictionary<int, CsvImportFile.FieldInfo>> CsvImportFieldsAsync(Guid tableId, IServiceProvider services, CancellationToken ct)
    {
        var tableRepo = services.GetService<IAppTableRepository>();
        var fieldRepo = services.GetService<IAppFieldRepository>();
        if (tableRepo is null || fieldRepo is null) return new Dictionary<int, CsvImportFile.FieldInfo>();
        try
        {
            var table = await tableRepo.GetByPublicIdAsync(tableId, ct);
            return (await fieldRepo.ListByTableAsync(table.Id, ct)).Where(f => f.Fid is not null)
                .GroupBy(f => f.Fid!.Value).ToDictionary(g => g.Key, g => new CsvImportFile.FieldInfo(g.First().TypeCode, g.First().Label ?? g.First().Name, g.First().IsUnique));
        }
        catch (NotFoundException)
        {
            return new Dictionary<int, CsvImportFile.FieldInfo>();
        }
    }

    private static CsvImportCounts CountsOf(IReadOnlyList<ImportRunStatusResponse> statuses) => new(
        statuses.Sum(s => s.Inserted + s.Updated + s.Unchanged), statuses.Sum(s => s.Inserted), statuses.Sum(s => s.Updated), statuses.Sum(s => s.Unchanged));

    private static bool IsCsvImportClean(ImportRunStatusResponse status) =>
        string.Equals(status.Status, ImportRunStatus.Success, StringComparison.OrdinalIgnoreCase) && status.Errored == 0 && status.Skipped == 0;

    private static async Task<ImportRunStart> StartCsvImportPartAsync(CsvImportPart part, Guid tableId, IServiceProvider services, CsvImportCounts sofar, CancellationToken ct)
    {
        try
        {
            using var content = new MemoryStream(part.Csv);
            var fileId = await services.GetRequiredService<IPowerFlowImportActions>().UploadAsync(tableId, content, "import.csv", part.Csv.Length, ct);
            var baseUrl = services.GetService<IConfiguration>()?["Frontend:BaseUrl"];
            return await services.GetRequiredService<StartQuickImportHandler>()
                .HandleAsync(tableId, new QuickImportRequest(part.Import, fileId, null), baseUrl, ct);
        }
        catch (Exception ex) when (ex is UnauthorizedActionException or UnauthorizedAccessException)
        {
            throw sofar.Fail("You don't have permission to perform this action. Ensure your user has the required access permissions.");
        }
        catch (NotFoundException)
        {
            throw sofar.Fail("The table was not found. It may have been deleted: open the step and select a table.");
        }
        catch (DomainException ex) when (ex is not PipelineNonRetryableException)
        {
            throw sofar.Fail(ex.Message);
        }
    }

    private async Task<ImportRunStatusResponse> WaitForCsvImportAsync(Guid appId, Guid runId, Guid messageId, IServiceProvider services,
        CsvImportCounts sofar, CancellationToken ct)
    {
        var statusHandler = services.GetRequiredService<GetImportRunStatusHandler>();
        var waited = TimeSpan.Zero;
        while (true)
        {
            ImportRunStatusResponse status;
            try { status = await statusHandler.HandleAsync(appId, runId, ct); }
            catch (DomainException ex) when (ex is not PipelineNonRetryableException)
            {
                throw sofar.Fail($"The import could not be read: {ex.Message}");
            }
            if (ImportWithCsvExecutor.IsFinished(status.Status)) return status;
            if (messageId == Guid.Empty)
            {
                if (waited >= CsvImportMaxDirectWait) throw sofar.Fail("The import did not finish in time.");
                await Task.Delay(ImportWithCsvExecutor.PollInterval, ct);
                waited += ImportWithCsvExecutor.PollInterval;
                continue;
            }
            if (await TryWaitOutPauseInlineAsync(DateTime.UtcNow.Add(ImportWithCsvExecutor.PollInterval), ct)) continue;
            throw new PipelineWaitException(DateTime.UtcNow.Add(ImportWithCsvExecutor.ResumeInterval));
        }
    }

    /// <summary>The step's output when every row went in; otherwise it fails (what was imported stays, as in Quickbase), naming the problem and the counts.</summary>
    public static string CsvImportOutcome(IReadOnlyList<ImportRunStatusResponse> statuses, int totalRows, string? firstIssue = null)
    {
        var counts = CountsOf(statuses);
        foreach (var status in statuses)
        {
            var failed = string.Equals(status.Status, ImportRunStatus.Failed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.Status, ImportRunStatus.Cancelled, StringComparison.OrdinalIgnoreCase);
            var detail = string.IsNullOrWhiteSpace(status.ErrorDetail) ? "" : status.ErrorDetail.Trim();
            if (failed && (detail.Length > 0 || string.IsNullOrWhiteSpace(firstIssue)))
                throw counts.Fail(detail.Length > 0 ? detail : $"The import {status.Status.ToLowerInvariant()} (import run {status.RunId}).");
            if (status.Errored > 0 || status.Skipped > 0)
                throw counts.Fail($"Invalid input: {(string.IsNullOrWhiteSpace(firstIssue) ? "" : firstIssue.Trim().TrimEnd('.') + ". ")}{status.Errored + status.Skipped} of {status.RowsRead} records could not be imported (import run {status.RunId}).{(detail.Length > 0 ? " " + detail : "")}");
        }
        if (totalRows > CsvImportFile.MaxRecords)
            throw counts.Fail($"The CSV has {totalRows} records: only the first {CsvImportFile.MaxRecords} were imported. Import with CSV supports a maximum of {CsvImportFile.MaxRecords} records in a single transfer.");
        return counts.Output();
    }

    /// <summary>The CSV's text: fetched from an http(s) URL (with the error Quickbase gives when that fails), or read from a file an earlier step
    /// holds. Read as UTF-8.</summary>
    private async Task<string> FetchCsvTextAsync(string source, IServiceProvider services, CancellationToken ct)
    {
        if (source.Length == 0) throw CsvImportCounts.None.Fail("Validation error: {'csv_url': 'required field'}");
        var isUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (isUrl && ImportWithCsvExecutor.ReadFileValue(source) is null)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClientFactory.CreateClient().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw CsvImportCounts.None.Fail($"Encountered exception when fetching data from the provided URL: {ex.Message}");
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw CsvImportCounts.None.Fail($"Encountered exception when fetching data from the provided URL: Remote service reported an error: HTTP status code {(int)response.StatusCode} for URL {uri}");
                if (response.Content.Headers.ContentLength > ImportFileFormats.MaxFileBytes)
                    throw CsvImportCounts.None.Fail($"The CSV file is larger than {ImportFileFormats.MaxFileBytes / (1024 * 1024)} MB.");
                return Decode(await response.Content.ReadAsByteArrayAsync(ct));
            }
        }

        var reader = new ImportWithCsvExecutor(services, _fileStorageService,
            (u, token) => _httpClientFactory.CreateClient().GetAsync(u, HttpCompletionOption.ResponseHeadersRead, token));
        try
        {
            await using var file = await reader.OpenFileAsync(new ImportWithCsvDefinition { CsvFile = source }, source, ct);
            using var memory = new MemoryStream();
            await file.Content.CopyToAsync(memory, ct);
            return Decode(memory.ToArray());
        }
        catch (PipelineNonRetryableException ex) when (ex.Message.Contains("is empty", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        catch (PipelineNonRetryableException ex)
        {
            throw CsvImportCounts.None.Fail($"Encountered exception when fetching data from the provided URL: {ex.Message}");
        }
    }

    private static string Decode(byte[] bytes) => new UTF8Encoding(false, false).GetString(bytes);
}

/// <summary>The import run an Import with CSV step started for one part, how many rows its CSV held, and how many parts the step runs.</summary>
internal sealed record CsvImportReceipt(Guid ImportRunId, int TotalRows, int Parts = 1)
{
    public string Write() => System.Text.Json.JsonSerializer.Serialize(this);

    public static CsvImportReceipt? TryRead(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var read = System.Text.Json.JsonSerializer.Deserialize<CsvImportReceipt>(json);
            return read is null || read.ImportRunId == Guid.Empty ? null : read;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
