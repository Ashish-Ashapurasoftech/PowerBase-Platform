using Microsoft.AspNetCore.DataProtection;
using NSubstitute;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using System.Net;
using System.Text.Json;
namespace PowerBase.UnitTests.Pipelines;
public class RequestConnectionServiceTests
{
    [Fact] public async Task CredentialsAreEncryptedAndBoundToOwnerAndPipeline()
    {
        var repo=Substitute.For<IPipelineRepository>(); var factory=Substitute.For<IHttpClientFactory>();
        var service=new RequestConnectionService(repo,new EphemeralDataProtectionProvider(),factory);
        var row=new PipelineConnection { PublicId=Guid.NewGuid(),PipelineId=7,CreatedBy=9,Type="http-request" };
        row.CredentialsJson=service.Protect(new(){Connected=true,Config=new(){Password="secret-password",AuthType="Basic Authentication"}});
        Assert.DoesNotContain("secret-password",row.CredentialsJson);
        repo.GetConnectionByPublicIdAsync(row.PublicId,default).Returns(row);
        Assert.Equal("secret-password",(await service.ResolveAsync(row.PublicId,7,9,default)).Password);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ResolveAsync(row.PublicId,7,10,default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ResolveAsync(row.PublicId,8,9,default));
    }
    [Fact] public async Task RefreshPersistsRotatedTokenEncrypted()
    {
        var repo=Substitute.For<IPipelineRepository>(); var factory=Substitute.For<IHttpClientFactory>();
        factory.CreateClient("PipelineMakeRequest").Returns(new HttpClient(new TokenHandler()));
        var service=new RequestConnectionService(repo,new EphemeralDataProtectionProvider(),factory);
        var row=new PipelineConnection {PublicId=Guid.NewGuid(),PipelineId=7,CreatedBy=9,Type="http-request"};
        row.CredentialsJson=service.Protect(new(){Connected=true,ExpiresAt=DateTimeOffset.UtcNow.AddHours(-1),Config=new(){AuthType="OAuth 2.0",OAuthGrantType="Authorization code",OAuthRefreshToken="old-refresh",OAuthTokenEndpoint="https://example.com/token",OAuthClientId="client"}});
        repo.GetConnectionByPublicIdAsync(row.PublicId,default).Returns(row);
        var config=await service.ResolveAsync(row.PublicId,7,9,default);
        Assert.Equal("new-access",config.OAuthAccessToken);
        Assert.Equal("new-refresh",service.Read(row).Config.OAuthRefreshToken);
        Assert.DoesNotContain("new-refresh",row.CredentialsJson);
        await repo.Received(1).UpdateConnectionAsync(row,ct:default);
    }
    private sealed class TokenHandler:HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Contains("refresh_token=old-refresh",await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}")};
        }
    }
}
