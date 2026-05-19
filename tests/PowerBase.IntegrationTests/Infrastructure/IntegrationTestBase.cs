using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PowerBase.IntegrationTests.Infrastructure;

public abstract class IntegrationTestBase
{
    protected readonly HttpClient Client;
    protected static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    protected IntegrationTestBase(PowerBaseWebApplicationFactory factory)
    {
        Client = factory.CreateClient();
    }

    // HTTP helpers

    protected async Task<HttpResponseMessage> PostAsync(string url, object body, string? token = null) =>
        await SendAsync(HttpMethod.Post, url, Serialize(body), token);

    protected async Task<HttpResponseMessage> GetAsync(string url, string? token = null) =>
        await SendAsync(HttpMethod.Get, url, null, token);

    protected async Task<HttpResponseMessage> PatchAsync(string url, object body, string? token = null) =>
        await SendAsync(HttpMethod.Patch, url, Serialize(body), token);

    protected async Task<HttpResponseMessage> DeleteAsync(string url, string? token = null) =>
        await SendAsync(HttpMethod.Delete, url, null, token);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, StringContent? content, string? token)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Client.SendAsync(request);
    }

    private static StringContent Serialize(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // Response helpers

    protected async Task<T> ReadData<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        return doc.GetProperty("data").Deserialize<T>(JsonOpts)!;
    }

    protected async Task<List<T>> ReadListData<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        return doc.GetProperty("data").Deserialize<List<T>>(JsonOpts)!;
    }

    // Setup helpers — each call creates its own tenant via a unique email

    protected async Task<(string Token, string Email)> SignupAsync(
        string? email = null, string? password = null)
    {
        var e = email ?? $"test-{Guid.NewGuid():N}@example.com";
        var p = password ?? "Password123!";

        // Step 1: create user, get identity token
        var signupRes = await PostAsync("/auth/signup", new
        {
            email = e,
            password = p,
            name = "Test User",
        });
        signupRes.EnsureSuccessStatusCode();
        var identity = await ReadData<MinimalIdentity>(signupRes);

        // Step 2: create tenant with identity token → returns scoped JWT directly
        var tenantRes = await PostAsync("/tenants",
            new { name = $"Tenant {Guid.NewGuid():N}" },
            identity.IdentityToken);
        tenantRes.EnsureSuccessStatusCode();
        var auth = await ReadData<MinimalAuth>(tenantRes);

        return (auth.Token, e);
    }

    protected async Task<Guid> CreateAppAsync(string token, string? name = null)
    {
        var r = await PostAsync("/apps", new { name = name ?? $"App {Guid.NewGuid():N}" }, token);
        r.EnsureSuccessStatusCode();
        return (await ReadData<MinimalPublicId>(r)).PublicId;
    }

    protected async Task<Guid> CreateTableAsync(string token, Guid appId, string? name = null)
    {
        var r = await PostAsync($"/apps/{appId}/tables",
            new { name = name ?? $"Table {Guid.NewGuid():N}" }, token);
        r.EnsureSuccessStatusCode();
        return (await ReadData<MinimalPublicId>(r)).PublicId;
    }

    protected async Task<long> CreateFieldAsync(
        string token, Guid tableId, string typeCode = "Text", string? name = null)
    {
        var r = await PostAsync($"/tables/{tableId}/fields",
            new { typeCode, name = name ?? $"Field {Guid.NewGuid():N}" }, token);
        r.EnsureSuccessStatusCode();
        return (await ReadData<MinimalFieldId>(r)).Id;
    }

    protected async Task<Guid> CreateRecordAsync(
        string token, Guid tableId, Dictionary<string, object?>? fields = null)
    {
        var r = await PostAsync($"/tables/{tableId}/records",
            new { fields = fields ?? new Dictionary<string, object?>() }, token);
        r.EnsureSuccessStatusCode();
        return (await ReadData<MinimalId>(r)).Id;
    }

    protected async Task<Guid> CreateReportAsync(
        string token, Guid tableId, List<long>? columns = null, string? name = null)
    {
        var r = await PostAsync($"/tables/{tableId}/reports", new
        {
            name = name ?? $"Report {Guid.NewGuid():N}",
            visibility = "Personal",
            columns = columns ?? new List<long>(),
        }, token);
        r.EnsureSuccessStatusCode();
        return (await ReadData<MinimalId>(r)).Id;
    }

    private record MinimalAuth(string Token);
    private record MinimalIdentity(string IdentityToken);
    private record MinimalPublicId(Guid PublicId);
    private record MinimalId(Guid Id);
    private record MinimalFieldId(long Id);
}
