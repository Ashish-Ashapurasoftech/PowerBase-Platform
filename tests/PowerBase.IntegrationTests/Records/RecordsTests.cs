using System.Net;
using System.Text.Json;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Records;

[Collection("PowerBase")]
public class RecordsTests : IntegrationTestBase
{
    public RecordsTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Create_WithFieldValues_Returns201WithId()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var fieldId = await CreateFieldAsync(token, tableId, "Text", "Title");

        var response = await PostAsync($"/tables/{tableId}/records", new
        {
            fields = new Dictionary<string, object?> { [$"{fieldId}"] = "Hello World" },
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var record = await ReadData<RecordDto>(response);
        record.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_WithoutAuth_Returns401()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await PostAsync($"/tables/{tableId}/records", new { fields = new { } });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_ReturnsPagedRecords()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        await CreateRecordAsync(token, tableId);
        await CreateRecordAsync(token, tableId);
        await CreateRecordAsync(token, tableId);

        var response = await GetAsync($"/tables/{tableId}/records?page=1&pageSize=2", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var records = await ReadListData<RecordDto>(response);
        records.Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_ByPublicId_ReturnsRecordWithFields()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var fieldId = await CreateFieldAsync(token, tableId, "Text", "Name");
        var recordId = await CreateRecordAsync(token, tableId,
            new Dictionary<string, object?> { [$"{fieldId}"] = "Alice" });

        var response = await GetAsync($"/tables/{tableId}/records/{recordId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var record = await ReadData<RecordDto>(response);
        record.Id.Should().Be(recordId);
        record.Fields.Should().ContainKey($"{fieldId}");
    }

    [Fact]
    public async Task Update_ExistingRecord_Returns204AndPersistsValue()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var fieldId = await CreateFieldAsync(token, tableId, "Text", "Title");
        var recordId = await CreateRecordAsync(token, tableId,
            new Dictionary<string, object?> { [$"{fieldId}"] = "Original" });

        var patchResponse = await PatchAsync($"/tables/{tableId}/records/{recordId}",
            new { fields = new Dictionary<string, object?> { [$"{fieldId}"] = "Updated" } },
            token);

        patchResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await GetAsync($"/tables/{tableId}/records/{recordId}", token);
        var record = await ReadData<RecordDto>(getResponse);
        var fieldValue = ((JsonElement)record.Fields[$"{fieldId}"]!).GetString();
        fieldValue.Should().Be("Updated");
    }

    [Fact]
    public async Task Delete_ExistingRecord_Returns204()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var recordId = await CreateRecordAsync(token, tableId);

        var response = await DeleteAsync($"/tables/{tableId}/records/{recordId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Get_DeletedRecord_Returns404()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var recordId = await CreateRecordAsync(token, tableId);
        await DeleteAsync($"/tables/{tableId}/records/{recordId}", token);

        var response = await GetAsync($"/tables/{tableId}/records/{recordId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_CrossTenantTableAccess_Returns404()
    {
        var (tokenA, _) = await SignupAsync();
        var appIdA = await CreateAppAsync(tokenA);
        var tableIdA = await CreateTableAsync(tokenA, appIdA);
        await CreateRecordAsync(tokenA, tableIdA);

        // Tenant B tries to access Tenant A's table — TenantId filter blocks it
        var (tokenB, _) = await SignupAsync();

        var response = await GetAsync($"/tables/{tableIdA}/records", tokenB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record RecordDto(Guid Id, DateTime CreatedOn, Dictionary<string, object?> Fields);
}
