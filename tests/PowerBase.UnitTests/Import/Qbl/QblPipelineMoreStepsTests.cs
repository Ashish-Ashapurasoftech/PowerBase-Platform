using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>
/// The steps and settings of the second real export (PowerFlows.qbl): Handle Errors, Upload a File, Send an Email, Import with
/// CSV, the two callable steps, trigger events/fields/limit, the search limit, and the tokens they read (loop item, user details,
/// built-in fields, {{ERROR.…}}, calling_pipeline and call arguments).
/// </summary>
public class QblPipelineMoreStepsTests
{
    private const string App = """
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
                    Properties: { Label: Record ID# }
                  $Field_Date_Created:
                    Type: QB::Field::DateCreated
                    Id: 1
                    Properties: { Label: Date Created }
                  $Field_Name:
                    Type: QB::Field::Text
                    Id: 20
                    Properties: { Label: Name }
                  $Field_Email:
                    Type: QB::Field::Email
                    Id: 21
                    Properties: { Label: Email }
                  $Field_TCheckbox:
                    Type: QB::Field::Checkbox
                    Id: 22
                    Properties: { Label: TCheckbox }
                  $Field_TestUser:
                    Type: QB::Field::User
                    Id: 23
                    Properties: { Label: TestUser }
                  $Field_T_Formula_Email:
                    Type: QB::Field::Email::Formula
                    Id: 47
                    Properties: { Label: T Formula Email }
                  $Field_TAddress:
                    Type: QB::Field::Address
                    Id: 30
                    Properties:
                      Label: T Address
                      City: !Ref
                        Field: $Field_City
                  $Field_City:
                    Type: QB::Field::AddressCity
                    Id: 31
              $Table_Employee1:
                Type: QB::Table
                Properties:
                  Name: Employee1
                  KeyField: !Ref
                    Field: $Field_Employee_Code
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                    Properties: { Label: Record ID# }
                  $Field_Employee_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties: { Label: Employee Name }
                  $Field_Employee_Code:
                    Type: QB::Field::Text
                    Id: 7
                    Properties: { Label: Employee Code }
                  $Field_Date_Created:
                    Type: QB::Field::DateCreated
                    Id: 1
                    Properties: { Label: Date Created }
        """;

    private const string Accounts = """
          $PipelineChannelAccount_Quickbase:
            Type: QB::PipelineChannelAccount
            Properties: { Channel: quickbase, AccountType: tokenless }
          $PipelineChannelAccount_Outlook:
            Type: QB::PipelineChannelAccount
            Properties: { Channel: microsoft-outlook }
          $PipelineChannelAccount_Http:
            Type: QB::PipelineChannelAccount
            Properties: { Channel: http, Alias: ysl-dev }
        """;

    private static string Trigger(string meta = "On_Add_Record: true") => $$"""
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: aa
                      Name: On New Event
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                      Channel: { Resource: record, Id: quickbase, Selector: on_new_event }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        Properties: { {{meta}} }
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                              Name: table
                          $Ds2:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields:
                                - !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_Name }
                                - !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_Email }
                              Name: trigger_fields
        """;

