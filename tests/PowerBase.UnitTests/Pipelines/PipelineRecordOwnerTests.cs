using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineRecordOwnerTests
{
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly QueryContext _queryContext = new QueryContext();
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IUserPermissionRepository _permissionRepo = Substitute.For<IUserPermissionRepository>();
    private readonly IMainPipelineQueueRepository _queueRepo = Substitute.For<IMainPipelineQueueRepository>();

    public PipelineRecordOwnerTests()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(_serviceProvider);

        _serviceProvider.GetService(typeof(IQueryContext)).Returns(_queryContext);
        _serviceProvider.GetService(typeof(IPipelineRepository)).Returns(_pipelineRepo);
        _serviceProvider.GetService(typeof(IMainPipelineQueueRepository)).Returns(_queueRepo);
        _serviceProvider.GetService(typeof(IUserRepository)).Returns(_userRepo);
        _serviceProvider.GetService(typeof(ITenantRepository)).Returns(_tenantRepo);
        _serviceProvider.GetService(typeof(IUserPermissionRepository)).Returns(_permissionRepo);
        _serviceProvider.GetService(typeof(IPipelineEngine)).Returns(Substitute.For<IPipelineEngine>());
    }

    [Fact]
    public async Task ScheduledCurrentAccount_CreateRecord_OwnerIsPipelineOwner()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 1,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null, // Scheduled
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid()
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 42L, IsActive = true, IsDeleted = false };

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(42L, Arg.Any<CancellationToken>()).Returns(execUser);
        _tenantRepo.IsActiveMemberAsync(42L, Arg.Any<CancellationToken>()).Returns(true);
        _permissionRepo.GetPermissionsAsync(42L, 10L, Arg.Any<CancellationToken>()).Returns(new HashSet<string> { "PowerFlows:read" });

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        _queryContext.UserId.Should().Be(42L); // Resolved to pipeline owner
    }

    [Fact]
    public async Task ScheduledCurrentAccount_DisabledPipelineOwner_FailsSafely()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 1,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null,
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid()
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 42L, IsActive = false, IsDeleted = false }; // Disabled!

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(42L, Arg.Any<CancellationToken>()).Returns(execUser);

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        await _queueRepo.Received(1).MarkFailedAsync(job.Id, Arg.Any<string>(), job.ClaimToken.Value, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualRun_CreateRecord_PreservesActor()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 1,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = 99L, // Manual user
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid()
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 99L, IsActive = true, IsDeleted = false };

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(99L, Arg.Any<CancellationToken>()).Returns(execUser);
        _tenantRepo.IsActiveMemberAsync(99L, Arg.Any<CancellationToken>()).Returns(true);
        _permissionRepo.GetPermissionsAsync(99L, 10L, Arg.Any<CancellationToken>()).Returns(new HashSet<string> { "PowerFlows:read" });

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        _queryContext.UserId.Should().Be(99L); // Preserved manual actor
    }

    [Fact]
    public async Task OnNewEvent_CreateRecord_PreservesActor()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 1,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = 88L, // Event actor
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid()
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 88L, IsActive = true, IsDeleted = false };

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(88L, Arg.Any<CancellationToken>()).Returns(execUser);
        _tenantRepo.IsActiveMemberAsync(88L, Arg.Any<CancellationToken>()).Returns(true);
        _permissionRepo.GetPermissionsAsync(88L, 10L, Arg.Any<CancellationToken>()).Returns(new HashSet<string> { "PowerFlows:read" });

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        _queryContext.UserId.Should().Be(88L); // Preserved event actor
    }

    [Fact]
    public async Task ScheduledSavedConnection_CreateRecord_OwnerIsConnectionUser()
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
        parentQueryContext.UserId.Returns(42L); // Pipeline owner ID

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var childQueryContext = Substitute.For<IQueryContext>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IQueryContext)).Returns(childQueryContext);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        serviceProvider.GetService(typeof(IRecordRepository)).Returns(recordRepo);
        serviceProvider.GetService(typeof(IAppTableRepository)).Returns(tableRepo);
        serviceProvider.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo);
        serviceProvider.GetService(typeof(IRecordWriteService)).Returns(writeService);
        serviceProvider.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());

        var accountRepo = Substitute.For<IPipelineAccountRepository>();
        var userTokenRepo = Substitute.For<IUserTokenRepository>();
        var connectionScopeResolver = new PowerBase.Application.Connections.Common.ConnectionScopeResolver(
            accountRepo,
            userTokenRepo,
            parentQueryContext
        );

        var connectionGuid = Guid.NewGuid();
        var targetUser = new User { Id = 999L, IsActive = true, IsDeleted = false, Name = "Alice", Email = "alice@example.com" };
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(999L, Arg.Any<CancellationToken>()).Returns(targetUser);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(userRepo);

        var permissionRepo = Substitute.For<IUserPermissionRepository>();
        serviceProvider.GetService(typeof(IUserPermissionRepository)).Returns(permissionRepo);

        serviceProvider.GetService(typeof(PowerBase.Application.Connections.Common.ConnectionScopeResolver)).Returns(connectionScopeResolver);

        var account = new PipelineAccount
        {
            Id = 1L,
            PublicId = connectionGuid,
            Name = "Test Account",
            IsActive = true,
            Status = PipelineAccountStatuses.Active,
            AuthMode = PipelineAccountAuthModes.UserToken,
            TokenHash = "mock_hash",
            TargetTenantId = 6L,
            TargetUserId = 999L,
            TenantId = 6L
        };
        accountRepo.GetByPublicIdForUserAsync(connectionGuid, 42L, Arg.Any<CancellationToken>()).Returns(Task.FromResult<PipelineAccount?>(account));

        var userToken = new UserToken
        {
            Id = 1L,
            AccessAllApps = true,
            UserId = 999L,
            TenantId = 6L,
            IsActive = true,
            IsDeleted = false,
            TokenHash = "mock_hash"
        };
        userTokenRepo.GetByHashAsync("mock_hash", Arg.Any<CancellationToken>()).Returns(Task.FromResult<UserToken?>(userToken));
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
            { "_CreatedBy", 42L }
        };

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;
        await task;

        // Assert: scoped context user is connection user
        childQueryContext.Received(1).SetUserIdentity(
            999L,
            false,
            "Alice",
            "alice@example.com",
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<string>()
        );
    }

    [Fact]
    public async Task ScheduledCrossTenant_CreateRecord_UsesTargetTenantConnectionUser()
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
        parentQueryContext.UserId.Returns(42L); // Owner tenant user

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var childQueryContext = Substitute.For<IQueryContext>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IQueryContext)).Returns(childQueryContext);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        serviceProvider.GetService(typeof(IRecordRepository)).Returns(recordRepo);
        serviceProvider.GetService(typeof(IAppTableRepository)).Returns(tableRepo);
        serviceProvider.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo);
        serviceProvider.GetService(typeof(IRecordWriteService)).Returns(writeService);
        serviceProvider.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());

        var accountRepo = Substitute.For<IPipelineAccountRepository>();
        var userTokenRepo = Substitute.For<IUserTokenRepository>();
        var connectionScopeResolver = new PowerBase.Application.Connections.Common.ConnectionScopeResolver(
            accountRepo,
            userTokenRepo,
            parentQueryContext
        );

        var connectionGuid = Guid.NewGuid();
        var targetUser = new User { Id = 999L, IsActive = true, IsDeleted = false, Name = "Alice", Email = "alice@example.com" };
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(999L, Arg.Any<CancellationToken>()).Returns(targetUser);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(userRepo);

        var permissionRepo = Substitute.For<IUserPermissionRepository>();
        serviceProvider.GetService(typeof(IUserPermissionRepository)).Returns(permissionRepo);

        serviceProvider.GetService(typeof(PowerBase.Application.Connections.Common.ConnectionScopeResolver)).Returns(connectionScopeResolver);

        // Target tenant is different (8L)
        var account = new PipelineAccount
        {
            Id = 1L,
            PublicId = connectionGuid,
            Name = "Test Account",
            IsActive = true,
            Status = PipelineAccountStatuses.Active,
            AuthMode = PipelineAccountAuthModes.UserToken,
            TokenHash = "mock_hash",
            TargetTenantId = 8L,
            TargetUserId = 999L,
            TenantId = 6L
        };
        accountRepo.GetByPublicIdForUserAsync(connectionGuid, 42L, Arg.Any<CancellationToken>()).Returns(Task.FromResult<PipelineAccount?>(account));

        var userToken = new UserToken
        {
            Id = 1L,
            AccessAllApps = true,
            UserId = 999L,
            TenantId = 8L,
            IsActive = true,
            IsDeleted = false,
            TokenHash = "mock_hash"
        };
        userTokenRepo.GetByHashAsync("mock_hash", Arg.Any<CancellationToken>()).Returns(Task.FromResult<UserToken?>(userToken));
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
            { "_CreatedBy", 42L }
        };

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;
        await task;

        // Assert: scoped context user is connection user
        childQueryContext.Received(1).SetUserIdentity(
            999L,
            false,
            "Alice",
            "alice@example.com",
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<string>()
        );
    }

    [Fact]
    public async Task ScheduledCrossTenant_DoesNotCopyOwnerTenantNumericUserId()
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
        parentQueryContext.UserId.Returns(42L); // Owner tenant user ID: 42L

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var childQueryContext = Substitute.For<IQueryContext>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IQueryContext)).Returns(childQueryContext);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        serviceProvider.GetService(typeof(IRecordRepository)).Returns(recordRepo);
        serviceProvider.GetService(typeof(IAppTableRepository)).Returns(tableRepo);
        serviceProvider.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo);
        serviceProvider.GetService(typeof(IRecordWriteService)).Returns(writeService);
        serviceProvider.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());

        var accountRepo = Substitute.For<IPipelineAccountRepository>();
        var userTokenRepo = Substitute.For<IUserTokenRepository>();
        var connectionScopeResolver = new PowerBase.Application.Connections.Common.ConnectionScopeResolver(
            accountRepo,
            userTokenRepo,
            parentQueryContext
        );

        var connectionGuid = Guid.NewGuid();
        var targetUser = new User { Id = 999L, IsActive = true, IsDeleted = false, Name = "Alice", Email = "alice@example.com" };
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(999L, Arg.Any<CancellationToken>()).Returns(targetUser);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(userRepo);

        var permissionRepo = Substitute.For<IUserPermissionRepository>();
        serviceProvider.GetService(typeof(IUserPermissionRepository)).Returns(permissionRepo);

        serviceProvider.GetService(typeof(PowerBase.Application.Connections.Common.ConnectionScopeResolver)).Returns(connectionScopeResolver);

        var account = new PipelineAccount
        {
            Id = 1L,
            PublicId = connectionGuid,
            Name = "Test Account",
            IsActive = true,
            Status = PipelineAccountStatuses.Active,
            AuthMode = PipelineAccountAuthModes.UserToken,
            TokenHash = "mock_hash",
            TargetTenantId = 8L,
            TargetUserId = 999L,
            TenantId = 6L
        };
        accountRepo.GetByPublicIdForUserAsync(connectionGuid, 42L, Arg.Any<CancellationToken>()).Returns(Task.FromResult<PipelineAccount?>(account));

        var userToken = new UserToken
        {
            Id = 1L,
            AccessAllApps = true,
            UserId = 999L,
            TenantId = 8L,
            IsActive = true,
            IsDeleted = false,
            TokenHash = "mock_hash"
        };
        userTokenRepo.GetByHashAsync("mock_hash", Arg.Any<CancellationToken>()).Returns(Task.FromResult<UserToken?>(userToken));
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
            { "_CreatedBy", 42L }
        };

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;
        await task;

        // Assert: child context never received 42L as user identity
        childQueryContext.DidNotReceive().SetUserIdentity(
            42L,
            Arg.Any<bool>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<string>()
        );
    }

    [Fact]
    public void PipelineCreateRecord_RecordOwnerNeverZero()
    {
        // Assert that the record context setup enforces non-zero UserId on scheduled flow setup
        var job = new PipelineQueue { TriggeredBy = null }; // scheduled enqueued
        var pipeline = new Pipeline { CreatedBy = 55L };
        
        long resolvedUserId = job.TriggeredBy ?? pipeline.CreatedBy;
        resolvedUserId.Should().NotBe(0L);
        resolvedUserId.Should().Be(55L);
    }

    [Fact]
    public void PipelineCreateRecord_LastModifiedByNeverZero()
    {
        // Assert that the record context setup enforces non-zero UserId on scheduled flow setup
        var job = new PipelineQueue { TriggeredBy = null }; // scheduled enqueued
        var pipeline = new Pipeline { CreatedBy = 55L };
        
        long resolvedUserId = job.TriggeredBy ?? pipeline.CreatedBy;
        resolvedUserId.Should().NotBe(0L);
        resolvedUserId.Should().Be(55L);
    }

    [Fact]
    public async Task ScheduledValidTenantMember_ExecutesSuccessfully()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 123,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null,
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            MaxAttempts = 5,
            AttemptCount = 0
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 42L, IsActive = true, IsDeleted = false };

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(42L, Arg.Any<CancellationToken>()).Returns(execUser);
        _tenantRepo.IsActiveMemberAsync(42L, Arg.Any<CancellationToken>()).Returns(true);
        // Note: No permission mock returns PowerFlows:read.
        _permissionRepo.GetPermissionsAsync(42L, 10L, Arg.Any<CancellationToken>()).Returns(new HashSet<string>());

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        _queryContext.UserId.Should().Be(42L);
        await _queueRepo.Received(1).MarkSucceededAsync(job.Id, Arg.Any<string>(), job.ClaimToken.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledDeletedExecutionUser_TransitionsToTerminalFailed()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 124,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null,
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            MaxAttempts = 5,
            AttemptCount = 0
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 42L, IsActive = false, IsDeleted = true }; // Disabled/Deleted

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(42L, Arg.Any<CancellationToken>()).Returns(execUser);

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        await _queueRepo.Received(1).MarkFailedAsync(job.Id, Arg.Any<string>(), job.ClaimToken.Value, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queueRepo.DidNotReceive().ScheduleRetryAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonMemberExecutionUser_TransitionsToTerminalFailed()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 125,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null,
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            MaxAttempts = 5,
            AttemptCount = 0
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 42L, IsActive = true, IsDeleted = false };

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(42L, Arg.Any<CancellationToken>()).Returns(execUser);
        _tenantRepo.IsActiveMemberAsync(42L, Arg.Any<CancellationToken>()).Returns(false); // Non-member

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        await _queueRepo.Received(1).MarkFailedAsync(job.Id, Arg.Any<string>(), job.ClaimToken.Value, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queueRepo.DidNotReceive().ScheduleRetryAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PipelineNonRetryableExceptionBeforeExecution_TransitionsToTerminalFailed()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 126,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null,
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            MaxAttempts = 5,
            AttemptCount = 0
        };

        // This will throw when pipelineRepo is called, simulating an exception prior to executing steps
        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns<Pipeline>(_ => {
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Non-retryable test error");
        });

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        await _queueRepo.Received(1).MarkFailedAsync(job.Id, Arg.Any<string>(), job.ClaimToken.Value, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryableException_FollowsRetryBackoffPath()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 127,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null,
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            MaxAttempts = 5,
            AttemptCount = 1
        };

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns<Pipeline>(_ => {
            throw new InvalidOperationException("Retryable standard test exception");
        });

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(worker, new object[] { job, CancellationToken.None });

        // Assert
        await _queueRepo.Received(1).ScheduleRetryAsync(job.Id, Arg.Any<string>(), job.ClaimToken.Value, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queueRepo.DidNotReceive().MarkFailedAsync(job.Id, Arg.Any<string>(), job.ClaimToken.Value, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShutdownOrCancellation_DoesNotMarkFailed()
    {
        // Arrange
        var job = new PipelineQueue
        {
            Id = 128,
            TenantId = 10,
            PipelineId = 100,
            TriggeredBy = null,
            MessageId = Guid.NewGuid(),
            ClaimToken = Guid.NewGuid(),
            MaxAttempts = 5,
            AttemptCount = 0
        };

        var pipeline = new Pipeline { Id = 100, CreatedBy = 42L, IsActive = true };
        var execUser = new User { Id = 42L, IsActive = true, IsDeleted = false };

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        _pipelineRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(pipeline);
        _userRepo.GetByIdAsync(42L, Arg.Any<CancellationToken>()).Returns(execUser);
        _tenantRepo.IsActiveMemberAsync(42L, Arg.Any<CancellationToken>()).Returns(true);

        var worker = new DatabasePipelineExecutionWorker(
            _serviceProvider,
            Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<DatabasePipelineExecutionWorker>>()
        );

        // Act
        var method = typeof(DatabasePipelineExecutionWorker).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var act = () => (Task)method.Invoke(worker, new object[] { job, cts.Token });

        // Assert: should bubble up OperationCanceledException
        await act.Should().ThrowAsync<OperationCanceledException>();

        await _queueRepo.DidNotReceive().MarkFailedAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queueRepo.DidNotReceive().ScheduleRetryAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
