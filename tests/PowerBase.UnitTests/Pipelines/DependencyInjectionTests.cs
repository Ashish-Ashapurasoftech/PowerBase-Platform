using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class DependencyInjectionTests
{
    [Fact]
    public void ServiceProvider_ValidatesScopesAndOnBuild_Successfully()
    {
        var services = new ServiceCollection();

        // Register mocks/shims to satisfy registrations
        services.AddLogging();
        services.AddSingleton(Substitute.For<IControlConnectionFactory>());
        services.AddSingleton(Substitute.For<ITenantConnectionResolver>());
        services.AddScoped(sp => Substitute.For<ITenantConnectionFactory>());
        services.AddScoped(sp => Substitute.For<IPipelineRepository>());
        services.AddScoped(sp => Substitute.For<IPipelineEngine>());

        // Register our target services
        services.AddScoped<IQueryContext, QueryContext>();
        services.AddScoped<IMainPipelineQueueRepository, MainPipelineQueueRepository>();
        services.AddScoped<IPipelineExecutionQueue, DatabasePipelineExecutionQueue>();

        // Build with validation enabled
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // Resolve inside scope
        using (var scope = provider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();
            var queue = scope.ServiceProvider.GetRequiredService<IPipelineExecutionQueue>();

            repo.Should().NotBeNull();
            queue.Should().NotBeNull();
        }
    }

    [Fact]
    public void TwoIndependentlyCreatedScopes_ReceiveDifferentQueryContextInstances()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IQueryContext, QueryContext>();

        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var ctx1 = scope1.ServiceProvider.GetRequiredService<IQueryContext>();
        var ctx2 = scope2.ServiceProvider.GetRequiredService<IQueryContext>();

        ctx1.Should().NotBeNull();
        ctx2.Should().NotBeNull();
        ctx1.Should().NotBeSameAs(ctx2);
    }

    [Fact]
    public async Task ConcurrentTenantJobs_DoNotShareQueryContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IQueryContext, QueryContext>();

        var provider = services.BuildServiceProvider();

        var barrier = new Barrier(2);

        var task1 = Task.Run(() =>
        {
            using var scope = provider.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<IQueryContext>();
            ctx.SetTenantId(1);
            if (ctx is QueryContext qc)
            {
                qc.UserId = 100;
            }

            barrier.SignalAndWait();
            
            ctx.TenantId.Should().Be(1);
            if (ctx is QueryContext qcCheck)
            {
                qcCheck.UserId.Should().Be(100);
            }
        });

        var task2 = Task.Run(() =>
        {
            using var scope = provider.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<IQueryContext>();
            ctx.SetTenantId(2);
            if (ctx is QueryContext qc)
            {
                qc.UserId = 200;
            }

            barrier.SignalAndWait();

            ctx.TenantId.Should().Be(2);
            if (ctx is QueryContext qcCheck)
            {
                qcCheck.UserId.Should().Be(200);
            }
        });

        await Task.WhenAll(task1, task2);
    }
}
