using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.API.Controllers;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using System.Net.Http;
using PowerBase.Domain.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class SendEmailExecutionTests
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IEmailService _emailService;
    private readonly IFileStorageService _fileStorageService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PipelineEngine _engine;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public SendEmailExecutionTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _emailService = Substitute.For<IEmailService>();
        _fileStorageService = Substitute.For<IFileStorageService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _recordWriteService.ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<bool>())
            .Returns(new Dictionary<long, object?>());

        _engine = new PipelineEngine(
            _pipelineRepo,
            _recordRepo,
            _recordWriteService,
            _tableRepo,
            _fieldRepo,
            Substitute.For<IRelationshipRepository>(),
            _emailService,
            _httpClientFactory,
            _fileStorageService,
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
    }


    [Theory]
    [InlineData("send-email-outlook")]
    [InlineData("send-email")]
    [InlineData("Send an Email")]
    public async Task ResolvesMessageAndOptionsFromPreviousStepsUsingConfiguredMail(string subtype)
    {
        var step = new PipelineStep
        {
            Id = 2, RefId = "b", Type = "email", Subtype = subtype,
            ConfigJson = JsonSerializer.Serialize(new {
                toAddresses = "{{steps.a.email}}", subject = "Hello {{steps.a.name}}",
                body = "<strong>{{steps.a.name}}</strong>", ccAddresses = "{{steps.a.cc}}",
                fromAddress = "", contentType = "{{steps.a.format}}", importance = "High"
            })
        };
        var payload = JsonSerializer.Serialize(new { steps = new { a = new {
            email = "one@example.com,two@example.com", name = "Customer", cc = "cc@example.com", format = "HTML"
        } } });
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(_engine, new object[] { step, payload, new Dictionary<string, object>(), new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "trigger_1", CancellationToken.None })!;
        await task;
        await _emailService.Received(1).SendPipelineEmailAsync(Arg.Is<PipelineEmailMessage>(message =>
            message.To == "one@example.com,two@example.com" && message.Subject == "Hello Customer" &&
            message.Body == "<strong>Customer</strong>" && message.Cc == "cc@example.com" &&
            message.ContentType == "HTML" && message.Importance == "High"), Arg.Any<CancellationToken>());
    }
}
