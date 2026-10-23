using System.Net;
using System.Text.Json;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.ConnectedTables;

[Collection("PowerBase")]
public class ConnectedTablesTests : IntegrationTestBase
{
    public ConnectedTablesTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    private sealed record FieldDto(Guid PublicId, int Fid);
    private sealed record ConnectionDto(Guid PublicId, string Status, string RowVersion);
    private sealed record SourceTableDto(Guid PublicId, string Name);
    private sealed record RunDto(Guid PublicId, string Status, long Inserted, string? ErrorDetail);
    private sealed record ConnectedTableDto(Guid PublicId, Guid TablePublicId, string Status, RunDto? ActiveRun, RunDto? LastRun);

    private async Task<int> CreateFieldFidAsync(string token, Guid tableId, string typeCode, string label)
    {
        var r = await PostAsync($"/tables/{tableId}/fields", new { typeCode, label }, token);
        r.EnsureSuccessStatusCode();
        return (await ReadData<FieldDto>(r)).Fid;
    }

    /// <summary>A source app with a "Clients" table holding two records; returns the app, table and Name field FID.</summary>
    private async Task<(Guid App, Guid Table, int NameFid)> SourceAsync(string token)
    {
        var app = await CreateAppAsync(token, $"Source {Guid.NewGuid():N}");
        var table = await CreateTableAsync(token, app, "Clients");
        var nameFid = await CreateFieldFidAsync(token, table, "Text", "Client name");
        await CreateRecordAsync(token, table, new() { [nameFid.ToString()] = "Acme" });
        await CreateRecordAsync(token, table, new() { [nameFid.ToString()] = "Globex" });
        return (app, table, nameFid);
    }

    private async Task<ConnectionDto> ConnectAsync(string token, Guid sourceApp)
    {
        var r = await PostAsync("/connected/connections", new { name = "Same realm", authMode = "same_realm_user", appId = sourceApp }, token);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return await ReadData<ConnectionDto>(r);
    }

    private async Task<ConnectedTableDto> WaitForRefreshAsync(string token, Guid connectedTableId)
    {
        for (var i = 0; i < 60; i++)
        {
            var t = await ReadData<ConnectedTableDto>(await GetAsync($"/connected-tables/{connectedTableId}", token));
            if (t.ActiveRun is null && t.LastRun is not null) return t;
            await Task.Delay(500);
        }
        throw new TimeoutException("The first refresh did not finish.");
    }

    [Fact]
    public async Task Create_SameRealm_CopiesRecordsAndBlocksManualWrites()
    {
        var (token, _) = await SignupAsync();
        var (sourceApp, sourceTable, nameFid) = await SourceAsync(token);
        var destinationApp = await CreateAppAsync(token, $"Destination {Guid.NewGuid():N}");
        var connection = await ConnectAsync(token, sourceApp);

        var tables = await ReadListData<SourceTableDto>(await GetAsync($"/connected/connections/{connection.PublicId}/tables", token));
        tables.Should().Contain(t => t.PublicId == sourceTable);

        var created = await PostAsync("/connected-tables", new
        {
            appId = destinationApp, name = "Clients copy", connectionId = connection.PublicId, sourceTableId = sourceTable,
            sourceFids = new[] { nameFid }, updateMode = "mirror", notifyOnFailure = false,
        }, token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var connected = await ReadData<ConnectedTableDto>(created);

        var refreshed = await WaitForRefreshAsync(token, connected.PublicId);
        refreshed.LastRun!.Status.Should().Be("success", refreshed.LastRun.ErrorDetail);
        refreshed.LastRun.Inserted.Should().Be(2);

        var add = await PostAsync($"/tables/{connected.TablePublicId}/records", new { fields = new Dictionary<string, object?>() }, token);
        add.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await add.Content.ReadAsStringAsync()).Should().Contain("CONNECTED_TABLE_READ_ONLY");
    }

    [Fact]
    public async Task Refresh_WhileRefreshing_Returns409OrAccepted()
    {
        var (token, _) = await SignupAsync();
        var (sourceApp, sourceTable, nameFid) = await SourceAsync(token);
        var destinationApp = await CreateAppAsync(token);
        var connection = await ConnectAsync(token, sourceApp);
        var connected = await ReadData<ConnectedTableDto>(await PostAsync("/connected-tables", new
        {
            appId = destinationApp, name = "Copy", connectionId = connection.PublicId, sourceTableId = sourceTable,
            sourceFids = new[] { nameFid }, updateMode = "append", notifyOnFailure = false,
        }, token));

        var response = await PostAsync($"/connected-tables/{connected.PublicId}/refresh", new { }, token);

        // The first refresh may still be queued (409) or already done (a new one is accepted).
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DeleteConnection_InUse_Returns409()
    {
        var (token, _) = await SignupAsync();
        var (sourceApp, sourceTable, nameFid) = await SourceAsync(token);
        var destinationApp = await CreateAppAsync(token);
        var connection = await ConnectAsync(token, sourceApp);
        await PostAsync("/connected-tables", new
        {
            appId = destinationApp, name = "Copy", connectionId = connection.PublicId, sourceTableId = sourceTable,
            sourceFids = new[] { nameFid }, updateMode = "mirror", notifyOnFailure = false,
        }, token);

        var response = await DeleteAsync($"/connected/connections/{connection.PublicId}?rowVersion={Uri.EscapeDataString(connection.RowVersion)}", token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CrossRealm_WithoutApproval_Returns403DataShareNotApproved()
    {
        var (token, _) = await SignupAsync();
        var response = await PostAsync("/connected/connections", new
        {
            name = "Other", authMode = "user_token", realm = "no-such-realm-" + Guid.NewGuid().ToString("N")[..8], appId = Guid.NewGuid(), userToken = "pb_ut_x",
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/connected/connections")]
    [InlineData("/connected/data-sharing/incoming")]
    public async Task Endpoints_WithoutAuth_Return401(string url)
    {
        (await GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DataSharingPolicy_TenantAdmin_CanReadAndSave()
    {
        var (token, _) = await SignupAsync();
        var saved = await PutAsync("/connected/data-sharing/policy", new { outboundPolicy = "disabled", inboundEnabled = true }, token);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var json = JsonSerializer.Deserialize<JsonElement>(await (await GetAsync("/connected/data-sharing/policy", token)).Content.ReadAsStringAsync());
        json.GetProperty("data").GetProperty("outboundPolicy").GetString().Should().Be("disabled");
    }
}
