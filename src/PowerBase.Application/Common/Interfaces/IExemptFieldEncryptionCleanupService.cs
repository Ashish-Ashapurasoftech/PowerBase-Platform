using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>Summary counts for a single tenant cleanup run.</summary>
public class CleanupExemptFieldEncryptionResult
{
    public long TenantId      { get; set; }
    public string? TenantName { get; set; }
    public int AppsScanned    { get; set; }
    public int TablesScanned  { get; set; }
    public int TablesFailed   { get; set; }
    public int FieldsFixed    { get; set; }
    public int FieldsFailed   { get; set; }
    public int RowsDecrypted  { get; set; }
    public string? Error      { get; set; }
}

/// <summary>
/// Service that scans fields whose TypeCode is now encryption-exempt
/// (Number, Date, Currency, Boolean, etc.) and if they were previously encrypted:
/// 1. Decrypts existing ciphertext rows back to plaintext.
/// 2. Alters physical VARCHAR(MAX) columns back to native SQL types.
/// 3. Resets IsEncrypted = 0 in meta.AppField.
/// </summary>
public interface IExemptFieldEncryptionCleanupService
{
    Task<CleanupExemptFieldEncryptionResult> CleanupTenantAsync(long tenantId, CancellationToken ct = default);
    Task<List<CleanupExemptFieldEncryptionResult>> CleanupAllTenantsAsync(CancellationToken ct = default);
}
