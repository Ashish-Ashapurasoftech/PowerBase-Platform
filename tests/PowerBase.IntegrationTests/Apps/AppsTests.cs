using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Apps;

[Collection("PowerBase")]
public class AppsTests : IntegrationTestBase
{
    public AppsTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithPublicId()
    {
        var (token, _) = await SignupAsync();

        var response = await PostAsync("/apps", new { name = "My First App" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var app = await ReadData<AppDto>(response);
        app.PublicId.Should().NotBeEmpty();
        app.Name.Should().Be("My First App");
        app.Status.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Create_WithoutAuth_Returns401()
    {
        var response = await PostAsync("/apps", new { name = "Unauthorized App" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_MissingName_Returns400()
    {
        var (token, _) = await SignupAsync();

        var response = await PostAsync("/apps", new { name = "" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ReturnsCreatedApps()
    {
        var (token, _) = await SignupAsync();
        await CreateAppAsync(token, "Alpha App");
        await CreateAppAsync(token, "Beta App");

        var response = await GetAsync("/apps", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apps = await ReadListData<AppDto>(response);
        apps.Select(a => a.Name).Should().Contain(new[] { "Alpha App", "Beta App" });
    }

    [Fact]
    public async Task List_DoesNotReturnOtherTenantsApps()
    {
        var (tokenA, _) = await SignupAsync();
        await CreateAppAsync(tokenA, "Tenant A App");

        var (tokenB, _) = await SignupAsync();

        var response = await GetAsync("/apps", tokenB);
        var apps = await ReadListData<AppDto>(response);
        apps.Select(a => a.Name).Should().NotContain("Tenant A App");
    }

    [Fact]
    public async Task Get_ByPublicId_ReturnsApp()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token, "Get Test App");

        var response = await GetAsync($"/apps/{appId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var app = await ReadData<AppDto>(response);
        app.PublicId.Should().Be(appId);
        app.Name.Should().Be("Get Test App");
    }

    [Fact]
    public async Task Get_NonExistentApp_Returns404()
    {
        var (token, _) = await SignupAsync();

        var response = await GetAsync($"/apps/{Guid.NewGuid()}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ExistingApp_Returns204()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);

        var response = await DeleteAsync($"/apps/{appId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Get_DeletedApp_Returns404()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        await DeleteAsync($"/apps/{appId}", token);

        var response = await GetAsync($"/apps/{appId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record AppDto(Guid PublicId, string Name, string Status);
}
