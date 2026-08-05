using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Intercepts QBL's three custom YAML tags before YamlDotNet's default node deserializers see
/// them, producing <see cref="QblRef"/>/<see cref="QblBadRef"/>/<see cref="QblVarRef"/> markers
/// instead of generic dictionaries/strings:
///   - <c>!Ref</c> — a mapping, e.g. <c>Field: !Ref {Field: $Field_X}</c>.
///   - <c>!BadRef</c> — a **scalar string**, e.g. <c>Field: !BadRef "Referenced resource does
///     not exist."</c> (confirmed against a real export — not a mapping, despite public docs
///     implying otherwise).
///   - <c>!Var</c> — a mapping, e.g. <c>Value: !Var {Name: Variable_Value_1}</c>, resolved
///     against the document's <c>ParameterDefinitions</c> map by <see cref="QblSerializer"/>.
/// Any node without one of these tags falls through (returns false) so YamlDotNet's normal
/// deserializers still handle it.
/// </summary>
internal sealed class QblTagNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value, ObjectDeserializer rootDeserializer)
    {
        var tag = (parser.Current as NodeEvent)?.Tag;

        if (tag is { IsEmpty: false } && tag.Value == "!BadRef" && parser.Current is Scalar)
        {
            value = new QblBadRef(parser.Consume<Scalar>().Value);
            return true;
        }

        if (tag is { IsEmpty: false } && (tag.Value == "!Ref" || tag.Value == "!Var"))
        {
            // Deliberately not delegating to nestedObjectDeserializer here: the parser's
            // current node still carries this same tag, so re-entering the deserialization
            // pipeline would hit this deserializer again for the same node and recurse forever.
            // Every real !Ref/!Var mapping is flat scalar-key → scalar-value, so consuming the
            // mapping's events directly is both correct and simpler.
            var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
            parser.Consume<MappingStart>();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>().Value;
                var val = parser.Consume<Scalar>().Value;
                scopes[key] = val;
            }

            value = tag.Value == "!Ref"
                ? new QblRef(scopes)
                : new QblVarRef(scopes.TryGetValue("Name", out var name) ? name : "");
            return true;
        }

        value = null;
        return false;
    }
}
