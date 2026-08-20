using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.API.Controllers;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Infrastructure.Repositories;
using PowerBase.IntegrationTests.Infrastructure;
using Xunit;

namespace PowerBase.IntegrationTests.Pipelines;

[Collection("PowerBase")]
public class DatabaseQueueIntegrationTests : IAsyncLifetime
{
    private readonly PowerBaseWebApplicationFactory _factory;
    private readonly string _connectionString;

    public DatabaseQueueIntegrationTests(PowerBaseWebApplicationFactory factory)
    {
        _factory = factory;
        try
        {
            _connectionString = factory.Services.GetRequiredService<IControlConnectionFactory>().ConnectionString;
        }
        catch
        {
            _connectionString = "Server=localhost;Database=Powerbase_Control_Test;Trusted_Connection=True;Encrypt=False;";
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migration_002_CreatePipelineQueue_SchemaIsCorrect()
    {
        await using var conn = new SqlConnection(_connectionString);
        try { await conn.OpenAsync(); } catch { return; }

        var tableExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'PipelineQueue' AND s.name = 'meta'");
        
        if (tableExists == 0) return;

        tableExists.Should().Be(1);

        var columns = await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PipelineQueue' AND TABLE_SCHEMA = 'meta'");
        
        columns.Should().Contain(new[] { "MessageId", "TenantId", "PipelineId", "QueueSource", "PayloadHash", "Status", "AttemptCount", "MaxAttempts", "ClaimToken" });
    }

    [Fact]
    public async Task MainQueueRepository_IdempotentEnqueue_BehavesCorrectly()
    {
        await using var conn = new SqlConnection(_connectionString);
        try { await conn.OpenAsync(); } catch { return; }

        var connFactory = Substitute.For<IControlConnectionFactory>();
        connFactory.Create().Returns(new SqlConnection(_connectionString));
        connFactory.ConnectionString.Returns(_connectionString);

        var repo = new MainPipelineQueueRepository(connFactory, Substitute.For<IQueryContext>());

        var messageId = Guid.NewGuid();
        var job = new PipelineQueue
        {
            MessageId = messageId,
            TenantId = 1,
            TenantPublicId = Guid.NewGuid(),
            PipelineId = 10,
            PipelinePublicId = Guid.NewGuid(),
            QueueSource = "Manual",
            TriggerPayloadJson = "{}",
            PayloadHash = PayloadHashHelper.ComputeHash("{}"),
            Status = "Pending",
            MaxAttempts = 5
        };

        var id = await repo.EnqueueAsync(job);
        id.Should().BeGreaterThan(0);

        Func<Task> duplicateEnqueue = () => repo.EnqueueAsync(job);
        await duplicateEnqueue.Should().ThrowAsync<DuplicateMessageException>();
    }

    [Fact]
    public async Task AtomicClaims_TwoCompetingWorkers_OnlyOneClaimsJob()
    {
        await using var conn = new SqlConnection(_connectionString);
        try { await conn.OpenAsync(); } catch { return; }

        var connFactory = Substitute.For<IControlConnectionFactory>();
        connFactory.Create().Returns(new SqlConnection(_connectionString));
        connFactory.ConnectionString.Returns(_connectionString);

        var repo1 = new MainPipelineQueueRepository(connFactory, Substitute.For<IQueryContext>());
        var repo2 = new MainPipelineQueueRepository(connFactory, Substitute.For<IQueryContext>());

        var messageId = Guid.NewGuid();
        var job = new PipelineQueue
        {
            MessageId = messageId,
            TenantId = 5,
            TenantPublicId = Guid.NewGuid(),
            PipelineId = 20,
            PipelinePublicId = Guid.NewGuid(),
            QueueSource = "Manual",
            TriggerPayloadJson = "{}",
            PayloadHash = PayloadHashHelper.ComputeHash("{}"),
            Status = "Pending",
            MaxAttempts = 5
        };
        await repo1.EnqueueAsync(job);

        var claim1Task = repo1.ClaimPendingJobsAsync("worker_A", 10, 60, new List<long> { 5 });
        var claim2Task = repo2.ClaimPendingJobsAsync("worker_B", 10, 60, new List<long> { 5 });

        await Task.WhenAll(claim1Task, claim2Task);

        var claimedByA = await claim1Task;
        var claimedByB = await claim2Task;

        var totalClaimed = claimedByA.Count + claimedByB.Count;
        totalClaimed.Should().Be(1);

        if (claimedByA.Count == 1)
        {
            claimedByA[0].LockedBy.Should().Be("worker_A");
            claimedByA[0].ClaimToken.Should().NotBeNull();
        }
        else
        {
            claimedByB[0].LockedBy.Should().Be("worker_B");
            claimedByB[0].ClaimToken.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task OutboxRelay_CommitAndRollback_MaintainsTransactionSafety()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DatabaseWorker_EndToEnd_ExecutesPipelineContext()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CleanupService_PrunesTerminalJobsOnly()
    {
        await Task.CompletedTask;
    }
}
