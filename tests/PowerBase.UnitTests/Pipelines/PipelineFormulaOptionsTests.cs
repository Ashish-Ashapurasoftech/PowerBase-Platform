using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>A step value's formula runs on the app's formula engine with the options every other formula gets: the acting user,
/// the PowerFlow's app, the frontend URL.</summary>
public class PipelineFormulaOptionsTests
{
    private static readonly Guid AppPublicId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static (PipelineEngine Engine, FormulaEngine Formulas) CreateEngine()
    {
        var formulas = new FormulaEngine();
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(FormulaEngine)).Returns(formulas);
        var apps = Substitute.For<IAppRepository>();
        apps.GetPublicIdByIdAsync(5, Arg.Any<CancellationToken>()).Returns(AppPublicId);
        services.GetService(typeof(IAppRepository)).Returns(apps);
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns("https://powerbase.example");
        services.GetService(typeof(IFormulaRuntimeContext)).Returns(runtime);
        var pipelines = Substitute.For<IPipelineRepository>();
        pipelines.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 7, AppId = 5 });
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(3L);
        context.UserId.Returns(40021L);
        context.UserEmail.Returns("owner@example.com");
        context.UserName.Returns("Owner");
        var engine = new PipelineEngine(pipelines, Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), context, Substitute.For<IServiceScopeFactory>(), services,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());
        return (engine, formulas);
    }

    private static string Evaluate(PipelineEngine engine, string expression)
    {
        var value = PipelineFormulaValue.Create(new PipelineFormulaValue.Definition(expression, []));
        var steps = new List<PipelineStep> { new() { Id = 1, PipelineId = 7, RefId = "ref_aa", Type = "action", Subtype = "create-record" } };
        return (string)typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, new object?[] { value, """{"steps":{}}""", null, steps })!;
    }

    [Fact]
    public void AppID_IsThePowerFlowsApp()
    {
        Evaluate(CreateEngine().Engine, "AppID()").Should().Be(AppPublicId.ToString());
    }

    [Fact]
    public void User_IsTheActingUser()
    {
        var engine = CreateEngine().Engine;

        Evaluate(engine, "UserToEmail(User())").Should().Be("owner@example.com");
        Evaluate(engine, "UserToID(User())").Should().Be("40021");
    }

    [Fact]
    public void TheFormulaRunsOnTheAppsFormulaEngine()
    {
        var (engine, formulas) = CreateEngine();
        var services = (IServiceProvider)typeof(PipelineEngine).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

        Evaluate(engine, "\"A\" & \"B\"").Should().Be("AB");
        services.Received().GetService(typeof(FormulaEngine));
        formulas.Should().NotBeNull();
    }
}
