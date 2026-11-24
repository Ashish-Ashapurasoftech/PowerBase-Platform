using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using PowerBase.Infrastructure.Compute;
using Xunit;

namespace PowerBase.IntegrationTests.Compute;

/// <summary>One worker per field across replicas: the second taker is refused at once, and the field is free again when the first lets go.</summary>
public class FieldWorkLockTests : IClassFixture<ComputeSqlFixture>
{
    private readonly ComputeSqlFixture _db;
    public FieldWorkLockTests(ComputeSqlFixture db) => _db = db;

    private Task<SqlConnection> Open(CancellationToken ct) => Task.FromResult(new SqlConnection(_db.ConnectionString));

    [Fact]
    public async Task A_field_is_worked_on_by_one_worker_at_a_time()
    {
        var first = await FieldWorkLock.TryAcquireAsync(Open, 4242, CancellationToken.None);
        first.Should().NotBeNull();

        (await FieldWorkLock.TryAcquireAsync(Open, 4242, CancellationToken.None)).Should().BeNull("another worker holds the field");
        await using (var other = await FieldWorkLock.TryAcquireAsync(Open, 4343, CancellationToken.None))
            other.Should().NotBeNull("a different field is free");

        await first!.DisposeAsync();
        await using var again = await FieldWorkLock.TryAcquireAsync(Open, 4242, CancellationToken.None);
        again.Should().NotBeNull("released when the first worker let go, even with pooled connections");
    }
}
