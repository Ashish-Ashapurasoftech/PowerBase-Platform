using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Fields.Commands.CleanupExemptFieldEncryption;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/fields")]
public class AdminFieldsController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AdminFieldsController(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// One-time data migration: finds all fields whose TypeCode is now encryption-exempt
    /// (Number, Currency, Percent, Rating, Date, DateTime, Time, Duration, DateRange,
    /// NumericRange, Boolean, User, MultiUser) but still have IsEncrypted = true.
    ///
    /// For each such field it:
    ///   1. Decrypts existing ciphertext rows back to plaintext.
    ///   2. Alters the physical column from VARCHAR(MAX) → native SQL type (DECIMAL, DATE, BIT…).
    ///   3. Sets IsEncrypted = false in meta.AppField metadata.
    ///
    /// Parameters:
    ///   - allTenants: if true, automatically loops through all active tenant databases.
    ///   - tenantId: if specified, runs for this specific tenant; otherwise runs for the current user's tenant.
    ///
    /// Safe to run multiple times (idempotent — already-fixed fields are skipped).
    /// Runs in the background and logs progress. Returns 202 immediately.
    /// </summary>
    [HttpPost("cleanup-exempt-encryption")]
    [RequireSuperAdmin]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult CleanupExemptEncryption([FromQuery] bool allTenants = false, [FromQuery] long? tenantId = null)
    {
        var queryContext = HttpContext.RequestServices
            .GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>();
        var targetTenantId = tenantId ?? queryContext.TenantId;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider
                    .GetRequiredService<CleanupExemptFieldEncryptionCommandHandler>();
                var cmd = new CleanupExemptFieldEncryptionCommand(
                    TenantId: allTenants ? null : targetTenantId,
                    AllTenants: allTenants);

                var results = await handler.HandleAsync(cmd);

                foreach (var res in results)
                {
                    Console.WriteLine(
                        $"[CleanupExemptEncryption] Tenant {res.TenantId} ({res.TenantName ?? "Current"}) complete: " +
                        $"Apps={res.AppsScanned}, Tables={res.TablesScanned} " +
                        $"({res.TablesFailed} failed), " +
                        $"Fields fixed={res.FieldsFixed} ({res.FieldsFailed} failed), " +
                        $"Rows decrypted={res.RowsDecrypted}" +
                        (string.IsNullOrEmpty(res.Error) ? "" : $" | Error: {res.Error}"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CleanupExemptEncryption] FATAL: {ex}");
            }
        });

        var message = allTenants
            ? "Cleanup job started for ALL active tenants in background. Watch server logs for progress."
            : $"Cleanup job started for Tenant {targetTenantId} in background. Watch server logs for progress.";

        return Accepted(new ApiResponse<string>(message));
    }
}
