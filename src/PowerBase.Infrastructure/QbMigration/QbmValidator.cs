using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>The data validation gates, run after a table is loaded.
/// Gate 3: reads QuickBase again and compares every value of every record (or a sample of chunks) with what PowerBase
/// actually holds, decrypted. Gate 7: every encrypted column really holds ciphertext that decrypts with the app's key.
/// Gate 4: every reference names an existing parent record. QuickBase must not change while this runs (POC: frozen app;
/// later: after the final delta, during the freeze), otherwise legitimate edits show up as differences.</summary>
public sealed class QbmValidator(IQuickBaseClient client, ITenantConnectionFactory connections, IEncryptionService encryption, QbmPowerBaseReader pb)
{
    private const int MaxIssuesPerTable = 500;

    public sealed record TableResult(int Records, int Compared, int Mismatches, int Missing, int Extra, int NotEncrypted, IReadOnlyList<QbIssue> Issues)
    {
        public bool Gate3Passed => Mismatches == 0 && Missing == 0 && Extra == 0;
        public bool Gate7Passed => NotEncrypted == 0;
    }

    /// <param name="chunks">Done chunks of the table; with <paramref name="sample"/> below 1 an even spread of them is checked.</param>
    public async Task<TableResult> CompareTableAsync(QbmTableContext ctx, IReadOnlyList<QbRange> chunks, double sample, CancellationToken ct = default)
    {
        var picked = Pick(chunks, sample);
        var issues = new List<QbIssue>();
        int records = 0, compared = 0, mismatches = 0, missing = 0, extra = 0, notEncrypted = 0;

        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var appEncrypted = await conn.QuerySingleAsync<bool>(new CommandDefinition(
            "SELECT IsEncrypted FROM meta.App WHERE Id = @appId AND IsDeleted = 0", new { appId = ctx.PbTable.AppId }, cancellationToken: ct));
        var enc = await FieldEncryptionContext.ResolveAsync(conn, ctx.PbTable.AppId, ctx.TenantId, encryption, null, ct);

        var loaded = ctx.Mappings.Where(m => m.Action == QbFieldAction.Load).ToList();
        var fieldsByFid = ctx.PbFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Fid!.Value);
        var columns = loaded.Select(m => PhysicalNaming.ColumnName(m.PbFid!.Value)).Distinct().ToList();
        var reader = new QbChunkReader(client);
        var lookups = new QbmLookupLoader(pb);

        foreach (var range in picked)
        {
            var qbRows = await reader.ReadAsync(ctx.QbTableId, ctx.SelectFids, range, ct);
            var transformer = new QbValueTransformer(ctx.Mappings, await lookups.LoadAsync(ctx, qbRows, ct), ctx.Options, ctx.ReferencesByKey.Keys.ToList());
            var expected = qbRows.Select(r => transformer.Transform(r, [])).ToDictionary(r => r.RecordId);

            var select = string.Join(", ", new[] { "Id", "CreatedOn", "CreatedBy", "ModifiedOn", "ModifiedBy" }.Concat(columns));
            var actual = (await conn.QueryAsync(new CommandDefinition(
                    $"SELECT {select} FROM {PhysicalNaming.FullTableName(ctx.PbTable.Id)} WHERE Id >= @from AND Id < @to AND IsDeleted = 0",
                    new { from = range.From, to = range.To }, cancellationToken: ct)))
                .Cast<IDictionary<string, object?>>().ToDictionary(r => (long)r["Id"]!);
            records += expected.Count;

            foreach (var id in expected.Keys.Except(actual.Keys))
            {
                missing++;
                Add(issues, new("error", "QBM_GATE3_MISSING_RECORD", "In QuickBase but not in PowerBase.", id));
            }
            foreach (var id in actual.Keys.Except(expected.Keys))
            {
                extra++;
                Add(issues, new("error", "QBM_GATE3_EXTRA_RECORD", "In PowerBase but not in QuickBase (deleted in QuickBase since the load?).", id));
            }

            foreach (var (id, exp) in expected)
            {
                if (!actual.TryGetValue(id, out var row)) continue;
                compared++;
                var differs = false;

                differs |= Check(issues, id, null, "Date Created", exp.CreatedOn, row["CreatedOn"], hide: false);
                differs |= exp.ModifiedOn is not null && Check(issues, id, null, "Date Modified", exp.ModifiedOn, row["ModifiedOn"], hide: false);
                differs |= Check(issues, id, null, "Record Owner", exp.CreatedBy, row["CreatedBy"], hide: false);

                foreach (var m in loaded)
                {
                    var fid = m.PbFid!.Value;
                    var stored = row[PhysicalNaming.ColumnName(fid)];
                    var field = fieldsByFid[fid];
                    var ciphered = FieldEncryptionContext.StoresCiphertext(field, appEncrypted);
                    if (ciphered && stored is string cipher && cipher.Length > 0)
                    {
                        var plain = await TryDecryptAsync(cipher, enc, ctx, ct);
                        if (plain is null)
                        {
                            notEncrypted++;
                            Add(issues, new("error", "QBM_GATE7_NOT_ENCRYPTED", $"'{m.Qb.Label}' is stored without valid encryption.", id, m.Qb.Id));
                            differs = true;
                            continue;
                        }
                        stored = plain;
                    }
                    differs |= Check(issues, id, m.Qb.Id, m.Qb.Label, exp.Values.GetValueOrDefault(fid), stored, hide: ciphered);
                }
                if (differs) mismatches++;
            }
        }

