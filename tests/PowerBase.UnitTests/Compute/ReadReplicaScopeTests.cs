using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>
/// The read replica is opt-in per read: only a read made inside a <see cref="ReadReplicaScope"/> asks for a read-only connection. Saves,
/// metadata and every read outside a scope stay on the primary exactly as before.
/// </summary>
public class ReadReplicaScopeTests
{
    // Nothing listens on port 1: opening it fails at once with a SqlException that is NOT "column missing" (so no primary retry).
    private static SqlConnection Unreachable() => new("Server=tcp:127.0.0.1,1;Initial Catalog=x;User ID=u;Password=p;Connect Timeout=1;Encrypt=False");

    [Fact]
    public void Without_a_scope_there_is_no_lane() => ReadReplicaScope.Current.Should().BeNull();

    [Fact]
    public void A_scope_sets_its_lane_and_leaving_it_restores_the_previous_one()
    {
        using (ReadReplicaScope.Begin(ReadReplicaLane.Interactive))
        {
            ReadReplicaScope.Current.Should().Be(ReadReplicaLane.Interactive);
            using (ReadReplicaScope.Begin(ReadReplicaLane.Background))
                ReadReplicaScope.Current.Should().Be(ReadReplicaLane.Background);
            ReadReplicaScope.Current.Should().Be(ReadReplicaLane.Interactive);
        }
        ReadReplicaScope.Current.Should().BeNull();
    }

    [Fact]
    public void Disposing_a_scope_twice_does_not_undo_an_outer_scope()
    {
        using var outer = ReadReplicaScope.Begin(ReadReplicaLane.Interactive);
        var inner = ReadReplicaScope.Begin(ReadReplicaLane.Background);
        inner.Dispose();
        inner.Dispose();
        ReadReplicaScope.Current.Should().Be(ReadReplicaLane.Interactive);
    }

    [Fact]
    public async Task The_lane_follows_the_work_across_awaits_but_not_into_other_flows()
    {
        using (ReadReplicaScope.Begin(ReadReplicaLane.Background))
        {
            await Task.Delay(1);
            ReadReplicaScope.Current.Should().Be(ReadReplicaLane.Background);
        }
        await Task.Delay(1);
        ReadReplicaScope.Current.Should().BeNull();

        // A flow started before the scope never sees it.
        var before = Task.Run(async () => { await Task.Delay(50); return ReadReplicaScope.Current; });
        using (ReadReplicaScope.Begin(ReadReplicaLane.Interactive)) { }
        (await before).Should().BeNull();
    }

