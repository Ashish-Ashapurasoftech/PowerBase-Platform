using Microsoft.Data.SqlClient;

namespace PowerBase.Migrator;

/// <summary>
/// Stored columns (c_{fid}) by hand, per tenant:
///   materialize --id N status                 every Lookup / Summary / Formula field with its stored-column mode and state
///   materialize --id N on    --field ID       switch a field on (the worker checks the rules, builds it, then keeps it up to date)
///   materialize --id N off   --field ID       switch it off (reads use the dynamic path; the c_ column stays until a clean-up drops it)
///   materialize --id N reset --field ID       a Failed field back to "not built", so the worker tries again
/// Which fields to switch on is a product decision (design open question Q3); nothing is switched on automatically.
/// </summary>
public static class MaterializeCommands
{
    public static async Task<int> RunAsync(string tenantConnectionString, string[] rest)
    {
        await using var conn = new SqlConnection(tenantConnectionString);
        await conn.OpenAsync();

        switch (rest)
        {
            case ["status"]:
                await StatusAsync(conn);
                return 0;
            case [var action and ("on" or "off" or "reset"), "--field", var idText] when long.TryParse(idText, out var fieldId):
                var sql = action switch
                {
                    "on" => "UPDATE meta.AppField SET MaterializeMode = 1, MaterializeState = 0 WHERE Id = @fieldId AND IsDeleted = 0 AND MaterializeMode <> 1",
                    // 3 = Excluded: switched off on purpose, so the automatic switch-on (decision Q3) leaves it off.
                    "off" => "UPDATE meta.AppField SET MaterializeMode = 3, MaterializeState = 0, MaterializeVersion = MaterializeVersion + 1 WHERE Id = @fieldId AND IsDeleted = 0",
                    _ => "UPDATE meta.AppField SET MaterializeState = 0 WHERE Id = @fieldId AND IsDeleted = 0 AND MaterializeState = 3"
                };
                await using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@fieldId", fieldId);
                    var changed = await cmd.ExecuteNonQueryAsync();
                    Console.WriteLine(changed == 1 ? $"Field {fieldId}: {action} done." : $"Field {fieldId}: nothing changed (already so, deleted, or not Failed).");
                }
                return 0;
            default:
                Console.WriteLine("Usage: materialize --id N status | on --field ID | off --field ID | reset --field ID");
                return 1;
        }
    }

    private static async Task StatusAsync(SqlConnection conn)
    {
        const string sql = """
            SELECT af.Id, af.AppTableId, af.Fid, ft.Code, af.Name, af.MaterializeMode, af.MaterializeState, af.MaterializeVersion, af.MaterializedOn,
                   (SELECT COUNT_BIG(*) FROM meta.ComputeOutbox o WHERE o.TableId = af.AppTableId) AS Waiting
            FROM meta.AppField af
            JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
            WHERE af.IsDeleted = 0 AND (ft.Code IN ('Lookup', 'Summary', 'Formula') OR ft.Code LIKE 'Formula[_]%')
              AND (af.MaterializeMode <> 0 OR af.MaterializeState <> 0)
            ORDER BY af.AppTableId, af.Fid
            """;
        string[] modes = ["off", "on", "not allowed"];
        string[] states = ["none", "building", "ready", "failed"];
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Console.WriteLine($"{"FieldId",8} {"Table",7} {"Fid",5} {"Type",-8} {"Mode",-11} {"State",-9} {"Ver",4} {"Built (UTC)",-20} {"Waiting",8}  Name");
        var any = false;
        while (await r.ReadAsync())
        {
            any = true;
            var mode = r.GetByte(5);
            var state = r.GetByte(6);
            Console.WriteLine($"{r.GetInt64(0),8} {r.GetInt64(1),7} {r.GetInt32(2),5} {r.GetString(3),-8} {(mode < 3 ? modes[mode] : mode.ToString()),-11} " +
                              $"{(state < 4 ? states[state] : state.ToString()),-9} {r.GetInt32(7),4} {(r.IsDBNull(8) ? "" : r.GetDateTime(8).ToString("yyyy-MM-dd HH:mm:ss")),-20} {r.GetInt64(9),8}  {r.GetString(4)}");
        }
        if (!any) Console.WriteLine("No field is switched on.");
    }
}
