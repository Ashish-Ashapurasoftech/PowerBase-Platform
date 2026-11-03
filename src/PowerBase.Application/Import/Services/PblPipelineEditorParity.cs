using System.Text.Json;
using System.Text.Json.Nodes;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Writes an imported step's configuration the way the PowerFlows editor leaves it once the step has been opened, so opening an
/// imported PowerFlow saves nothing new. Every rule mirrors what a step card does to its config when it starts (the file named on
/// each rule); a config the editor would leave as it is is not touched. Without this the editor's first save adds keys, and
/// <c>SavePipelineStepsCommandHandler</c> turns an active PowerFlow off when its steps change.
/// </summary>
public static class PblPipelineEditorParity
{
    public static void Apply(List<SavePipelineStepDto> steps)
    {
        // The editor reads a step's earlier steps as the canvas lists them: every step above it, branch contents included.
        var above = new List<SavePipelineStepDto>();
        foreach (var step in steps)
        {
            Visit(step, above);
            above.AddRange(Flatten([step]));
        }
    }

    private static void Visit(SavePipelineStepDto step, List<SavePipelineStepDto> preceding)
    {
        var config = Parse(step.ConfigJson);
        if (config is not null)
        {
            Normalize(step, config, preceding);
            step.ConfigJson = config.ToJsonString();
        }

        // Each branch card hands its children the steps above the container, the container, then the earlier siblings
        // (condition.ts, loop.ts, handle-errors.ts); On Success also sees the monitored steps.
        VisitBranch(step.Children, [.. preceding, step]);
        VisitBranch(step.ElseChildren, [.. preceding, step]);
        VisitBranch(step.SuccessChildren, [.. preceding, step, .. step.Children ?? []]);
        VisitBranch(step.ErrorChildren, [.. preceding, step]);
    }

    private static void VisitBranch(List<SavePipelineStepDto>? children, List<SavePipelineStepDto> preceding)
    {
        if (children is null)
            return;
        for (var i = 0; i < children.Count; i++)
            Visit(children[i], [.. preceding, .. children.Take(i)]);
    }

    private static void Normalize(SavePipelineStepDto step, JsonObject config, List<SavePipelineStepDto> preceding)
    {
        switch (step.Subtype)
        {
            case "make-request": MakeRequest(config); break;
            case "webhook": IncomingRequest(config); break;
            case "pause": Pause(config); break;
            case "if-else": Condition(config); break;
            case "search-records" or "new-event" or "new-bulk-event": SyncFlatFilters(config); break;
            case "update-record": UpdateRecord(config, preceding); break;
        }
        if (step.Subtype is "create-record" or "update-record" or "look-up-record")
            LinkRecord(config, preceding);
        ErrorChips(config);
    }

    /// <summary>pipeline-builder.ts <c>translatePlaceholderFields</c>: a value that is just one Handle Errors detail is a chip, which
    /// the editor saves as <c>{{ERROR.x}}</c>. (Text around it, and Condition operands, keep <c>{{steps.ERROR.x}}</c>.)</summary>
    private static void ErrorChips(JsonObject config)
    {
        static JsonNode? Chip(JsonNode? value) =>
            Text(value) is var text && System.Text.RegularExpressions.Regex.Match(text, @"^\{\{\s*steps\.ERROR\.(reference_id|name|step|channel|error_message)\s*\}\}$") is { Success: true } m
                ? JsonValue.Create($"{{{{ERROR.{m.Groups[1].Value}}}}}")
                : value;
        foreach (var mapping in config["fieldMappings"] as JsonArray ?? [])
            if (mapping is JsonObject m && m.ContainsKey("value"))
                m["value"] = Chip(m["value"])?.DeepClone();
        if (config["rowValues"] is JsonObject row)
            foreach (var key in row.Select(p => p.Key).ToList())
                row[key] = Chip(row[key])?.DeepClone();
    }

