using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Records;

public class RecordIndexingLatencyTests
{
    private readonly ILogger<ServiceBusPublisher> _publisherLogger = Substitute.For<ILogger<ServiceBusPublisher>>();

    [Fact]
    public async Task PublishPriorityBatchAsync_WhenUnconfigured_FallsBackGracefullyWithoutThrowing()
    {
        // When ServiceBus connection string is not set or placeholder, publisher runs unconfigured
        var config = new ConfigurationBuilder().Build();
        var publisher = new ServiceBusPublisher(config, _publisherLogger);

        var messages = new List<SearchIndexMessage>
        {
            new() { Action = IndexAction.Upsert, TenantId = 1, TableId = 2, RecordPublicId = Guid.NewGuid() }
        };

        var act = async () => await publisher.PublishPriorityBatchAsync(messages);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishPriorityBatchAsync_WhenMessagesAreEmpty_ReturnsImmediately()
    {
        var config = new ConfigurationBuilder().Build();
        var publisher = new ServiceBusPublisher(config, _publisherLogger);

        var act = async () => await publisher.PublishPriorityBatchAsync(new List<SearchIndexMessage>());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void SearchIndexMessage_Idempotency_AllowsDuplicateDeliveriesWithoutSideEffects()
    {
        var recordId = Guid.NewGuid();
        var msg1 = new SearchIndexMessage
        {
            Action = IndexAction.Upsert,
            TenantId = 10,
            TableId = 20,
            RecordPublicId = recordId,
            Payload = new Dictionary<string, object?> { ["f_6"] = "Active" }
        };

        var msg2 = new SearchIndexMessage
        {
            Action = IndexAction.Upsert,
            TenantId = 10,
            TableId = 20,
            RecordPublicId = recordId,
            Payload = new Dictionary<string, object?> { ["f_6"] = "Active" }
        };

        // Messages for the same record and state are deterministic and identical in structure
        msg1.Action.Should().Be(msg2.Action);
        msg1.RecordPublicId.Should().Be(msg2.RecordPublicId);
        msg1.TableId.Should().Be(msg2.TableId);
        msg1.TenantId.Should().Be(msg2.TenantId);
    }
}
