using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

public class QblToPblConverterTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    [Fact]
    public void Convert_ApplicationAndTable_ProducesPblAppAndTable()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
              Description: A test app
              AppIcon: Application
              AppColor: '#72509a'
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                  RecordNameSingular: Client
                  RecordNamePlural: Clients
                  TableIconName: Contact
                Fields: {}
        """;

        var result = Convert(yaml);

        result.Document.App.Name.Should().Be("Test App");
        result.Document.App.Description.Should().Be("A test app");
        result.Document.App.Icon.Should().Be("Application");
        result.Document.App.Color.Should().Be("#72509a");
        result.Document.Tables.Should().ContainSingle();
        result.Document.Tables[0].Name.Should().Be("Clients");
        result.Document.Tables[0].SingularLabel.Should().Be("Client");
    }

    [Fact]
    public void Convert_SameLocalFieldKeyAcrossTables_ProducesDistinctGloballyUniqueLogicalRefs()
    {
        // Confirmed real bug from an actual production QBL export: Quickbase auto-names field
        // resource keys from their label, so two unrelated tables that each have a field
        // labelled "Name" both end up with the identical local key "$Field_Name" - only unique
        // within their own table's Fields map, not across the whole document. PBL's LogicalRef
        // must be unique across the entire document (PblValidator checks this with one shared
        // set for every construct), so the converter must qualify by table rather than using the
        // raw QBL key verbatim.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Name
              $Table_Vendors:
                Type: QB::Table
                Properties:
                  Name: Vendors
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Name
        """;

        var result = Convert(yaml);

        var clientsField = result.Document.Tables.Single(t => t.Name == "Clients").Fields.Should().ContainSingle().Subject;
        var vendorsField = result.Document.Tables.Single(t => t.Name == "Vendors").Fields.Should().ContainSingle().Subject;

        clientsField.LogicalRef.Should().NotBe(vendorsField.LogicalRef);

        var validation = new PblValidator().Validate(result.Document);
        validation.Errors.Should().NotContain(e => e.Code == "DUPLICATE_LOGICAL_REF");
    }

    [Fact]
    public void Convert_ScalarField_UsesLabelAsName()
    {
        var result = Convert(TableWithFields("""
              $Field_Name:
                Type: QB::Field::Text
                Id: 6
                Properties:
                  Label: Company Name
                  IsRequired: true
            """));

        var field = result.Document.Tables[0].Fields.Should().ContainSingle().Subject;
        field.Name.Should().Be("Company Name");
        field.TypeCode.Should().Be("Text");
        field.IsRequired.Should().BeTrue();
    }

    [Fact]
    public void Convert_FileUserMultiUserFields_MapToSupportedTypeCodesNotFlaggedUnsupported()
    {
        // File/User/MultiUser are fully wired PowerBase field types (core.FieldType, generic
        // CreateFieldCommandHandler) - confirms they map through the converter AND pass
        // PblValidator without an UNSUPPORTED_FIELD_TYPE warning.
        var result = Convert(TableWithFields("""
              $Field_Attachment:
                Type: QB::Field::FileAttachment
                Properties:
                  Label: Attachment
              $Field_Owner:
                Type: QB::Field::User
                Properties:
                  Label: Owner
              $Field_Watchers:
                Type: QB::Field::ListUser
                Properties:
                  Label: Watchers
            """));

        result.Document.Tables[0].Fields.Should().HaveCount(3);
        result.Document.Tables[0].Fields.Select(f => f.TypeCode).Should().BeEquivalentTo(["File", "User", "MultiUser"]);
        result.Issues.Should().NotContain(i => i.Code == "UNSUPPORTED_FIELD_TYPE");

        var validation = new PblValidator().Validate(result.Document);
        validation.Warnings.Should().NotContain(w => w.Code == "UNSUPPORTED_FIELD_TYPE");
    }

    [Fact]
    public void Convert_FormulaWithQuickbaseTableIdRef_RewritesToDbidCall()
    {
        // [_DBID_FILE_LOGS] is Quickbase's way of naming another table's id; PowerBase spells the
        // same thing Dbid("File Logs"). Common in record-URL formulas, so worth translating.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
                  $Field_Link:
                    Type: QB::Field::URL::Formula
                    Properties:
                      Label: Add Log
                      Formula: URLRoot() & "db/" & [_DBID_FILE_LOGS]
              $Table_File_Logs:
                Type: QB::Table
                Properties:
                  Name: File Logs
                Fields: {}
        """;

        var result = Convert(yaml);

        var field = result.Document.Tables.Single(t => t.Name == "Clients").Fields.Single();
        field.FormulaExpression.Should().Be("""URLRoot() & "db/" & Dbid("File Logs")""");
        result.Issues.Should().NotContain(i => i.Code == "QBL_FORMULA_UNKNOWN_TABLE_REF");
    }

    [Fact]
    public void Convert_FormulaWithTableIdRefInsideStringLiteral_LeavesItUntouched()
    {
        // Real formulas embed [_DBID_X] inside URL strings as part of Quickbase's own
        // <!~db~...~db~> markup, where it's literal text. Substituting Dbid("X") there would
        // splice quotes into the middle of the string and break the formula outright.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Projects:
                Type: QB::Table
                Properties:
                  Name: Projects
                Fields:
                  $Field_Link:
                    Type: QB::Field::RichText::Formula
                    Properties:
                      Label: Report Link
                      Formula: '"/table/<!~db~[_DBID_PROJECTS]~db~>/action/q" & [_DBID_PROJECTS]'
        """;

        var result = Convert(yaml);

        var expr = result.Document.Tables[0].Fields.Single().FormulaExpression!;
        expr.Should().Contain("<!~db~[_DBID_PROJECTS]~db~>");   // inside the string: untouched
        expr.Should().EndWith("""& Dbid("Projects")""");        // outside the string: translated
    }

    [Fact]
    public void Convert_FormulaWithTableIdRefOutsideImport_LeavesItAndFlags()
    {
        // Real exports reference tables from other apps or ones since deleted. There is nothing
        // to point those at, so they stay put and get reported rather than invented.
        var result = Convert(TableWithFields("""
              $Field_Link:
                Type: QB::Field::URL::Formula
                Properties:
                  Label: Add Thing
                  Formula: URLRoot() & "db/" & [_DBID_SOME_OTHER_APP]
            """));

        result.Document.Tables[0].Fields.Single().FormulaExpression.Should().Contain("[_DBID_SOME_OTHER_APP]");
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_FORMULA_UNKNOWN_TABLE_REF");
    }

    [Fact]
    public void Convert_FormulaField_MapsResultTypeAndExpression()
    {
        var result = Convert(TableWithFields("""
              $Field_Add_File:
                Type: QB::Field::URL::Formula
                Id: 9
                Properties:
                  Label: Add File
                  Formula: |-
                    URLRoot() & "db/" & [_DBID_FILES]
            """));

        var field = result.Document.Tables[0].Fields.Should().ContainSingle().Subject;
        field.TypeCode.Should().Be("Formula");
        field.ResultType.Should().Be("Text");
        field.FormulaExpression.Should().Contain("URLRoot()");
    }

    [Fact]
    public void Convert_NumericFormulaField_UsesNumberResultTypeDespiteNamingInconsistency()
    {
        // Real-export quirk: every other formula type is <BaseType>::Formula, but numeric is
        // QB::Field::Numeric::Formula, not Number::Formula.
        var result = Convert(TableWithFields("""
              $Field_Total:
                Type: QB::Field::Numeric::Formula
                Properties:
                  Label: Total
                  Formula: '[Price] * [Quantity]'
            """));

        result.Document.Tables[0].Fields.Should().ContainSingle().Which.ResultType.Should().Be("Number");
    }

    [Fact]
    public void Convert_EmptyFormula_SkippedWithWarning()
    {
        var result = Convert(TableWithFields("""
              $Field_Test_Formula:
                Type: QB::Field::Text::Formula
                Properties:
                  Label: Test Formula
                  Formula:
            """));

        result.Document.Tables[0].Fields.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_EMPTY_FORMULA" && i.Severity == PblIssueSeverity.Warning);
    }

    [Fact]
    public void Convert_MultiselectFormula_FlaggedUnsupportedNotApproximated()
    {
        // Confirmed real occurrence: no PowerBase FormulaResultTypes equivalent for a
        // multi-select formula - must be flagged, not coerced into Text.
        var result = Convert(TableWithFields("""
              $Field_NamesBeforeMe:
                Type: QB::Field::MultiselectText::Formula
                Properties:
                  Label: NamesBeforeMe
                  Formula: 'List(...)'
            """));

        result.Document.Tables[0].Fields.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_UNSUPPORTED_FORMULA_RESULT_TYPE");
    }

    [Fact]
    public void Convert_QuickbaseOwnUnsupportedField_SurfacesExplanationVerbatim()
    {
        // Confirmed real occurrence: QB::Field::Unsupported is Quickbase's own escape hatch,
        // carrying an Explanation - the most useful diagnostic text available.
        var result = Convert(TableWithFields("""
              $Field_Related_Client:
                Type: QB::Field::Unsupported
                Properties:
                  Explanation: The QB::Field::Reference for this QB::Field::Lookup is referring to a deleted, or otherwise missing, field.
            """));

        result.Document.Tables[0].Fields.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i =>
            i.Code == "QBL_UNSUPPORTED_FIELD_TYPE" &&
            i.Message == "The QB::Field::Reference for this QB::Field::Lookup is referring to a deleted, or otherwise missing, field.");
    }

    [Theory]
    [InlineData("QB::Field::RecordID")]
    [InlineData("QB::Field::RecordOwner")]
    [InlineData("QB::Field::DateCreated")]
    public void Convert_SystemFields_SkippedInformationallyWithoutIssue(string qblType)
    {
        var result = Convert(TableWithFields($"""
              $Field_Sys:
                Type: {qblType}
                Properties:
                  Label: System Field
            """));

        result.Document.Tables[0].Fields.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Convert_AddressSubComponents_SkippedInFavorOfComposite()
    {
        // Confirmed real occurrence: Quickbase exports each address sub-component as its own
        // shadow field alongside the composite Address field - only the composite imports.
        var result = Convert(TableWithFields("""
              $Field_Street_1:
                Type: QB::Field::AddressStreet1
                Properties:
                  Label: Street 1
              $Field_address:
                Type: QB::Field::Address
                Properties:
                  Label: Address
            """));

        var field = result.Document.Tables[0].Fields.Should().ContainSingle().Subject;
        field.TypeCode.Should().Be("Address");
        result.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("QB::Field::Reference")]
    [InlineData("QB::Field::Lookup")]
    [InlineData("QB::Field::Summary")]
    [InlineData("QB::Field::ReportLink")]
    public void Convert_RelationshipFamilyFields_NotCreatedAsStandaloneFields(string qblType)
    {
        // Slice 2 folds these into PblRelationship - Slice 1 just skips them (no issue, since
        // this is expected until Slice 2 lands, not a genuine unsupported-type gap).
        var result = Convert(TableWithFields($"""
              $Field_Rel:
                Type: {qblType}
                Properties:
                  Label: Related
            """));

        result.Document.Tables[0].Fields.Should().BeEmpty();
    }

    [Fact]
    public void Convert_UnknownFieldType_FlaggedRatherThanSilentlyDropped()
    {
        var result = Convert(TableWithFields("""
              $Field_Weird:
                Type: QB::Field::SomeFutureType
                Properties:
                  Label: Weird
            """));

        result.Document.Tables[0].Fields.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_UNKNOWN_FIELD_TYPE");
    }

    [Fact]
    public void Convert_TableReportWithExplicitColumns_ResolvesFieldNamesInOrder()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Company Name
                  $Field_Balance:
                    Type: QB::Field::Currency
                    Properties:
                      Label: Balance
                Reports:
                  $Report_List:
                    Type: QB::Report::Table
                    Properties:
                      Name: List All
                      Columns:
                        - !Ref
                          Field: $Field_Balance
                        - !Ref
                          Field: $Field_Name
                      SortAndGroup:
                        - Field: !Ref
                            Field: $Field_Balance
                          SortOrder: Descending
        """;

        var result = Convert(yaml);

        var report = result.Document.Tables[0].Reports.Should().ContainSingle().Subject;
        report.ReportType.Should().Be("Table");
        report.Columns.Should().Equal("Balance", "Company Name");
        report.SortFields.Should().ContainSingle(s => s.FieldName == "Balance" && s.Desc);
    }

    [Fact]
    public void Convert_ReportWithDefaultColumns_LeavesColumnsEmptyMeaningAllFields()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Company Name
                Reports:
                  $Report_List:
                    Type: QB::Report::Table
                    Properties:
                      Name: List All
                      Columns: Default
        """;

        var result = Convert(yaml);

        // QBL "Default" and a PowerBase report with no columns mean the same thing — every
        // reportable field — and PowerBase's own seeded "List All" is defined exactly this way.
        // Enumerating the fields instead would tie the table's main view to every one of them
        // importing successfully.
        result.Document.Tables[0].Reports[0].Columns.Should().BeEmpty();
    }

    [Theory]
    [InlineData("QB::Report::DefaultTimeline")]
    [InlineData("QB::Report::Kanban")]
    [InlineData("QB::Report::Calendar")]
    [InlineData("QB::Report::Map")]
    public void Convert_UnsupportedReportTypes_FlaggedNotApproximated(string qblReportType)
    {
        const string yamlTemplate = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields: {}
                Reports:
                  $Report_X:
                    Type: {0}
                    Properties:
                      Name: Some Report
        """;

        var result = Convert(yamlTemplate.Replace("{0}", qblReportType));

        result.Document.Tables[0].Reports.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_UNSUPPORTED_REPORT_TYPE");
    }

    [Fact]
    public void Convert_TwoReportsShareSameNameOnSameTable_SkipsRepeatWithWarningNotError()
    {
        // Confirmed real bug from an actual production QBL export: Quickbase can carry multiple
        // auto-generated "Embedded for X" reports with the identical name on the same table (one
        // per relationship, no dedup on Quickbase's side) - PowerBase requires report names
        // unique per table, so this must be flagged and skipped, not allowed to hard-block the
        // entire import as a validator error.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Employees:
                Type: QB::Table
                Properties:
                  Name: Employees
                Fields: {}
                Reports:
                  $Report_Embedded_1:
                    Type: QB::Report::Table
                    Properties:
                      Name: Embedded for Departments
                  $Report_Embedded_2:
                    Type: QB::Report::Table
                    Properties:
                      Name: Embedded for Departments
        """;

        var result = Convert(yaml);

        result.Document.Tables[0].Reports.Should().ContainSingle();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_DUPLICATE_REPORT_NAME");

        var validation = new PblValidator().Validate(result.Document);
        validation.Errors.Should().NotContain(e => e.Code == "DUPLICATE_REPORT_NAME");
    }

    [Fact]
    public void Convert_ReportGroup_NoIssueRaised()
    {
        // Organizational grouping construct only - not a data view, nothing to flag.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields: {}
                Reports:
                  $Report_Group_1:
                    Type: QB::ReportGroup
                    Properties:
                      Name: My Group
        """;

        var result = Convert(yaml);

        result.Document.Tables[0].Reports.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Convert_Relationship_ResolvesParentChildReferenceLookupAndSummary()
    {
        // Mirrors the confirmed real shape: the Child relationship node lives on the child
        // table and gives Parent.Table; the Reference field's Reference.Relationship ref
        // matches the Child node's own key; Lookup.TargetField points at a parent field;
        // Summary.ReferenceField points at the Reference field's own ref (not the relationship
        // ref), and Summary.FieldToSummarize points at a child field.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Departments:
                Type: QB::Table
                Properties:
                  Name: Departments
                Fields:
                  $Field_Dept_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Department Name
                  $Field_Budget:
                    Type: QB::Field::Currency
                    Properties:
                      Label: Budget
                  $Field_Employee_Count:
                    Type: QB::Field::Summary
                    Properties:
                      Label: Employee Count
                      Summary:
                        ReferenceField: !Ref
                          Table: $Table_Employees
                          Field: $Field_Related_Department
                        Function: Count
                Relationships:
                  $Relationship_to_Departments_1:
                    Type: QB::Relationship::Parent
              $Table_Employees:
                Type: QB::Table
                Properties:
                  Name: Employees
                Fields:
                  $Field_Employee_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Employee Name
                  $Field_Related_Department:
                    Type: QB::Field::Reference
                    Properties:
                      Label: Related Department
                      Reference:
                        Relationship: !Ref
                          Relationship: $Relationship_to_Departments
                  $Field_Dept_Name_Lookup:
                    Type: QB::Field::Lookup
                    Properties:
                      Label: Department Name
                      Lookup:
                        Relationship: !Ref
                          Relationship: $Relationship_to_Departments
                        TargetField: !Ref
                          Table: $Table_Departments
                          Field: $Field_Dept_Name
                Relationships:
                  $Relationship_to_Departments:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Departments
                        Relationship: $Relationship_to_Departments_1
        """;

        var result = Convert(yaml);

        result.Document.Relationships.Should().ContainSingle();
        var rel = result.Document.Relationships[0];
        rel.LogicalRef.Should().Be("$Table_Employees::$Relationship_to_Departments");
        rel.ParentTableRef.Should().Be("$Table_Departments");
        rel.ChildTableRef.Should().Be("$Table_Employees");
        rel.ReferenceFieldName.Should().Be("Related Department");

        rel.Lookups.Should().ContainSingle();
        rel.Lookups[0].Name.Should().Be("Department Name");
        rel.Lookups[0].SourceFieldName.Should().Be("Department Name");

        rel.Summaries.Should().ContainSingle();
        rel.Summaries[0].Name.Should().Be("Employee Count");
        rel.Summaries[0].Function.Should().Be("Count");
        rel.Summaries[0].TargetFieldName.Should().BeNull();

        // Relationship-family fields aren't standalone PblFields.
        result.Document.Tables.Single(t => t.LogicalRef == "$Table_Employees").Fields
            .Should().NotContain(f => f.Name == "Related Department" || f.Name == "Department Name");
    }

    [Fact]
    public void Convert_TwoRelationshipsShareSameLocalReferenceFieldKey_SummariesNotCrossMatched()
    {
        // Confirmed real bug from an actual production QBL export: two unrelated child tables
        // (here Static_Values and Import_Logs) independently auto-name their own Reference field
        // with the identical local key "$Field_Related_Import", and both relate to the same
        // parent (Imports). Summary.ReferenceField only disambiguates by (Table, Field) - using
        // Field alone would make both relationships match the SAME summaries list, duplicating
        // every summary onto both relationships (and producing duplicate LogicalRefs downstream).
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Imports:
                Type: QB::Table
                Properties:
                  Name: Imports
                Fields:
                  $Field_Static_Value_Count:
                    Type: QB::Field::Summary
                    Properties:
                      Label: '# of Static Values'
                      Summary:
                        ReferenceField: !Ref
                          Table: $Table_Static_Values
                          Field: $Field_Related_Import
                        Function: Count
                  $Field_Import_Log_Count:
                    Type: QB::Field::Summary
                    Properties:
                      Label: '# of Import Logs'
                      Summary:
                        ReferenceField: !Ref
                          Table: $Table_Import_Logs
                          Field: $Field_Related_Import
                        Function: Count
              $Table_Static_Values:
                Type: QB::Table
                Properties:
                  Name: Static Values
                Fields:
                  $Field_Related_Import:
                    Type: QB::Field::Reference
                    Properties:
                      Label: Related Import
                      Reference:
                        Relationship: !Ref
                          Relationship: $Relationship_Static_Values
                Relationships:
                  $Relationship_Static_Values:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Imports
                        Relationship: $Relationship_Static_Values_1
              $Table_Import_Logs:
                Type: QB::Table
                Properties:
                  Name: Import Logs
                Fields:
                  $Field_Related_Import:
                    Type: QB::Field::Reference
                    Properties:
                      Label: Related Import
                      Reference:
                        Relationship: !Ref
                          Relationship: $Relationship_Import_Logs
                Relationships:
                  $Relationship_Import_Logs:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Imports
                        Relationship: $Relationship_Import_Logs_1
        """;

        var result = Convert(yaml);

        result.Document.Relationships.Should().HaveCount(2);

        var staticValuesRel = result.Document.Relationships.Single(r => r.ChildTableRef == "$Table_Static_Values");
        staticValuesRel.Summaries.Should().ContainSingle();
        staticValuesRel.Summaries[0].Name.Should().Be("# of Static Values");

        var importLogsRel = result.Document.Relationships.Single(r => r.ChildTableRef == "$Table_Import_Logs");
        importLogsRel.Summaries.Should().ContainSingle();
        importLogsRel.Summaries[0].Name.Should().Be("# of Import Logs");

        var validation = new PblValidator().Validate(result.Document);
        validation.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Convert_SummaryWithStdDeviationFunction_FlaggedUnsupportedNotApproximated()
    {
        var result = Convert(TableWithFields("""
              $Field_StdDev:
                Type: QB::Field::Summary
                Properties:
                  Label: Score StdDev
                  Summary:
                    ReferenceField: !Ref
                      Table: $Table_Related
                      Field: $Field_Related_Test
                    FieldToSummarize: !Ref
                      Field: $Field_Score
                    Function: StdDeviation
            """));

        result.Issues.Should().ContainSingle(i => i.Code == "QBL_UNSUPPORTED_SUMMARY_FUNCTION");
    }

    [Fact]
    public void Convert_RelationshipWithBrokenReferenceField_FlaggedNotSilentlyDropped()
    {
        // Confirmed real occurrence: a Relationship::Child node with no matching Reference
        // field on its own table (e.g. the field was deleted from the source app).
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Parent:
                Type: QB::Table
                Properties:
                  Name: Parent
                Fields: {}
              $Table_Child:
                Type: QB::Table
                Properties:
                  Name: Child
                Fields: {}
                Relationships:
                  $Relationship_Orphan:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Parent
                        Relationship: $Relationship_Orphan_1
        """;

        var result = Convert(yaml);

        result.Document.Relationships.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_RELATIONSHIP_MISSING_REFERENCE_FIELD");
    }

    [Fact]
    public void Convert_CrossAppRelationship_FlaggedUnsupported()
    {
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Child:
                Type: QB::Table
                Properties:
                  Name: Child
                Fields: {}
                Relationships:
                  $Relationship_CrossApp:
                    Type: QB::Relationship::CrossAppChild
                    Properties:
                      Enabled: true
        """;

        var result = Convert(yaml);

        result.Document.Relationships.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_UNSUPPORTED_CROSS_APP_RELATIONSHIP");
    }

    private static string TableWithForm(string formYaml) => $"""
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Company Name
                Forms:
                  Resources:
        {Reindent(formYaml, 12)}
        """;

    [Fact]
    public void Convert_FormV2_FlattensPagesAndResolvesFieldElement()
    {
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                  DisplayPagesAs: Tabs
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Properties:
                      Name: Untitled Page
                    Sections:
                      $FormV2Section_1:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: ''
                          IsCollapsedByDefault: false
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Properties:
                              LayoutWidth: 12
                            Elements:
                              $FormV2Element_1:
                                Type: QB::FormV2::Element::Field
                                Properties:
                                  ForceRequireOnForm: false
                                  LabelMode: Default
                                  ForceReadOnlyOnForm: false
                                  Field: !Ref
                                    Field: $Field_Name
                                  Width: Default
                                  ModesToShowIn:
                                    - Create
                                    - Edit
                                    - View
            """));

        result.Document.Forms.Should().ContainSingle();
        var form = result.Document.Forms[0];
        form.Name.Should().Be("Main Form");
        form.TableRef.Should().Be("$Table_Clients");
        form.Sections.Should().ContainSingle();

        var section = form.Sections[0];
        section.Blocks.Should().ContainSingle();
        var block = section.Blocks[0];
        block.Width.Should().Be(12);

        var element = block.Elements.Should().ContainSingle().Subject;
        element.ElementType.Should().Be("Field");
        element.FieldName.Should().Be("Company Name");
        element.ShowOnAdd.Should().BeTrue();
        element.ShowOnEdit.Should().BeTrue();
        element.ShowOnView.Should().BeTrue();
        element.WidthMode.Should().Be("Auto");
    }

    [Fact]
    public void Convert_LegacyFormType_IsSkippedInFavorOfFormV2()
    {
        var result = Convert(TableWithForm("""
              $Form_Legacy:
                Type: QB::Form
                Properties:
                  Name: Legacy Form
                Elements: {}
            """));

        result.Document.Forms.Should().BeEmpty();
    }

    [Fact]
    public void Convert_FormElementGroup_FlattensChildrenWithWarning()
    {
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Sections:
                      $FormV2Section_1:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: Section 1
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Elements:
                              $FormV2Element_1:
                                Type: QB::FormV2::Element::Group
                                Elements:
                                  $FormV2Element_2:
                                    Type: QB::FormV2::Element::Field
                                    Properties:
                                      LabelMode: Default
                                      Field: !Ref
                                        Field: $Field_Name
            """));

        var block = result.Document.Forms[0].Sections[0].Blocks[0];
        block.Elements.Should().ContainSingle(e => e.FieldName == "Company Name");
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_FORM_ELEMENT_GROUP_FLATTENED");
    }

    [Fact]
    public void Convert_FormElementReport_FlaggedUnsupported()
    {
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Sections:
                      $FormV2Section_1:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: Section 1
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Elements:
                              $FormV2Element_1:
                                Type: QB::FormV2::Element::Report
                                Properties:
                                  EmbeddedView:
                                    Editable: false
            """));

        // The section/block still exist (a block doesn't need any elements) - only the
        // unsupported element itself is dropped from it, flagged rather than silently.
        result.Document.Forms[0].Sections.Should().ContainSingle();
        result.Document.Forms[0].Sections[0].Blocks.Should().ContainSingle().Which.Elements.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_UNSUPPORTED_FORM_ELEMENT");
    }

    [Fact]
    public void Convert_FormElementFieldNotAvailable_FlaggedNotSilentlyDropped()
    {
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Sections:
                      $FormV2Section_1:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: Section 1
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Elements:
                              $FormV2Element_1:
                                Type: QB::FormV2::Element::Field
                                Properties:
                                  LabelMode: Default
                                  Field: !Ref
                                    Field: $Field_DoesNotExist
            """));

        result.Issues.Should().ContainSingle(i => i.Code == "QBL_FORM_ELEMENT_FIELD_UNAVAILABLE");
    }

    [Fact]
    public void Convert_FormRule_ShowActionTargetingSection_ResolvesConditionAndTarget()
    {
        // Mirrors the real "Show Dev" rule shape (minus the unsupported Role condition, covered
        // separately below): a Field condition, TrueWhen: Any, Action::Show targeting a Section.
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Sections:
                      $FormV2Section_Dev:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: Dev Section
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Elements: {}
                Rules:
                  $FormV2Rule_Show_Dev:
                    Type: QB::FormV2::Rule
                    Properties:
                      Name: Show Dev
                      Disabled: false
                      RunOn: RecordOpenSaveOrChange
                    When:
                      - Type: QB::FormV2::Rule::Condition::Group
                        Properties:
                          TrueWhen: Any
                        When:
                          - Type: QB::FormV2::Rule::Condition::Field
                            Properties:
                              Field: !Ref
                                Field: $Field_Name
                              Comparison: IsEqualTo
                              Value: dev
                    Actions:
                      - Type: QB::FormV2::Rule::Action::Show
                        Properties:
                          Target: !Ref
                            FormV2Page: $FormV2Page_1
                            FormV2Section: $FormV2Section_Dev
            """));

        result.Document.Forms.Should().ContainSingle();
        var rule = result.Document.Forms[0].Rules.Should().ContainSingle().Subject;
        rule.Name.Should().Be("Show Dev");
        rule.RunTrigger.Should().Be("AnyChange");
        rule.ConditionLogic.Should().Be("any");
        rule.IsExpressionMode.Should().BeFalse();

        var condition = rule.Conditions.Should().ContainSingle().Subject;
        condition.FieldName.Should().Be("Company Name");
        condition.Operator.Should().Be("eq");
        condition.Value.Should().Be("dev");

        var action = rule.Actions.Should().ContainSingle().Subject;
        action.ActionType.Should().Be("Show");
        action.TargetType.Should().Be("Section");
        action.TargetSectionRef.Should().Be("$Table_Clients::$FormV2_Main_Form::$FormV2Section_Dev");
    }

    [Fact]
    public void Convert_FormRule_RequireActionTargetingField_ResolvesElementRef()
    {
        // Mirrors the real "Require LEA" rule: Action::Require with Target: !Ref{Field: ...}
        // (not Section) - resolves to the form ELEMENT displaying that field, not the field itself.
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Sections:
                      $FormV2Section_1:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: Section 1
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Elements:
                              $FormV2Element_1:
                                Type: QB::FormV2::Element::Field
                                Properties:
                                  LabelMode: Default
                                  Field: !Ref
                                    Field: $Field_Name
                Rules:
                  $FormV2Rule_Require_Name:
                    Type: QB::FormV2::Rule
                    Properties:
                      Name: Require Name
                      RunOn: RecordSave
                    When:
                      - Type: QB::FormV2::Rule::Condition::Group
                        When:
                          - Type: QB::FormV2::Rule::Condition::Field
                            Properties:
                              Field: !Ref
                                Field: $Field_Name
                              Comparison: IsEqualTo
                              Value: '1'
                    Actions:
                      - Type: QB::FormV2::Rule::Action::Require
                        Properties:
                          Target: !Ref
                            Field: $Field_Name
            """));

        var rule = result.Document.Forms[0].Rules.Should().ContainSingle().Subject;
        rule.RunTrigger.Should().Be("Save");
        rule.ConditionLogic.Should().Be("all");

        var action = rule.Actions.Should().ContainSingle().Subject;
        action.ActionType.Should().Be("Require");
        action.TargetType.Should().Be("Field");
        action.TargetElementRef.Should().Be("$Table_Clients::$FormV2_Main_Form::$FormV2Section_1::$FormV2Column_1::$FormV2Element_1");
    }

    [Fact]
    public void Convert_FormRule_ChangeActionWithLiteralValue_MapsToChangeValueAction()
    {
        // Confirmed real shape: Action::Change targets via Field: (not Target: like every other
        // action) and, when Value: is a plain literal, maps cleanly onto PowerBase's
        // ChangeValue action - the same static-ActionValue pattern ChangeLabel/SetColor use.
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Sections:
                      $FormV2Section_1:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: Section 1
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Elements:
                              $FormV2Element_1:
                                Type: QB::FormV2::Element::Field
                                Properties:
                                  LabelMode: Default
                                  Field: !Ref
                                    Field: $Field_Name
                Rules:
                  $FormV2Rule_Change_Name:
                    Type: QB::FormV2::Rule
                    Properties:
                      Name: Change Name
                      RunOn: RecordSave
                    When:
                      - Type: QB::FormV2::Rule::Condition::Group
                        When:
                          - Type: QB::FormV2::Rule::Condition::Field
                            Properties:
                              Field: !Ref
                                Field: $Field_Name
                              Comparison: IsEqualTo
                              Value: '1'
                    Actions:
                      - Type: QB::FormV2::Rule::Action::Change
                        Properties:
                          Field: !Ref
                            Field: $Field_Name
                          Value: dev
            """));

        var rule = result.Document.Forms[0].Rules.Should().ContainSingle().Subject;
        var action = rule.Actions.Should().ContainSingle().Subject;
        action.ActionType.Should().Be("ChangeValue");
        action.TargetType.Should().Be("Field");
        action.TargetElementRef.Should().Be("$Table_Clients::$FormV2_Main_Form::$FormV2Section_1::$FormV2Column_1::$FormV2Element_1");
        action.ActionValue.Should().Be("dev");
    }

    [Fact]
    public void Convert_FormRule_ChangeActionWithFieldRefValue_FlaggedUnsupportedRuleSkipped()
    {
        // Confirmed real shape: Change's Value: can itself be !Ref{Field: ...} - "copy that
        // other field's live value in". PowerBase's ActionValue is a static string fixed at
        // import time, not re-resolved at evaluation time, so this would silently freeze in
        // whatever the source field held at import - flagged and skipped, not approximated.
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages:
                  $FormV2Page_1:
                    Type: QB::FormV2::Page
                    Sections:
                      $FormV2Section_1:
                        Type: QB::FormV2::Section
                        Properties:
                          Title: Section 1
                        Columns:
                          $FormV2Column_1:
                            Type: QB::FormV2::Column
                            Elements:
                              $FormV2Element_1:
                                Type: QB::FormV2::Element::Field
                                Properties:
                                  LabelMode: Default
                                  Field: !Ref
                                    Field: $Field_Name
                Rules:
                  $FormV2Rule_Change_Name:
                    Type: QB::FormV2::Rule
                    Properties:
                      Name: Change Name
                      RunOn: RecordSave
                    When:
                      - Type: QB::FormV2::Rule::Condition::Group
                        When:
                          - Type: QB::FormV2::Rule::Condition::Field
                            Properties:
                              Field: !Ref
                                Field: $Field_Name
                              Comparison: IsEqualTo
                              Value: '1'
                    Actions:
                      - Type: QB::FormV2::Rule::Action::Change
                        Properties:
                          Field: !Ref
                            Field: $Field_Name
                          Value: !Ref
                            Field: $Field_Name
            """));

        result.Document.Forms[0].Rules.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == "QBL_FORM_RULE_ACTION_VALUE_UNRESOLVABLE");
    }

    [Fact]
    public void Convert_FormRule_RoleCondition_FlaggedUnsupportedRuleSkipped()
    {
        // Confirmed real occurrence: Condition::Role ("IsInRole") has no PowerBase equivalent -
        // when it's the rule's only condition, the whole rule is skipped, not silently dropped.
        var result = Convert(TableWithForm("""
              $FormV2_Main_Form:
                Type: QB::FormV2
                Properties:
                  Name: Main Form
                Pages: {}
                Rules:
                  $FormV2Rule_Show_Dev:
                    Type: QB::FormV2::Rule
                    Properties:
                      Name: Show Dev
                      RunOn: RecordOpenSaveOrChange
                    When:
                      - Type: QB::FormV2::Rule::Condition::Group
                        When:
                          - Type: QB::FormV2::Rule::Condition::Role
                            Properties:
                              Comparison: IsInRole
                              Role: !Ref
                                Role: $Role_Administrator
                    Actions:
                      - Type: QB::FormV2::Rule::Action::Show
                        Properties:
                          Target: !Ref
                            Field: $Field_Name
            """));

        result.Document.Forms[0].Rules.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_FORM_RULE_CONDITION");
        result.Issues.Should().Contain(i => i.Code == "QBL_FORM_RULE_NO_SUPPORTED_CONDITIONS");
    }

    [Fact]
    public void Convert_Role_ResolvesIdentityAndTablePermissionsFromEachTable()
    {
        // Confirmed real shape: the Role node only supplies Name/Description/Default - actual
        // CRUD grants live on each Table's own RolePermissions map, keyed by role ref.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Roles:
              $Role_Viewer:
                Type: QB::Application::Role
                Properties:
                  Name: Viewer
                  Default: false
                  ManageUsers: true
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                  RolePermissions:
                    $Role_Viewer:
                      CanAddRecords: true
                      CanDeleteRecords: false
                      CanSaveCommonReports: true
                      CanEditFieldProperties: false
                      CanViewRecords:
                        When: Always
                      CanModifyRecords:
                        When: Never
                Fields: {}
        """;

        var result = Convert(yaml);

        var role = result.Document.Roles.Should().ContainSingle().Subject;
        role.Name.Should().Be("Viewer");
        role.IsDefault.Should().BeFalse();

        var perm = role.TablePermissions.Should().ContainSingle().Subject;
        perm.TableRef.Should().Be("$Table_Clients");
        perm.ViewScope.Should().Be("AllRecords");
        perm.ModifyScope.Should().Be("None");
        perm.CanAdd.Should().BeTrue();
        perm.CanDelete.Should().BeFalse();
        perm.CanSaveSharedReports.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Convert_RoleTablePermissionWithCustomAccessCriteria_FlaggedNotTranslated()
    {
        // Confirmed real occurrence: a row-level filter in Quickbase's own query-criteria
        // syntax (e.g. "({'8'.CT.'Test'})") - a different language entirely from
        // PowerBase.Formula, so it's flagged rather than attempted.
        const string yaml = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Roles:
              $Role_Rep:
                Type: QB::Application::Role
                Properties:
                  Name: Rep
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                  RolePermissions:
                    $Role_Rep:
                      CanAddRecords: true
                      CanDeleteRecords: false
                      CanSaveCommonReports: false
                      CanEditFieldProperties: false
                      CanViewRecords:
                        When: Always
                        CustomAccessCriteria: "({'8'.CT.'Test'})"
                      CanModifyRecords:
                        When: Always
                        CustomAccessCriteria: "({'200'.EX.'1'})"
                Fields: {}
        """;

        var result = Convert(yaml);

        var perm = result.Document.Roles[0].TablePermissions.Should().ContainSingle().Subject;
        perm.ViewScope.Should().Be("AllRecords"); // imported without the filter, not dropped
        result.Issues.Should().Contain(i => i.Code == "QBL_ROLE_ACCESS_CRITERIA_UNSUPPORTED");
    }

    private static string TableWithFields(string fieldsYaml) => $"""
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
        {Reindent(fieldsYaml, 10)}
        """;

    /// <summary>Re-bases a YAML fragment to start at <paramref name="targetIndent"/> spaces,
    /// preserving the fragment's own relative nesting (a plain per-line TrimStart would flatten
    /// nested keys onto the same level and break the YAML entirely).</summary>
    private static string Reindent(string text, int targetIndent)
    {
        var lines = text.Replace("\r\n", "\n").Trim('\n').Split('\n');
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        var minIndent = nonEmpty.Count == 0 ? 0 : nonEmpty.Min(l => l.Length - l.TrimStart(' ').Length);
        var prefix = new string(' ', targetIndent);
        return string.Join('\n', lines.Select(l => l.Trim().Length == 0 ? "" : prefix + l[Math.Min(minIndent, l.Length)..]));
    }
}