    /// <summary>make-request.ts <c>initializeHttpConfig</c>: the HTTP mode's defaults.</summary>
    private static void MakeRequest(JsonObject config)
    {
        Default(config, "requestMode", "http");
        if (config["requestMode"]?.GetValue<string>() != "http")
            return;
        Default(config, "accountName", "");
        Default(config, "authType", "No Authentication");
        Default(config, "baseUrl", config["url"]?.DeepClone() ?? JsonValue.Create(""));
        if (!Has(config, "connectionHeaders")) config["connectionHeaders"] = BlankRows();
        Default(config, "method", "GET");
        Default(config, "path", "");
        if (!Has(config, "stepHeaders")) config["stepHeaders"] = BlankRows();
        if (!Has(config, "queryParams")) config["queryParams"] = BlankRows();
        Default(config, "expectedPayloadType", "JSON");
        Default(config, "schemaSampleType", "JSON sample");
        Default(config, "schemaSample", "");
        Default(config, "validateResponsePayload", "Yes");
        Default(config, "errorsOption", "automatic");
        Default(config, "exemptErrorStatuses", "");
        foreach (var key in new[] { "username", "password", "bearerToken", "apiKeyName", "apiKeyValue" })
            Default(config, key, "");
        Default(config, "apiKeyPlacement", "Header");
        Default(config, "jwtAlg", "HS256 (symmetric)");
        Default(config, "jwtSigningKey", "");
        Default(config, "jwtHeaders", "{}");
        Default(config, "jwtClaims", "{}");
        Default(config, "jwtUseIat", false);
        Default(config, "jwtExp", "5m");
        Default(config, "oauthGrantType", "Authorization code");
        Default(config, "oauthClientAuth", "Send client credentials as basic auth header");
        foreach (var key in new[] { "oauthAuthEndpoint", "oauthTokenEndpoint", "oauthClientId", "oauthClientSecret", "oauthScope" })
            Default(config, key, "");
    }

    private static JsonArray BlankRows() => [new JsonObject { ["key"] = "", ["value"] = "" }];

    /// <summary>webhook-trigger.ts <c>ngOnInit</c> / <c>initializeSharedFilters</c> for a request without filters (the import writes none).</summary>
    private static void IncomingRequest(JsonObject config)
    {
        Default(config, "authType", "no-auth");
        Default(config, "methodType", "ANY BELOW");
        Default(config, "proxyViaOnPremisesAgent", false);
        config["tableLabel"] = "Request";
        // incoming-webhook.ts buildWebhookResponseFields, before the webhook has been called (no parameter names yet).
        config["responseFields"] = JsonNode.Parse("""
            [{"id":"method","name":"method","label":"Method","type":"string"},
             {"id":"headers","name":"headers","label":"Headers","type":"array","children":[
               {"id":"headers.name","name":"headers.name","label":"Name","type":"string"},
               {"id":"headers.value","name":"headers.value","label":"Value","type":"string"}]},
             {"id":"body","name":"body","label":"Body","type":"string"},
             {"id":"json","name":"json","label":"Body JSON","type":"object"},
             {"id":"origin_ip","name":"origin_ip","label":"Origin IP","type":"string"},
             {"id":"jwt_payload","name":"jwt_payload","label":"JWT Payload","type":"object"},
             {"id":"url_params","name":"url_params","label":"URL Parameters","type":"object"},
             {"id":"body_params","name":"body_params","label":"Body Parameters","type":"object"}]
            """);
        Default(config, "jwtAlgorithm", "RS256");
        if (Has(config, "conditions") || Has(config, "filterGroups") || Has(config, "isSimpleFilter"))
            return;
        config["isSimpleFilter"] = true;
        config["filterGroups"] = JsonNode.Parse("""[{"logicalOp":"OR","rules":[{"type":"rule","field":"","operator":"","value":"","logicalOp":"AND"}]}]""");
        config["conditions"] = JsonNode.Parse("""[{"rules":[{"field":"","operator":"","value":""}]}]""");
    }

