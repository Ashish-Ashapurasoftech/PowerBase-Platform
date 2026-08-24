using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Resolves the wrapped DEK for an App and provides transparent per-field
/// encrypt / decrypt helpers.  All callers (List, GetById, Create, Update)
/// go through this single class so that encryption logic is never duplicated.
///
/// Rules:
///  - If the App is NOT encrypted (IsEncrypted = 0), both methods are no-ops
///    — existing apps are completely unaffected.
///  - If the App IS encrypted, ALL non-system data fields are automatically
///    encrypted on write and decrypted on read.  No per-field flag needed.
///  - System fields (Id, CreatedOn, ModifiedOn, CreatedBy, ModifiedBy) are
///    never encrypted so queries / ordering continue to work normally.
/// </summary>
public sealed class FieldEncryptionContext
{
    private readonly IEncryptionService _encryptionService;
    private readonly long _tenantId;
    private readonly long _appId;
    private string? _wrappedDek;   // null → encryption not active

    private FieldEncryptionContext(IEncryptionService svc, long tenantId, long appId, string? wrappedDek)
    {
        _encryptionService = svc;
        _tenantId = tenantId;
        _appId = appId;
        _wrappedDek = wrappedDek;
    }

    /// <summary>Whether encryption is active (DEK is available).</summary>
    public bool IsActive => !string.IsNullOrEmpty(_wrappedDek);

    /// <summary>Whether the App is marked as globally encrypted.</summary>
    public bool IsAppEncrypted { get; private set; }

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the DEK from the tenant DB using an already-open connection.
    /// Returns a context whose <see cref="IsActive"/> is false when the App
    /// is not encrypted — all subsequent calls become no-ops.
    /// </summary>
    public static async Task<FieldEncryptionContext> ResolveAsync(
        IDbConnection tenantConnection,
        long appId,
        long tenantId,
        IEncryptionService encryptionService,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT SecurityOptions, IsEncrypted
            FROM meta.App
            WHERE Id = @appId AND IsDeleted = 0
            """;

        string? wrappedDek = null;
        bool isAppEncrypted = false;
        try
        {
            var row = await tenantConnection.QuerySingleOrDefaultAsync(
                new CommandDefinition(sql, new { appId }, transaction, cancellationToken: ct));

            if (row != null)
            {
                isAppEncrypted = row.IsEncrypted;
                string secOpts = row.SecurityOptions;
                if (!string.IsNullOrEmpty(secOpts) && secOpts.TrimStart().StartsWith("{"))
                {
                    var settings = System.Text.Json.JsonSerializer.Deserialize<
                        PowerBase.Domain.ValueObjects.AppSecurityOptionsSettings>(
                            secOpts,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    wrappedDek = settings?.WrappedDek;
                }
            }
        }
        catch (Exception ex)
        {
            // Any failure → treat app as non-encrypted; no crash
            Console.WriteLine($"Error in ResolveAsync: {ex}");
        }

        var ctx = new FieldEncryptionContext(encryptionService, tenantId, appId, wrappedDek);
        ctx.IsAppEncrypted = isAppEncrypted;
        return ctx;
    }

    /// <summary>
    /// Lazily generates and persists a Master Encryption Key (DEK) for the App if it doesn't have one.
    /// Used when a user enables field-level encryption on an unencrypted app.
    /// </summary>
    public async Task EnsureDekAsync(IDbConnection tenantConnection, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (IsActive) return;

        _wrappedDek = await _encryptionService.GenerateAndWrapDekAsync(_tenantId, _appId, ct);
        
        var settings = new PowerBase.Domain.ValueObjects.AppSecurityOptionsSettings { WrappedDek = _wrappedDek };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        
        const string sql = "UPDATE meta.App SET SecurityOptions = @json WHERE Id = @appId";
        await tenantConnection.ExecuteAsync(new CommandDefinition(sql, new { json, appId = _appId }, transaction, cancellationToken: ct));
    }

    // ------------------------------------------------------------------
    // Helpers: which fields to encrypt
    // ------------------------------------------------------------------

    /// <summary>
    /// If App is globally encrypted, encrypt ALL non-system fields.
    /// Otherwise, encrypt ONLY non-system fields explicitly marked as IsEncrypted = true.
    /// </summary>
    private IEnumerable<AppField> FieldsToEncrypt(IEnumerable<AppField> fields)
        => fields.Where(f => f.Fid.HasValue && !f.IsSystem && (this.IsAppEncrypted || f.IsEncrypted));

    // ------------------------------------------------------------------
    // Encrypt helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Encrypts plaintext values for ALL non-system fields when the App is
    /// encrypted.  Returns a new dictionary — the original is never mutated.
    /// If encryption is not active, returns the same reference unchanged.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, object?>> EncryptValuesAsync(
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> values,
        CancellationToken ct = default)
    {
        if (!IsActive) return values;

        var toEncrypt = FieldsToEncrypt(fields).ToList();
        if (toEncrypt.Count == 0) return values;

        var copy = new Dictionary<long, object?>(values);
        foreach (var f in toEncrypt)
        {
            var key = (long)f.Fid!.Value;
            if (copy.TryGetValue(key, out var raw) && raw is string plainText && !string.IsNullOrEmpty(plainText))
                copy[key] = await _encryptionService.EncryptDataAsync(plainText, _wrappedDek!, _tenantId, _appId, ct);
        }
        return copy;
    }

    // ------------------------------------------------------------------
    // Decrypt helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Decrypts ALL non-system field values inside a row dictionary
    /// (keyed by physical column name).  Mutates the dictionary in-place.
    /// Any decryption failure is swallowed so legacy plaintext rows in a
    /// newly-encrypted app are returned as-is rather than crashing.
    /// </summary>
    public async Task DecryptRowAsync(
        IDictionary<string, object?> row,
        IEnumerable<AppField> fields,
        CancellationToken ct = default)
    {
        if (!IsActive) return;

        foreach (var f in FieldsToEncrypt(fields))
        {
            var col = PhysicalNaming.ColumnName(f.Fid!.Value);
            if (row.TryGetValue(col, out var val) && val is string cipher && !string.IsNullOrEmpty(cipher))
            {
                try { row[col] = await _encryptionService.DecryptDataAsync(cipher, _wrappedDek!, _tenantId, _appId, ct); }
                catch (Exception ex) 
                { 
                    Console.WriteLine($"[DECRYPT ERROR] Col {col}: {ex.Message}");
                    /* leave value as-is — may be a legacy plaintext row */ 
                }
            }
        }
    }

    /// <summary>Decrypts a single string value. Swallows failures and returns the original.</summary>
    public async Task<string> DecryptValueAsync(string value, CancellationToken ct = default)
    {
        if (!IsActive || string.IsNullOrEmpty(value)) return value;
        try { return await _encryptionService.DecryptDataAsync(value, _wrappedDek!, _tenantId, _appId, ct); }
        catch { return value; }
    }

    /// <summary>Convenience: decrypts many rows returned from a list query.</summary>
    public async Task DecryptRowsAsync(
        IEnumerable<IDictionary<string, object?>> rows,
        IReadOnlyList<AppField> fields,
        CancellationToken ct = default)
    {
        if (!IsActive) return;
        var toDecrypt = FieldsToEncrypt(fields).ToList();
        if (toDecrypt.Count == 0) return;

        foreach (var row in rows)
            await DecryptRowAsync(row, toDecrypt, ct);
    }
}
