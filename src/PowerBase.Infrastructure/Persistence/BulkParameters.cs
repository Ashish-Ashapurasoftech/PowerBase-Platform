using System.Data;
using Dapper;

namespace PowerBase.Infrastructure.Persistence;

/// <summary>
/// Parameters for a statement that carries hundreds or thousands of them (multi-row INSERTs, long value lists).
/// Dapper's <see cref="DynamicParameters"/> looks every parameter up among the ones already on the command before adding it —
/// a linear, case-insensitive search — so a statement with n parameters costs n² name comparisons: a 100-row history insert
/// (1,800 parameters) spent ~240 ms building its command and 6 ms running in SQL Server. These are added straight to the
/// command, typed exactly as Dapper types them (same DbType, strings sized 4,000 so the plan is reused).
/// Names must be unique within the statement.
/// </summary>
public sealed class BulkParameters : SqlMapper.IDynamicParameters
{
    private readonly List<(string Name, object? Value, DbType? DbType, int? Size)> _parameters = new();

    public void Add(string name, object? value, DbType? dbType = null, int? size = null) => _parameters.Add((name, value, dbType, size));

    public int Count => _parameters.Count;

    void SqlMapper.IDynamicParameters.AddParameters(IDbCommand command, SqlMapper.Identity identity)
    {
        foreach (var (name, value, declaredType, size) in _parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Direction = ParameterDirection.Input;
            SqlMapper.ITypeHandler? handler = null;
            var dbType = declaredType;
            if (dbType == null && value != null)
            {
#pragma warning disable CS0618 // Dapper's own type lookup, so these parameters are typed like DynamicParameters types them.
                dbType = SqlMapper.LookupDbType(value.GetType(), name, true, out handler);
#pragma warning restore CS0618
            }
            if (handler != null)
            {
                handler.SetValue(parameter, value!);
            }
            else
            {
                if (dbType.HasValue) parameter.DbType = dbType.Value;
                parameter.Value = value ?? DBNull.Value;
                if (size.HasValue) parameter.Size = size.Value;
                else if (value is string text && text.Length <= DbString.DefaultLength) parameter.Size = DbString.DefaultLength;
            }
            command.Parameters.Add(parameter);
        }
    }
}
