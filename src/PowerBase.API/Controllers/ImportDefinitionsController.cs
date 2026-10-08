using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Imports;

namespace PowerBase.API.Controllers;

/// <summary>Saved imports (Table-to-Table) and their runs. Permissions are enforced in the handlers against the
/// source and destination tables; the controller only shapes requests and responses.</summary>
[ApiController]
[RequireAuth]
public sealed class ImportDefinitionsController(
    ListImportDefinitionsHandler list,
    ListAppImportDefinitionsHandler listApp,
    GetImportDefinitionHandler get,
    SaveImportDefinitionHandler save,
    DeleteImportDefinitionHandler delete,
    DeleteImportDefinitionsHandler deleteMany,
    StartImportRunHandler start,
    ListImportRunsHandler listRuns,
    GetImportRunHandler getRun,
    GetImportFeedbackHandler getFeedback,
    ListImportHistoryHandler history,
    GetImportSourceFileHandler getSourceFile,
    CancelImportRunHandler cancel,
    ListMyImportRunsHandler listMine,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>Optional body when starting a run: the people to email for this run only, replacing those saved on the import.</summary>
    public sealed record StartImportRunRequest(List<string>? NotifyEmails, Guid? FileId = null);

    /// <summary>Every import of the app. Creating and editing keep using the table routes: the first table an import fills is where it is saved.</summary>
    [HttpGet("apps/{appId:guid}/import-definitions")]
    public async Task<IActionResult> ListForApp(Guid appId, CancellationToken ct) =>
        Ok(new { data = await listApp.HandleAsync(appId, ct) });

    /// <summary>Body of the bulk delete: the imports to delete.</summary>
    public sealed record DeleteImportsRequest(List<Guid>? Ids);

    /// <summary>Deletes several imports of the app in one call (all or nothing).</summary>
    [HttpPost("apps/{appId:guid}/import-definitions/delete")]
    public async Task<IActionResult> DeleteMany(Guid appId, [FromBody] DeleteImportsRequest request, CancellationToken ct) =>
        Ok(new { data = new { deleted = await deleteMany.HandleAsync(appId, request.Ids ?? [], ct) } });

    [HttpGet("tables/{tableId:guid}/import-definitions")]
    public async Task<IActionResult> List(Guid tableId, CancellationToken ct) =>
        Ok(new { data = await list.HandleAsync(tableId, ct) });

    [HttpPost("tables/{tableId:guid}/import-definitions")]
    public async Task<IActionResult> Create(Guid tableId, [FromBody] ImportDefinitionConfig request, CancellationToken ct) =>
        Ok(new { data = new { id = await save.HandleAsync(tableId, null, request, ct) } });

    [HttpPut("tables/{tableId:guid}/import-definitions/{id:guid}")]
    public async Task<IActionResult> Update(Guid tableId, Guid id, [FromBody] ImportDefinitionConfig request, CancellationToken ct)
    {
        await save.HandleAsync(tableId, id, request, ct);
        return NoContent();
    }

    [HttpGet("import-definitions/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(new { data = await get.HandleAsync(id, ct) });

    [HttpDelete("import-definitions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await delete.HandleAsync(id, ct);
        return NoContent();
    }

    /// <summary>Queues a run and returns at once; the run executes in the background.</summary>
    [HttpPost("import-definitions/{id:guid}/runs")]
    public async Task<IActionResult> Run(Guid id, [FromBody] StartImportRunRequest? request, CancellationToken ct) =>
        Accepted(new { data = new { runId = (await start.StartAsync(id, ImportTrigger.Manual, request?.NotifyEmails, FrontendBaseUrl(), null, null, request?.FileId, ct)).RunId } });

    [HttpGet("import-definitions/{id:guid}/runs")]
    public async Task<IActionResult> Runs(Guid id, [FromQuery] int take = 20, CancellationToken ct = default) =>
        Ok(new { data = await listRuns.HandleAsync(id, take, ct) });

    /// <summary>The CSV of every row that was not imported, with the reason. Authorised like the run itself.</summary>
    [HttpGet("import-runs/{runId:guid}/feedback")]
    public async Task<IActionResult> Feedback(Guid runId, [FromQuery] Guid? tableId, CancellationToken ct)
    {
        var file = await getFeedback.HandleAsync(runId, ct, tableId);
        return File(file.Content, "text/csv", file.FileName);
    }

    /// <summary>The file that was uploaded for the run, while it is still kept.</summary>
    [HttpGet("import-runs/{runId:guid}/source-file")]
    public async Task<IActionResult> SourceFile(Guid runId, CancellationToken ct)
    {
        var file = await getSourceFile.HandleAsync(runId, ct);
        var type = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv"
            : file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "application/octet-stream";
        return File(file.Content, type, file.FileName);
    }

    /// <summary>The app's import history: every run in a period (the last 30 days by default), a row per table, newest first.</summary>
    [HttpGet("apps/{appId:guid}/import-runs")]
    public async Task<IActionResult> History(
        Guid appId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] Guid? importId, [FromQuery] Guid? tableId,
        [FromQuery] string? status, [FromQuery] string? trigger, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(new { data = await history.HandleAsync(appId, from, to, importId, tableId, status, trigger, page, pageSize, ct) });

    /// <summary>Stops a run: at once if it has not started, otherwise after the chunk it is on. Rows already imported stay.</summary>
    [HttpPost("import-runs/{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid runId, CancellationToken ct) =>
        Accepted(new { data = new { stoppedNow = await cancel.HandleAsync(runId, ct) == ImportCancelOutcome.Cancelled } });

    /// <summary>The current user's active runs and those finished since <paramref name="completedSince"/>, so the app can tell them a run
    /// finished wherever they are. One small query for all of them.</summary>
    [HttpGet("import-runs/mine")]
    public async Task<IActionResult> Mine([FromQuery] DateTime? completedSince, CancellationToken ct) =>
        Ok(new { data = await listMine.HandleAsync(completedSince, ct) });

    [HttpGet("import-runs/{runId:guid}")]
    public async Task<IActionResult> GetRun(Guid runId, CancellationToken ct) => Ok(new { data = await getRun.HandleAsync(runId, ct) });

    /// <summary>The web address the completion email links to: the configured frontend, or the caller's Origin when that is a known
    /// frontend. Never an address an attacker could choose.</summary>
    private string? FrontendBaseUrl() => ImportLinks.TrustedBaseUrl(
        configuration["Frontend:BaseUrl"], Request.Headers.Origin.ToString(), configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []);
}
