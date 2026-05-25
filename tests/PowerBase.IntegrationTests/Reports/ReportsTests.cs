using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Reports;

[Collection("PowerBase")]
public class ReportsTests : IntegrationTestBase
{
    public ReportsTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithId()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await PostAsync($"/tables/{tableId}/reports", new
        {
            name = "All Contacts",
            visibility = "Personal",
            columns = new List<long>(),
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var report = await ReadData<ReportDto>(response);
        report.Id.Should().NotBeEmpty();
        report.Name.Should().Be("All Contacts");
    }

    [Fact]
    public async Task Create_WithSpecificColumns_StoresColumnFieldIds()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var fieldId = await CreateFieldAsync(token, tableId, "Text", "Email");

        var response = await PostAsync($"/tables/{tableId}/reports", new
        {
            name = "Email Report",
            visibility = "Personal",
            columns = new List<long> { fieldId },
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var report = await ReadData<ReportDto>(response);
        report.Definition.Columns.Should().Contain(fieldId);
    }

    [Fact]
    public async Task Create_WithUnknownColumnFieldId_Returns400()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await PostAsync($"/tables/{tableId}/reports", new
        {
            name = "Bad Report",
            visibility = "Personal",
            columns = new List<long> { 999_999_999L },
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ByApp_ReturnsCreatedReports()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        await CreateReportAsync(token, tableId, name: "Report One");
        await CreateReportAsync(token, tableId, name: "Report Two");

        var response = await GetAsync($"/apps/{appId}/reports", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var reports = await ReadListData<ReportDto>(response);
        reports.Select(r => r.Name).Should().Contain(new[] { "Report One", "Report Two" });
    }

    [Fact]
    public async Task Get_ByPublicId_ReturnsReportWithDefinition()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var fieldId = await CreateFieldAsync(token, tableId, "Text", "Title");
        var reportId = await CreateReportAsync(token, tableId, columns: new List<long> { fieldId });

        var response = await GetAsync($"/reports/{reportId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await ReadData<ReportDto>(response);
        report.Id.Should().Be(reportId);
        report.Definition.Columns.Should().Contain(fieldId);
    }

    [Fact]
    public async Task Run_WithExplicitColumns_ReturnsMatchingColumnsAndRows()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var fieldId = await CreateFieldAsync(token, tableId, "Text", "Name");
        await CreateRecordAsync(token, tableId, new Dictionary<string, object?> { [$"{fieldId}"] = "Alice" });
        await CreateRecordAsync(token, tableId, new Dictionary<string, object?> { [$"{fieldId}"] = "Bob" });
        var reportId = await CreateReportAsync(token, tableId, columns: new List<long> { fieldId });

        var response = await GetAsync($"/reports/{reportId}/run", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await ReadData<ReportRunDto>(response);
        run.TotalCount.Should().Be(2);
        run.Columns.Should().ContainSingle(c => c.FieldId == fieldId && c.Name == "Name");
        run.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Run_EmptyColumns_ReturnsAllReportableFields()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var fieldId = await CreateFieldAsync(token, tableId, "Text", "Description");
        await CreateRecordAsync(token, tableId, new Dictionary<string, object?> { [$"{fieldId}"] = "Test" });
        var reportId = await CreateReportAsync(token, tableId, columns: new List<long>());

        var response = await GetAsync($"/reports/{reportId}/run", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await ReadData<ReportRunDto>(response);
        run.TotalCount.Should().Be(1);
        run.Columns.Should().ContainSingle(c => c.FieldId == fieldId);
    }

    [Fact]
    public async Task Update_ValidRequest_Returns204AndGetReflectsChange()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId, name: "Old Name");

        var patchResponse = await PatchAsync($"/reports/{reportId}", new
        {
            name = "New Name",
            visibility = "Shared",
            columns = new List<long>(),
            sortDesc = false,
        }, token);

        patchResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await GetAsync($"/reports/{reportId}", token);
        var report = await ReadData<ReportDto>(getResponse);
        report.Name.Should().Be("New Name");
    }

    [Fact]
    public async Task Update_InvalidVisibility_Returns400()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId);

        var response = await PatchAsync($"/reports/{reportId}", new
        {
            name = "R",
            visibility = "NotValid",
            columns = new List<long>(),
            sortDesc = false,
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_WithoutAuth_Returns401()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId);

        var response = await PatchAsync($"/reports/{reportId}", new
        {
            name = "R",
            visibility = "Personal",
            columns = new List<long>(),
            sortDesc = false,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_ExistingReport_Returns204()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId);

        var response = await DeleteAsync($"/reports/{reportId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_ThenGet_Returns404()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId);
        await DeleteAsync($"/reports/{reportId}", token);

        var response = await GetAsync($"/reports/{reportId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_WithoutAuth_Returns401()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId);

        var response = await DeleteAsync($"/reports/{reportId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- SetDefault ---

    [Fact]
    public async Task SetDefault_ExistingReport_Returns204()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId, name: "My Report");

        var response = await PatchAsync($"/tables/{tableId}/reports/{reportId}/set-default", new { }, token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SetDefault_OnlyOneReportIsDefault_WhenMultipleExist()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportA = await CreateReportAsync(token, tableId, name: "Report A");
        var reportB = await CreateReportAsync(token, tableId, name: "Report B");

        await PatchAsync($"/tables/{tableId}/reports/{reportA}/set-default", new { }, token);
        await PatchAsync($"/tables/{tableId}/reports/{reportB}/set-default", new { }, token);

        var listResponse = await GetAsync($"/tables/{tableId}/reports", token);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reports = await ReadListData<ReportListItemDto>(listResponse);
        reports.Count(r => r.IsDefault).Should().Be(1);
        reports.Single(r => r.IsDefault).Id.Should().Be(reportB);
    }

    [Fact]
    public async Task SetDefault_WithoutAuth_Returns401()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        var reportId = await CreateReportAsync(token, tableId);

        var response = await PatchAsync($"/tables/{tableId}/reports/{reportId}/set-default", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- ListByTable ---

    [Fact]
    public async Task ListByTable_ReturnsOnlyReportsForThatTable()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableA = await CreateTableAsync(token, appId, "TableA");
        var tableB = await CreateTableAsync(token, appId, "TableB");
        await CreateReportAsync(token, tableA, name: "Report on A");
        await CreateReportAsync(token, tableB, name: "Report on B");

        var response = await GetAsync($"/tables/{tableA}/reports", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var reports = await ReadListData<ReportListItemDto>(response);
        reports.Should().ContainSingle(r => r.Name == "Report on A");
        reports.Should().NotContain(r => r.Name == "Report on B");
    }

    [Fact]
    public async Task ListByTable_WithoutAuth_Returns401()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await GetAsync($"/tables/{tableId}/reports");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record ReportDto(Guid Id, string Name, ReportDefinitionDto Definition);
    private record ReportDefinitionDto(List<long> Columns, long? SortFieldId, bool SortDesc);
    private record ReportRunDto(List<ReportColumnDto> Columns, List<ReportRowDto> Rows, int TotalCount, int Page, int PageSize);
    private record ReportColumnDto(long FieldId, string Name, string TypeCode);
    private record ReportRowDto(Guid Id);
    private record ReportListItemDto(Guid Id, string Name, bool IsDefault);
}
