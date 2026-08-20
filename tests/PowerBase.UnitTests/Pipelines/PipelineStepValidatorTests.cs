using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineStepValidatorTests
{
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly PipelineStepValidator _validator;

    public PipelineStepValidatorTests()
    {
        _validator = new PipelineStepValidator(
            _pipelineRepo,
            _appRepo,
            _tableRepo,
            _fieldRepo,
            _appAccessService,
            _tenantRepo,
            _queryContext
        );
    }

    [Fact]
    public async Task Validate_EmptyConfig_ThrowsValidationException()
    {
        var act = () => _validator.ValidateNewEventStepAsync("", CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("ConfigJson"));
    }

    [Fact]
    public async Task Validate_MalformedConfig_ThrowsValidationException()
    {
        var act = () => _validator.ValidateNewEventStepAsync("{ invalid json }", CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("ConfigJson"));
    }

    [Fact]
    public async Task Validate_MissingConnection_ThrowsValidationException()
    {
        var config = new
        {
            ConnectionPublicId = "",
            AppPublicId = Guid.NewGuid().ToString(),
            TablePublicId = Guid.NewGuid().ToString(),
            TriggerOnAdded = true
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("ConnectionPublicId"));
    }

    [Fact]
    public async Task Validate_SystemConnection_PassesConnectionCheck()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().NotThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Validate_UnknownConnection_ThrowsValidationException()
    {
        var connGuid = Guid.NewGuid();
        _pipelineRepo.GetConnectionByPublicIdAsync(connGuid, Arg.Any<CancellationToken>()).Returns((PipelineConnection)null);

        var config = new
        {
            ConnectionPublicId = connGuid.ToString(),
            AppPublicId = Guid.NewGuid().ToString(),
            TablePublicId = Guid.NewGuid().ToString(),
            TriggerOnAdded = true
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("ConnectionPublicId"));
    }

    [Fact]
    public async Task Validate_InactiveConnection_ThrowsValidationException()
    {
        var connGuid = Guid.NewGuid();
        _pipelineRepo.GetConnectionByPublicIdAsync(connGuid, Arg.Any<CancellationToken>()).Returns(new PipelineConnection { IsDeleted = true });

        var config = new
        {
            ConnectionPublicId = connGuid.ToString(),
            AppPublicId = Guid.NewGuid().ToString(),
            TablePublicId = Guid.NewGuid().ToString(),
            TriggerOnAdded = true
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("ConnectionPublicId"));
    }

    [Fact]
    public async Task Validate_UnauthorizedApp_ThrowsValidationException()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        _appAccessService.RequirePermissionByAppPublicIdAsync(appGuid, PermissionCodes.PowerFlowsUpdate, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new UnauthorizedAccessException("No access")));

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = Guid.NewGuid().ToString(),
            TriggerOnAdded = true
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("AppPublicId"));
    }

    [Fact]
    public async Task Validate_TableAppMismatch_ThrowsValidationException()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 999 }); // mismatched AppId

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("TablePublicId") && e.Errors["TablePublicId"].Any(x => x.Contains("belong")));
    }

    [Fact]
    public async Task Validate_InvalidTriggerFields_ThrowsValidationException()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 101, Name = "Name" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            TriggerOnAnyField = false,
            TriggerFields = new[] { "fid_999" } // non-existent
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("TriggerFields"));
    }

    [Fact]
    public async Task Validate_NoEventOptionSelected_ThrowsValidationException()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = false,
            TriggerOnModified = false,
            TriggerOnDeleted = false
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("TriggerOptions"));
    }

    [Fact]
    public async Task Validate_InvalidMaxRecords_ThrowsValidationException()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            LimitRecords = true,
            MaxRecords = 0 // invalid non-positive value
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("MaxRecords"));
    }

    [Fact]
    public async Task Validate_InvalidSubsequentFields_ThrowsValidationException()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 101, Name = "Name" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            SubsequentFields = new[] { "fid_999" } // non-existent subsequent field
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("SubsequentFields"));
    }

    [Fact]
    public async Task Validate_ValidTriggerAndSubsequentFields_Passes()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> 
        { 
            new AppField { Fid = 101, Name = "Name" },
            new AppField { Fid = 102, Name = "Age" }
        });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            TriggerOnAnyField = false,
            TriggerFields = new[] { "fid_101" },
            SubsequentFields = new[] { "fid_102" }
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().NotThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Activation_Revalidation_ThrowsWhenFieldInvalid()
    {
        // Test Activation Revalidation inside UpdatePipelineCommandHandler
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var auditRepo = Substitute.For<IAuditRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var appRepo = Substitute.For<IAppRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var appAccessService = Substitute.For<IAppAccessService>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var handler = new UpdatePipelineCommandHandler(pipelineRepo, auditRepo, queryContext, appRepo, tableRepo, fieldRepo, appAccessService, tenantRepo, Substitute.For<IServiceProvider>());

        var pipelinePublicId = Guid.NewGuid();
        var pipeline = new Pipeline { Id = 101, PublicId = pipelinePublicId, AppId = 1, Name = "Test" };
        pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        // Active trigger step with invalid config (missing connection)
        var triggerStep = new PipelineStep
        {
            Id = 1,
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new { AppPublicId = Guid.NewGuid().ToString() }) // missing connection and table
        };
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { triggerStep });

        var command = new UpdatePipelineCommand(pipelinePublicId, "Test", "Desc", true, new byte[] { 1 }); // Active = true triggers revalidation

        var act = () => handler.HandleAsync(command, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Runtime_Revalidation_FetchesLatestStepConfig()
    {
        // Verify engine executes against the latest re-fetched step configs from repo
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var writeService = Substitute.For<IRecordWriteService>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();

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
            Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var task = new PipelineExecutionTask { PipelineId = 101, TenantId = 1, TriggerEvent = "new-event" };
        pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        pipelineRepo.GetByIdAsync(101, Arg.Any<CancellationToken>()).Returns(new Pipeline { IsActive = true, IsDeleted = false });

        // Mock re-fetching steps
        var steps = new List<PipelineStep> { new PipelineStep { Type = "trigger", Subtype = "new-event", ConfigJson = "{}" } };
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(steps);

        await engine.ExecuteAsync(task, CancellationToken.None);

        // Verify it fetched steps from repository rather than using cached or stale configs
        await pipelineRepo.Received(1).GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_NoFilter_Passes()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 101, Name = "Name", TypeCode = "TEXT" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().NotThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Validate_EmptyFilterRulesList_Passes()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 101, Name = "Name", TypeCode = "TEXT" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            Filters = new List<object>()
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().NotThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Validate_BlankPlaceholderRule_Passes()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 101, Name = "Name", TypeCode = "TEXT" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            Filters = new[]
            {
                new { Field = "", Operator = "", Value = "" }
            }
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().NotThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Validate_PartialRule_ThrowsValidationException()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 6, Name = "Price", TypeCode = "NUMBER" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            Filters = new[]
            {
                new { Field = "fid_6", Operator = "", Value = "" }
            }
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().ContainKey("Filters.Rules[0]");
        ex.Which.Errors["Filters.Rules[0]"].Should().Contain(x => x.Contains("requires an operator"));
    }

    [Fact]
    public async Task Validate_ValidRule_Passes()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 6, Name = "Price", TypeCode = "NUMBER" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            Filters = new[]
            {
                new { Field = "fid_6", Operator = "greater_than", Value = "100" }
            }
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().NotThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Validate_BlankPlaceholderRuleWithIsOperator_Passes()
    {
        var systemConn = PipelineStepValidator.SystemConnectionIds.First();
        var appGuid = Guid.NewGuid();
        var tableGuid = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appGuid, Arg.Any<CancellationToken>()).Returns(new App { Id = 1 });
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, AppId = 1 });
        _fieldRepo.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new AppField { Fid = 101, Name = "Name", TypeCode = "TEXT" } });

        var config = new
        {
            ConnectionPublicId = systemConn.ToString(),
            AppPublicId = appGuid.ToString(),
            TablePublicId = tableGuid.ToString(),
            TriggerOnAdded = true,
            Filters = new[]
            {
                new { Field = "", Operator = "is", Value = "" }
            }
        };

        var act = () => _validator.ValidateNewEventStepAsync(JsonSerializer.Serialize(config), CancellationToken.None);
        await act.Should().NotThrowAsync<ValidationException>();
    }
}
