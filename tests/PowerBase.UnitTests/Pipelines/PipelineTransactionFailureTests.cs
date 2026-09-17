using System.Data;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Infrastructure.UOW;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineTransactionFailureTests
{
    [Fact]
    public async Task InterceptionFailurePropagatesToMutationOwner()
    {
        var repository = Substitute.For<IPipelineRepository>();
        var failure = new InvalidOperationException("Cannot read trigger subscriptions");
        repository.ListAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Pipeline>>(failure));
        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var interceptor = new PipelineTriggerInterceptor(repository, Substitute.For<IRecordRepository>(),
            Substitute.For<IQueryContext>(), uow, Substitute.For<ILogger<PipelineTriggerInterceptor>>());
        var change = new PipelineRecordChange(Guid.NewGuid(), new Dictionary<long, object?>(),
            new Dictionary<long, object?> { [6] = "new" }, new List<long> { 6 }, PipelineRecordEventType.Modified);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => interceptor.InterceptBulkAsync(
            new AppTable(), Array.Empty<AppField>(), new[] { change }, Guid.NewGuid(), Guid.NewGuid(), 1));
        Assert.Same(failure, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackClearsPendingCallbacksEvenWhenCleanupFails(bool fails)
    {
        var inner = Substitute.For<ITenantUnitOfWork>();
        using var uow = new TriggerPublishingTenantUnitOfWork(inner);
        var published = false;
        uow.RegisterPostCommitAction(() => { published = true; return Task.CompletedTask; });
        inner.RollbackAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.False(call.Arg<CancellationToken>().IsCancellationRequested);
            return fails ? Task.FromException(new InvalidOperationException("Rollback failed")) : Task.CompletedTask;
        });
        var cancelled = new CancellationToken(true);
        if (fails) await Assert.ThrowsAsync<InvalidOperationException>(() => uow.RollbackAsync(cancelled));
        else await uow.RollbackAsync(cancelled);
        await uow.BeginAsync();
        await uow.CommitAsync();
        Assert.False(published);
    }
}

