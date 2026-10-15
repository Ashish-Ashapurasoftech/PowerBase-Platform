using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.UnitTests.Common;

public class BulkParametersTests
{
    private static readonly (string Name, object? Value)[] Values =
    {
        ("text", "abc"), ("longText", new string('x', 5000)), ("int64", 42L), ("int32", 7), ("guid", Guid.NewGuid()),
        ("date", new DateTime(2026, 10, 15, 7, 13, 0)), ("money", 1.5m), ("flag", true), ("missing", null), ("bytes", new byte[] { 1, 2, 3 })
    };

    /// <summary>Dapper's command identity (its constructor is internal), as Dapper passes it when it builds a command.</summary>
    private static SqlMapper.Identity DapperIdentity()
    {
        var constructor = typeof(SqlMapper.Identity)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .OrderBy(c => c.GetParameters().Length).First(c => c.GetParameters().FirstOrDefault()?.ParameterType == typeof(string));
        var arguments = constructor.GetParameters().Select(p =>
            p.ParameterType == typeof(string) ? "SELECT 1"
            : p.ParameterType == typeof(System.Data.CommandType?) || p.ParameterType == typeof(System.Data.CommandType) ? System.Data.CommandType.Text
            : p.ParameterType == typeof(System.Data.IDbConnection) ? new SqlConnection()
            : p.ParameterType == typeof(Type) ? typeof(object)
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
            : (object?)null).ToArray();
        return (SqlMapper.Identity)constructor.Invoke(arguments);
    }

    [Fact]
    public void EveryParameter_IsTypedSizedAndValuedLikeDapperDynamicParameters()
    {
        using var dapperCommand = new SqlCommand();
        using var bulkCommand = new SqlCommand();
        var dynamic = new DynamicParameters();
        var bulk = new BulkParameters();
        foreach (var (name, value) in Values) { dynamic.Add(name, value); bulk.Add(name, value); }
        dynamic.Add("hash", new byte[32], System.Data.DbType.Binary, size: 32);
        bulk.Add("hash", new byte[32], System.Data.DbType.Binary, size: 32);

        ((SqlMapper.IDynamicParameters)dynamic).AddParameters(dapperCommand, DapperIdentity());
        ((SqlMapper.IDynamicParameters)bulk).AddParameters(bulkCommand, DapperIdentity());

        Assert.Equal(dapperCommand.Parameters.Count, bulkCommand.Parameters.Count);
        for (var i = 0; i < dapperCommand.Parameters.Count; i++)
        {
            var expected = dapperCommand.Parameters[i];
            var actual = bulkCommand.Parameters[i];
            Assert.Equal(expected.ParameterName, actual.ParameterName);
            Assert.Equal(expected.DbType, actual.DbType);
            Assert.Equal(expected.SqlDbType, actual.SqlDbType);
            Assert.Equal(expected.Size, actual.Size);
            Assert.Equal(expected.Value, actual.Value);
        }
    }

    [Fact]
    public void ThousandsOfParameters_AreAddedOnceEach_WithoutSearchingTheOnesBefore()
    {
        // 20,000 parameters: with a lookup per parameter that is 200 million name comparisons; added straight, a few milliseconds.
        using var command = new SqlCommand();
        var bulk = new BulkParameters();
        for (var i = 0; i < 20_000; i++) bulk.Add("p" + i, "value " + i);
        var timer = Stopwatch.StartNew();

        ((SqlMapper.IDynamicParameters)bulk).AddParameters(command, null!);

        Assert.Equal(20_000, command.Parameters.Count);
        Assert.InRange(timer.ElapsedMilliseconds, 0, 2_000);
    }
}
