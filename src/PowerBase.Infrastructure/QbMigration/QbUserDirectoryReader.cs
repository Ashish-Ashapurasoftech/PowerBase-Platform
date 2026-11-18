using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PowerBase.Application.QbMigration;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Reads an app's roles and users from QuickBase, read-only:
/// <list type="bullet">
/// <item>API_GetRoleInfo: the app's roles, in the order of its Roles page (QuickBase's precedence).</item>
/// <item>API_UserRoles: every user and group with their roles (built-in "None" is role 9, a Super User without an app role is role 0).</item>
/// <item>API_GetUsersInGroup: the members of each group, who take the group's roles.</item>
/// <item>Get users (REST): email addresses by QuickBase user id.</item>
/// <item>API_GetUserInfo: "Registered" (isVerified) per email.</item>
/// </list>
/// The token goes in the request body, never in a URL. Requests share the migration's rate limit.</summary>
public sealed class QbUserDirectoryReader(HttpClient http, QuickBaseClientOptions options, QbRateLimiter limiter)
{
    public async Task<QbUserDirectory> ReadAsync(string appId, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var roles = (await XmlAsync(appId, "API_GetRoleInfo", "", ct)).Descendants("role").Select(ParseRole).ToList();

        var users = new List<(string Id, string Name, string? First, string? Last, string? Email, List<QbRole> Roles)>();
        foreach (var u in (await XmlAsync(appId, "API_UserRoles", "", ct)).Descendants("users").Elements("user"))
        {
            var id = (string?)u.Attribute("id") ?? "";
            var userRoles = u.Element("roles")?.Elements("role").Select(ParseRole).ToList() ?? [];
            if ((string?)u.Attribute("type") == "group")
            {
                var members = await GroupMembersAsync(id, 0, ct);
                if (members.Count == 0) warnings.Add($"Group '{(string?)u.Element("name")}' ({id}) has no members QuickBase could list.");
                users.AddRange(members.Select(m => (m.Id, m.Name, m.First, m.Last, m.Email, userRoles)));
                continue;
            }
            users.Add((id, (string?)u.Element("name") ?? id, (string?)u.Element("firstName"), (string?)u.Element("lastName"), null, userRoles));
        }

        var emails = await EmailsAsync(appId, null, ct);
        // Super Users reach the app through the account, not an app role, so the app's user list leaves them out: look the
        // missing ones up account-wide (needs a realm/account admin token; without it they stay unresolved and are reported).
        var missing = users.Where(u => u.Email is null && !emails.ContainsKey(u.Id)).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        if (missing.Count > 0)
        {
            try { foreach (var (k, v) in await EmailsAsync(null, missing, ct)) emails.TryAdd(k, v); }
            catch (QuickBaseRequestException ex) { warnings.Add($"Account-wide user lookup for {missing.Count} user(s) without an app email failed: {ex.Message}"); }
        }
        var registered = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var result = new List<QbAppUser>();
        foreach (var u in users)
        {
            var email = u.Email ?? emails.GetValueOrDefault(u.Id);
            if (email is not null && !registered.ContainsKey(email)) registered[email] = await IsRegisteredAsync(email, ct);
            result.Add(new QbAppUser(u.Id, email, u.Name, u.First, u.Last, email is null ? null : registered[email], u.Roles));
        }
        return new QbUserDirectory(roles, result, warnings);
    }

    private static QbRole ParseRole(XElement r) => new(
        int.TryParse((string?)r.Attribute("id"), out var id) ? id : -1,
        ((string?)r.Element("name") ?? "").Trim(),
        int.TryParse((string?)r.Element("access")?.Attribute("id"), out var a) ? a : 0);

