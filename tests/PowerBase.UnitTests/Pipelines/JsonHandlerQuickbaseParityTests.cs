using System.Text.Json.Nodes;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>Iterate over JSON records, checked value by value against live Quickbase runs of the "json" pipeline (2026-11-20 / 23).</summary>
public class JsonHandlerQuickbaseParityTests
{
    private const string Sample = """{"id":1,"name":"x","email":"a@b.com","active":true,"score":1.5,"address":{"city":"c"},"tags":["x"]}""";

    private const string Data = """
        [{"id":1,"name":"Alice","active":true,"score":1.5,"tags":["a","b"],"address":{"city":"X"},"email":"alice@x.com"},
         {"id":2,"name":"bob","active":false,"score":0,"tags":[],"address":{},"email":null},
         {"id":3,"name":"","active":"true","score":"7","tags":"a"},
         {"id":4,"active":null,"score":null},
         {"id":"5","name":"Eve","active":1,"score":10.25,"tags":["c"],"address":{"city":"Y"},"email":"eve@y.org"},
         {"id":6,"name":"alice smith","active":0,"score":-2,"tags":["A"],"address":"str","email":"ALICE@X.COM"}]
        """;

    private static JsonArray Run(string sample, string data, params JsonRecordFilter[] rules) => JsonRecordsIterator.Iterate(data,
        new IterateJsonDefinition { JsonSourceStepId = "aa", JsonSchemaSample = sample, Filters = rules.Length == 0 ? new() : new() { rules.ToList() } });

    private static int[] Ids(params JsonRecordFilter[] rules) =>
        Run(Sample, Data, rules).Select(r => r!["id"]!.GetValue<int>()).ToArray();

    private static JsonRecordFilter Rule(string field, string condition, JsonNode? value = null) => new() { Field = field, Condition = condition, Value = value };

    private static string One(string sample, string record) => Run(sample, "[" + record + "]")[0]!.AsObject()
        .Where(p => p.Key != "raw_record").Aggregate(new JsonObject(), (o, p) => { o[p.Key] = p.Value?.DeepClone(); return o; }).ToJsonString();

    // ---- values: Python conversions -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"abc\"", "null")] [InlineData("1.7", "1")] [InlineData("-1.7", "-1")] [InlineData("\"1.7\"", "null")] [InlineData("true", "1")]
    [InlineData("\"\"", "null")] [InlineData("\" 12 \"", "12")] [InlineData("[1]", "null")] [InlineData("{\"a\":1}", "null")] [InlineData("\"1_000\"", "1000")]
    [InlineData("\"+5\"", "5")] [InlineData("false", "0")] [InlineData("1e20", "100000000000000000000")] [InlineData("\"0x1A\"", "null")]
    public void Integer_IsPythonInt(string raw, string expected) => Assert.Equal($$"""{"id":{{expected}}}""", One("""{"id":1}""", $$"""{"id":{{raw}}}"""));

    [Theory]
    [InlineData("\"abc\"", "null")] [InlineData("true", "1")] [InlineData("\"1e3\"", "1000")] [InlineData("\" 2.5 \"", "2.5")] [InlineData("[1]", "null")]
    [InlineData("\"\"", "null")] [InlineData("\"1,5\"", "null")] [InlineData("\"nan\"", "\"nan\"")] [InlineData("\"inf\"", "\"inf\"")]
    public void Number_IsPythonFloat(string raw, string expected) => Assert.Equal($$"""{"score":{{expected}}}""", One("""{"score":1.5}""", $$"""{"score":{{raw}}}"""));

    [Theory]
    [InlineData("\"false\"", "true")] [InlineData("\"False\"", "true")] [InlineData("\"no\"", "true")] [InlineData("\"0\"", "true")] [InlineData("0", "false")]
    [InlineData("2", "true")] [InlineData("\"\"", "null")] [InlineData("[]", "false")] [InlineData("{}", "false")] [InlineData("[1]", "true")] [InlineData("0.0", "false")]
    public void Boolean_IsPythonBool(string raw, string expected) => Assert.Equal($$"""{"active":{{expected}}}""", One("""{"active":true}""", $$"""{"active":{{raw}}}"""));

    [Theory]
    [InlineData("123", "123")] [InlineData("true", "True")] [InlineData("1.5", "1.5")] [InlineData("[1,\"a\"]", "[1, 'a']")]
    [InlineData("{\"a\":1,\"b\":\"x\"}", "{'a': 1, 'b': 'x'}")] [InlineData("\"  \"", "  ")] [InlineData("1e21", "1e+21")] [InlineData("false", "False")] [InlineData("0.1", "0.1")]
    public void String_IsPythonStr(string raw, string expected) =>
        Assert.Equal(new JsonObject { ["name"] = expected }.ToJsonString(), One("""{"name":"x"}""", $$"""{"name":{{raw}}}"""));

