using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>
/// Pipeline conversion against the shapes of a real Quickbase export (QBL_PBL.qbl, v0.12): every step kind it contains, the
/// way steps point at tables and fields, and what happens to steps PowerFlows cannot represent.
/// </summary>
public class QblPipelineConverterTests
{
    internal const string App = """
        Version: 0.12
        Resources:
          $App_Docs:
            Type: QB::Application
            Properties:
              Name: Docs
            Tables:
              $Table_Employee:
                Type: QB::Table
                Properties:
                  Name: Employee
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                    Properties:
                      Label: Record ID#
                  $Field_Name:
                    Type: QB::Field::Text
                    Id: 20
                    Properties:
                      Label: Name
                  $Field_TAddress:
                    Type: QB::Field::Address
                    Id: 30
                    Properties:
                      Label: TAddress
                      City: !Ref
                        Field: $Field_City
                  $Field_City:
                    Type: QB::Field::AddressCity
                    Id: 31
                    Properties:
                      Label: City
              $Table_Employee1:
                Type: QB::Table
                Properties:
                  Name: Employee1
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                    Properties:
                      Label: Record ID#
                  $Field_Employee_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Employee Name
        """;

    internal const string Account = """
          $PipelineChannelAccount_Quickbase:
            Type: QB::PipelineChannelAccount
            Properties:
              Channel: quickbase
              AccountType: tokenless
        """;

