using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Pipelines;

[Collection("PowerBase")]
public class PipelineMetadataTests : IntegrationTestBase
{
    public PipelineMetadataTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task MetadataEndpoints_ReturnCompleteCurrentTenantHierarchy()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token, "Pipeline Metadata App");
        var tableId = await CreateTableAsync(token, appId, "Pipeline Metadata Table");

        var appsResponse = await GetAsync("/pipelines/metadata/apps", token);
        appsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var apps = await ReadListData<MetadataItem>(appsResponse);
        apps.Should().Contain(item => item.PublicId == appId && item.Name == "Pipeline Metadata App");

        var tablesResponse = await GetAsync($"/pipelines/metadata/apps/{appId}/tables", token);
        tablesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tables = await ReadListData<MetadataItem>(tablesResponse);
        tables.Should().Contain(item => item.PublicId == tableId && item.Name == "Pipeline Metadata Table");

        var fieldsResponse = await GetAsync($"/pipelines/metadata/tables/{tableId}/fields", token);
        fieldsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fields = await ReadListData<FieldMetadataItem>(fieldsResponse);
        fields.Should().NotBeEmpty();
        fields.Should().OnlyContain(field => field.PublicId != Guid.Empty && field.Fid.HasValue);
    }

    [Theory]
    [InlineData("/pipelines/metadata/apps")]
    public async Task MetadataEndpoints_RequireAuthentication(string path)
    {
        var response = await GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record MetadataItem(Guid PublicId, string Name);
    private sealed record FieldMetadataItem(Guid PublicId, string Name, int? Fid);
}