        return new TableResult(records, compared, mismatches, missing, extra, notEncrypted, issues);
    }

    /// <summary>Gate 4: references in this table that name no existing parent record (Record ID matched, or already translated from a key).</summary>
    public async Task<IReadOnlyList<QbIssue>> OrphanReferencesAsync(long appId, long childTableId, CancellationToken ct = default)
    {
        var issues = new List<QbIssue>();
        await using var conn = await connections.CreateAsync(ct);
        foreach (var r in (await pb.ReferencesAsync(appId, ct)).Where(r => r.ChildTableId == childTableId))
        {
            var col = PhysicalNaming.ColumnName(r.ReferenceFid);
            var orphans = (await conn.QueryAsync<(long Id, long Ref)>(new CommandDefinition($"""
                SELECT TOP 50 c.Id, c.{col} FROM {PhysicalNaming.FullTableName(childTableId)} c
                WHERE c.IsDeleted = 0 AND c.{col} IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM {PhysicalNaming.FullTableName(r.ParentTableId)} p WHERE p.Id = c.{col} AND p.IsDeleted = 0)
                ORDER BY c.Id
                """, cancellationToken: ct))).ToList();
            issues.AddRange(orphans.Select(o => new QbIssue("error", "QBM_GATE4_ORPHAN_REFERENCE",
                $"Field {r.ReferenceFid} points to record {o.Ref}, which doesn't exist in the parent table (also orphaned in QuickBase?).", o.Id, r.ReferenceFid)));
        }
        return issues;
    }

    /// <summary>Plain text where ciphertext belongs fails here: it isn't Base64 or doesn't decrypt with the app's key.</summary>
    private async Task<string?> TryDecryptAsync(string cipher, FieldEncryptionContext enc, QbmTableContext ctx, CancellationToken ct)
    {
        if (!enc.IsActive) return null;
        try
        {
            Convert.FromBase64String(cipher);
            return await encryption.DecryptDataAsync(cipher, enc.WrappedDek!, ctx.TenantId, ctx.PbTable.AppId, ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Check(List<QbIssue> issues, long id, int? qbFid, string label, object? expected, object? actual, bool hide)
    {
        var e = QbValueCanonical.Of(expected);
        var a = QbValueCanonical.Of(actual);
        if (string.Equals(e, a, StringComparison.Ordinal) || QbValueCanonical.SameNumber(e, a)) return false;
        // Encrypted values are never written into the issue log.
        var detail = hide ? "values differ (encrypted field, values not shown)" : $"QuickBase {Show(e)}, PowerBase {Show(a)}";
        Add(issues, new("error", "QBM_GATE3_MISMATCH", $"'{label}': {detail}.", id, qbFid));
        return true;
    }

    private static string Show(string? v) => v is null ? "(empty)" : v.Length <= 120 ? $"'{v}'" : $"'{v[..120]}…'";

    private static void Add(List<QbIssue> issues, QbIssue issue)
    {
        if (issues.Count < MaxIssuesPerTable) issues.Add(issue);
        else if (issues.Count == MaxIssuesPerTable) issues.Add(new("error", "QBM_GATE_TOO_MANY", $"More than {MaxIssuesPerTable} problems; the rest are not listed."));
    }

    /// <summary>All chunks at sample 1; otherwise every n-th, always including the first and last.</summary>
    public static IReadOnlyList<QbRange> Pick(IReadOnlyList<QbRange> chunks, double sample)
    {
        if (sample >= 1 || chunks.Count <= 2) return chunks;
        if (sample <= 0) throw new ArgumentOutOfRangeException(nameof(sample));
        var step = Math.Max(1, (int)Math.Round(1 / sample, MidpointRounding.AwayFromZero));
        return chunks.Where((_, i) => i % step == 0 || i == chunks.Count - 1).ToList();
    }
}
