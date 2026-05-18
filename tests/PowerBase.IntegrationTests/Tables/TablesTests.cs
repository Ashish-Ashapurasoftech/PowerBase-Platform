using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Tables;

[Collection("PowerBase")]
public class TablesTests : IntegrationTestBase
{
    public TablesTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithPhysicalTableName()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);

        var response = await PostAsync($"/apps/{appId}/tables", new { name = "Contacts" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var table = await ReadData<TableDto>(response);
        table.PublicId.Should().NotBeEmpty();
        table.Name.Should().Be("Contacts");
        table.PhysicalTableName.Should().MatchRegex(@"t_\d+");
    }

    [Fact]
    public async Task Create_WithoutAuth_Returns401()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);

        var response = await PostAsync($"/apps/{appId}/tables", new { name = "Contacts" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        await PostAsync($"/apps/{appId}/tables", new { name = "Orders" }, token);

        var response = await PostAsync($"/apps/{appId}/tables", new { name = "Orders" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_ReturnsTablesForApp()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        await CreateTableAsync(token, appId, "Orders");
        await CreateTableAsync(token, appId, "Products");

        var response = await GetAsync($"/apps/{appId}/tables", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tables = await ReadListData<TableDto>(response);
        tables.Select(t => t.Name).Should().Contain(new[] { "Orders", "Products" });
    }

    [Fact]
    public async Task Get_ByPublicId_IncludesFields()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId, "Items");
        await CreateFieldAsync(token, tableId, "Text", "Title");

        var response = await GetAsync($"/tables/{tableId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var table = await ReadData<TableWithFieldsDto>(response);
        table.PublicId.Should().Be(tableId);
        table.Fields.Should().ContainSingle(f => f.Name == "Title");
    }

    [Fact]
    public async Task Delete_ExistingTable_Returns204()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await DeleteAsync($"/tables/{tableId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Get_DeletedTable_Returns404()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        await DeleteAsync($"/tables/{tableId}", token);

        var response = await GetAsync($"/tables/{tableId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record TableDto(Guid PublicId, string Name, string? PhysicalTableName);
    private record TableWithFieldsDto(Guid PublicId, string Name, List<FieldDto> Fields);
    private record FieldDto(long Id, string Name, string TypeCode);
}