    /// <summary>A pipeline made of <paramref name="parts"/>, each a step list written at any indentation.</summary>
    private static PblPipeline Convert(params string[] parts)
    {
        var steps = string.Join("\n", parts.Select(Dedent));
        var content = App + "\n  $Pipeline_P:\n    Type: QB::Pipeline\n    Properties: { Name: P }\n    Steps:\n" + Indent(steps, 6) + "\n" + Accounts;
        return QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(content), content).Should().ContainSingle().Subject;
    }

    private static string Dedent(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var margin = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join("\n", lines.Select(l => l.Trim().Length == 0 ? "" : l[margin..]));
    }

    private static string Indent(string text, int by) => string.Join("\n", text.Split('\n').Select(l => new string(' ', by) + l));

    private static IEnumerable<PblPipelineStep> Flatten(IEnumerable<PblPipelineStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(Flatten(s.Children)).Concat(Flatten(s.ElseChildren))
            .Concat(Flatten(s.SuccessChildren)).Concat(Flatten(s.ErrorChildren)));

    private static PblPipelineStep Step(PblPipeline pipeline, string refId) => Flatten(pipeline.Steps).Single(s => s.RefId == refId);

    private static string Text(object? value) => value switch
    {
        PblPipelineTextValue text => string.Concat(text.Parts.Select(p => p is PblPipelineFieldValue f ? $"[{f.Label}]" : p.ToString())),
        _ => value?.ToString() ?? "",
    };

    private static string Mapping(PblPipelineStep step, string field) =>
        Text(((List<object?>)step.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>()
            .Single(m => ((PblPipelineFieldValue)m["field"]!).Label == field)["value"]);

    [Fact]
    public void Trigger_DeleteEventWatchedFieldsAndMaximum_AreImported()
    {
        var pipeline = Convert(Trigger("On_Add_Record: true, On_Delete_Record: true, Trigger_On_All_Fields: false, Allow_Triggers: Set a maximum, Triggers_Threshold: 10"));

        var trigger = pipeline.Steps.Single();
        trigger.Config["triggerOnDeleted"].Should().Be(true);
        trigger.Config["triggerOnAnyField"].Should().Be(false);
        ((List<object?>)trigger.Config["triggerFields"]!).Cast<PblPipelineFieldValue>().Select(f => f.Label).Should().Equal("Name", "Email");
        ((List<object?>)trigger.Config["monitoredFields"]!).Should().HaveCount(2);
        trigger.Config["limitRecords"].Should().Be(true);
        trigger.Config["maxRecords"].Should().Be(10);
        trigger.IsComplete.Should().BeTrue();
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_SETTING_NOT_IMPORTED");
    }

    [Fact]
    public void Trigger_FlagsMissingFromTheFile_KeepQuickbaseDefaults()
    {
        var trigger = Convert(Trigger("On_Add_Record: true")).Steps.Single();

        trigger.Config["triggerOnAdded"].Should().Be(true);
        trigger.Config["triggerOnModified"].Should().Be(true);
        trigger.Config["triggerOnDeleted"].Should().Be(false);
        trigger.Config["triggerOnAnyField"].Should().Be(true);
    }

    [Fact]
    public void Trigger_ModifyExplicitlyOff_StaysOff()
    {
        var trigger = Convert(Trigger("On_Add_Record: true, On_Modify_Record: false")).Steps.Single();

        trigger.Config["triggerOnModified"].Should().Be(false);
    }

    [Fact]
    public void Trigger_OnAllFields_KeepsAnyFieldAndListsNoWatchedFields()
    {
        var trigger = Convert(Trigger("On_Modify_Record: true, Trigger_On_All_Fields: true")).Steps.Single();

        trigger.Config["triggerOnAnyField"].Should().Be(true);
        // The editor's own defaults, so opening the imported step does not count as a change.
        ((List<object?>)trigger.Config["triggerFields"]!).Should().BeEmpty();
        ((List<object?>)trigger.Config["monitoredFields"]!).Should().BeEmpty();
        trigger.Config["limitRecords"].Should().Be(false);
    }

    [Fact]
    public void SearchLimitLoopItemUserDetailsAndBuiltIns_AreConverted()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Search Records
                      Channel: { Resource: record, Id: quickbase, Selector: search }
                      Inputs: { Limit: !!int 105 }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                              Name: table
                  - Type: QB::PipelineStep::Loop
                    Properties: { Type: loop, Name: Loop, Target: { StepId: ab, Type: step-reference } }
                    Steps:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ac
                          Name: Update Record
                          Channel: { Resource: record, Id: quickbase, Selector: update }
                          Target: { StepId: aa, Type: step-reference }
                          Inputs:
                            Name: '{{ab.email}} {{aa.created_at}}'
                            T_Checkbox: '{{ab.t_checkbox}}'
                            Email: '{{aa.test_user.email}}'
                            T_Address_City: x
            """);

        Step(pipeline, "ref_ab").Config["maxResults"].Should().Be(105);
        var update = Step(pipeline, "ref_ac");
        Mapping(update, "Name").Should().Be("{{steps.ref_ab.[Email]}} {{steps.ref_aa.[Date Created]}}");
        Mapping(update, "TCheckbox").Should().Be("{{steps.ref_ab.[TCheckbox]}}");
        Mapping(update, "Email").Should().Be("{{steps.ref_aa.[TestUser].email}}");
        // T_Address_City sets the City part of T Address (fid_N.city).
        var cityMapping = ((List<object?>)update.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>()
            .Single(m => ((PblPipelineFieldValue)m["field"]!).Part == "city");
        ((PblPipelineFieldValue)cityMapping["field"]!).Label.Should().Be("T Address");
        cityMapping["value"].Should().Be("x");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_ADDRESS_PART_INPUT");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
    }

    [Fact]
    public void HandleErrors_KeepsItsThreeBranches_AndErrorDetailsOnlyInOnError()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::ErrorHandling
                    Properties: { Type: error_handling, Mode: Handle, Name: Handle Errors }
                    Try:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: an
                          Name: Create Record
                          Channel: { Resource: record, Id: quickbase, Selector: create }
                          Inputs: { Employee_Name: '{{ERROR.name}}' }
                        InputsMeta:
                          $Meta:
                            Type: QB::PipelineStep::InputsMeta
                            PipelineStep_DataSources:
                              $Ds:
                                Type: QB::PipelineStep::DataSource::Table
                                Properties:
                                  Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                                  Name: table
                    OnError:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ap
                          Name: Create Record
                          Channel: { Resource: record, Id: quickbase, Selector: create }
                          Inputs: { Employee_Code: '{{ERROR.error_message}}', Employee_Name: '{{ERROR.name}}' }
                        InputsMeta:
                          $Meta:
                            Type: QB::PipelineStep::InputsMeta
                            PipelineStep_DataSources:
                              $Ds:
                                Type: QB::PipelineStep::DataSource::Table
                                Properties:
                                  Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                                  Name: table
                      - Type: QB::PipelineStep::Stop
                        Properties: { Type: stop, Name: Stop }
                    OnSuccess:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ao
                          Name: Update Record
                          Channel: { Resource: record, Id: quickbase, Selector: update }
                          Inputs: { Employee_Name: '{{aa.name}}' }
                          Target: { StepId: an, Type: step-reference }
            """);

        var handle = pipeline.Steps[1];
        handle.Subtype.Should().Be("handle-errors");
        handle.Config["fallbackAction"].Should().Be("handle");
        handle.Children.Select(s => s.RefId).Should().Equal("ref_an");
        handle.SuccessChildren.Select(s => s.RefId).Should().Equal("ref_ao");
        handle.ErrorChildren.Select(s => s.Subtype).Should().Equal("create-record", "stop");
        Mapping(Step(pipeline, "ref_ap"), "Employee Code").Should().Be("{{steps.ERROR.error_message}}");
        Mapping(Step(pipeline, "ref_ap"), "Employee Name").Should().Be("{{steps.ERROR.name}}");
        Step(pipeline, "ref_ap").IsComplete.Should().BeTrue();
        Step(pipeline, "ref_an").IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.StepId == "an" && i.Code == "PIPELINE_REFERENCE_CLEARED" && i.Message.Contains("On Error"));
        pipeline.SkippedStepCount.Should().Be(0);
    }

    [Fact]
    public void UploadFile_TargetsTheRecordStep_AndWaitsForTheFileField()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ag
                      Name: Upload a File
                      Channel: { Resource: attachment, Id: quickbase, Selector: upload }
                      Inputs: { Name: '{{aa.name}}', Url: 'https://example.test/a.pdf' }
                      Target: { StepId: aa, Type: step-reference }
            """);

        var upload = Step(pipeline, "ref_ag");
        upload.Type.Should().Be("file");
        upload.Config["fileRecordStepId"].Should().Be("ref_aa");
        Text(upload.Config["fileName"]).Should().Be("{{steps.ref_aa.[Name]}}");
        upload.Config["fileUrl"].Should().Be("https://example.test/a.pdf");
        upload.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.StepId == "ag" && i.Message.Contains("file attachment field"));
    }

    [Fact]
    public void SendEmail_MapsEveryInput_AndUsesPlatformSmtpInsteadOfTheOutlookAccount()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::MicrosoftOutlook
                    Properties:
                      Id: ar
                      Name: Send an Email
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Outlook }
                      Channel: { Resource: email, Id: microsoft-outlook, Selector: send_email }
                      Inputs:
                        To_Addresses: '{{aa.email}}'
                        Cc_Addresses: c@x.test
                        Bcc_Addresses: b@x.test
                        From_Address: f@x.test
                        Subject: '{{aa.name}}'
                        Body: 'Hi {{aa.name}}'
                        Content_Type: HTML
                        Importance: Low
                        Save_To_Sent_Items: false
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Mailbox: shared@x.test } }
            """);

        var email = Step(pipeline, "ref_ar");
        email.Subtype.Should().Be("send-email-outlook");
        Text(email.Config["toAddresses"]).Should().Be("{{steps.ref_aa.[Email]}}");
        email.Config["ccAddresses"].Should().Be("c@x.test");
        email.Config["bccAddresses"].Should().Be("b@x.test");
        email.Config["fromAddress"].Should().Be("f@x.test");
        Text(email.Config["body"]).Should().Be("Hi {{steps.ref_aa.[Name]}}");
        email.Config["contentType"].Should().Be("HTML");
        email.Config["importance"].Should().Be("Low");
        email.Config["saveToSentItems"].Should().Be("No");
        email.Config["sharedMailbox"].Should().Be("shared@x.test");
        email.IsComplete.Should().BeTrue();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_EMAIL_PLATFORM_SMTP" && !i.ChangesExecution);
        pipeline.Issues.Should().NotContain(i => i.StepId == "ar" && i.ChangesExecution);
    }

    [Fact]
    public void CallAnotherPipeline_TakesItsDefinitionAndArguments()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::CloudpipesMeta
                    Properties:
                      Id: at
                      Name: Call Another Pipeline
                      Channel: { Resource: pipeline, Id: cloudpipes-meta, Selector: call_pipeline }
                      Inputs: { Name: '{{aa.name}}', Status: Active }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Call_Definition: 'Flow1(Name , Status)' } }
            """);

        var call = Step(pipeline, "ref_at");
        call.Config["callDefinition"].Should().Be("Flow1(Name , Status)");
        var arguments = (Dictionary<string, object?>)call.Config["arguments"]!;
        Text(arguments["Name"]).Should().Be("{{steps.ref_aa.[Name]}}");
        arguments["Status"].Should().Be("Active");
        call.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PipelineCalled_IsAValidStart_AndItsArgumentsAndCallerAreReadable()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::CloudpipesMeta
                    Properties:
                      Id: aa
                      Name: Pipeline Called
                      Channel: { Resource: pipeline, Id: cloudpipes-meta, Selector: on_called }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Call_Definition: 'Flow1(Name, Status)' } }
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Create Record
                      Channel: { Resource: record, Id: quickbase, Selector: create }
                      Inputs: { Employee_Name: '{{aa.calling_pipeline.name}}', Employee_Code: '{{aa.status}}' }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                              Name: table
            """);

        pipeline.Steps.Select(s => s.Subtype).Should().Equal("pipeline-called", "create-record");
        pipeline.Steps[0].Type.Should().Be("trigger");
        pipeline.Steps[0].Config["callDefinition"].Should().Be("Flow1(Name, Status)");
        var create = pipeline.Steps[1];
        Mapping(create, "Employee Name").Should().Be("{{steps.ref_aa.calling_pipeline.name}}");
        Mapping(create, "Employee Code").Should().Be("{{steps.ref_aa.Status}}");
        create.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void CopyRecords_TwoSourcesIntoOneDestination_KeepsTheFirstAndReportsTheOther()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ak
                      Name: Copy Records
                      Channel: { Resource: bulk_record_set, Id: quickbase, Selector: copy_records }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds1:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields: [ !Ref { App: $App_Docs, Table: $Table_Employee1, Field: $Field_Record_ID } ]
                              Name: merge_field
                          $Ds2:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields: [ !Ref { App: $App_Docs, Table: $Table_Employee1, Field: $Field_Employee_Name } ]
                              Name: qb_field_for_email
                          $Ds3:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields: [ !Ref { App: $App_Docs, Table: $Table_Employee1, Field: $Field_Employee_Name } ]
                              Name: qb_field_for_name
                          $Ds7:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields: [ !Ref { App: $App_Docs, Table: $Table_Employee1, Field: $Field_Date_Created } ]
                              Name: qb_field_for_date_created
                          $Ds4:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                              Name: source_table
                          $Ds5:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields:
                                - !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_Date_Created }
                                - !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_Name }
                                - !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_Email }
                              Name: source_table_export_fields
                          $Ds6:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                              Name: table
            """);

        var copy = Step(pipeline, "ref_ak");
        // Every source field stays selected, in order; a pair PowerFlows cannot copy has no destination (and a reason).
        ((List<object?>)copy.Config["sourceFields"]!).Cast<PblPipelineFieldValue>().Select(f => f.Label).Should().Equal("Date Created", "Name", "Email");
        // Date Created → Date Created is mapped as in Quickbase (the copy keeps the destination's own dates).
        ((List<object?>)copy.Config["destinationFields"]!).Select(f => f is PblPipelineFieldValue field ? field.Label : f).Should().Equal("Date Created", "Employee Name", "");
        copy.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_COPY_DESTINATION_REPEATED")
            .Which.Message.Should().Contain("'Email'").And.Contain("'Name' already fills");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_COPY_DESTINATION_READ_ONLY");
    }

    private static string Search(string expression) => $$"""
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Search Records
                      Channel: { Resource: record, Id: quickbase, Selector: search }
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
            {{expression}}
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                              Name: table
        """;

    private static string Relational(string field, string op, string value = "", string target = "") =>
        $"              - {{ Type: relational-expression, Field: {field}, Operator: '{op}'{(value.Length > 0 ? ", Value: " + value : "")}{(target.Length > 0 ? ", Target: " + target : "")} }}";

    private static string Logical(string op) => $"              - {{ Type: logical-operator, Operator: {op} }}";

    /// <summary>Several expression lines for <see cref="Search"/>: its placeholder line keeps 4 columns once the raw string is dedented.</summary>
    private static string Expressions(params string[] lines) => string.Join("\n" + new string(' ', 4), lines);

    private static List<Dictionary<string, object?>> Rules(object? groups) =>
        ((List<object?>)groups!).Cast<Dictionary<string, object?>>().SelectMany(g => ((List<object?>)g["rules"]!).Cast<Dictionary<string, object?>>()).ToList();

    [Theory]
    [InlineData("name", "equals", "'Ronak'", "is", "equals")]
    [InlineData("name", "!equals", "'Ronak'", "is-not", "not-equals")]
    [InlineData("name", "is", "'Ronak'", "is", "equals")]
    [InlineData("name", "!is", "'Ronak'", "is-not", "not-equals")]
    [InlineData("name", "contains", "'Ron'", "contains", "contains")]
    [InlineData("name", "!contains", "'Ron'", "not-contains", "not-contains")]
    [InlineData("name", "starts", "'Ron'", "starts-with", "starts-with")]
    [InlineData("name", "set", "", "is-not-empty", "is_not_blank")]
    [InlineData("name", "!empty", "", "is-not-empty", "is_not_blank")]
    [InlineData("name", "!set", "", "is-empty", "is_blank")]
    [InlineData("name", "empty", "", "is-empty", "is_blank")]
    [InlineData("tcheckbox", "is", "true", "is_true", "is_true")]
    [InlineData("tcheckbox", "is", "false", "is_false", "is_false")]
    [InlineData("tcheckbox", "!is", "true", "is_false", "is_false")]
    [InlineData("id", "greater", "900000", "greater_than", "greater-than")]
    [InlineData("id", "less", "5", "less_than", "less-than")]
    [InlineData("date_created", "after", "'{{aa.date_created}}'", "is-after", "after")]
    [InlineData("date_created", "before", "'2026-01-01'", "is-before", "before")]
    [InlineData("date_created", "greater", "'2026-01-01'", "is-after", "after")]
    // Quickbase's "!" is the exact opposite of the operator.
    [InlineData("name", "!starts", "'Ron'", "not-starts-with", "not-starts-with")]
    [InlineData("name", "ends", "'ak'", "ends-with", "ends-with")]
    [InlineData("name", "!ends", "'ak'", "not-ends-with", "not-ends-with")]
    [InlineData("name", "!exists", "", "is-empty", "is_blank")]
    [InlineData("id", "!greater", "5", "less_than_or_equals", "less-than-or-equal")]
    [InlineData("id", "!less", "5", "greater_than_or_equals", "greater-than-or-equal")]
    [InlineData("date_created", "!after", "'2026-01-01'", "is-on-or-before", "on-or-before")]
    [InlineData("date_created", "!before", "'2026-01-01'", "is-on-or-after", "on-or-after")]
    [InlineData("date_created", "!on_date", "'2026-01-01'", "is-not", "not-equals")]
    public void FilterOperators_BecomeTheEditorsOperatorForTheFieldType(string field, string op, string value, string filterOp, string conditionOp)
    {
        var search = Convert(Trigger(), Search(Relational(field, op, value)));
        var rule = Rules(Step(search, "ref_ab").Config["filterGroups"]).Should().ContainSingle().Subject;
        rule["operator"].Should().Be(filterOp);
        search.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_OPERATOR" || i.Code == "PIPELINE_FILTER_NOT_VERIFIED");

        var condition = Convert(Trigger(), $$"""
                  - Type: QB::PipelineStep::Condition
                    Properties:
                      Name: Condition
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
            {{Relational(field, op, value, "aa")}}
            """);
        var conditionRule = Rules(Flatten(condition.Steps).Single(s => s.Subtype == "if-else").Config["ruleGroups"]).Should().ContainSingle().Subject;
        conditionRule["op"].Should().Be(conditionOp);
        condition.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_OPERATOR" || i.Code == "PIPELINE_FILTER_NOT_VERIFIED");
    }

    [Fact]
    public void FilterWithAndOr_BecomesOrEdGroupsOfAndEdRules()
    {
        // (name starts Ron AND email contains @) OR name set  →  [starts, contains] OR [set]
        var pipeline = Convert(Trigger(), Search(Expressions(
            Relational("name", "starts", "'Ron'"), Relational("email", "contains", "'@'"), Logical("AND"),
            Relational("name", "set"), Logical("OR"))));

        var config = Step(pipeline, "ref_ab").Config;
        var groups = ((List<object?>)config["filterGroups"]!).Cast<Dictionary<string, object?>>().ToList();
        groups.Select(g => ((List<object?>)g["rules"]!).Cast<Dictionary<string, object?>>().Select(r => (string)r["operator"]!).ToArray())
            .Should().BeEquivalentTo(new[] { new[] { "starts-with", "contains" }, new[] { "is-not-empty" } }, o => o.WithStrictOrdering());
        groups.Should().OnlyContain(g => (string)g["logicalOp"]! == "AND");
        ((List<object?>)config["filters"]!).Should().HaveCount(3);
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_NOT_VERIFIED");
    }

    [Fact]
    public void FilterWithAnUnknownOperator_IsLeftEmpty_NotPartlyImported()
    {
        var pipeline = Convert(Trigger(), Search(Expressions(
            Relational("name", "starts", "'Ron'"), Relational("name", "regex", "'R.*'"), Logical("AND"))));

        Step(pipeline, "ref_ab").Config.Should().NotContainKey("filterGroups");
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_FILTER_OPERATOR").Which.Message.Should().Contain("'regex'");
    }

    [Theory]
    [InlineData("!empty", "not-equals")]
    [InlineData("exists", "not-equals")]
    [InlineData("empty", "equals")]
    public void ConditionOnASearchHavingRecords_TestsItsCount(string op, string expected)
    {
        var pipeline = Convert(Trigger(), Search(Relational("name", "starts", "'Ron'")), $$"""
                  - Type: QB::PipelineStep::Condition
                    Properties:
                      Name: Condition
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
                          - { Type: relational-expression, Operator: '{{op}}', Target: ab }
            """);

        var rule = Rules(Flatten(pipeline.Steps).Single(s => s.Subtype == "if-else").Config["ruleGroups"]).Single();
        rule["left"].Should().Be("{{steps.ref_ab.count}}");
        rule["op"].Should().Be(expected);
        rule["right"].Should().Be("0");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_NOT_VERIFIED" || i.Code == "PIPELINE_FILTER_OPERATOR");
    }

    [Fact]
    public void ConditionOnTheTriggerHavingARecord_TestsItsRecordId()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Condition
                    Properties:
                      Name: Condition
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
                          - { Type: relational-expression, Operator: exists, Target: aa }
            """);

        var rule = Rules(Flatten(pipeline.Steps).Single(s => s.Subtype == "if-else").Config["ruleGroups"]).Single();
        rule["op"].Should().Be("is_not_blank");
        Text(rule["left"]).Should().Be("{{steps.ref_aa.[Record ID#]}}");
    }

    [Fact]
    public void WebhooksMakeRequest_IsAnHttpRequestWithItsUrlBodyAndHeaders()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Webhooks
                    Properties:
                      Id: ab
                      Name: Make Request
                      Channel: { Resource: request, Id: webhooks, Selector: make }
                      Inputs:
                        Method: POST
                        Url: https://signature.example.com/api/files?x=1
                        Content_Type: application/json
                        Body: '{"id": 1}'
                        Headers:
                          Name: "Authorization\nX-Realm"
                          Value: "Token abc\nysl"
            """);

        var request = Step(pipeline, "ref_ab");
        request.Subtype.Should().Be("make-request");
        request.Config["requestMode"].Should().Be("http");
        request.Config["method"].Should().Be("POST");
        request.Config["baseUrl"].Should().Be("https://signature.example.com");
        request.Config["path"].Should().Be("api/files?x=1");
        request.Config["contentType"].Should().Be("application/json");
        request.Config["body"].Should().Be("{\"id\": 1}");
        ((List<object?>)request.Config["stepHeaders"]!).Cast<Dictionary<string, object?>>().Select(h => $"{h["key"]}={h["value"]}")
            .Should().Equal("Authorization=Token abc", "X-Realm=ysl");
        request.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_HTTP_CONNECTION_MISSING");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_STEP_UNSUPPORTED" || i.Code == "PIPELINE_SETTING_NOT_IMPORTED");
    }

    [Fact]
    public void MakeRequestOutputs_StatusContentAndHeaders_AreReadFromTheRequestsMetadata()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Webhooks
                    Properties:
                      Id: ab
                      Name: Make Request
                      Channel: { Resource: request, Id: webhooks, Selector: make }
                      Inputs: { Method: GET, Url: 'https://example.com/check' }
                  - Type: QB::PipelineStep::Condition
                    Properties:
                      Name: Condition
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
                          - { Type: relational-expression, Target: ab, Field: status, Operator: '!equals', Value: 200 }
                    Then:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ac
                          Name: Create Record
                          PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                          Channel: { Resource: record, Id: quickbase, Selector: create }
                          Inputs:
                            Employee_Name: '{{ab.content}} / {{ab.json.message}}'
                        InputsMeta:
                          $Meta:
                            Type: QB::PipelineStep::InputsMeta
                            PipelineStep_DataSources:
                              $Ds:
                                Type: QB::PipelineStep::DataSource::Table
                                Properties:
                                  Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                                  Name: table
            """);

        var rule = Rules(Flatten(pipeline.Steps).Single(s => s.Subtype == "if-else").Config["ruleGroups"]).Single();
        Text(rule["left"]).Should().Be("{{steps.ref_ab._metadata.status_code}}");
        rule["op"].Should().Be("not-equals");
        Mapping(Step(pipeline, "ref_ac"), "Employee Name").Should().Be("{{steps.ref_ab._metadata.body.text}} / {{steps.ref_ab.message}}");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_REFERENCE_CLEARED");
    }

    [Fact]
    public void LookUpByTheTablesKeyField_KeepsTheKeyFieldAndItsValue()
    {
        // Employee1's key field is Employee Code: Quickbase names the Look Up input after it.
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Look Up a Record
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                      Channel: { Resource: record, Id: quickbase, Selector: look_up }
                      Inputs:
                        Employee_Code: '{{aa.name}}'
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                              Name: table
            """);

        var lookUp = Step(pipeline, "ref_ab");
        ((PblPipelineFieldValue)lookUp.Config["lookupFieldId"]!).Label.Should().Be("Employee Code");
        Text(lookUp.Config["recordIdValue"]).Should().Be("{{steps.ref_aa.[Name]}}");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_INPUT_MISSING" || i.Code == "PIPELINE_SETTING_NOT_IMPORTED");
    }

    [Fact]
    public void LookUpByAnInputThatIsNotTheKeyField_IsStillReported()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Look Up a Record
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                      Channel: { Resource: record, Id: quickbase, Selector: look_up }
                      Inputs:
                        Employee_Name: '{{aa.name}}'
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                              Name: table
            """);

        Step(pipeline, "ref_ab").Config.Should().NotContainKey("lookupFieldId");
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_INPUT_MISSING");
    }

    [Fact]
    public void QuickbaseMakeRequestHeaders_AreKept()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Make Request
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Quickbase }
                      Channel: { Resource: quickbase_api, Id: quickbase, Selector: make }
                      Inputs:
                        Method: POST
                        Url: /tables/x/records
                        Headers:
                          Name: "QB-Realm-Hostname\nContent-Type\n"
                          Value: "ysl.quickbase.com\napplication/json"
            """);

        ((List<object?>)Step(pipeline, "ref_ab").Config["headersList"]!).Cast<Dictionary<string, object?>>().Select(h => $"{h["name"]}={h["value"]}")
            .Should().Equal("QB-Realm-Hostname=ysl.quickbase.com", "Content-Type=application/json");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_SETTING_NOT_IMPORTED");
    }

    [Theory]
    [InlineData("on_create", true, false)]
    [InlineData("on_update", false, true)]
    public void RecordCreatedAndRecordUpdated_AreOnNewEventForThatEvent(string selector, bool added, bool modified)
    {
        var pipeline = Convert(Trigger().Replace("on_new_event", selector));

        var trigger = pipeline.Steps.Single();
        trigger.Subtype.Should().Be("new-event");
        trigger.Config["triggerOnAdded"].Should().Be(added);
        trigger.Config["triggerOnModified"].Should().Be(modified);
        trigger.Config["triggerOnDeleted"].Should().Be(false);
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_STEP_UNSUPPORTED" || i.Code == "PIPELINE_NO_VALID_START");
    }

    [Fact]
    public void UpdateRecordInsideALoop_TargetingTheLoopedSearch_UpdatesTheLoopsRecord()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Search Records
                      Channel: { Resource: record, Id: quickbase, Selector: search }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                              Name: table
                  - Type: QB::PipelineStep::Loop
                    Properties:
                      Name: Loop
                      Target: { StepId: ab, Type: step-reference }
                    Steps:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ac
                          Name: Update Record
                          Channel: { Resource: record, Id: quickbase, Selector: update }
                          Inputs:
                            Employee_Code: Done
                          Target: { StepId: ab, Type: step-reference }
            """);

        var update = Step(pipeline, "ref_ac");
        var loop = Flatten(pipeline.Steps).Single(s => s.Subtype == "for-each");
        update.Config["targetRecordId"].Should().Be($"{{{{steps.{loop.RefId}.item.RecordPublicId}}}}");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_STEP_TARGET_INVALID");
        Mapping(update, "Employee Code").Should().Be("Done");
    }

    [Fact]
    public void HttpMakeRequest_KeepsMethodPathBodyAndPayload_AndWaitsForTheHttpConnection()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::Http
                    Properties:
                      Id: ab
                      Name: Make Request
                      Channel: { Resource: request, Id: http, Selector: make_request }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        Properties:
                          Body: '{"name": "Ronak"}'
                          Expected_Payload_Type: json
                          Method: POST
                          Path: /post
                          Schema_Sample_Type: json-sample
                          Validate_Response_Payload_Type: true
            """);

        var request = pipeline.Steps.Single();
        request.Subtype.Should().Be("make-request");
        request.Config["requestMode"].Should().Be("http");
        request.Config["method"].Should().Be("POST");
        request.Config["path"].Should().Be("/post");
        request.Config["body"].Should().Be("{\"name\": \"Ronak\"}");
        request.Config["expectedPayloadType"].Should().Be("JSON");
        request.Config["schemaSampleType"].Should().Be("JSON sample");
        request.Config["validateResponsePayload"].Should().Be("Yes");
        request.IsComplete.Should().BeFalse();
        // The step names no account in this file: it waits for one, with no name to keep.
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_HTTP_ACCOUNT_MISSING");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_SETTING_NOT_IMPORTED" || i.Code == "PIPELINE_NO_VALID_START");
    }

    [Theory]
    [InlineData(null, "Yes")]
    [InlineData("true", "Yes")]
    [InlineData("false", "No")]
    public void HttpMakeRequest_ValidateResponsePayload_MissingMeansQuickbasesDefaultOn(string? fileValue, string expected)
    {
        var validate = fileValue is null ? "" : $", Validate_Response_Payload_Type: {fileValue}";
        var request = Convert($$"""
                  - Type: QB::PipelineStep::Http
                    Properties:
                      Id: ad
                      Name: Make Request
                      Channel: { Resource: request, Id: http, Selector: make_request }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Method: PUT, Path: /put{{validate}} } }
            """).Steps.Single();

        request.Config["method"].Should().Be("PUT");
        request.Config["validateResponsePayload"].Should().Be(expected);
        request.Config["schemaSample"].Should().Be("");
    }

    [Fact]
    public void HttpMakeRequest_KeepsTheQuickbaseAccountName_AndCreatesNoConnection()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::Http
                    Properties:
                      Id: aa
                      Name: Make Request
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Http }
                      Channel: { Resource: request, Id: http, Selector: make_request }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Method: GET, Path: /get } }
                  - Type: QB::PipelineStep::Http
                    Properties:
                      Id: ab
                      Name: Make Request
                      PipelineChannelAccount: !Ref { PipelineChannelAccount: $PipelineChannelAccount_Http }
                      Channel: { Resource: request, Id: http, Selector: make_request }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Method: POST, Path: /post } }
            """);

        pipeline.Steps.Should().OnlyContain(s => (string?)s.Config["accountName"] == "ysl-dev");
        pipeline.Steps.Should().OnlyContain(s => !s.Config.ContainsKey("httpConnectionId") && !s.IsComplete);
        pipeline.Issues.Where(i => i.Code == "PIPELINE_HTTP_CONNECTION_MISSING").Should().HaveCount(2)
            .And.OnlyContain(i => i.Message.Contains("'ysl-dev'"));
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_ACCOUNT_UNSUPPORTED" || i.Code == "PIPELINE_HTTP_ACCOUNT_MISSING");
    }

    [Fact]
    public void HttpMakeRequest_WithoutAnAccount_WarnsAndLeavesTheNameEmpty()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::Http
                    Properties:
                      Id: aa
                      Name: Make Request
                      Channel: { Resource: request, Id: http, Selector: make_request }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Method: GET, Path: /get } }
            """);

        var request = pipeline.Steps.Single();
        request.Config.Should().NotContainKey("accountName");
        request.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_HTTP_ACCOUNT_MISSING");
    }

    [Fact]
    public void WritesToCalculatedFields_AreLeftOutAndReported_OtherMappingsUnchanged()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ae
                      Name: Update Record
                      Channel: { Resource: record, Id: quickbase, Selector: update }
                      Target: { StepId: aa, Type: step-reference }
                      Inputs: { Name: '{{aa.name}}', T_Formula_Email: '{{aa.email}}', Email: '{{aa.email}}' }
            """);

        var update = Step(pipeline, "ref_ae");
        ((List<object?>)update.Config["fieldMappings"]!).Cast<Dictionary<string, object?>>()
            .Select(m => ((PblPipelineFieldValue)m["field"]!).Label).Should().Equal("Name", "Email");
        Mapping(update, "Name").Should().Be("{{steps.ref_aa.[Name]}}");
        update.IsComplete.Should().BeTrue();
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_COMPUTED_FIELD_WRITE")
            .Which.Message.Should().Contain("'T Formula Email'");
    }

    [Fact]
    public void QuickbaseMakeRequest_IsThePowerBaseAccountMode_AndNeverKeepsAQuickbaseApiUrl()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: aa
                      Name: Make Request
                      Channel: { Resource: quickbase_api, Id: quickbase, Selector: make }
                      Inputs: { Content_Type: application/json, Method: GET, Url: 'https://api.quickbase.com/v1/records/query' }
            """);

        var request = pipeline.Steps.Single();
        request.Config["requestMode"].Should().Be("quickbase");
        request.Config["connectionPublicId"].Should().BeOfType<PblPipelineConnectionValue>();
        request.Config["method"].Should().Be("GET");
        request.Config["contentType"].Should().Be("application/json");
        Text(request.Config["url"]).Should().Be("https://api.quickbase.com/v1/records/query");
        request.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_REQUEST_URL_QUICKBASE")
            .Which.Message.Should().Contain("https://api.quickbase.com/v1/records/query");
    }

    [Fact]
    public void IncomingRequest_IsAWebhookTrigger_AndItsBodyAndParametersAreReadable()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::Webhooks
                    Properties:
                      Id: aa
                      Name: Incoming Request
                      Channel: { Resource: request, Id: webhooks, Selector: on_incoming }
                    InputsMeta:
                      $Meta: { Type: QB::PipelineStep::InputsMeta, Properties: { Authentication_Schema: no-auth, Method_Type: POST, Proxy_Via_Agent: true } }
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ab
                      Name: Create Record
                      Channel: { Resource: record, Id: quickbase, Selector: create }
                      Inputs: { Name: '{{aa.body_params.name}}', Email: '{{aa.body}}', T_Checkbox: '{{aa.headers.name.x}}' }
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

        var trigger = pipeline.Steps[0];
        (trigger.Type, trigger.Subtype).Should().Be(("trigger", "webhook"));
        trigger.Config["authType"].Should().Be("no-auth");
        trigger.Config["methodType"].Should().Be("POST");
        trigger.Config["proxyViaOnPremisesAgent"].Should().Be(true);
        pipeline.Issues.Should().Contain(i => i.Code == "PIPELINE_WEBHOOK_NEW_URL");
        var create = pipeline.Steps[1];
        Mapping(create, "Name").Should().Be("{{steps.ref_aa.body_params.name}}");
        Mapping(create, "Email").Should().Be("{{steps.ref_aa.body}}");
        Mapping(create, "TCheckbox").Should().Be("");
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_REFERENCE_CLEARED" && i.Message.Contains("headers.name.x"));
    }

    [Fact]
    public void OnNewBulkEvent_UsesTheTriggerSettingsAndFilter_AndALoopReadsItsRecords()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: aa
                      Name: On New Bulk Event
                      Channel: { Resource: record, Id: quickbase, Selector: on_new_bulk_event }
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
                          - { Type: relational-expression, Field: name, Operator: starts, Value: aaa }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        Properties: { On_Add_Record: true, On_Delete_Record: true, Allow_Triggers: Set a maximum, Triggers_Threshold: 50 }
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                              Name: table
                  - Type: QB::PipelineStep::Loop
                    Properties: { Type: loop, Name: Loop, Target: { StepId: aa, Type: step-reference } }
                    Steps:
                      - Type: QB::PipelineStep::Quickbase
                        Properties:
                          Id: ab
                          Name: Create Record
                          Channel: { Resource: record, Id: quickbase, Selector: create }
                          Inputs: { Employee_Name: '{{aa.name}}' }
                        InputsMeta:
                          $Meta:
                            Type: QB::PipelineStep::InputsMeta
                            PipelineStep_DataSources:
                              $Ds:
                                Type: QB::PipelineStep::DataSource::Table
                                Properties:
                                  Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                                  Name: table
            """);

        var trigger = pipeline.Steps[0];
        (trigger.Type, trigger.Subtype).Should().Be(("trigger", "new-bulk-event"));
        trigger.Config["triggerOnAdded"].Should().Be(true);
        trigger.Config["triggerOnDeleted"].Should().Be(true);
        // On_Modify_Record is not in the file: Quickbase's default (on) is kept.
        trigger.Config["triggerOnModified"].Should().Be(true);
        trigger.Config["limitRecords"].Should().Be(true);
        trigger.Config["maxRecords"].Should().Be(50);
        trigger.Config.Should().ContainKey("filterGroups");
        ((List<object?>)trigger.Config["filters"]!).Should().ContainSingle();
        trigger.IsComplete.Should().BeTrue();
        Mapping(Step(pipeline, "ref_ab"), "Employee Name").Should().Be("{{steps.ref_aa.[Name]}}");
        pipeline.Issues.Should().NotContain(i => i.ChangesExecution);
    }

    [Fact]
    public void BulkUpsert_PrepareFieldsAndRowValues_KeepAddressParts()
    {
        var pipeline = Convert("""
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ah
                      Name: Prepare Bulk Record Upsert
                      Channel: { Resource: bulk_record_set, Id: quickbase, Selector: define }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        PipelineStep_DataSources:
                          $Ds1:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee }
                              Name: table
                          $Ds2:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields:
                                - !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_Name }
                                - !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_City }
                              Name: export_fields
                          $Ds3:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields: [ !Ref { App: $App_Docs, Table: $Table_Employee, Field: $Field_Record_ID } ]
                              Name: merge_field
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: ai
                      Name: Add a Bulk Upsert Row
                      Channel: { Resource: bulk_record_set, Id: quickbase, Selector: create }
                      Inputs: { Name: Ronak, T_Address_City: Pune }
                      Target: { StepId: ah, Type: step-reference }
            """);

        var prepare = Step(pipeline, "ref_ah");
        ((List<object?>)prepare.Config["fields"]!).Cast<PblPipelineFieldValue>().Select(f => (f.Label, f.Part))
            .Should().Equal(("Name", null), ("T Address", "city"));
        var row = (PblPipelineFieldMapValue)Step(pipeline, "ref_ai").Config["rowValues"]!;
        row.Entries.Select(e => (e.Field.Label, e.Field.Part, e.Value)).Should().Equal(("Name", null, (object?)"Ronak"), ("T Address", "city", "Pune"));
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_ADDRESS_PARTS" || i.Code == "PIPELINE_ADDRESS_PART_INPUT");
    }

    [Fact]
    public void ImportWithCsv_KeepsTheUrl_AndListsTheQuickbaseColumnsForTheTemplate()
    {
        var pipeline = Convert(Trigger(), """
                  - Type: QB::PipelineStep::Quickbase
                    Properties:
                      Id: au
                      Name: Import with CSV
                      Channel: { Resource: bulk_record_set, Id: quickbase, Selector: csv_import }
                    InputsMeta:
                      $Meta:
                        Type: QB::PipelineStep::InputsMeta
                        Properties: { Csv_Url: 'https://example.test/a.csv', Header_Row: '1', Row_Quote_Char: '"' }
                        PipelineStep_DataSources:
                          $Ds:
                            Type: QB::PipelineStep::DataSource::Table
                            Properties:
                              Table: !Ref { App: $App_Docs, Table: $Table_Employee1 }
                              Name: table
                          $Ds2:
                            Type: QB::PipelineStep::DataSource::Fields
                            Properties:
                              Fields:
                                - !Ref { App: $App_Docs, Table: $Table_Employee1, Field: $Field_Employee_Code }
                              Name: qb_field_for_email
            """);

        var import = Step(pipeline, "ref_au");
        import.Config["csvFile"].Should().Be("https://example.test/a.csv");
        import.Config["tableId"].Should().BeOfType<PblPipelineTableValue>();
        import.IsComplete.Should().BeFalse();
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_IMPORT_TEMPLATE_MISSING")
            .Which.Message.Should().Contain("column 'email' → 'Employee Code'").And.Contain("Header Row '1'");
        pipeline.Issues.Should().NotContain(i => i.StepId == "au" && i.Code == "PIPELINE_SETTING_NOT_IMPORTED");
    }

    // ---- "on date" filters with a time zone ----

    /// <summary><see cref="Convert"/> on an app whose Employee table also has a Date field "Hire Date" (key hire_date).</summary>
    private static PblPipeline ConvertWithDateField(params string[] parts)
    {
        var steps = string.Join("\n", parts.Select(Dedent));
        var nameLine = App.Replace("\r", "").Split('\n').First(l => l.TrimStart().StartsWith("$Field_Name:", StringComparison.Ordinal));
        var indent = nameLine[..(nameLine.Length - nameLine.TrimStart().Length)];
        var app = App.Replace(nameLine.TrimStart(), "$Field_Hire_Date:\n" + indent + "  Type: QB::Field::Date\n" + indent + "  Id: 40\n" + indent
            + "  Properties: { Label: Hire Date }\n" + indent + "$Field_Name:");
        var content = app + "\n  $Pipeline_P:\n    Type: QB::Pipeline\n    Properties: { Name: P }\n    Steps:\n" + Indent(steps, 6) + "\n" + Accounts;
        return QblPipelineConverter.ConvertAll(QblSerializer.Deserialize(content), content).Should().ContainSingle().Subject;
    }

    private static Dictionary<string, object?> OnlySearchRule(PblPipeline pipeline) =>
        Rules(Step(pipeline, "ref_ab").Config["filterGroups"]).Should().ContainSingle().Subject;

    [Fact]
    public void OnDateInAZone_OnADateField_KeepsTheDay_TheZoneChangesNothing()
    {
        var pipeline = ConvertWithDateField(Trigger(), Search(Relational("hire_date", "on_date", "['2026-01-01', America/New_York]")));

        var rule = OnlySearchRule(pipeline);
        rule["operator"].Should().Be("is");
        rule["value"].Should().Be("2026-01-01");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_TIME_ZONE");
    }

    [Fact]
    public void OnDateInAZone_OnADateFieldFromAStep_KeepsTheToken_TheZoneChangesNothing()
    {
        var pipeline = ConvertWithDateField(Trigger(), Search(Relational("hire_date", "on_date", "['{{aa.date_created}}', America/New_York]")));

        OnlySearchRule(pipeline)["value"].Should().NotBe("");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_TIME_ZONE");
    }

    [Theory]
    [InlineData("2026-01-01", "2026-01-01T05:00:00.000Z")] // New York winter: UTC-5
    [InlineData("2026-07-01", "2026-07-01T04:00:00.000Z")] // New York summer: UTC-4
    [InlineData("07-01-2026", "2026-07-01T04:00:00.000Z")]
    public void OnDateInAZone_OnADateTimeField_StartsTheDayAtMidnightInThatZone(string day, string instant)
    {
        var pipeline = Convert(Trigger(), Search(Relational("date_created", "on_date", $"['{day}', America/New_York]")));

        var rule = OnlySearchRule(pipeline);
        rule["operator"].Should().Be("is");
        rule["value"].Should().Be(instant);
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_TIME_ZONE");
    }

    [Fact]
    public void NotOnDateInAZone_OnADateTimeField_AlsoUsesThatZone()
    {
        var pipeline = Convert(Trigger(), Search(Relational("date_created", "!on_date", "['2026-01-01', America/New_York]")));

        var rule = OnlySearchRule(pipeline);
        rule["operator"].Should().Be("is-not");
        rule["value"].Should().Be("2026-01-01T05:00:00.000Z");
    }

    [Fact]
    public void OnDateInAZone_OnADateTimeFieldFromAStep_KeepsTheTokenAndSaysTheZoneIsNotApplied()
    {
        var pipeline = Convert(Trigger(), Search(Relational("date_created", "on_date", "['{{aa.date_created}}', America/New_York]")));

        OnlySearchRule(pipeline)["value"].Should().NotBe("");
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_FILTER_TIME_ZONE").Which.Message.Should().Contain("America/New_York");
    }

    [Fact]
    public void OnDateInAnUnknownZone_KeepsTheDayAndSaysTheZoneIsNotApplied()
    {
        var pipeline = Convert(Trigger(), Search(Relational("date_created", "on_date", "['2026-01-01', Mars/Base]")));

        OnlySearchRule(pipeline)["value"].Should().Be("2026-01-01");
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_FILTER_TIME_ZONE");
    }

    [Fact]
    public void OnDateInAZone_InACondition_KeepsTheDayAndSaysTheZoneIsNotApplied()
    {
        var pipeline = Convert(Trigger(), $$"""
                  - Type: QB::PipelineStep::Condition
                    Properties:
                      Name: Condition
                      Filters:
                        Type: composite-expression
                        Notation: postfix
                        Expression:
            {{Relational("date_created", "on_date", "['2026-01-01', America/New_York]", "aa")}}
            """);

        var rule = Rules(Flatten(pipeline.Steps).Single(s => s.Subtype == "if-else").Config["ruleGroups"]).Should().ContainSingle().Subject;
        rule["right"].Should().Be("2026-01-01");
        pipeline.Issues.Should().ContainSingle(i => i.Code == "PIPELINE_FILTER_TIME_ZONE");
    }

    [Fact]
    public void OnDateWithoutAZone_IsUnchanged()
    {
        var pipeline = Convert(Trigger(), Search(Relational("date_created", "on_date", "'2026-01-01'")));

        OnlySearchRule(pipeline)["value"].Should().Be("2026-01-01");
        pipeline.Issues.Should().NotContain(i => i.Code == "PIPELINE_FILTER_TIME_ZONE");
    }
}
