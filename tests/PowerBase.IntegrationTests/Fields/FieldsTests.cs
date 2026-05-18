using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Fields;

[Collection("PowerBase")]
public class FieldsTests : IntegrationTestBase
{
    public FieldsTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Create_TextType_Returns201WithFieldIdAndPhysicalColumn()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await PostAsync($"/tables/{tableId}/fields", new
        {
            typeCode = "Text",
            name = "Full Name",
            isRequired = true,
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var field = await ReadData<FieldDto>(response);
        field.Id.Should().BeGreaterThan(0);
        field.TypeCode.Should().Be("Text");
        field.PhysicalColumnName.Should().MatchRegex(@"^f_\d+$");
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Number")]
    [InlineData("Date")]
    [InlineData("Boolean")]
    public async Task Create_AllFourSkeletonTypes_Return201(string typeCode)
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await PostAsync($"/tables/{tableId}/fields", new
        {
            typeCode,
            name = $"My {typeCode} Field",
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: $"'{typeCode}' is a valid skeleton field type");
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        await PostAsync($"/tables/{tableId}/fields", new { typeCode = "Text", name = "Status" }, token);

        var response = await PostAsync($"/tables/{tableId}/fields", new { typeCode = "Text", name = "Status" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_InvalidTypeCode_Returns400()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await PostAsync($"/tables/{tableId}/fields", new
        {
            typeCode = "NonExistentType",
            name = "Bad Field",
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ReturnsFieldsForTable()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);
        await CreateFieldAsync(token, tableId, "Text", "First Name");
        await CreateFieldAsync(token, tableId, "Number", "Age");

        var response = await GetAsync($"/tables/{tableId}/fields", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fields = await ReadListData<FieldDto>(response);
        fields.Select(f => f.Name).Should().Contain(new[] { "First Name", "Age" });
    }

    [Fact]
    public async Task List_WithoutAuth_Returns401()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        var response = await GetAsync($"/tables/{tableId}/fields");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record FieldDto(long Id, Guid PublicId, string Name, string TypeCode, string? PhysicalColumnName);
}