    [Fact]
    public void NullIsLeftOut_EmptyTextIsNull_ExtraKeysDropped()
    {
        Assert.Equal("""{"id":4}""", One(Sample, """{"id":4,"active":null,"score":null}"""));
        Assert.Equal("""{"id":3,"name":null}""", One(Sample, """{"id":3,"name":"","zzz":1}"""));
    }

    [Fact]
    public void DictsAndLists_ConvertTheirItems()
    {
        Assert.Equal("""{"address":{"city":"5"}}""", One(Sample, """{"address":{"city":5,"zip":"9"}}"""));
        Assert.Equal("""{"address":{}}""", One(Sample, """{"address":{"city":null}}"""));
        Assert.Equal("""{"address":{"city":null}}""", One(Sample, """{"address":{"city":""}}"""));
        Assert.Equal("""{"address":"str"}""", One(Sample, """{"address":"str"}"""));
        Assert.Equal("""{"tags":["1","2","True"]}""", One(Sample, """{"tags":[1,2,true]}"""));
        Assert.Equal("""{"tags":["","None","a","5"]}""", One(Sample, """{"tags":["",null,"a",5]}"""));
        Assert.Equal("""{"tags":5}""", One(Sample, """{"tags":5}"""));
        Assert.Equal("""{"lo":[{"k":2},null,"s",{"k":null}]}""", One("""{"lo":[{"k":1}]}""", """{"lo":[{"k":"2","z":1},null,"s",{"k":""}]}"""));
        Assert.Equal("""{"e":[1,"x"]}""", One("""{"e":[]}""", """{"e":[1,"x"]}"""));
        Assert.Equal("""{"o":{}}""", One("""{"o":{}}""", """{"o":{"a":1}}"""));
    }

    [Fact]
    public void RecordThatIsNotAnObject_FailsTheStep() =>
        Assert.Equal("Something went wrong executing a step. Try modifying your pipeline.",
            Assert.Throws<PipelineNonRetryableException>(() => Run("""{"a":1}""", """[1,"x"]""")).Message);

    // ---- schema: names, titles, types -----------------------------------------------------------------------------------------

    [Fact]
    public void Schema_NormalizesKeysAsQuickbase()
    {
        var fields = JsonSampleSchema.Build(JsonNode.Parse("""
            {"firstName":1,"first_name":1,"FIRST name":1,"_x":1,"a-b":1,"2x":1,"":1,"HTTPServer":1,"über":1,"a.b":1,"type":1,"type_0":1,"ß":1}
            """)!.AsObject());
        Assert.Equal(new[] { "first_name", "first_name_0", "first_name_1", "x", "a_b", "n2x", "field", "http_server", "uber", "a_b_0", "type_0", "type_0_0", "ss" },
            fields.Select(f => f.Key).ToArray());
        Assert.Equal(new[] { "First Name", "First Name", "First Name", " X", "A B", "2X", "", "Http Server", "Über", "A.B", "Type", "Type 0", "Ss" },
            fields.Select(f => f.Title).ToArray());
    }

    [Fact]
    public void Schema_TypesAndContainerTitles()
    {
        var fields = JsonSampleSchema.Build(JsonNode.Parse("""
            {"myDict":{},"myList":[1],"myStr":"a","myEmpty":[],"myNull":null,"myBool":true,"myNum":1.0,"lo":[{"aB":1}]}
            """)!.AsObject());
        Assert.Equal(new[] { "dict", "list", "string", "dict", "string", "boolean", "number", "list" }, fields.Select(f => f.Type).ToArray());
        Assert.Equal(new[] { "My dict", "My list", "My Str", "My empty", "My Null", "My Bool", "My Num", "Lo" }, fields.Select(f => f.Title).ToArray());
        Assert.Equal("integer", fields[1].Item!.Type);
        Assert.Equal("a_b", fields[7].Item!.Children![0].Key);
    }

    [Fact]
    public void Schema_ListOfLists_IsRefused() =>
        Assert.Contains(new IterateJsonDefinition { JsonSourceStepId = "aa", JsonSchemaSample = """{"ll":[[1]]}""" }.Errors(), e => e.StartsWith("JSON Schema Sample:"));

    [Fact]
    public void Record_UsesNormalizedNames() =>
        Assert.Equal("""{"first_name":"Ann","n2x":"1"}""", One("""{"firstName":"x","2x":"y"}""", """{"firstName":"Ann","2x":1}"""));

