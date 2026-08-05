using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Parses a raw QBL YAML document into a <see cref="QblDocument"/> AST. Mirrors
/// <c>PblSerializer.Deserialize(string)</c>'s role for the PBL/JSON path — throws
/// <see cref="YamlDotNet.Core.YamlException"/> on malformed YAML, caught the same way
/// <see cref="System.Text.Json.JsonException"/> is for PBL.
/// </summary>
public static class QblSerializer
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithTagMapping("!Ref", typeof(QblRef))
        .WithTagMapping("!BadRef", typeof(QblBadRef))
        .WithTagMapping("!Var", typeof(QblVarRef))
        .WithNodeDeserializer(new QblTagNodeDeserializer(), s => s.Before<ScalarNodeDeserializer>())
        .IgnoreUnmatchedProperties()
        .Build();

    public static QblDocument Deserialize(string yaml)
    {
        try
        {
            return ParseCore(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Recovery path: a real, confirmed corruption in QB::CodePage RawCode content
            // (minified JS with a stray under-indented line) can break strict YAML parsing.
            // CodePages have no PowerBase equivalent regardless, so retrying without that
            // whole block loses nothing importable — but only attempt this once, and only
            // when we can actually locate the block; otherwise surface the original error.
            var sanitized = QblRawTextSanitizer.TryStripUnsupportedPagesBlock(yaml);
            if (sanitized is null)
                throw;

            return ParseCore(sanitized);
        }
    }

    private static QblDocument ParseCore(string yaml)
    {
        var raw = Deserializer.Deserialize<object>(yaml) as IDictionary<object, object>
            ?? throw new YamlDotNet.Core.YamlException("QBL document did not deserialize to a mapping.");

        var version = raw.TryGetValue("Version", out var v) ? v?.ToString() ?? "" : "";

        var parameterDefinitions = BuildParameterDefinitions(raw.TryGetValue("ParameterDefinitions", out var pd) ? pd : null);

        var resourcesRaw = raw.TryGetValue("Resources", out var res) ? res : null;
        var resources = new Dictionary<string, QblResourceNode>(StringComparer.Ordinal);
        if (resourcesRaw is IDictionary<object, object> resourcesDict)
        {
            foreach (var kv in resourcesDict)
            {
                var walked = WalkValue(kv.Value, parameterDefinitions);
                if (walked is QblResourceNode node)
                    resources[kv.Key?.ToString() ?? ""] = node;
            }
        }

        return new QblDocument
        {
            Version = version,
            ParameterDefinitions = parameterDefinitions,
            Resources = resources,
        };
    }

    /// <summary>Each entry under <c>ParameterDefinitions</c> is itself a <c>{Type, Description,
    /// Value}</c> node (e.g. <c>Type: String</c>) — pull just the resolved <c>Value</c> per
    /// parameter name, since that's all <c>!Var</c> resolution needs.</summary>
    private static Dictionary<string, object?> BuildParameterDefinitions(object? raw)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (raw is not IDictionary<object, object> dict)
            return result;

        foreach (var kv in dict)
        {
            var name = kv.Key?.ToString() ?? "";
            if (kv.Value is IDictionary<object, object> entry && entry.TryGetValue("Value", out var val))
                result[name] = val is IDictionary<object, object> or IList<object> ? WalkValue(val, result) : val;
            else
                result[name] = null;
        }
        return result;
    }

    /// <summary>Recursively converts the generic YamlDotNet tree (Dictionary/List/scalars, plus
    /// <see cref="QblRef"/>/<see cref="QblBadRef"/>/<see cref="QblVarRef"/> markers already
    /// produced by <see cref="QblTagNodeDeserializer"/>) into <see cref="QblResourceNode"/>s
    /// wherever a mapping carries a <c>Type</c> key, resolving any <see cref="QblVarRef"/>
    /// against <paramref name="parameterDefinitions"/> along the way.</summary>
    private static object? WalkValue(object? raw, IReadOnlyDictionary<string, object?> parameterDefinitions)
    {
        switch (raw)
        {
            case null:
                return null;
            case QblVarRef varRef:
                return parameterDefinitions.TryGetValue(varRef.ParameterName, out var resolved) ? resolved : null;
            case QblRef or QblBadRef:
                return raw;
            case IDictionary<object, object> dict:
            {
                var strDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kv in dict)
                    strDict[kv.Key?.ToString() ?? ""] = WalkValue(kv.Value, parameterDefinitions);

                return strDict.ContainsKey("Type") ? BuildNode(strDict) : strDict;
            }
            case IList<object> list:
                return list.Select(item => WalkValue(item, parameterDefinitions)).ToList();
            default:
                return raw;
        }
    }

    private static QblResourceNode BuildNode(Dictionary<string, object?> raw)
    {
        var type = raw.TryGetValue("Type", out var t) ? t?.ToString() ?? "" : "";
        var properties = raw.TryGetValue("Properties", out var p) && p is Dictionary<string, object?> pd
            ? pd
            : new Dictionary<string, object?>();

        var children = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, val) in raw)
        {
            if (key is "Type" or "Properties" or "Id")
                continue;
            children[key] = val;
        }

        return new QblResourceNode { Type = type, Properties = properties, Children = children };
    }
}
