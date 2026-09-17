using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using PowerBase.API.Attributes;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Services;
using System.Net;
using System.Text.Json;

namespace PowerBase.UnitTests.Pipelines;
public class PowerBaseApiRequestDispatcherTests
{
    [Theory] [InlineData(true,200)] [InlineData(false,403)]
    public async Task DispatchUsesMvcAndAccountPermissions(bool granted,int expected)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName="Testing" });
        builder.Services.AddControllers().AddApplicationPart(typeof(RequestProbeController).Assembly);
        builder.Services.AddScoped<IQueryContext,QueryContext>();
        await using var app=builder.Build();
        using var scope=app.Services.CreateScope();
        var identity=scope.ServiceProvider.GetRequiredService<IQueryContext>();
        identity.SetTenantId(17);
        identity.SetUserIdentity(25,false,"Account user","",granted?new HashSet<string>{"probe"}:new HashSet<string>(),"member");
        identity.SetTokenScope(true,false,new HashSet<long>{42});
        var dispatcher=new PowerBaseApiRequestDispatcher(app.Services,app.Configuration);
        using var response=await dispatcher.SendAsync(new HttpRequestMessage(HttpMethod.Post,"https://powerbase.internal/request-probe") {Content=new StringContent("{\"value\":\"hello\"}",System.Text.Encoding.UTF8,"application/json")},scope.ServiceProvider,default);
        Assert.Equal(expected,(int)response.StatusCode);
        if(granted)
        {
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(17,json.RootElement.GetProperty("tenantId").GetInt64());
            Assert.Equal(25,json.RootElement.GetProperty("userId").GetInt64());
            Assert.True(json.RootElement.GetProperty("isUserToken").GetBoolean());
            Assert.False(json.RootElement.GetProperty("tokenAccessAllApps").GetBoolean());
            Assert.Equal("hello",json.RootElement.GetProperty("value").GetString());
        }
    }
}
[ApiController]
public class RequestProbeController:ControllerBase
{
    public sealed record Payload(string Value);
    [HttpPost("request-probe")][RequirePermission("probe")]
    public IActionResult Echo(Payload body,[FromServices] IQueryContext identity)=>Ok(new {identity.TenantId,identity.UserId,identity.IsUserToken,identity.TokenAccessAllApps,body.Value});
}