    // ---- filters ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TextFilters_AreCaseInsensitive_RegexIsNot()
    {
        Assert.Equal(new[] { 1, 6 }, Ids(Rule("name", "starts", "A")));
        Assert.Equal(new[] { 2, 3, 4, 5 }, Ids(Rule("name", "!starts", "a")));
        Assert.Equal(new[] { 1, 5 }, Ids(Rule("name", "ends", "E")));
        Assert.Equal(new[] { 1, 6 }, Ids(Rule("name", "contains", "LI")));
        Assert.Equal(new[] { 2, 3, 4, 5 }, Ids(Rule("name", "!contains", "li")));
        Assert.Equal(new[] { 1 }, Ids(Rule("name", "equals", "ALICE")));
        Assert.Equal(new[] { 1, 3, 4, 5, 6 }, Ids(Rule("name", "!equals", "bob")));
        Assert.Empty(Ids(Rule("name", "matches", "^a.*e$")));
        Assert.Equal(new[] { 1 }, Ids(Rule("name", "matches", "^A")));
        Assert.Equal(new[] { 1, 6 }, Ids(Rule("name", "matches", "li")));
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, Ids(Rule("name", "!matches", "^A")));
        Assert.Equal(new[] { 1, 2, 5, 6 }, Ids(Rule("name", "contains", "")));
    }

    [Fact]
    public void ValueFilters_SetAndEmpty()
    {
        Assert.Equal(new[] { 1, 2, 5, 6 }, Ids(Rule("name", "set")));
        Assert.Equal(new[] { 3, 4 }, Ids(Rule("name", "!set")));
        Assert.Equal(new[] { 1, 2, 3, 5, 6 }, Ids(Rule("score", "set")));
        Assert.Equal(new[] { 1, 2, 5, 6 }, Ids(Rule("address", "set")));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, Ids(Rule("raw_record", "set")));
        Assert.Equal(new[] { 2, 4 }, Ids(Rule("tags", "empty")));
        Assert.Equal(new[] { 1, 3, 5, 6 }, Ids(Rule("tags", "!empty")));
    }

    [Fact]
    public void NumberFilters()
    {
        Assert.Equal(new[] { 4, 5, 6 }, Ids(Rule("id", "greater", "3")));
        Assert.Equal(new[] { 1, 2 }, Ids(Rule("id", "less", "3")));
        Assert.Equal(new[] { 5 }, Ids(Rule("id", "equals", "5")));
        Assert.Equal(new[] { 5 }, Ids(Rule("id", "equals", "5.0")));
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, Ids(Rule("id", "!equals", "5")));
        Assert.Equal(new[] { 2, 3, 4 }, Ids(Rule("id", "between", new JsonArray("2", "4"))));
        Assert.Equal(new[] { 1, 5, 6 }, Ids(Rule("id", "!between", new JsonArray("2", "4"))));
        Assert.Equal(new[] { 1, 3, 5 }, Ids(Rule("score", "greater", "1")));
        Assert.Equal(new[] { 3 }, Ids(Rule("score", "equals", "7")));
        Assert.Equal("Validation error: Invalid literal for Decimal: 'abc'",
            Assert.Throws<PipelineNonRetryableException>(() => Ids(Rule("id", "greater", "abc"))).Message);
    }

    [Fact]
    public void BooleanFilters()
    {
        Assert.Equal(new[] { 1, 3, 5 }, Ids(Rule("active", "is", true)));
        Assert.Equal(new[] { 2, 6 }, Ids(Rule("active", "is", false)));
        Assert.Equal(new[] { 2, 4, 6 }, Ids(Rule("active", "!is", true)));
        Assert.Equal(new[] { 1, 3, 5 }, Ids(Rule("active", "is", "true")));
        Assert.Equal(new[] { 4 }, Ids(Rule("active", "!set")));
    }

    [Fact]
    public void ListFilters_ContainIsItemMembership()
    {
        Assert.Equal(new[] { 1, 3, 6 }, Ids(Rule("tags", "contains", "a")));
        Assert.Equal(new[] { 2, 4, 5 }, Ids(Rule("tags", "!contains", "a")));
        Assert.Empty(Ids(Rule("tags", "contains", "[")));
        Assert.Equal("Validation error: argument of type 'Proxy' is not a container or iterable",
            Assert.Throws<PipelineNonRetryableException>(() => Ids(Rule("address", "contains", "x"))).Message);
        Assert.Equal("Validation error: argument of type 'int' is not a container or iterable",
            Assert.Throws<PipelineNonRetryableException>(() => Ids(Rule("id", "contains", "5"))).Message);
    }

    [Fact]
    public void ExpressionRule_StoredWithoutField()
    {
        var definition = IterateJsonDefinition.Read("""
            {"jsonSourceStepId":"aa","filters":[[{"custom":true,"condition":"true","value":"{{ab.a > 5}}"}]]}
            """);
        Assert.True(definition.Filters[0][0].Custom);
        var kept = JsonRecordsIterator.Iterate("""[{"a":3},{"a":7}]""", new IterateJsonDefinition
        {
            JsonSourceStepId = "aa", JsonSchemaSample = """{"a":1}""", Filters = definition.Filters
        }, null, (record, _) => record["a"]!.GetValue<int>() > 5);
        Assert.Single(kept);
    }
}
