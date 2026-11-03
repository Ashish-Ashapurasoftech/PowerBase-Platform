using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Apps;

[Collection("PowerBase")]
public class AppCopyTests : IntegrationTestBase
{
    public AppCopyTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Copy_ValidRequest_Returns201WithSameTablesAndNoRecords()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token, "Copy Source App");
        var departments = await CreateTableAsync(token, appId, "Departments");
        await CreateTableAsync(token, appId, "Employees");
        await CreateFieldAsync(token, departments, "Text", "Department Name");
        await CreateRecordAsync(token, departments);

        var response = await PostAsync($"/apps/{appId}/copy", new { name = "Copy of Copy Source App" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var copy = await ReadData<CopyDto>(response);
        copy.PublicId.Should().NotBe(appId);
        copy.Name.Should().Be("Copy of Copy Source App");

        var sourceTables = await ReadListData<TableDto>(await GetAsync($"/apps/{appId}/tables", token));
        var copiedTables = await ReadListData<TableDto>(await GetAsync($"/apps/{copy.PublicId}/tables", token));
        copiedTables.Select(t => t.Name).Should().BeEquivalentTo(sourceTables.Select(t => t.Name));
        copiedTables.Select(t => t.PublicId).Should().NotIntersectWith(sourceTables.Select(t => t.PublicId));

        var copiedDepartments = copiedTables.Single(t => t.Name == "Departments");
        var records = await ReadListData<RecordDto>(await GetAsync($"/tables/{copiedDepartments.PublicId}/records", token));
        records.Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_NameAlreadyUsed_Returns409()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token, "Copy Name Clash App");

        var response = await PostAsync($"/apps/{appId}/copy", new { name = "Copy Name Clash App" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Copy_MissingName_Returns400()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);

        var response = await PostAsync($"/apps/{appId}/copy", new { name = "" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Copy_WithoutAuth_Returns401()
    {
        var response = await PostAsync($"/apps/{Guid.NewGuid()}/copy", new { name = "Copy" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record CopyDto(Guid PublicId, string Name, List<WarningDto> Warnings);
    private record WarningDto(string Code, string Message);
    private record TableDto(Guid PublicId, string Name);
    private record RecordDto(Guid Id);
}
