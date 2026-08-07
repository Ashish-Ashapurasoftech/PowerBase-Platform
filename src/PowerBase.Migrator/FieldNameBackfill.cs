using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Domain.Constants;

namespace PowerBase.Migrator;

/// <summary>
/// One-time, idempotent data fix that regenerates AppField.Name (the stable third-party
/// identifier) from AppField.Label for every field created before the Label-driven naming
/// scheme existed. Safe to re-run: rows whose Name already matches the new "C_"/"S_" pattern
/// are skipped, so a second run is a no-op.
///
/// Run 026_appfield_backfill_label.sql first (copies the old display value from Name into Label
/// wherever Label is blank) — this only regenerates Name, it never touches Label.
/// </summary>
public static class FieldNameBackfill
{
    private sealed record FieldRow(long Id, long AppTableId, string Name, string? Label, bool IsSystem);

    public static async Task<int> RunAsync(string tenantConnectionString, string tenantLabel)
    {
        await using var connection = new SqlConnection(tenantConnectionString);
        await connection.OpenAsync();

        var rows = (await connection.QueryAsync<FieldRow>("""
            SELECT Id, AppTableId, Name, Label, IsSystem
            FROM meta.AppField
            WHERE IsDeleted = 0
              AND Name NOT LIKE 'C[_]%' ESCAPE '['
              AND Name NOT LIKE 'S[_]%' ESCAPE '['
            ORDER BY AppTableId, Id
            """)).ToList();

        if (rows.Count == 0)
        {
            Console.WriteLine($"  {tenantLabel}: nothing to backfill.");
            return 0;
        }

        // Existing Names per table, seeded from every field (including ones we're about to skip/rename),
        // so generated candidates never collide with a sibling field.
        var existingNamesByTable = (await connection.QueryAsync<(long AppTableId, string Name)>("""
            SELECT AppTableId, Name FROM meta.AppField WHERE IsDeleted = 0
            """))
            .GroupBy(r => r.AppTableId)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(r => r.Name), StringComparer.OrdinalIgnoreCase));

        var updated = 0;
        foreach (var row in rows)
        {
            var label = string.IsNullOrWhiteSpace(row.Label) ? row.Name : row.Label;
            var baseName = FieldNaming.GenerateBaseName(label, row.IsSystem);

            var existing = existingNamesByTable.TryGetValue(row.AppTableId, out var set)
                ? set
                : existingNamesByTable[row.AppTableId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var candidate = baseName;
            var suffix = 2;
            while (existing.Contains(candidate))
            {
                candidate = $"{baseName}{suffix}";
                suffix++;
            }

            existing.Remove(row.Name);
            existing.Add(candidate);

            await connection.ExecuteAsync(
                "UPDATE meta.AppField SET Name = @candidate WHERE Id = @id",
                new { candidate, id = row.Id });
            updated++;
        }

        Console.WriteLine($"  {tenantLabel}: regenerated {updated} field name(s).");
        return updated;
    }
}