    /// <summary>pause.ts <c>validate</c>: a valid duration is also kept as seconds.</summary>
    private static void Pause(JsonObject config)
    {
        if (config["durationText"] is not JsonValue text || text.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetValue<string>()))
            return;
        TimeSpan duration;
        try
        {
            duration = PauseStepConfig.ParseDuration(new JsonObject { ["durationText"] = text.DeepClone() }.ToJsonString());
        }
        catch (ArgumentException)
        {
            return;
        }
        var seconds = duration.TotalSeconds;
        config["duration"] = seconds == Math.Floor(seconds) ? JsonValue.Create((long)seconds) : JsonValue.Create(seconds);
        config["unit"] = "seconds";
    }

    /// <summary>condition.ts <c>set step</c>: a condition without groups gets one blank rule, and the flat <c>rules</c> list mirrors the groups.</summary>
    private static void Condition(JsonObject config)
    {
        if (config["ruleGroups"] is not JsonArray { Count: > 0 })
        {
            var rule = new JsonObject
            {
                ["left"] = Text(config["leftOperand"]),
                ["op"] = Text(config["operator"]) is { Length: > 0 } op ? op : "equals",
                ["right"] = Text(config["rightOperand"]),
            };
            config["ruleGroups"] = new JsonArray(new JsonObject { ["logicalOp"] = "OR", ["rules"] = new JsonArray(rule) });
        }
        var flat = new JsonArray();
        void Collect(JsonNode? rules)
        {
            foreach (var item in rules as JsonArray ?? [])
            {
                if (item is not JsonObject rule)
                    continue;
                if (Text(rule["type"]) != "nested")
                {
                    var copy = new JsonObject();
                    foreach (var key in new[] { "left", "op", "right" })
                        if (rule.TryGetPropertyValue(key, out var value))
                            copy[key] = value?.DeepClone();
                    flat.Add(copy);
                }
                else
                {
                    foreach (var group in rule["groups"] as JsonArray ?? [])
                        Collect(group?["rules"]);
                }
            }
        }
        foreach (var group in (JsonArray)config["ruleGroups"]!)
            Collect(group?["rules"]);
        config["rules"] = flat;
    }

    /// <summary>pipeline-filter-editor.component.ts <c>getFilterGroups</c> + <c>syncFilterGroupsToFilters</c>: boxes are AND-ed
    /// inside, and the flat list holds only field / operator / value.</summary>
    private static void SyncFlatFilters(JsonObject config)
    {
        if (config["filterGroups"] is not JsonArray groups)
            return;
        var flat = new JsonArray();
        void Collect(JsonNode? rules)
        {
            foreach (var item in rules as JsonArray ?? [])
            {
                if (item is not JsonObject rule)
                    continue;
                if (Text(rule["type"]) != "nested")
                    flat.Add(new JsonObject { ["field"] = rule["field"]?.DeepClone(), ["operator"] = rule["operator"]?.DeepClone(), ["value"] = rule["value"]?.DeepClone() });
                else
                    foreach (var group in rule["groups"] as JsonArray ?? [])
                        Collect(group?["rules"]);
            }
        }
        foreach (var group in groups.OfType<JsonObject>())
        {
            group["logicalOp"] = "AND";
            Collect(group["rules"]);
        }
        config["filters"] = flat;
    }

    /// <summary>update-record.ts <c>ngOnInit</c>: the card takes its connection and table from the step whose record it updates
    /// (<c>resolveSourceContext</c>); with no record and not exactly one step to take it from, it clears the record and its table.</summary>
    private static void UpdateRecord(JsonObject config, List<SavePipelineStepDto> preceding)
    {
        var target = Text(config["targetRecordId"]);
        if (target.Length > 0)
        {
            if (SourceOf(target, preceding) is { } source)
            {
                config["connection"] = Text(source["connection"]);
                config["connectionPublicId"] = Text(source["connectionPublicId"]) is { Length: > 0 } cp ? cp : Text(source["connection"]);
                var table = new[] { "tablePublicId", "tableLabel", "tableId" }.Select(k => Text(source[k])).FirstOrDefault(t => t.Length > 0) ?? "";
                config["tableId"] = table;
                config["tableLabel"] = table;
                config["tablePublicId"] = table;
            }
            return;
        }
        if (preceding.Count(IsUpdateSource(preceding)) == 1)
            return;
        foreach (var key in new[] { "targetRecordId", "connection", "connectionPublicId", "tableId", "tableLabel", "tablePublicId" })
            config[key] = "";
    }

    /// <summary>The config the card reads its table from for <paramref name="target"/> (<c>{{steps.ref_x.RecordPublicId}}</c>,
    /// <c>{{steps.ref_loop.item.RecordPublicId}}</c> or <c>{{trigger.RecordPublicId}}</c>): a record step, or for a Loop the
    /// Search / bulk event it loops over. Null when the card would not find one, and then leaves the table as it is.</summary>
    private static JsonObject? SourceOf(string target, List<SavePipelineStepDto> preceding)
    {
        var match = System.Text.RegularExpressions.Regex.Match(target, @"^\{\{\s*(?:steps\.([A-Za-z0-9_]+)|(trigger))\.");
        if (!match.Success)
            return null;
        var step = match.Groups[2].Success ? preceding.FirstOrDefault(s => s.Type == "trigger") : preceding.FirstOrDefault(s => s.RefId == match.Groups[1].Value);
        if (step is null)
            return null;
        if (step.Type == "loop")
        {
            var looped = Text(Parse(step.ConfigJson)?["loopOverStepId"]);
            var source = looped.Length == 0 ? null : preceding.FirstOrDefault(s => s.RefId == looped);
            return source is not null && source.Subtype is "search-records" or "bulk" or "new-bulk-event" ? Parse(source.ConfigJson) : null;
        }
        return step.Subtype is "create-record" or "update-record" or "new-event" or "record-added" or "record-updated" or "record-deleted" or "look-up-record"
            ? Parse(step.ConfigJson)
            : null;
    }

    /// <summary>update-record.ts <c>getEligiblePrecedingSteps</c>.</summary>
    private static Func<SavePipelineStepDto, bool> IsUpdateSource(List<SavePipelineStepDto> preceding) => s =>
    {
        if (s.Type == "loop")
        {
            var source = Text(Parse(s.ConfigJson)?["loopOverStepId"]);
            var looped = source.Length == 0 ? null : preceding.FirstOrDefault(x => x.RefId == source);
            return looped is not null && looped.Subtype is "search-records" or "bulk" or "new-bulk-event";
        }
        return s.Subtype is "create-record" or "update-record" or "new-event" or "record-added" or "record-updated" or "record-deleted" or "look-up-record";
    };

    /// <summary>create-record.ts / update-record.ts / look-up-record.ts: an empty link is set to the first earlier step that
    /// returns a record (<c>isLinkResourceProvider</c> in pipeline-step.model.ts).</summary>
    private static void LinkRecord(JsonObject config, List<SavePipelineStepDto> preceding)
    {
        var providers = preceding.Where(IsLinkResourceProvider).ToList();
        var link = Text(config["linkRecordId"]);
        if (link.Length > 0 && providers.Any(p => p.RefId == link))
            return;
        // A link to a step that is no longer above it (a trigger left out on a retried save) is cleared, as look-up-record.ts
        // and the editor's reference sanitizer do, then filled again like an empty one.
        if (providers.Count > 0)
            config["linkRecordId"] = providers[0].RefId;
        else if (link.Length > 0)
            config["linkRecordId"] = "";
    }

    private static bool IsLinkResourceProvider(SavePipelineStepDto step) =>
        step.Type == "loop" || (step.Type != "condition" && step.Subtype is "new-event" or "record-added" or "record-updated" or "record-deleted"
            or "new-bulk-event" or "bulk" or "search-records" or "look-up-record" or "create-record" or "update-record");

    private static IEnumerable<SavePipelineStepDto> Flatten(IEnumerable<SavePipelineStepDto>? steps)
    {
        foreach (var step in steps ?? [])
        {
            yield return step;
            foreach (var child in Flatten(step.Children)) yield return child;
            foreach (var child in Flatten(step.ElseChildren)) yield return child;
            foreach (var child in Flatten(step.SuccessChildren)) yield return child;
            foreach (var child in Flatten(step.ErrorChildren)) yield return child;
        }
    }

    private static JsonObject? Parse(string? json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Has(JsonObject config, string key) => config.TryGetPropertyValue(key, out var value) && value is not null;

    /// <summary>The editor's <c>??=</c> / <c>=== undefined</c> default: set only when the key is missing.</summary>
    private static void Default(JsonObject config, string key, JsonNode value)
    {
        if (!config.ContainsKey(key))
            config[key] = value;
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;
}