    /// <summary>The step list of the real export, trimmed to the fields it uses.</summary>
    internal const string AllSteps = """
          $Pipeline_All_Step:
            Type: QB::Pipeline
            Properties:
              Name: All Step
              Tags: []
              Owner: YslDevTeam
            Steps:
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: aa
                  Type: pipe
                  Name: On New Event
                  SubType: trigger
                  PipelineChannelAccount: !Ref
                    PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                  Channel:
                    Resource: record
                    Id: quickbase
                    Selector: on_new_event
                InputsMeta:
                  $PipelineStep_InputsMeta_aa:
                    Type: QB::PipelineStep::InputsMeta
                    Properties:
                      On_Add_Record: true
                      On_Modify_Record: false
                    PipelineStep_DataSources:
                      $PipelineStep_DataSource_1:
                        Type: QB::PipelineStep::DataSource::App
                        Properties:
                          App: !Ref
                            App: $App_Docs
                          Name: app
                      $PipelineStep_DataSource_2:
                        Type: QB::PipelineStep::DataSource::Fields
                        Properties:
                          Fields:
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee
                              Field: $Field_Name
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee
                              Field: $Field_TAddress
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee
                              Field: $Field_City
                          Name: export_fields
                      $PipelineStep_DataSource_3:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee
                          Name: table
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ab
                  Type: pipe
                  Name: Create Record
                  SubType: action
                  PipelineChannelAccount: !Ref
                    PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                  Channel:
                    Resource: record
                    Id: quickbase
                    Selector: create
                  Inputs:
                    Employee_Name: |-
                      {{aa.name}}
                InputsMeta:
                  $PipelineStep_InputsMeta_ab:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $PipelineStep_DataSource_1:
                        Type: QB::PipelineStep::DataSource::App
                        Properties:
                          App: !Ref
                            App: $App_Docs
                          Name: app
                      $PipelineStep_DataSource_2:
                        Type: QB::PipelineStep::DataSource::Fields
                        Properties:
                          Fields:
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee1
                              Field: $Field_Employee_Name
                          Name: export_fields
                      $PipelineStep_DataSource_3:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee1
                          Name: table
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ac
                  Type: pipe
                  Name: Update Record
                  SubType: action
                  Channel:
                    Resource: record
                    Id: quickbase
                    Selector: update
                  Inputs:
                    Employee_Name: |-
                      {{aa.name}}
                  Target:
                    StepId: ab
                    Type: step-reference
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ad
                  Type: pipe
                  Name: Delete Record
                  SubType: action
                  Channel:
                    Resource: record
                    Id: quickbase
                    Selector: delete
                  Target:
                    StepId: ab
                    Type: step-reference
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ae
                  Type: pipe
                  Name: Search Records
                  SubType: query
                  PipelineChannelAccount: !Ref
                    PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                  Channel:
                    Resource: record
                    Id: quickbase
                    Selector: search
                  Filters:
                    Type: composite-expression
                    Notation: postfix
                    Expression:
                      - Type: relational-expression
                        Field: name
                        Operator: starts
                        Value: Ronak
                InputsMeta:
                  $PipelineStep_InputsMeta_ae:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $PipelineStep_DataSource_1:
                        Type: QB::PipelineStep::DataSource::App
                        Properties:
                          App: !Ref
                            App: $App_Docs
                          Name: app
                      $PipelineStep_DataSource_3:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee
                          Name: table
              - Type: QB::PipelineStep::Loop
                Properties:
                  Type: loop
                  Name: Loop
                  Target:
                    StepId: ae
                    Type: step-reference
                Steps:
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: af
                      Type: pipe
                      Name: Create Record
                      SubType: action
                      PipelineChannelAccount: !Ref
                        PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                      Channel:
                        Resource: record
                        Id: quickbase
                        Selector: create
                      Inputs:
                        Employee_Name: |-
                          {{aa.name}}
                    InputsMeta:
                      $PipelineStep_InputsMeta_af:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $PipelineStep_DataSource_3:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref
                                App: $App_Docs
                                Table: $Table_Employee1
                              Name: table
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ag
                  Type: pipe
                  Name: Look Up a Record
                  SubType: lookup
                  PipelineChannelAccount: !Ref
                    PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                  Channel:
                    Resource: record
                    Id: quickbase
                    Selector: look_up
                  Inputs:
                    Id: |-
                      {{aa.id}}
                InputsMeta:
                  $PipelineStep_InputsMeta_ag:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $PipelineStep_DataSource_3:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee
                          Name: table
              - Type: QB::PipelineStep::Condition
                Properties:
                  Type: branch
                  Name: Condition
                  Filters:
                    Type: composite-expression
                    Notation: postfix
                    Expression:
                      - Type: relational-expression
                        Target: aa
                        Field: name
                        Operator: starts
                        Value: Ronak
                Then:
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ah
                      Type: pipe
                      Name: Create Record
                      SubType: action
                      Channel:
                        Resource: record
                        Id: quickbase
                        Selector: create
                      Inputs:
                        Employee_Name: |-
                          {{aa.name}}
                    InputsMeta:
                      $PipelineStep_InputsMeta_ah:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $PipelineStep_DataSource_3:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref
                                App: $App_Docs
                                Table: $Table_Employee1
                              Name: table
                Else:
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ai
                      Type: pipe
                      Name: Copy of Create Record
                      SubType: action
                      Channel:
                        Resource: record
                        Id: quickbase
                        Selector: create
                      Inputs:
                        Employee_Name: |-
                          {{aa.name}}
                    InputsMeta:
                      $PipelineStep_InputsMeta_ai:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $PipelineStep_DataSource_3:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref
                                App: $App_Docs
                                Table: $Table_Employee1
                              Name: table
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: aj
                  Type: pipe
                  Name: Copy Records
                  SubType: action
                  PipelineChannelAccount: !Ref
                    PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                  Channel:
                    Resource: bulk_record_set
                    Id: quickbase
                    Selector: copy_records
                InputsMeta:
                  $PipelineStep_InputsMeta_aj:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $PipelineStep_DataSource_1:
                        Type: QB::PipelineStep::DataSource::Fields
                        Properties:
                          Fields:
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee1
                              Field: $Field_Record_ID
                          Name: merge_field
                      $PipelineStep_DataSource_2:
                        Type: QB::PipelineStep::DataSource::Fields
                        Properties:
                          Fields:
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee1
                              Field: $Field_Employee_Name
                          Name: qb_field_for_name
                      $PipelineStep_DataSource_3:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee
                          Name: source_table
                      $PipelineStep_DataSource_4:
                        Type: QB::PipelineStep::DataSource::Fields
                        Properties:
                          Fields:
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee
                              Field: $Field_Name
                          Name: source_table_export_fields
                      $PipelineStep_DataSource_5:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee1
                          Name: table
              - Type: QB::PipelineStep::Clock
                Properties:
                  Id: ak
                  Type: pipe
                  Name: Pause
                  SubType: action
                  Channel:
                    Resource: timer
                    Id: clock
                    Selector: pause
                InputsMeta:
                  $PipelineStep_InputsMeta_ak:
                    Type: QB::PipelineStep::InputsMeta
                    Properties:
                      Duration: 1 s
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: al
                  Type: pipe
                  Name: Prepare Bulk Record Upsert
                  SubType: action
                  PipelineChannelAccount: !Ref
                    PipelineChannelAccount: $PipelineChannelAccount_Quickbase
                  Channel:
                    Resource: bulk_record_set
                    Id: quickbase
                    Selector: define
                InputsMeta:
                  $PipelineStep_InputsMeta_al:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $PipelineStep_DataSource_1:
                        Type: QB::PipelineStep::DataSource::App
                        Properties:
                          App: !Ref
                            App: $App_Docs
                          Name: app
                      $PipelineStep_DataSource_2:
                        Type: QB::PipelineStep::DataSource::Fields
                        Properties:
                          Fields:
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee
                              Field: $Field_Name
                          Name: export_fields
                      $PipelineStep_DataSource_3:
                        Type: QB::PipelineStep::DataSource::Fields
                        Properties:
                          Fields:
                            - !Ref
                              App: $App_Docs
                              Table: $Table_Employee
                              Field: $Field_Record_ID
                          Name: merge_field
                      $PipelineStep_DataSource_4:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref
                            App: $App_Docs
                            Table: $Table_Employee
                          Name: table
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: am
                  Type: pipe
                  Name: Add a Bulk Upsert Row
                  SubType: action
                  Channel:
                    Resource: bulk_record_set
                    Id: quickbase
                    Selector: create
                  Inputs:
                    Name: |-
                      {{al.$namespace}}
                  Target:
                    StepId: al
                    Type: step-reference
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: an
                  Type: pipe
                  Name: Commit Upsert
                  SubType: action
                  Channel:
                    Resource: bulk_record_set
                    Id: quickbase
                    Selector: commit_bulk_upsert
                  Target:
                    StepId: al
                    Type: step-reference
              - Type: QB::PipelineStep::Condition
                Properties:
                  Type: branch
                  Name: Condition
                  Filters:
                    Type: composite-expression
                    Notation: postfix
                    Expression:
                      - Type: relational-expression
                        Target: aa
                        Field: name
                        Operator: starts
                        Value: Ronak
                Else:
                  - Type: QB::PipelineStep::Stop
                    Properties:
                      Type: stop
                      Name: Stop
        """;

