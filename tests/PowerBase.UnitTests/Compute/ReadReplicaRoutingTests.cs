using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Persistence;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>CreateReadOnlyAsync: the replica is used only when every switch is on and it is close enough behind; anything else = primary.</summary>
public class ReadReplicaRoutingTests
{
    private const string Primary = "Server=tcp:pb.database.windows.net;Initial Catalog=Powerbase_44;User ID=u;Password=p";

    [Fact]
    public void Built_in_replica_adds_read_only_intent_to_the_same_database()
    {
        var cs = new SqlConnectionStringBuilder(ReadReplicaRouting.BuildReadOnlyConnectionString(Primary, true, true, ReadReplicaRouting.ModeBuiltIn, null));
        cs.ApplicationIntent.Should().Be(ApplicationIntent.ReadOnly);
        cs.InitialCatalog.Should().Be("Powerbase_44");
    }

    [Fact]
    public void Named_replica_points_at_its_own_database()
    {
        var cs = new SqlConnectionStringBuilder(ReadReplicaRouting.BuildReadOnlyConnectionString(Primary, true, true, ReadReplicaRouting.ModeNamed, "Powerbase_44_ro"));
        cs.InitialCatalog.Should().Be("Powerbase_44_ro");
    }

    [Theory]
    [InlineData(false, true, ReadReplicaRouting.ModeBuiltIn, "x")]   // global switch off
    [InlineData(true, false, ReadReplicaRouting.ModeBuiltIn, "x")]   // tenant switch off
    [InlineData(true, true, ReadReplicaRouting.ModeNone, "x")]       // no replica
    [InlineData(true, true, ReadReplicaRouting.ModeNamed, null)]     // named replica without a database name
    public void Any_switch_off_or_no_replica_means_primary(bool global, bool tenant, byte mode, string? named) =>
        ReadReplicaRouting.BuildReadOnlyConnectionString(Primary, global, tenant, mode, named).Should().BeNull();

    [Theory]
    [InlineData(1.0, ReadReplicaIntent.Interactive, false)]
    [InlineData(6.0, ReadReplicaIntent.Interactive, true)]
    [InlineData(6.0, ReadReplicaIntent.Background, false)]
    [InlineData(61.0, ReadReplicaIntent.Background, true)]
    [InlineData(null, ReadReplicaIntent.Background, true)]           // unknown lag = primary
    public void Lag_decides_replica_or_primary(double? lag, ReadReplicaIntent intent, bool primary) =>
        ReadReplicaRouting.IsTooFarBehind(lag, intent, new ReadReplicaOptions()).Should().Be(primary);

    [Fact]
    public async Task With_the_switch_off_a_read_only_connection_is_the_primary()
    {
        var resolver = Substitute.For<ITenantConnectionResolver>();
        resolver.ResolveAsync(44, Arg.Any<CancellationToken>()).Returns(Primary);
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(44);
        var lag = Substitute.For<IReplicaLagMonitor>();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();   // ReadReplica:Enabled missing = off

        var factory = new TenantConnectionFactory(resolver, context, lag, config);
        await using var conn = await factory.CreateReadOnlyAsync(ReadReplicaIntent.Background);

        new SqlConnectionStringBuilder(conn.ConnectionString).ApplicationIntent.Should().Be(ApplicationIntent.ReadWrite);
        await resolver.DidNotReceive().ResolveReadOnlyAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await lag.DidNotReceive().GetLagSecondsAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_replica_that_is_behind_falls_back_to_the_primary()
    {
        var replica = ReadReplicaRouting.BuildReadOnlyConnectionString(Primary, true, true, ReadReplicaRouting.ModeBuiltIn, null)!;
        var resolver = Substitute.For<ITenantConnectionResolver>();
        resolver.ResolveAsync(44, Arg.Any<CancellationToken>()).Returns(Primary);
        resolver.ResolveReadOnlyAsync(44, Arg.Any<CancellationToken>()).Returns(replica);
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(44);
        var lag = Substitute.For<IReplicaLagMonitor>();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["ReadReplica:Enabled"] = "true" }).Build();
        var factory = new TenantConnectionFactory(resolver, context, lag, config);

        lag.GetLagSecondsAsync(44, replica, Arg.Any<CancellationToken>()).Returns(30.0);
        await using (var behind = await factory.CreateReadOnlyAsync(ReadReplicaIntent.Interactive))
            new SqlConnectionStringBuilder(behind.ConnectionString).ApplicationIntent.Should().Be(ApplicationIntent.ReadWrite);

        lag.GetLagSecondsAsync(44, replica, Arg.Any<CancellationToken>()).Returns(0.5);
        await using (var fresh = await factory.CreateReadOnlyAsync(ReadReplicaIntent.Interactive))
            new SqlConnectionStringBuilder(fresh.ConnectionString).ApplicationIntent.Should().Be(ApplicationIntent.ReadOnly);
    }
}
