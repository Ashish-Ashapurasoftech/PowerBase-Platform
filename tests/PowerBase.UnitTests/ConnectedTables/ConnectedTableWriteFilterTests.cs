using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PowerBase.API.ConnectedTables;
using PowerBase.API.Models.Records;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.Imports;

namespace PowerBase.UnitTests.ConnectedTables;

public class ConnectedTableWriteFilterTests
{
    private static long _tenant = 5000;
    private readonly long _tenantId = Interlocked.Increment(ref _tenant);
    private readonly Guid _connected = Guid.NewGuid();
    private readonly Guid _ordinary = Guid.NewGuid();
    private readonly IConnectedTableRepository _repository = Substitute.For<IConnectedTableRepository>();

    public ConnectedTableWriteFilterTests()
    {
        _repository.ListConnectedAppTablesAsync(Arg.Any<CancellationToken>()).Returns([(10L, _connected)]);
        _repository.ListConnectedFidsAsync(_connected, Arg.Any<CancellationToken>()).Returns([6, 7]);
    }

    private async Task<(IActionResult? Result, bool Called)> RunAsync(string method, string template, Guid tableId, object? argument = null)
    {
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.TenantId.Returns(_tenantId);
        var services = new ServiceCollection()
            .AddSingleton(queryContext)
            .AddSingleton(_repository)
            .AddSingleton(Substitute.For<IReportRepository>())
            .AddSingleton(Substitute.For<IImportDefinitionRepository>())
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = method;
        var routeData = new RouteData();
        routeData.Values["tableId"] = tableId.ToString();
        var descriptor = new ActionDescriptor { AttributeRouteInfo = new AttributeRouteInfo { Template = template } };
        var arguments = new Dictionary<string, object?>();
        if (argument is not null) arguments["request"] = argument;
        var context = new ActionExecutingContext(new ActionContext(http, routeData, descriptor), [], arguments, controller: new object());

        var called = false;
        ConnectedTableCache.Invalidate(_tenantId);
        await new ConnectedTableWriteFilter().OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller: new object()));
        });
        return (context.Result, called);
    }

    private static UpdateRecordRequest Update(params string[] fids) =>
        new() { Fields = fids.ToDictionary(f => f, _ => JsonSerializer.SerializeToElement("x")) };

    [Fact]
    public async Task CreateRecord_OnConnectedTable_Is409()
    {
        var (result, called) = await RunAsync("POST", "tables/{tableId:guid}/records", _connected);
        called.Should().BeFalse();
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task CreateRecord_OnOrdinaryTable_PassesThrough()
    {
        var (result, called) = await RunAsync("POST", "tables/{tableId:guid}/records", _ordinary);
        called.Should().BeTrue();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("DELETE", "tables/{tableId:guid}/records/{id:guid}")]
    [InlineData("POST", "tables/{tableId:guid}/records/bulk-delete")]
    [InlineData("POST", "tables/{tableId:guid}/quick-imports")]
    [InlineData("POST", "tables/{tableId:guid}/import-definitions")]
    [InlineData("POST", "tables/{tableId:guid}/records/{id:guid}/actions/{fid:int}")]
    public async Task OtherWrites_OnConnectedTable_Are409(string method, string template)
    {
        (await RunAsync(method, template, _connected)).Called.Should().BeFalse();
    }

    [Fact]
    public async Task Patch_ConnectedField_Is409_LocalField_PassesThrough()
    {
        (await RunAsync("PATCH", "tables/{tableId:guid}/records/{id:guid}", _connected, Update("6"))).Called.Should().BeFalse();
        (await RunAsync("PATCH", "tables/{tableId:guid}/records/{id:guid}", _connected, Update("12"))).Called.Should().BeTrue();
    }

    [Fact]
    public async Task MassUpdate_ConnectedField_Is409()
    {
        var request = new MassUpdateRecordsRequest { Fields = new() { ["7"] = JsonSerializer.SerializeToElement(1) } };
        (await RunAsync("POST", "tables/{tableId:guid}/records/mass-update", _connected, request)).Called.Should().BeFalse();
    }

    [Theory]
    [InlineData("GET", "tables/{tableId:guid}/records")]
    [InlineData("POST", "tables/{tableId:guid}/records/{id:guid}/actions/{fid:int}/check")]
    [InlineData("POST", "tables/{tableId:guid}/records/{id:guid}/files/{fid:long}/reservation")]
    [InlineData("POST", "tables/{tableId:guid}/fields")]
    [InlineData("POST", "connected-tables/{id:guid}/refresh")]
    public async Task ReadsAndNonDataWrites_PassThrough(string method, string template)
    {
        (await RunAsync(method, template, _connected)).Called.Should().BeTrue();
    }
}
