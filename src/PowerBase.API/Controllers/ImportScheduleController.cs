using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Imports;

namespace PowerBase.API.Controllers;

/// <summary>The on/off switch of an import's schedule, for the list of imports. The import's own screen keeps editing the whole schedule through the
/// definition routes; this only flips the flag. Permissions are enforced in the handler.</summary>
[ApiController]
[RequireAuth]
public sealed class ImportScheduleController(SetImportScheduleEnabledHandler setEnabled) : ControllerBase
{
    public sealed record SetScheduleEnabledRequest(bool Enabled);

    [HttpPut("import-definitions/{id:guid}/schedule/enabled")]
    public async Task<IActionResult> SetEnabled(Guid id, [FromBody] SetScheduleEnabledRequest request, CancellationToken ct) =>
        Ok(new { data = await setEnabled.HandleAsync(id, request.Enabled, ct) });
}
