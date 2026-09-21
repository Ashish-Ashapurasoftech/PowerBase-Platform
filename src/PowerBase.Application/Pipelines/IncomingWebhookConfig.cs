using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Scriban;
using Scriban.Runtime;

namespace PowerBase.Application.Pipelines;

/// <summary>The same definition is validated on save and before accepting a public request.</summary>
public sealed class IncomingWebhookConfig
{
    public string AuthType { get; set; } = "no-auth";
    public string? AuthSecret { get; set; }
    public string? JsonSchema { get; set; }
    public string MethodType { get; set; } = "ANY BELOW";
    public bool ProxyViaOnPremisesAgent { get; set; }
    public string JwtAlgorithm { get; set; } = "RS256";
    public string? PublicKey { get; set; }
    public List<IncomingWebhookGroup> Conditions { get; set; } = new();
    public static readonly string[] Methods = { "GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS" };
    public static readonly string[] Algorithms = { "RS256", "RS384", "RS512", "ES256", "ES384", "ES512" };
    public static readonly string[] Operators = { "is", "is-not", "contains", "not-contains", "starts-with", "not-starts-with", "ends-with", "not-ends-with", "matches-regex", "not-matches-regex", "is-empty", "is-not-empty" };
    public bool? IsSimpleFilter { get; set; }
    public string? AdvancedQuery { get; set; }
    public List<IncomingWebhookEditorGroup>? FilterGroups { get; set; }
    public static IncomingWebhookConfig Read(string? json)
    {
        var config = JsonSerializer.Deserialize<IncomingWebhookConfig>(
            string.IsNullOrWhiteSpace(json) ? "{}" : json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Incoming Request configuration is required.");
        // The editor state is authoritative, including when saved through an API client.
        if (config.IsSimpleFilter == false)
            config.Conditions = string.IsNullOrWhiteSpace(config.AdvancedQuery) ? new() : new()
            {
                new() { Rules = new() { new() { Field = "expression", Value = config.AdvancedQuery.Trim() } } }
            };
        else if (config.FilterGroups != null)
            config.Conditions = ExpandGroups(config.FilterGroups, 0).Select(rules => new IncomingWebhookGroup { Rules = rules }).ToList();
        return config;
    }

    private static List<List<IncomingWebhookRule>> ExpandGroups(List<IncomingWebhookEditorGroup> groups, int depth)
    {
        if (depth > 16 || groups.Count > 50) throw new ArgumentException("Too many nested trigger conditions.");
        var result = new List<List<IncomingWebhookRule>>();
        foreach (var group in groups)
        {
            if (group?.Rules == null || group.Rules.Count > 50) throw new ArgumentException("Invalid trigger condition group.");
            var combinations = new List<List<IncomingWebhookRule>> { new() };
            foreach (var rule in group.Rules)
            {
                if (rule == null) throw new ArgumentException("Invalid trigger condition.");
                if (rule.Type == "nested")
                {
                    var alternatives = ExpandGroups(rule.Groups ?? new(), depth + 1);
                    // Empty editor placeholders must not erase siblings or introduce an always-true OR branch.
                    if (alternatives.Count == 0) continue;
                    if (combinations.Count * alternatives.Count > 50) throw new ArgumentException("Too many trigger condition combinations.");
                    combinations = combinations.SelectMany(prefix => alternatives.Select(suffix => prefix.Concat(suffix).ToList())).ToList();
                }
                else if (!rule.IsBlank)
                    foreach (var combination in combinations) combination.Add(rule);
                if (combinations.Any(rules => rules.Count > 50)) throw new ArgumentException("Too many trigger conditions.");
            }
            result.AddRange(combinations.Where(rules => rules.Count > 0));
            if (result.Count > 50) throw new ArgumentException("Too many trigger condition combinations.");
        }
        return result;
    }

    public void Validate()
    {
        if (AuthType is not ("no-auth" or "jwt" or "bearer")) throw new ArgumentException("Unsupported authentication schema.");
        if (MethodType != "ANY BELOW" && !Methods.Contains(MethodType)) throw new ArgumentException("Unsupported incoming request method.");
        if (AuthType == "bearer" && (string.IsNullOrWhiteSpace(AuthSecret) || AuthSecret.Contains('•')))
            throw new ArgumentException("The legacy bearer token must contain a real secret.");
        if (AuthType == "jwt")
        {
            if (!Algorithms.Contains(JwtAlgorithm)) throw new ArgumentException("Unsupported JWT signing algorithm.");
            if (string.IsNullOrWhiteSpace(PublicKey) || PublicKey.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new ArgumentException("Enter a PEM public key, not a private key.");
            try
            {
                if (JwtAlgorithm.StartsWith("RS")) { using var key = RSA.Create(); key.ImportFromPem(PublicKey); if (key.KeySize < 2048) throw new ArgumentException("RSA public key must be at least 2048 bits."); }
                else
                {
                    using var key = ECDsa.Create(); key.ImportFromPem(PublicKey);
                    var expectedSize = JwtAlgorithm == "ES256" ? 256 : JwtAlgorithm == "ES384" ? 384 : 521;
                    if (key.KeySize != expectedSize) throw new ArgumentException("The public key curve does not match the JWT signing algorithm.");
                }
            }
            catch (CryptographicException) { throw new ArgumentException("The JWT public key is invalid."); }
        }
        if (Conditions == null || Conditions.Count > 50) throw new ArgumentException("Invalid trigger conditions.");
        foreach (var group in Conditions)
        {
            if (group?.Rules == null || group.Rules.Count > 50) throw new ArgumentException("Invalid trigger condition group.");
            if (group.Rules.Any(r => r == null)) throw new ArgumentException("Invalid trigger condition.");
            foreach (var rule in group.Rules.Where(r => !r.IsBlank))
            {
                if (rule.Field == "expression")
                {
                    if (string.IsNullOrWhiteSpace(rule.Value) || Template.Parse(ExpressionTemplate(rule.Value)).HasErrors)
                        throw new ArgumentException("Enter a valid trigger expression.");
                }
                else
                {
                    if (!new[] { "method", "headers", "headers.name", "headers.value", "body", "json", "origin_ip", "jwt_payload" }.Contains(rule.Field))
                        throw new ArgumentException("Select a valid trigger field.");
                    if (!Operators.Contains(rule.Operator)) throw new ArgumentException("Select a valid trigger filter.");
                    if (rule.Operator is not ("is-empty" or "is-not-empty") && string.IsNullOrWhiteSpace(rule.Value))
                        throw new ArgumentException("Enter the trigger filter value.");
                }
            }
        }
    }

    public bool Matches(JsonElement request, string refId)
    {
        var groups = Conditions.Where(g => g.Rules.Any(r => !r.IsBlank)).ToList();
        return groups.Count == 0 || groups.Any(g => g.Rules.Where(r => !r.IsBlank).All(r => Match(r, request, refId)));
    }

    private static bool Match(IncomingWebhookRule rule, JsonElement request, string refId)
    {
        if (rule.Field == "expression")
        {
            var data = ToScript(request);
            var globals = new ScriptObject { ["a"] = data, ["trigger"] = data, ["steps"] = new ScriptObject { [refId] = data } };
            globals[refId] = data;
            var context = new TemplateContext { StrictVariables = true, LoopLimit = 1000, RecursiveLimit = 32 };
            context.PushGlobal(globals);
            var result = Template.Parse(ExpressionTemplate(rule.Value)).Render(context).Trim();
            return result.Equals("true", StringComparison.OrdinalIgnoreCase) || result == "1";
        }
        var values = new List<string>();
        if (rule.Field.StartsWith("headers.") || (rule.Field == "headers" && !string.IsNullOrWhiteSpace(rule.Path)))
        {
            var member = rule.Field == "headers" ? "value" : rule.Field[8..];
            foreach (var header in request.GetProperty("headers").EnumerateArray())
                if (string.IsNullOrWhiteSpace(rule.Path) || header.GetProperty("name").GetString()!.Equals(rule.Path, StringComparison.OrdinalIgnoreCase))
                    values.Add(header.GetProperty(member).GetString() ?? "");
        }
        else if (request.TryGetProperty(rule.Field, out var value))
        {
            foreach (var segment in (rule.Path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var child)) value = child;
                else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
                else { value = default; break; }
            }
            values.Add(value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.ToString());
        }
        if (values.Count == 0) values.Add("");
        bool Test(string value) => rule.Operator switch
        {
            "matches-regex" => Regex.IsMatch(value, rule.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
            "not-matches-regex" => !Regex.IsMatch(value, rule.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
            _ => PipelineFilterEvaluator.EvaluateConditionOperator(value, rule.Operator, rule.Value, "TEXT")
        };
        return rule.Operator is "is-not" or "not-contains" or "not-starts-with" or "not-ends-with" or "not-matches-regex" or "is-empty" ? values.All(Test) : values.Any(Test);
    }

    private static string ExpressionTemplate(string value) => value.Contains("{{") ? value : "{{ " + value + " }}";
    private static object? ToScript(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => ToObject(value),
        JsonValueKind.Array => value.EnumerateArray().Select(ToScript).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true, JsonValueKind.False => false, _ => null
    };
    private static ScriptObject ToObject(JsonElement value)
    {
        var result = new ScriptObject();
        foreach (var p in value.EnumerateObject()) result[p.Name] = ToScript(p.Value);
        return result;
    }
}
public sealed class IncomingWebhookGroup { public List<IncomingWebhookRule> Rules { get; set; } = new(); }
public class IncomingWebhookRule
{
    public string Field { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Path { get; set; }
    public bool IsBlank => string.IsNullOrWhiteSpace(Field) && string.IsNullOrWhiteSpace(Operator) && string.IsNullOrWhiteSpace(Value) && string.IsNullOrWhiteSpace(Path);
}
public sealed class IncomingWebhookEditorGroup { public List<IncomingWebhookEditorRule> Rules { get; set; } = new(); }
public sealed class IncomingWebhookEditorRule : IncomingWebhookRule
{
    public string? Type { get; set; }
    public List<IncomingWebhookEditorGroup>? Groups { get; set; }
}
