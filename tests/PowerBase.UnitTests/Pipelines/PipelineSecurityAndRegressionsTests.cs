using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineSecurityAndRegressionsTests
{
    [Fact]
    public async Task AppAccessService_NormalUser_AllowsCrossAppAccess()
    {
        // Arrange
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(10L);
        queryContext.IsUserToken.Returns(false);
        queryContext.TokenAccessAllApps.Returns(true);
        queryContext.AllowedAppIds.Returns(new HashSet<long>());

        var appUserRepo = Substitute.For<IAppUserRepository>();
        appUserRepo.GetUserAppPermissionsAsync(20L, 10L, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.PowerFlowsUpdate });

        var appAccessService = new AppAccessService(
            Substitute.For<IAppRepository>(),
            Substitute.For<IAppTableRepository>(),
            Substitute.For<IReportRepository>(),
            Substitute.For<IFormRepository>(),
            Substitute.For<IFormRuleRepository>(),
            Substitute.For<IPageRepository>(),
            appUserRepo,
            queryContext,
            Substitute.For<IPipelineRepository>()
        );

        // Act
        var act = () => appAccessService.RequirePermissionByAppIdAsync(20L, PermissionCodes.PowerFlowsUpdate, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AppAccessService_SavedToken_AllowedAppsAccess_Passes()
    {
        // Arrange
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(10L);
        queryContext.IsUserToken.Returns(true);
        queryContext.TokenAccessAllApps.Returns(false);
        queryContext.AllowedAppIds.Returns(new HashSet<long> { 20L, 30L });

        var appUserRepo = Substitute.For<IAppUserRepository>();
        appUserRepo.GetUserAppPermissionsAsync(30L, 10L, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.PowerFlowsUpdate });

        var appAccessService = new AppAccessService(
            Substitute.For<IAppRepository>(),
            Substitute.For<IAppTableRepository>(),
            Substitute.For<IReportRepository>(),
            Substitute.For<IFormRepository>(),
            Substitute.For<IFormRuleRepository>(),
            Substitute.For<IPageRepository>(),
            appUserRepo,
            queryContext,
            Substitute.For<IPipelineRepository>()
        );

        // Act
        var act = () => appAccessService.RequirePermissionByAppIdAsync(30L, PermissionCodes.PowerFlowsUpdate, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AppAccessService_SavedToken_AppAOnly_DeniesAppB()
    {
        // Arrange
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(10L);
        queryContext.IsUserToken.Returns(true);
        queryContext.TokenAccessAllApps.Returns(false);
        queryContext.AllowedAppIds.Returns(new HashSet<long> { 20L }); // App A only

        var appUserRepo = Substitute.For<IAppUserRepository>();

        var appAccessService = new AppAccessService(
            Substitute.For<IAppRepository>(),
            Substitute.For<IAppTableRepository>(),
            Substitute.For<IReportRepository>(),
            Substitute.For<IFormRepository>(),
            Substitute.For<IFormRuleRepository>(),
            Substitute.For<IPageRepository>(),
            appUserRepo,
            queryContext,
            Substitute.For<IPipelineRepository>()
        );

        // Act
        var act = () => appAccessService.RequirePermissionByAppIdAsync(30L, PermissionCodes.PowerFlowsUpdate, CancellationToken.None); // App B

        // Assert
        await act.Should().ThrowAsync<UnauthorizedActionException>()
            .WithMessage("*does not have access to this application*");
    }

    [Fact]
    public async Task PipelineEngine_CrossTenant_PreservesUserId()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var writeService = Substitute.For<IRecordWriteService>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var adminRepo = Substitute.For<IAdminRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var parentQueryContext = Substitute.For<IQueryContext>();
        parentQueryContext.TenantId.Returns(6L);
        parentQueryContext.UserId.Returns(999L);
        parentQueryContext.UserName.Returns("Alice");
        parentQueryContext.UserEmail.Returns("alice@example.com");
        parentQueryContext.Permissions.Returns(new HashSet<string> { "PowerFlowsRead" });
        parentQueryContext.TenantRole.Returns("Administrator");

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var childQueryContext = Substitute.For<IQueryContext>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IQueryContext)).Returns(childQueryContext);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        
        // Repos returned inside target scope
        serviceProvider.GetService(typeof(IRecordRepository)).Returns(recordRepo);
        serviceProvider.GetService(typeof(IAppTableRepository)).Returns(tableRepo);
        serviceProvider.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo);
        serviceProvider.GetService(typeof(IRecordWriteService)).Returns(writeService);
        serviceProvider.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());

        var connectionGuid = Guid.NewGuid();
        adminRepo.GetTenantIdByPublicIdAsync(connectionGuid, Arg.Any<CancellationToken>()).Returns(8L); // targetTenantId = 8 -> isCrossTenant = true
        tenantRepo.IsActiveMemberAsync(999L, Arg.Any<CancellationToken>()).Returns(true);

        var table = new AppTable { Id = 1, PublicId = Guid.NewGuid() };
        tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        var engine = new PipelineEngine(
            pipelineRepo,
            recordRepo,
            writeService,
            tableRepo,
            fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            parentQueryContext,
            scopeFactory,
            serviceProvider,
            adminRepo,
            tenantRepo,
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var step = new PipelineStep
        {
            Type = "action",
            Subtype = "create-record",
            ConfigJson = JsonSerializer.Serialize(new { connectionPublicId = connectionGuid.ToString(), tableId = Guid.NewGuid().ToString() })
        };

        var contextDict = new Dictionary<string, object>
        {
            { "_CreatedBy", 999L }
        };

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;
        await task;

        // Assert
        childQueryContext.Received(1).SetUserIdentity(
            999L,
            Arg.Any<bool>(),
            "Alice",
            "alice@example.com",
            Arg.Any<IReadOnlySet<string>>(),
            "Administrator"
        );
    }
}
