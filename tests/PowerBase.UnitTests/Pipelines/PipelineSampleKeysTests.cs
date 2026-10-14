using System.Text.Json;
using System.Text.Json.Nodes;
using PowerBase.Application.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineSampleKeysTests
{
    private static List<string> Keys(string json, string property) =>
        JsonNode.Parse(json)![property]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    [Fact]
    public void MergeAddsOnlyNewNamesAndKeepsTheRestOfTheConfig()
    {
        var updated = PipelineSampleKeys.Merge("{\"authType\":\"jwt\",\"sampleUrlParamKeys\":[\"id\"]}",
            new Dictionary<string, IEnumerable<string>> { [PipelineSampleKeys.UrlParams] = new[] { "ID", "page" }, [PipelineSampleKeys.BodyParams] = new[] { "email" } });

        Assert.NotNull(updated);
        Assert.Equal(new[] { "id", "page" }, Keys(updated!, PipelineSampleKeys.UrlParams));
        Assert.Equal(new[] { "email" }, Keys(updated!, PipelineSampleKeys.BodyParams));
        Assert.Equal("jwt", JsonNode.Parse(updated!)!["authType"]!.GetValue<string>());
    }

    [Fact]
    public void MergeReturnsNullWhenNothingNewWasSeen()
    {
        Assert.Null(PipelineSampleKeys.Merge("{\"sampleBodyParamKeys\":[\"email\"]}",
            new Dictionary<string, IEnumerable<string>> { [PipelineSampleKeys.BodyParams] = new[] { "EMAIL", "", " " } }));
        Assert.Null(PipelineSampleKeys.Merge("{bad", new Dictionary<string, IEnumerable<string>> { [PipelineSampleKeys.BodyParams] = new[] { "x" } }));
    }

    [Fact]
    public void MergeCapsTheNumberOfNames()
    {
        var updated = PipelineSampleKeys.Merge("{}", new Dictionary<string, IEnumerable<string>>
            { [PipelineSampleKeys.ResponseFields] = Enumerable.Range(0, 500).Select(i => $"k{i}") });
        Assert.Equal(PipelineSampleKeys.MaxKeys, Keys(updated!, PipelineSampleKeys.ResponseFields).Count);
    }

    [Fact]
    public void SaveKeepsNamesTheServerLearnedWhileTheEditorWasOpen()
    {
        var saved = "{\"methodType\":\"POST\",\"sampleUrlParamKeys\":[\"id\"],\"sampleBodyParamKeys\":[\"email\"]}";
        var incoming = "{\"methodType\":\"GET\",\"sampleUrlParamKeys\":[\"page\"]}";

        var stored = PipelineSampleKeys.PreserveOnSave(saved, incoming)!;

        Assert.Equal("GET", JsonNode.Parse(stored)!["methodType"]!.GetValue<string>());
        Assert.Equal(new[] { "page", "id" }, Keys(stored, PipelineSampleKeys.UrlParams));
        Assert.Equal(new[] { "email" }, Keys(stored, PipelineSampleKeys.BodyParams));
        Assert.Equal(incoming, PipelineSampleKeys.PreserveOnSave("{\"methodType\":\"POST\"}", incoming));
        Assert.Equal(incoming, PipelineSampleKeys.PreserveOnSave(null, incoming));
    }

    [Fact]
    public void LearnedNamesAndDerivedFieldListAreNotPartOfTheEdit()
    {
        var a = PipelineSampleKeys.WithoutDerived(JsonNode.Parse("{\"methodType\":\"POST\",\"responseFields\":[1],\"sampleUrlParamKeys\":[\"id\"]}"));
        var b = PipelineSampleKeys.WithoutDerived(JsonNode.Parse("{\"methodType\":\"POST\",\"responseFields\":[1,2],\"sampleResponseKeys\":[\"x\"]}"));
        Assert.True(JsonNode.DeepEquals(a, b));
        Assert.False(JsonNode.DeepEquals(a, PipelineSampleKeys.WithoutDerived(JsonNode.Parse("{\"methodType\":\"GET\"}"))));
    }

    [Fact]
    public void QueryParametersAreDecodedAndRepeatedNamesBecomeLists()
    {
        var values = RequestParameters.FromQuery("?name=hello%20world&tag=a&tag=b&empty=&=skip");
        Assert.Equal("hello world", values["name"]);
        Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(values["tag"]));
        Assert.Equal("", values["empty"]);
        Assert.Equal(3, values.Count);
    }

    [Theory]
    [InlineData("application/json", "{\"a\":1,\"b\":{\"c\":2}}", new[] { "a", "b" })]
    [InlineData(null, "{\"a\":1}", new[] { "a" })]
    [InlineData("application/x-www-form-urlencoded; charset=utf-8", "a=1&b=2", new[] { "a", "b" })]
    [InlineData("application/json", "[{\"a\":1}]", new string[0])]
    [InlineData("text/plain", "hello", new string[0])]
    [InlineData("application/json", "", new string[0])]
    public void BodyParametersComeFromFormsOrJsonObjects(string? mediaType, string body, string[] expected)
    {
        Assert.Equal(expected, RequestParameters.FromBody(mediaType, body).Keys.ToArray());
    }

    [Fact]
    public void JsonBodyParameterKeepsItsJsonValue()
    {
        var value = Assert.IsType<JsonElement>(RequestParameters.FromBody("application/json", "{\"b\":{\"c\":2}}")["b"]);
        Assert.Equal(2, value.GetProperty("c").GetInt32());
    }
}
