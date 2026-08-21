using System;
using System.Data.SqlClient;

class Program {
    static void Main() {
        string cs = "Server=DESKTOP-H5HE868\\SQLEXPRESS01;Database=Powerbase_Control;Trusted_Connection=True;";
        using var conn = new SqlConnection(cs);
        conn.Open();
        using var cmd = new SqlCommand("SELECT Id, DatabaseName FROM meta.Tenant", conn);
        using var reader = cmd.ExecuteReader();
        while(reader.Read()) {
            Console.WriteLine($"Tenant {reader[0]}: {reader[1]}");
        }
    }
}
