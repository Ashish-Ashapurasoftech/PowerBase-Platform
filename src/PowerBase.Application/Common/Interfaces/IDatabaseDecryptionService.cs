using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>Progress / outcome of one "decrypt a whole tenant database" run. Updated while the run is going.</summary>
public class DecryptDatabaseResult
{
    [JsonIgnore] public object Sync { get; } = new();

    public string Database { get; set; } = string.Empty;
    public long TenantId { get; set; }
    public string? TenantName { get; set; }
    public bool DryRun { get; set; }

    /// <summary>Running | Completed | CompletedWithErrors | Failed</summary>
    public string State { get; set; } = "Running";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }

    public int AppsScanned { get; set; }
    public int AppsDecrypted { get; set; }
    public int TablesScanned { get; set; }
    public int FieldsFound { get; set; }
    public int FieldsDecrypted { get; set; }
    public int FieldsFailed { get; set; }
    public long RowsDecrypted { get; set; }
    /// <summary>Values that were already plain text (legacy rows) and were copied as-is.</summary>
    public long RowsAlreadyPlain { get; set; }
    public string? CurrentTable { get; set; }
    public List<string> Errors { get; } = new();

    public void Update(Action<DecryptDatabaseResult> change)
    {
        lock (Sync) change(this);
    }

    public void AddError(string message)
    {
        lock (Sync)
        {
            if (Errors.Count < 200) Errors.Add(message);
        }
    }
}

/// <summary>Remembers the latest run per database so the API can report status and refuse a second parallel run.</summary>
public sealed class DecryptionJobTracker
{
    private readonly ConcurrentDictionary<string, DecryptDatabaseResult> _jobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers <paramref name="job"/> unless a run for the same database is still going.</summary>
    public bool TryStart(DecryptDatabaseResult job)
    {
        while (true)
        {
            if (_jobs.TryGetValue(job.Database, out var existing))
            {
                if (existing.State == "Running") return false;
                if (_jobs.TryUpdate(job.Database, job, existing)) return true;
            }
            else if (_jobs.TryAdd(job.Database, job)) return true;
        }
    }

    public DecryptDatabaseResult? Get(string database) =>
        _jobs.TryGetValue(database, out var job) ? job : null;
}

/// <summary>
/// Turns every encrypted column of one tenant database back into plain text:
/// for each app that is encrypted (or each field flagged encrypted) it decrypts the ciphertext, re-types the column
/// from VARCHAR(MAX) to the field type's native SQL type, clears the IsEncrypted flags and finally the app flag.
/// Idempotent: a column that is already plain is skipped, so a failed run can simply be started again.
/// </summary>
public interface IDatabaseDecryptionService
{
    /// <summary>Looks the tenant up by its database name (meta.Tenant.DatabaseName); null when no such tenant.</summary>
    Task<(long Id, string Name)?> FindTenantAsync(string database, CancellationToken ct = default);

    /// <summary>Runs the decryption (or, with dryRun, only counts and test-decrypts a sample). Progress goes into <paramref name="job"/>.</summary>
    Task RunAsync(DecryptDatabaseResult job, int tableParallelism, CancellationToken ct = default);
}