    private static (RecordRepository Repo, ITenantConnectionFactory Factory) Repository()
    {
        var factory = Substitute.For<ITenantConnectionFactory>();
        factory.CreateAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Unreachable()));
        factory.CreateReadOnlyAsync(Arg.Any<ReadReplicaIntent>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Unreachable()));
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(44);
        var repo = new RecordRepository(factory, context, Substitute.For<IMessagePublisher>(), Substitute.For<IEncryptionService>(),
            Substitute.For<IControlConnectionFactory>());
        return (repo, factory);
    }

    private static readonly AppTable Table = new() { Id = 7, AppId = 1, Name = "T" };

    [Fact]
    public async Task A_count_outside_a_scope_reads_the_primary_only()
    {
        var (repo, factory) = Repository();

        await FluentActions.Awaiting(() => repo.CountAsync(Table, new List<AppField>())).Should().ThrowAsync<SqlException>();

        await factory.Received().CreateAsync(Arg.Any<CancellationToken>());
        await factory.DidNotReceive().CreateReadOnlyAsync(Arg.Any<ReadReplicaIntent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ReadReplicaLane.Interactive, ReadReplicaIntent.Interactive)]
    [InlineData(ReadReplicaLane.Background, ReadReplicaIntent.Background)]
    public async Task A_count_inside_a_scope_asks_for_a_read_only_connection_with_the_lane_of_the_scope(ReadReplicaLane lane, ReadReplicaIntent intent)
    {
        var (repo, factory) = Repository();

        using (ReadReplicaScope.Begin(lane))
            await FluentActions.Awaiting(() => repo.CountAsync(Table, new List<AppField>())).Should().ThrowAsync<SqlException>();

        await factory.Received().CreateReadOnlyAsync(intent, Arg.Any<CancellationToken>());
        await factory.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Report_totals_inside_a_scope_use_the_replica_and_a_save_style_read_does_not()
    {
        var (repo, factory) = Repository();
        var fields = new List<AppField>();

        using (ReadReplicaScope.Begin(ReadReplicaLane.Interactive))
            await FluentActions.Awaiting(() => repo.AggregateColumnsAsync(Table, fields, new List<AppField> { new() { Fid = 5, TypeCode = "Number" } }))
                .Should().ThrowAsync<SqlException>();
        await factory.Received().CreateReadOnlyAsync(ReadReplicaIntent.Interactive, Arg.Any<CancellationToken>());

        // ExistsAsync backs unique / required style checks: never routed to the replica, even inside a scope.
        factory.ClearReceivedCalls();
        using (ReadReplicaScope.Begin(ReadReplicaLane.Interactive))
            await FluentActions.Awaiting(() => repo.ExistsAsync(Table, 1)).Should().ThrowAsync<SqlException>();
        await factory.DidNotReceive().CreateReadOnlyAsync(Arg.Any<ReadReplicaIntent>(), Arg.Any<CancellationToken>());
        await factory.Received().CreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_replica_that_does_not_know_a_column_yet_is_retried_on_the_primary()
    {
        // 207 / 208 on the replica = a schema change that has not arrived yet: the same read runs once more on the primary.
        var factory = Substitute.For<ITenantConnectionFactory>();
        factory.CreateReadOnlyAsync(Arg.Any<ReadReplicaIntent>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Unreachable()));
        factory.CreateAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Unreachable()));
        var calls = 0;

        var result = await ReadReplicaFallback.RunAsync(factory, ReadReplicaIntent.Interactive, _ =>
        {
            calls++;
            if (calls == 1) throw SqlExceptionFactory.With(207);
            return Task.FromResult("from primary");
        });

        result.Should().Be("from primary");
        calls.Should().Be(2);
        await factory.Received(1).CreateReadOnlyAsync(ReadReplicaIntent.Interactive, Arg.Any<CancellationToken>());
        await factory.Received(1).CreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Any_other_replica_error_is_not_hidden_by_a_retry()
    {
        var factory = Substitute.For<ITenantConnectionFactory>();
        factory.CreateReadOnlyAsync(Arg.Any<ReadReplicaIntent>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Unreachable()));

        await FluentActions.Awaiting(() => ReadReplicaFallback.RunAsync<int>(factory, ReadReplicaIntent.Background,
                _ => throw SqlExceptionFactory.With(1205)))   // deadlock victim
            .Should().ThrowAsync<SqlException>();

        await factory.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
    }
}

/// <summary>SqlException has no public constructor; this builds one with a given error number.</summary>
internal static class SqlExceptionFactory
{
    public static SqlException With(int number)
    {
        var errorCtor = typeof(SqlError).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)[0];
        var args = new List<object?>();
        foreach (var p in errorCtor.GetParameters())
        {
            args.Add(p.Name switch
            {
                "infoNumber" => number,
                "errorState" => (byte)0,
                "errorClass" => (byte)16,
                "server" => "s",
                "errorMessage" => "m",
                "procedure" => "p",
                "lineNumber" => 1,
                _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
            });
        }
        var error = (SqlError)errorCtor.Invoke(args.ToArray());
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(collection, [error]);
        var ctor = typeof(SqlException).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0];
        var exArgs = new List<object?>();
        foreach (var p in ctor.GetParameters())
            exArgs.Add(p.ParameterType == typeof(string) ? "m" : p.ParameterType == typeof(SqlErrorCollection) ? collection : p.ParameterType == typeof(Guid) ? Guid.NewGuid()
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null);
        return (SqlException)ctor.Invoke(exArgs.ToArray());
    }
}