    private static PblPipeline ConvertOne(string pipelineYaml)
    {
        var content = App + "\n" + pipelineYaml + "\n" + Account;
        return QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(content), content).Should().ContainSingle().Subject;
    }

    private static PblPipelineStep Root(PblPipeline pipeline, string refId) => pipeline.Steps.Single(s => s.RefId == refId);

    private static PblPipelineFieldValue Field(object? value) => value.Should().BeOfType<PblPipelineFieldValue>().Subject;

    /// <summary>A text value as it reads with every field written as its source label in brackets.</summary>
    private static string Text(object? value) => value switch
    {
        PblPipelineTextValue text => string.Concat(text.Parts.Select(p => p is PblPipelineFieldValue f ? $"[{f.Label}]" : p.ToString())),
        _ => value?.ToString() ?? "",
    };

    [Fact]
    public void ConvertAll_RealExportShape_ConvertsEveryStepInOrder()
    {
        var pipeline = ConvertOne(AllSteps);

        pipeline.Name.Should().Be("All Step");
        pipeline.Owner.Should().Be("YslDevTeam");
        pipeline.AppRef.Should().Be("$App_Docs");
        pipeline.SourceStepCount.Should().Be(18);
        pipeline.SkippedStepCount.Should().Be(0);
        pipeline.Steps.Select(s => s.Subtype).Should().Equal(
            "new-event", "create-record", "update-record", "delete-record", "search-records", "for-each",
            "look-up-record", "if-else", "copy-records", "pause", "prepare-bulk-upsert", "add-bulk-upsert-row", "commit-upsert", "if-else");
        pipeline.Steps.Take(5).Select(s => s.RefId).Should().Equal("ref_aa", "ref_ab", "ref_ac", "ref_ad", "ref_ae");
    }

    [Fact]
    public void ConvertAll_StepsWithoutQuickbaseIds_GetGeneratedReferences()
    {
        var pipeline = ConvertOne(AllSteps);

        var loop = pipeline.Steps[5];
        loop.SourceId.Should().BeNull();
        loop.RefId.Should().Be("ref_1");
        loop.Children.Should().ContainSingle(s => s.RefId == "ref_af");
        pipeline.Steps[13].RefId.Should().Be("ref_3");
        pipeline.Steps[13].ElseChildren.Should().ContainSingle(s => s.Subtype == "stop" && s.RefId == "ref_4");
    }

    [Fact]
    public void Trigger_MapsEventsTableAndOutputFields_AddressPartIsKeptAsThatPartOfItsAddress()
    {
        var trigger = Root(ConvertOne(AllSteps), "ref_aa");

        trigger.Type.Should().Be("trigger");
        trigger.Config["triggerOnAdded"].Should().Be(true);
        trigger.Config["triggerOnModified"].Should().Be(false);
        trigger.Config["triggerOnDeleted"].Should().Be(false);
        trigger.Config["tablePublicId"].Should().Be(new PblPipelineTableValue("$App_Docs", "$Table_Employee"));
        trigger.Config["appPublicId"].Should().Be(new PblPipelineAppValue("$App_Docs"));
        trigger.Config["connectionPublicId"].Should().BeOfType<PblPipelineConnectionValue>();
        trigger.Config["triggerOnNumRecordsChanged"].Should().Be("any");
        // The City part of TAddress stays a part of its own (fid_N.city), as Quickbase lists it.
        ((List<object?>)trigger.Config["subsequentFields"]!).Select(f => (Field(f).Label, Field(f).Part)).Should().Equal(("Name", null), ("TAddress", null), ("TAddress", "city"));
        trigger.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void CreateRecord_InputKeyIsTheFieldLabel_JinjaBecomesAStepToken()
    {
        var pipeline = ConvertOne(AllSteps);
        var create = Root(pipeline, "ref_ab");

        var mapping = ((List<object?>)create.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>().Single();
        Field(mapping["field"]).Should().Be(new PblPipelineFieldValue("$App_Docs", "$Table_Employee1", "$Field_Employee_Name", 6, "Employee Name", false));
        Text(mapping["value"]).Should().Be("{{steps.ref_aa.[Name]}}");
        create.Config["tablePublicId"].Should().Be(new PblPipelineTableValue("$App_Docs", "$Table_Employee1"));
        create.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void UpdateAndDelete_TakeTheirTableAndRecordFromTheTargetStep()
    {
        var pipeline = ConvertOne(AllSteps);
        var update = Root(pipeline, "ref_ac");
        var delete = Root(pipeline, "ref_ad");

        update.Config["targetRecordId"].Should().Be("{{steps.ref_ab.CreatedRecordPublicId}}");
        update.Config["tableId"].Should().Be(new PblPipelineTableValue("$App_Docs", "$Table_Employee1"));
        var mapping = ((List<object?>)update.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>().Single();
        Field(mapping["field"]).Label.Should().Be("Employee Name");
        update.Config["connectionPublicId"].Should().BeOfType<PblPipelineConnectionValue>();

        delete.Config["targetRecordId"].Should().Be("{{steps.ref_ab.CreatedRecordPublicId}}");
        delete.Config["tableId"].Should().Be(new PblPipelineTableValue("$App_Docs", "$Table_Employee1"));
    }

    [Fact]
    public void SearchRecords_FilterIsWrittenAsTheFilterEditorDoes()
    {
        var search = Root(ConvertOne(AllSteps), "ref_ae");

        search.Config["isSimpleFilter"].Should().Be(true);
        var group = ((List<object?>)search.Config["filterGroups"]!).Cast<Dictionary<string, object?>>().Single();
        var rule = ((List<object?>)group["rules"]!).Cast<Dictionary<string, object?>>().Single();
        Field(rule["field"]).Label.Should().Be("Name");
        rule["operator"].Should().Be("starts-with");
        Text(rule["value"]).Should().Be("Ronak");
        ((List<object?>)search.Config["filters"]!).Should().ContainSingle();
        search.Config["tableId"].Should().Be(new PblPipelineTableValue("$App_Docs", "$Table_Employee"));
        search.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void LoopLookUpAndCondition_KeepTargetsBranchesAndRecordId()
    {
        var pipeline = ConvertOne(AllSteps);

        pipeline.Steps[5].Config["loopOverStepId"].Should().Be("ref_ae");
        Text(Root(pipeline, "ref_ag").Config["recordIdValue"]).Should().Be("{{steps.ref_aa.[Record ID#]}}");

        var condition = pipeline.Steps[7];
        var rule = ((List<object?>)((Dictionary<string, object?>)((List<object?>)condition.Config["ruleGroups"]!)[0]!)["rules"]!)
            .Cast<Dictionary<string, object?>>().Single();
        Text(rule["left"]).Should().Be("{{steps.ref_aa.[Name]}}");
        rule["op"].Should().Be("starts-with");
        Text(rule["right"]).Should().Be("Ronak");
        condition.Children.Should().ContainSingle(s => s.RefId == "ref_ah");
        condition.ElseChildren.Should().ContainSingle(s => s.RefId == "ref_ai" && s.Name == "Copy of Create Record");
    }

    [Fact]
    public void CopyRecords_PairsEachSourceFieldWithItsDestination()
    {
        var copy = Root(ConvertOne(AllSteps), "ref_aj");

        copy.Config["sourceTable"].Should().Be(new PblPipelineTableValue("$App_Docs", "$Table_Employee"));
        copy.Config["destinationTable"].Should().Be(new PblPipelineTableValue("$App_Docs", "$Table_Employee1"));
        ((List<object?>)copy.Config["sourceFields"]!).Select(f => Field(f).Label).Should().Equal("Name");
        ((List<object?>)copy.Config["destinationFields"]!).Select(f => Field(f).Label).Should().Equal("Employee Name");
        Field(copy.Config["mergeField"]).IsBuiltIn.Should().BeTrue();
        copy.Config["terminateOnError"].Should().Be("Yes");
        copy.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PauseAndBulkUpsert_KeepDurationTextAndPrepareStep()
    {
        var pipeline = ConvertOne(AllSteps);

        var pause = Root(pipeline, "ref_ak");
        pause.Config["durationText"].Should().Be("1 s");
        pause.Config.Should().NotContainKey("duration");
        pause.IsComplete.Should().BeTrue();

        var prepare = Root(pipeline, "ref_al");
        ((List<object?>)prepare.Config["fields"]!).Select(f => Field(f).Label).Should().Equal("Name");
        Field(prepare.Config["mergeField"]).Label.Should().Be("Record ID#");

        Root(pipeline, "ref_an").Config["bulkRecordSetStepId"].Should().Be("ref_al");
    }

    [Fact]
    public void AddBulkUpsertRow_UnknownJinjaVariable_IsClearedAndTheStepIsADraft()
    {
        var pipeline = ConvertOne(AllSteps);
        var row = Root(pipeline, "ref_am");

        row.Config["bulkRecordSetStepId"].Should().Be("ref_al");
        var entry = ((PblPipelineFieldMapValue)row.Config["rowValues"]!).Entries.Single();
        entry.Field.Label.Should().Be("Name");
        entry.Value.Should().Be("");
        row.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.StepId == "am" && i.Message.Contains("{{al.$namespace}}"));
        pipeline.ChangesExecution.Should().BeTrue();
    }

    /// <summary>The All Step export with the n-th (0-based) occurrence of <paramref name="find"/> replaced.</summary>
    internal static string AllStepsWith(string find, int occurrence, string replacement)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            index = AllSteps.IndexOf(find, index + 1, StringComparison.Ordinal);
            index.Should().BeGreaterThanOrEqualTo(0, $"occurrence {i} of '{find}' must exist");
        }
        return AllSteps[..index] + replacement + AllSteps[(index + find.Length)..];
    }

    [Fact]
    public void Metadata_RecordTriggerContextRecordAndSchema_BecomeStepMetadataTokens()
    {
        var pipeline = ConvertOne(AllStepsWith("{{aa.name}}", 0,
            "{{metadata.aa.context.app_name}}/{{metadata.aa.context.realm_host}}/{{metadata.aa.context.occurred_at}}/{{metadata.aa.record.action}}/{{metadata.aa.struct.name.qb_field_id}}/{{metadata.aa.struct.name.qb_field_type}}"));
        var create = Root(pipeline, "ref_ab");

        var mapping = ((List<object?>)create.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>().Single();
        Text(mapping["value"]).Should().Be(
            "{{steps.ref_aa._metadata.context.app_name}}/{{steps.ref_aa._metadata.context.realm_host}}/{{steps.ref_aa._metadata.context.occurred_at}}/" +
            "{{steps.ref_aa._metadata.record.action}}/{{steps.ref_aa._metadata.struct.[Name].qb_field_id}}/{{steps.ref_aa._metadata.struct.[Name].qb_field_type}}");
        // The schema entry names the field itself, so it follows the field's id into the imported app.
        ((PblPipelineTextValue)mapping["value"]!).Parts.OfType<PblPipelineFieldValue>().Should().OnlyContain(f => f.Label == "Name" && f.SourceId == 20);
        create.IsComplete.Should().BeTrue();
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.StepId == "ab");
    }

    [Fact]
    public void Metadata_SearchBatchAndLoopIndex_InsideTheLoop_BecomeStepMetadataTokens()
    {
        var pipeline = ConvertOne(AllStepsWith("{{aa.name}}", 2, "{{metadata.ae.batch.size}}-{{metadata.ae.loop.index}}"));
        var inLoop = pipeline.Steps[5].Children.Single(s => s.RefId == "ref_af");

        var mapping = ((List<object?>)inLoop.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>().Single();
        Text(mapping["value"]).Should().Be("{{steps.ref_ae._metadata.batch.size}}-{{steps.ref_ae._metadata.loop.index}}");
        inLoop.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Metadata_CopyRecordsTableCache_BecomesStepMetadataToken()
    {
        var pipeline = ConvertOne(AllStepsWith("{{al.$namespace}}", 0, "{{metadata.aj.qb_table_cache_hit}}"));
        var row = Root(pipeline, "ref_am");

        var entry = ((PblPipelineFieldMapValue)row.Config["rowValues"]!).Entries.Single();
        Text(entry.Value).Should().Be("{{steps.ref_aj._metadata.qb_table_cache_hit}}");
        row.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void CopyRecordsOutputs_KeepTheirQuickbaseNames()
    {
        var pipeline = ConvertOne(AllStepsWith("{{al.$namespace}}", 0,
            "{{aj.records_processed}}/{{aj.records_added}}/{{aj.records_updated}}/{{aj.records_unchanged}}/{{aj.records_skipped}}/{{aj.error_message}}/{{aj.error_code}}/{{aj.warnings}}"));
        var row = Root(pipeline, "ref_am");

        Text(((PblPipelineFieldMapValue)row.Config["rowValues"]!).Entries.Single().Value).Should().Be(
            "{{steps.ref_aj.records_processed}}/{{steps.ref_aj.records_added}}/{{steps.ref_aj.records_updated}}/{{steps.ref_aj.records_unchanged}}/" +
            "{{steps.ref_aj.records_skipped}}/{{steps.ref_aj.error_message}}/{{steps.ref_aj.error_code}}/{{steps.ref_aj.warnings}}");
        row.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void CopyRecordsUnknownOutput_IsClearedAndTheStepIsADraft()
    {
        var pipeline = ConvertOne(AllStepsWith("{{al.$namespace}}", 0, "{{aj.records_total}}"));

        Root(pipeline, "ref_am").IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.StepId == "am" && i.Message.Contains("{{aj.records_total}}"));
    }

    // powerflowextraRd: Record → Previous record, both as the picker writes it and as Quickbase's docs do ($prev).
    [Theory]
    [InlineData("{{metadata.aa.record.previous.name}}")]
    [InlineData("{{aa.$prev.name}}")]
    public void Metadata_PreviousRecord_BecomesStepMetadataTokenByFieldNumber(string token)
    {
        var pipeline = ConvertOne(AllStepsWith("{{aa.name}}", 0, "was " + token));
        var create = Root(pipeline, "ref_ab");

        var mapping = ((List<object?>)create.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>().Single();
        Text(mapping["value"]).Should().Be("was {{steps.ref_aa._metadata.record.previous.[Name]}}");
        create.IsComplete.Should().BeTrue();
    }

    [Theory]
    [InlineData("{{metadata.aa.record.previous.no_such_field}}")]
    [InlineData("{{metadata.aa.record.previous.name.email}}")]   // Name is not a user field
    [InlineData("{{aa.$prev.no_such_field}}")]
    public void Metadata_PreviousRecordWithoutAField_IsClearedAndTheStepIsADraft(string token)
    {
        var pipeline = ConvertOne(AllStepsWith("{{aa.name}}", 1, token));

        Root(pipeline, "ref_ac").IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.StepId == "ac" && i.Message.Contains(token));
    }

    [Theory]
    [InlineData("{{metadata.aa.context.unknown_key}}")]
    [InlineData("{{metadata.aa.struct.no_such_field.qb_field_id}}")]
    [InlineData("{{metadata.ab.context.app_name}}")]   // Create Record has no Metadata in Quickbase
    [InlineData("{{metadata.zz.context.app_name}}")]   // no such step
    [InlineData("{{metadata.aa}}")]
    public void Metadata_WithoutAPowerFlowsEquivalent_IsClearedAndTheStepIsADraft(string token)
    {
        var pipeline = ConvertOne(AllStepsWith("{{aa.name}}", 1, token));
        var update = Root(pipeline, "ref_ac");

        var mapping = ((List<object?>)update.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>().Single();
        Text(mapping["value"]).Should().Be("");
        update.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.StepId == "ac" && i.Message.Contains(token));
    }

    internal const string Unsupported = """
          $Pipeline_P:
            Type: QB::Pipeline
            Properties:
              Name: P
            Steps:
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: aa
                  Name: On New Event
                  Channel: { Resource: record, Id: quickbase, Selector: on_new_event }
                InputsMeta:
                  $Meta:
                    Type: QB::PipelineStep::InputsMeta
                    Properties: { On_Add_Record: true }
                    PipelineStep_DataSources:
                      $Ds:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                          Name: table
              - Type: QB::PipelineStep::Slack
                Properties:
                  Id: ab
                  Name: Send Slack Message
                  Channel: { Resource: message, Id: slack, Selector: send }
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ac
                  Name: Update Slack Record
                  Channel: { Resource: record, Id: quickbase, Selector: update }
                  Target: { StepId: ab, Type: step-reference }
              - Type: QB::PipelineStep::Quickbase
                Properties:
                  Id: ad
                  Name: Create Record
                  Channel: { Resource: record, Id: quickbase, Selector: create }
                  Inputs:
                    Employee_Name: '{{ab.channel}} by {{aa.name}}'
                  Link: { StepId: aa }
                InputsMeta:
                  $Meta:
                    Type: QB::PipelineStep::InputsMeta
                    PipelineStep_DataSources:
                      $Ds:
                        Type: QB::PipelineStep::DataSource::Table
                        Properties:
                          Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                          Name: table
        """;

    [Fact]
    public void UnsupportedStep_IsSkippedWithTheRequiredMessage()
    {
        var pipeline = ConvertOne(Unsupported);

        pipeline.Steps.Select(s => s.RefId).Should().Equal("ref_aa", "ref_ad");
        var skipped = pipeline.Issues.Single(i => i.StepId == "ab");
        skipped.IsSkippedStep.Should().BeTrue();
        skipped.StepName.Should().Be("Send Slack Message");
        skipped.Message.Should().StartWith(QblPipelineConverter.UNSUPPORTED_STEP_MESSAGE).And.Contain("slack/message/send");
        pipeline.ChangesExecution.Should().BeTrue();
    }

    [Fact]
    public void StepTargetingAnUnsupportedStep_IsSkippedToo()
    {
        var pipeline = ConvertOne(Unsupported);

        pipeline.Issues.Should().Contain(i => i.StepId == "ac" && i.IsSkippedStep && i.Code == "PIPELINE_STEP_DEPENDENCY_SKIPPED");
        pipeline.SkippedStepCount.Should().Be(2);
    }

    [Fact]
    public void ValueReadingAnUnsupportedStep_IsClearedAndLinkIsIgnored()
    {
        var pipeline = ConvertOne(Unsupported);
        var create = Root(pipeline, "ref_ad");

        var mapping = ((List<object?>)create.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>().Single();
        mapping["value"].Should().Be("");
        create.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.StepId == "ad" && i.Code == "PIPELINE_REFERENCE_CLEARED");
        pipeline.Issues.Should().Contain(i => i.StepId == "ad" && i.Code == "PIPELINE_LINK_IGNORED" && i.Message == QblPipelineConverter.LINK_IGNORED_MESSAGE && !i.ChangesExecution);
    }

    [Fact]
    public void LoopOverAnUnsupportedStep_IsSkippedWithEverythingInside()
    {
        var pipeline = ConvertOne("""
              $Pipeline_P:
                Type: QB::Pipeline
                Properties: { Name: P }
                Steps:
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: aa
                      Name: Search
                      Channel: { Resource: record, Id: quickbase, Selector: search }
                  - Type: QB::PipelineStep::Gmail
                    Properties: { Id: ab, Name: Read mail, Channel: { Resource: mail, Id: gmail, Selector: list } }
                  - Type: QB::PipelineStep::Loop
                    Properties: { Type: loop, Name: Loop, Target: { StepId: ab, Type: step-reference } }
                    Steps:
                      - Type: QB::PipelineStep::Stop
                        Properties: { Type: stop, Name: Stop }
            """);

        pipeline.Steps.Select(s => s.RefId).Should().Equal("ref_aa");
        pipeline.Issues.Should().Contain(i => i.StepName == "Loop" && i.Code == "PIPELINE_STEP_DEPENDENCY_SKIPPED");
        pipeline.Issues.Should().Contain(i => i.StepName == "Stop" && i.Code == "PIPELINE_STEP_PARENT_SKIPPED");
    }

    [Fact]
    public void UnsupportedTrigger_LeavesNoValidFirstStep_SoNoStepIsSaved()
    {
        var pipeline = ConvertOne("""
              $Pipeline_P:
                Type: QB::Pipeline
                Properties: { Name: P }
                Steps:
                  - Type: QB::PipelineStep::Webhooks
                    Properties: { Id: aa, Name: Incoming, Channel: { Resource: request, Id: webhooks, Selector: incoming } }
                  - Type: QB::PipelineStep::Condition
                    Properties: { Type: branch, Name: Condition }
                    Then:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ab
                          Name: Create Record
                          Channel: { Resource: record, Id: quickbase, Selector: create }
            """);

        // A Condition still cannot start a PowerFlow.
        pipeline.Steps.Should().BeEmpty();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_NO_VALID_START");
        pipeline.Issues.Should().Contain(i => i.StepId == "ab" && i.Code == "PIPELINE_STEP_NOT_SAVED" && i.IsSkippedStep);
    }

    [Fact]
    public void UnsupportedTrigger_FollowedByCreateRecord_KeepsTheCreateRecordAsTheFirstStep()
    {
        // As in Quickbase, Create Record can start a PowerFlow.
        var pipeline = ConvertOne("""
              $Pipeline_P:
                Type: QB::Pipeline
                Properties: { Name: P }
                Steps:
                  - Type: QB::PipelineStep::Webhooks
                    Properties: { Id: aa, Name: Incoming, Channel: { Resource: request, Id: webhooks, Selector: incoming } }
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Create Record
                      Channel: { Resource: record, Id: quickbase, Selector: create }
            """);

        pipeline.Steps.Should().ContainSingle().Which.Subtype.Should().Be("create-record");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_NO_VALID_START");
    }

    [Fact]
    public void FilterWithUnconfirmedOperator_IsLeftEmptyAndTheStepIsADraft()
    {
        var pipeline = ConvertOne("""
              $Pipeline_P:
                Type: QB::Pipeline
                Properties: { Name: P }
                Steps:
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: aa
                      Name: Search
                      Channel: { Resource: record, Id: quickbase, Selector: search }
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
                          - { Type: relational-expression, Field: name, Operator: sounds_like, Value: x }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                              Name: table
            """);

        var search = pipeline.Steps.Single();
        search.Config.Should().NotContainKey("filterGroups");
        search.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_FILTER_OPERATOR" && i.Message.Contains("sounds_like"));
    }

    [Fact]
    public void StopAtTheTopLevel_IsSkippedBecausePowerFlowsOnlyAllowsItInsideABranch()
    {
        var pipeline = ConvertOne("""
              $Pipeline_P:
                Type: QB::Pipeline
                Properties: { Name: P }
                Steps:
                  - Type: QB::PipelineStep::Clock
                    Properties: { Id: aa, Name: Pause, Channel: { Resource: timer, Id: clock, Selector: pause } }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Duration: 5 s } }
                  - Type: QB::PipelineStep::Stop
                    Properties: { Type: stop, Name: Stop }
            """);

        pipeline.Steps.Select(s => s.Subtype).Should().Equal("pause");
        pipeline.Issues.Should().Contain(i => i.StepName == "Stop" && i.Code == "PIPELINE_STEP_PLACEMENT" && i.IsSkippedStep);
    }

    [Fact]
    public void ParseAll_FileWithoutPipelines_HasNoPipelines()
    {
        var apps = ImportDocumentParser.ParseAll(App);

        apps.Should().ContainSingle().Which.Pipelines.Should().BeEmpty();
    }

    [Fact]
    public void ParseAll_PipelinesGoToTheApplicationTheyRead_AndIssuesStayOffTheApp()
    {
        var apps = ImportDocumentParser.ParseAll(App + "\n" + Unsupported + "\n" + Account);

        var app = apps.Should().ContainSingle().Subject;
        app.Pipelines.Should().ContainSingle(p => p.Name == "P");
        app.Issues.Should().NotContain(i => i.Code.StartsWith("PIPELINE_"));
    }

    [Fact]
    public void ForApp_PipelineWithUnknownApplication_IsReportedOnTheFirstApplication()
    {
        var pipelines = new List<PblPipeline> { new() { Name = "A", AppRef = "$App_2" }, new() { Name = "B", AppRef = null } };

        QblPipelineConverter.ForApp(pipelines, ["$App_1", "$App_2"], 0).Select(p => p.Name).Should().Equal("B");
        QblPipelineConverter.ForApp(pipelines, ["$App_1", "$App_2"], 1).Select(p => p.Name).Should().Equal("A");
    }
}
