using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Quickbase's JSON Handler steps (Fetch JSON / Iterate over JSON records) in a QBL export, shaped as the user's real
/// AllActivePipelines.yaml writes them.</summary>
public class QblJsonHandlerImportTests
{
    private const string File = """
Version: 0.12
Resources:
  $Pipeline_A:
    Type: QB::Pipeline
    Properties:
      Name: Json import
      Tags: []
    Steps:
      - Type: QB::PipelineStep::JsonHandler
        Properties:
          Id: c
          Type: pipe
          Name: Fetch JSON
          SubType: action
          Channel:
            Resource: json_source
            Id: json-handler
            Selector: fetch_json_blob
        InputsMeta:
          $PipelineStep_InputsMeta_c:
            Type: QB::PipelineStep::InputsMeta
            Properties:
              Authentication_Schema: http-qb-token
              Body: |-
                {"from": "bvfkhtxys", "select": [10, 3]}
              Headers: |
                QB-Realm-Hostname: ysl.quickbase.com
              Json_Url: https://dummyjson.com/users
              Method_Type: POST
              Token: secret-token
      - Type: QB::PipelineStep::JsonHandler
        Properties:
          Id: d
          Type: pipe
          Name: Iterate over JSON records
          SubType: query
          Channel:
            Resource: json_object
            Id: json-handler
            Selector: search
          Target:
            StepId: c
            Type: step-reference
          Filters:
            Type: composite-expression
            Notation: postfix
            Expression:
              - Type: relational-expression
                Field: id
                Operator: greater
                Value: 3
              - Type: relational-expression
                Field: email
                Operator: '!ends'
                Value: .org
              - Type: logical-operator
                Operator: AND
        InputsMeta:
          $PipelineStep_InputsMeta_d:
            Type: QB::PipelineStep::InputsMeta
            Properties:
              Json_Records_Path: /data
              Json_Schema_Sample: |-
                {"data": [{"10": {"value": 4600535}}]}
      - Type: QB::PipelineStep::Loop
        Properties:
          Type: loop
          Name: Loop
          Target:
            StepId: d
            Type: step-reference
        Steps: []
""";

    private static string Text(object? value) => value is PblPipelineTextValue t ? string.Concat(t.Parts) : value?.ToString() ?? "";

    private static List<PblPipelineStep> Flatten(IEnumerable<PblPipelineStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(Flatten(s.Children))).ToList();

    [Fact]
    public void JsonHandlerSteps_ImportWithTheirSettings()
    {
        var pipeline = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(File), File).Should().ContainSingle().Subject;
        var steps = Flatten(pipeline.Steps);
        steps.Select(s => s.Subtype).Should().Equal("fetch-json", "iterate-json-records", "for-each");
        pipeline.SkippedStepCount.Should().Be(0);

        var fetch = steps[0].Config;
        fetch["authenticationSchema"].Should().Be("http-qb-token");
        fetch["methodType"].Should().Be("POST");
        Text(fetch["jsonUrl"]).Should().Be("https://dummyjson.com/users");
        Text(fetch["token"]).Should().Be("secret-token");
        Text(fetch["headers"]).Should().StartWith("QB-Realm-Hostname: ").And.NotContain("quickbase.com");
        Text(fetch["body"]).Should().Contain("\"from\": \"bvfkhtxys\"");

        var iterate = steps[1].Config;
        iterate["jsonSourceStepId"].Should().Be(steps[0].RefId);
        Text(iterate["jsonRecordsPath"]).Should().Be("/data");
        iterate["jsonSchemaSample"]!.ToString().Should().Contain("4600535");
        var filters = iterate["filters"].Should().BeAssignableTo<List<object?>>().Subject;
        filters.Should().ContainSingle();
        var rules = ((List<object?>)filters[0]!).Cast<Dictionary<string, object?>>().ToList();
        rules.Select(r => $"{r["field"]} {r["condition"]} {r["value"]}").Should().Equal("id greater 3", "email !ends .org");

        steps[2].Config["loopOverStepId"].Should().Be(steps[1].RefId);
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_STEP_UNSUPPORTED" || i.Code == "PIPELINE_STEP_TARGET_INVALID");
    }

    [Fact]
    public void RealAllActivePipelinesFile_ImportsEveryJsonStep()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
            "drive-download-20261119T051341Z-1-001", "AllActivePipelines.yaml");
        if (!System.IO.File.Exists(path)) return;
        var content = System.IO.File.ReadAllText(path);
        var binding = QblPipelineOnlyBinder.Bind(content, []);
        var pipelines = QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(binding.Content), binding.Content);
        var steps = pipelines.SelectMany(p => Flatten(p.Steps)).ToList();
        var fetches = steps.Where(s => s.Subtype == "fetch-json").ToList();
        var iterates = steps.Where(s => s.Subtype == "iterate-json-records").ToList();
        fetches.Should().HaveCount(3);
        iterates.Should().HaveCount(3);
        fetches.Should().OnlyContain(s => (string)s.Config["methodType"]! == "POST" && (string)s.Config["authenticationSchema"]! == "http-qb-token");
        iterates.Should().OnlyContain(s => Text(s.Config["jsonRecordsPath"]) == "/data" && (string)s.Config["jsonSourceStepId"]! != "");
        // Each Iterate is followed by the Loop over it, in its own pipeline.
        pipelines.Where(p => Flatten(p.Steps).Any(s => s.Subtype == "iterate-json-records")).Should().OnlyContain(p =>
            Flatten(p.Steps).Any(l => l.Subtype == "for-each" && Flatten(p.Steps).Any(i => i.Subtype == "iterate-json-records" && i.RefId == (string?)l.Config["loopOverStepId"])));
    }
}
