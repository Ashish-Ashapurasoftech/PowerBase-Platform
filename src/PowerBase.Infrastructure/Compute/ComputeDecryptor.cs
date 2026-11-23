using System.Data;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Decrypts what a stored field reads when its app is encrypted (plan.DecryptAppId), with the same keys and the same code the API uses
/// (FieldEncryptionContext over the DEK-caching IEncryptionService). The computed value is then stored PLAIN in c_ — that is the point:
/// a sort / filter in SQL over every record instead of decrypting and sorting in memory. One instance per tenant scope; the app's key is
/// resolved once per app.
/// </summary>
public sealed class ComputeDecryptor
{
    private readonly IEncryptionService _encryption;
    private readonly IQueryContext _context;
    private readonly Dictionary<long, FieldEncryptionContext> _byApp = new();

    public ComputeDecryptor(IEncryptionService encryption, IQueryContext context)
    {
        _encryption = encryption;
        _context = context;
    }

    private async Task<FieldEncryptionContext> ContextAsync(IDbConnection connection, long appId, CancellationToken ct)
    {
        if (_byApp.TryGetValue(appId, out var cached)) return cached;
        var resolved = await FieldEncryptionContext.ResolveAsync(connection, appId, _context.TenantId, _encryption, ct: ct);
        _byApp[appId] = resolved;
        return resolved;
    }

    /// <summary>Decrypts, in place, the columns of <paramref name="fields"/> that hold ciphertext in these rows (keyed by column name).
    /// A value that does not decrypt (a legacy plain row) is left as it is, exactly as the API's reads do.</summary>
    public async Task DecryptRowsAsync(IDbConnection connection, long appId, IReadOnlyList<IDictionary<string, object?>> rows,
        IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var enc = await ContextAsync(connection, appId, ct);
        await enc.DecryptRowsAsync(rows, fields, ct);
    }

    /// <summary>One value of the app (a lookup's source); null and empty stay as they are.</summary>
    public async Task<string?> DecryptValueAsync(IDbConnection connection, long appId, string? value, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var enc = await ContextAsync(connection, appId, ct);
        return await enc.DecryptValueAsync(value, ct);
    }
}