    private async Task<List<(string Id, string Name, string? First, string? Last, string? Email)>> GroupMembersAsync(string groupId, int depth, CancellationToken ct)
    {
        var found = new List<(string, string, string?, string?, string?)>();
        if (depth > 5) return found;
        var doc = await XmlAsync("main", "API_GetUsersInGroup", $"<gid>{SecurityElement.Escape(groupId)}</gid><includeAllMgrs>false</includeAllMgrs>", ct);
        foreach (var u in doc.Descendants("users").Elements("user"))
        {
            var first = (string?)u.Element("firstName");
            var last = (string?)u.Element("lastName");
            found.Add(((string?)u.Attribute("id") ?? "", $"{first} {last}".Trim(), first, last, (string?)u.Element("email")));
        }
        foreach (var g in doc.Descendants("subgroups").Elements("group"))
            found.AddRange(await GroupMembersAsync((string?)g.Attribute("id") ?? "", depth + 1, ct));
        return found;
    }

    /// <summary>Email by QuickBase user id (REST "Get users", paged): the app's users, or with <paramref name="appId"/> null the
    /// whole account's, stopping as soon as every id in <paramref name="wanted"/> is found.</summary>
    private async Task<Dictionary<string, string>> EmailsAsync(string? appId, IReadOnlySet<string>? wanted, CancellationToken ct)
    {
        var emails = new Dictionary<string, string>(StringComparer.Ordinal);
        string? next = null;
        for (var page = 0; page < 1000; page++)
        {
            var fields = new Dictionary<string, object>();
            if (appId is not null) fields["appIds"] = new[] { appId };
            if (next is not null) fields["nextPageToken"] = next;
            var body = JsonSerializer.Serialize(fields);
            await limiter.WaitAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.BaseUrl), "users"))
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("QB-Realm-Hostname", options.RealmHostname);
            request.Headers.Authorization = new AuthenticationHeaderValue("QB-USER-TOKEN", options.UserToken);
            request.Headers.UserAgent.ParseAdd("PowerBase-QbMigration/1.0");
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new QuickBaseRequestException((int)response.StatusCode, $"QuickBase Get users failed: {(int)response.StatusCode} {Shorten(text)}");
            using var doc = JsonDocument.Parse(text);
            foreach (var u in doc.RootElement.GetProperty("users").EnumerateArray())
                if (u.TryGetProperty("hashId", out var h) && u.TryGetProperty("emailAddress", out var e) && e.ValueKind == JsonValueKind.String)
                    if (wanted is null || wanted.Contains(h.GetString()!)) emails[h.GetString()!] = e.GetString()!;
            if (wanted is not null && wanted.All(emails.ContainsKey)) break;
            next = doc.RootElement.TryGetProperty("metadata", out var m) && m.TryGetProperty("nextPageToken", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            if (string.IsNullOrEmpty(next)) break;
        }
        return emails;
    }

    private async Task<bool?> IsRegisteredAsync(string email, CancellationToken ct)
    {
        var doc = await XmlAsync("main", "API_GetUserInfo", $"<email>{SecurityElement.Escape(email)}</email>", ct, allowError: true);
        var verified = (string?)doc.Descendants("isVerified").FirstOrDefault();
        return verified switch { "1" => true, "0" => false, _ => null };
    }

    /// <summary>One QuickBase XML API call (POST, token in the body). QuickBase errors (errcode ≠ 0) throw unless allowed.</summary>
    private async Task<XDocument> XmlAsync(string dbid, string action, string parameters, CancellationToken ct, bool allowError = false)
    {
        var body = $"<qdbapi><usertoken>{SecurityElement.Escape(options.UserToken)}</usertoken>{parameters}</qdbapi>";
        for (var attempt = 1; ; attempt++)
        {
            await limiter.WaitAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{options.RealmHostname}/db/{Uri.EscapeDataString(dbid)}")
                { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
            request.Headers.Add("QUICKBASE-ACTION", action);
            request.Headers.UserAgent.ParseAdd("PowerBase-QbMigration/1.0");
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new QuickBaseRequestException((int)response.StatusCode, $"QuickBase {action} failed: {(int)response.StatusCode} {Shorten(text)}");
            var doc = XDocument.Parse(text);
            var code = (string?)doc.Root?.Element("errcode");
            if (code != "0" && !allowError)
                throw new QuickBaseRequestException(400, $"QuickBase {action}: error {code} {(string?)doc.Root?.Element("errtext")} {(string?)doc.Root?.Element("errdetail")}");
            return doc;
        }
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] : s;
}
