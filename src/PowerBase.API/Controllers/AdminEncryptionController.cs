using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/encryption")]
public class AdminEncryptionController : ControllerBase
{
    private static readonly Regex DatabaseNamePattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DecryptionJobTracker _tracker;

    public AdminEncryptionController(IServiceScopeFactory scopeFactory, DecryptionJobTracker tracker)
    {
        _scopeFactory = scopeFactory;
        _tracker = tracker;
    }

    /// <summary>
    /// Decrypts ALL encrypted data of one tenant database, in place (e.g. database=Powerbase_17).
    ///
    /// For every encrypted app (and every field flagged encrypted) it decrypts the ciphertext, changes the column from
    /// VARCHAR(MAX) to the field type's native type, clears the IsEncrypted flags, and finally the app's IsEncrypted flag.
    /// Safe to run again: columns that are already plain are skipped. A column whose values will not decrypt (wrong key)
    /// is left untouched and reported.
    ///
    /// Runs in the background (it can take a long time on a big database) and returns 202 straight away;
    /// poll GET admin/encryption/decrypt-database/status?database=… for progress.
    /// Use dryRun=true first: it only counts the values and test-decrypts a sample.
    /// Make a copy of the database first (Azure: CREATE DATABASE … AS COPY OF …) — this rewrites columns.
    /// </summary>
    [HttpPost("decrypt-database")]
    [RequireSuperAdmin]
    [ProducesResponseType(typeof(ApiResponse<DecryptDatabaseResult>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DecryptDatabase(
        [FromQuery] string database,
        [FromQuery] bool dryRun = false,
        [FromQuery] int tableParallelism = 4,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(database) || !DatabaseNamePattern.IsMatch(database))
            return BadRequest(new { error = new { code = "INVALID_DATABASE", message = "database must be a database name such as Powerbase_17." } });

        tableParallelism = Math.Clamp(tableParallelism, 1, 16);

        using (var scope = _scopeFactory.CreateScope())
        {
            var tenant = await scope.ServiceProvider.GetRequiredService<IDatabaseDecryptionService>().FindTenantAsync(database, ct);
            if (tenant is null)
                return NotFound(new { error = new { code = "DATABASE_NOT_FOUND", message = $"No tenant uses database '{database}'." } });
        }

        var job = new DecryptDatabaseResult { Database = database, DryRun = dryRun };
        if (!_tracker.TryStart(job))
            return Conflict(new { error = new { code = "ALREADY_RUNNING", message = $"A decrypt run for '{database}' is still going." } });

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IDatabaseDecryptionService>();
                await service.RunAsync(job, tableParallelism, CancellationToken.None);
            }
            catch (Exception ex)
            {
                job.AddError(ex.Message);
                job.Update(j => { j.State = "Failed"; j.FinishedUtc = DateTime.UtcNow; });
                Console.WriteLine($"[DecryptDatabase] FATAL: {ex}");
            }
        });

        return Accepted(new ApiResponse<DecryptDatabaseResult>(job));
    }

    /// <summary>Progress / result of the latest decrypt run for a database (kept in memory on this instance).</summary>
    [HttpGet("decrypt-database/status")]
    [RequireSuperAdmin]
    [ProducesResponseType(typeof(ApiResponse<DecryptDatabaseResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DecryptDatabaseStatus([FromQuery] string database)
    {
        var job = string.IsNullOrWhiteSpace(database) ? null : _tracker.Get(database);
        if (job is null)
            return NotFound(new { error = new { code = "NO_RUN", message = $"No decrypt run for '{database}' on this API instance since it started." } });
        return Ok(new ApiResponse<DecryptDatabaseResult>(job));
    }
}
