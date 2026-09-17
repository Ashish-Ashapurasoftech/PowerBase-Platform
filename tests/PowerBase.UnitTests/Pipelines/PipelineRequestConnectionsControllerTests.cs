using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using NSubstitute;
using PowerBase.API.Controllers;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using System.Net;
using System.Text.Json;
namespace PowerBase.UnitTests.Pipelines;
public class PipelineRequestConnectionsControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connect_UsesPipelineAppPermissionWithoutTenantPermission(bool allowed)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var access = Substitute.For<IAppAccessService>();
        var identity = new QueryContext();
        identity.SetTenantId(2);
        identity.SetUserIdentity(1, false, "user", "", new HashSet<string>(), "member");
        var protection = new EphemeralDataProtectionProvider();
        var credentials = new RequestConnectionService(repo, protection, Substitute.For<IHttpClientFactory>());
        var controller = new PipelineRequestConnectionsController(repo, identity, credentials, protection, access);
        var pipeline = new Pipeline { Id = 7, PublicId = Guid.NewGuid(), CreatedBy = 1 };
        repo.GetByPublicIdAsync(pipeline.PublicId, default).Returns(pipeline);
        repo.GetConnectionsByPipelineIdAsync(7, default).Returns(Array.Empty<PipelineConnection>());
        var connectionId = Guid.NewGuid();
        repo.CreateConnectionAsync(Arg.Any<PipelineConnection>(), ct: default).Returns((connectionId, 10L));
        var input = new PipelineRequestConnectionsController.ConnectRequest(pipeline.PublicId, new()
        {
            AccountName = "My Test HTTP", BaseUrl = "https://jsonplaceholder.typicode.com", AuthType = "No Authentication",
            Path = "https://{{a.host}}/records", QueryParams = new() { new() { Key = "id", Value = "{{a.id}}" } }
        });
        if (!allowed)
        {
            access.RequirePermissionByPipelinePublicIdAsync(pipeline.PublicId, PowerBase.Domain.Constants.PermissionCodes.PowerFlowsUpdate, default)
                .Returns(Task.FromException(new PowerBase.Domain.Exceptions.UnauthorizedActionException("Denied")));
            await Assert.ThrowsAsync<PowerBase.Domain.Exceptions.UnauthorizedActionException>(() => controller.Connect(input, default));
            await repo.DidNotReceive().CreateConnectionAsync(Arg.Any<PipelineConnection>(), ct: default);
        }
        else
        {
            var result = Assert.IsType<OkObjectResult>(await controller.Connect(input, default));
            var response = JsonSerializer.SerializeToElement(result.Value);
            Assert.True(response.GetProperty("connected").GetBoolean());
            Assert.Equal(connectionId, response.GetProperty("connectionId").GetGuid());
        }
        await access.Received(1).RequirePermissionByPipelinePublicIdAsync(pipeline.PublicId, PowerBase.Domain.Constants.PermissionCodes.PowerFlowsUpdate, default);
        Assert.Empty(identity.Permissions);
        Assert.DoesNotContain(typeof(PipelineRequestConnectionsController).GetMethod("Connect")!.GetCustomAttributes(true),
            attribute => attribute is PowerBase.API.Attributes.RequirePermissionAttribute);
    }

    [Fact] public async Task OAuthCallbackUsesPkceAndRejectsStateReplay()
    {
        var repo=Substitute.For<IPipelineRepository>(); var clients=Substitute.For<IHttpClientFactory>();
        clients.CreateClient("PipelineMakeRequest").Returns(new HttpClient(new OAuthHandler()));
        var identity=new QueryContext(); identity.SetTenantId(2); identity.SetUserIdentity(1,false,"user","",new HashSet<string>(),"member");
        var protection=new EphemeralDataProtectionProvider();
        var service=new RequestConnectionService(repo,protection,clients);
        var controller=new PipelineRequestConnectionsController(repo,identity,service,protection,Substitute.For<IAppAccessService>())
        {ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext()}};
        controller.Request.Scheme="https"; controller.Request.Host=new HostString("powerbase.example");
        var pipeline=new Pipeline {Id=7,PublicId=Guid.NewGuid(),CreatedBy=1};
        repo.GetByPublicIdAsync(pipeline.PublicId,default).Returns(pipeline);
        repo.GetConnectionsByPipelineIdAsync(7,default).Returns(Array.Empty<PipelineConnection>());
        PipelineConnection? saved=null; var id=Guid.NewGuid();
        repo.CreateConnectionAsync(Arg.Any<PipelineConnection>(),ct:default).Returns(call=>{saved=call.Arg<PipelineConnection>(); saved.PublicId=id; saved.Id=10; return (id,10L);});
        repo.GetConnectionByPublicIdAsync(id,default).Returns(_=>saved);
        var result=Assert.IsType<OkObjectResult>(await controller.Connect(new(pipeline.PublicId,new(){AccountName="OAuth",BaseUrl="https://service.example",AuthType="OAuth 2.0",OAuthClientId="client",OAuthAuthEndpoint="https://service.example/authorize",OAuthTokenEndpoint="https://service.example/token"}),default));
        var response=JsonSerializer.SerializeToElement(result.Value);
        var uri=new Uri(response.GetProperty("authorizationUrl").GetString()!);
        var parameters=QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("S256",parameters["code_challenge_method"].ToString());
        Assert.False(service.Read(saved!).Connected);
        var state=parameters["state"].ToString();
        Assert.IsType<ContentResult>(await controller.Callback(state,"code",null,default));
        Assert.True(service.Read(saved!).Connected);
        Assert.Null(service.Read(saved!).Nonce);
        Assert.DoesNotContain("access-secret",saved!.CredentialsJson);
        Assert.IsType<BadRequestObjectResult>(await controller.Callback(state,"code",null,default));
        Assert.IsType<BadRequestObjectResult>(await controller.Callback(state+"tamper","code",null,default));
    }
    private sealed class OAuthHandler:HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var body=await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("code_verifier=",body); Assert.Contains("grant_type=authorization_code",body);
            return new(HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"access-secret\",\"refresh_token\":\"refresh-secret\",\"expires_in\":3600}")};
        }
    }
}
