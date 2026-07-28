using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Import;

[Collection("PowerBase")]
public class ImportTests : IntegrationTestBase
{
    public ImportTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    private const string ValidPbl = """
    {
      "version": "1.0",
      "app": {
        "logicalRef": "$App_Test_Import",
        "name": "Imported App",
        "description": "Created from a PBL fixture"
      },
      "tables": [
        {
          "logicalRef": "$Table_Clients",
          "name": "Clients",
          "fields": [
            { "logicalRef": "$Field_Clients_Company_Name", "name": "Company Name", "typeCode": "Text" },
            { "logicalRef": "$Field_Clients_Balance", "name": "Balance", "typeCode": "Currency" },
            { "logicalRef": "$Field_Clients_Legacy_Score", "name": "Legacy Score", "typeCode": "SomeUnsupportedQbType" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task Preview_ValidPbl_ReturnsDetectedTablesAndFlagsUnsupportedField()
    {
        var (token, _) = await SignupAsync();

        var response = await PostFileAsync("/apps/import/preview", ValidPbl, token: token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await ReadData<PreviewDto>(response);
        preview.IsValid.Should().BeTrue();
        preview.AppName.Should().Be("Imported App");
        preview.Tables.Should().ContainSingle();
        preview.Tables[0].Fields.Should().HaveCount(3);
        preview.Tables[0].Fields.Should().ContainSingle(f => !f.IsSupported && f.Name == "Legacy Score");
        preview.Warnings.Should().Contain(w => w.Code == "UNSUPPORTED_FIELD_TYPE");
    }

    [Fact]
    public async Task Preview_WithoutAuth_Returns401()
    {
        var response = await PostFileAsync("/apps/import/preview", ValidPbl);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Preview_MalformedJson_Returns200WithInvalidResult()
    {
        var (token, _) = await SignupAsync();

        var response = await PostFileAsync("/apps/import/preview", "{ not valid json", token: token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await ReadData<PreviewDto>(response);
        preview.IsValid.Should().BeFalse();
        preview.Errors.Should().Contain(e => e.Code == "INVALID_JSON");
    }

    [Fact]
    public async Task Import_ValidPbl_CreatesAppWithTableAndSupportedFieldsOnly()
    {
        var (token, _) = await SignupAsync();

        var response = await PostFileAsync("/apps/import", ValidPbl, token: token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var report = await ReadData<ImportReportDto>(response);
        report.AppName.Should().Be("Imported App");
        report.TablesCreated.Should().Be(1);
        report.FieldsCreated.Should().Be(2); // Legacy Score is skipped
        report.Skipped.Should().ContainSingle(s => s.Name == "Legacy Score");

        var getAppResponse = await GetAsync($"/apps/{report.AppPublicId}", token);
        getAppResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tablesResponse = await GetAsync($"/apps/{report.AppPublicId}/tables", token);
        var tables = await ReadListData<MinimalTableDto>(tablesResponse);
        tables.Should().ContainSingle(t => t.Name == "Clients");
    }

    [Fact]
    public async Task Import_InvalidPbl_Returns400()
    {
        var (token, _) = await SignupAsync();
        const string invalidPbl = """{ "version": "1.0" }"""; // missing app.name

        var response = await PostFileAsync("/apps/import", invalidPbl, token: token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_WithoutAuth_Returns401()
    {
        var response = await PostFileAsync("/apps/import", ValidPbl);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private const string PblWithFormulaAndReport = """
    {
      "version": "1.0",
      "app": { "logicalRef": "$App_Test_Formula", "name": "Formula App" },
      "tables": [
        {
          "logicalRef": "$Table_Orders",
          "name": "Orders",
          "fields": [
            { "logicalRef": "$Field_Qty", "name": "Qty", "typeCode": "Number" },
            { "logicalRef": "$Field_Price", "name": "Price", "typeCode": "Currency" },
            { "logicalRef": "$Field_Total", "name": "Total", "typeCode": "Formula", "resultType": "Number", "formulaExpression": "[Qty] * [Price]" }
          ],
          "reports": [
            { "logicalRef": "$Report_ByTotal", "name": "By Total", "columns": ["Qty", "Price", "Total"], "sortFields": [{ "fieldName": "Total", "desc": true }] }
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task Import_FormulaAndReport_CreatesFormulaFieldAndReport()
    {
        var (token, _) = await SignupAsync();

        var response = await PostFileAsync("/apps/import", PblWithFormulaAndReport, token: token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var report = await ReadData<ImportReportDto>(response);
        report.FieldsCreated.Should().Be(3); // Qty, Price, Total
        report.ReportsCreated.Should().Be(1);
        report.Skipped.Should().BeEmpty();
        report.FormulaTranslations.Should().ContainSingle(f => f.Name == "Total" && f.Status == "Clean");

        var tablesResponse = await GetAsync($"/apps/{report.AppPublicId}/tables", token);
        var tables = await ReadListData<MinimalTableDto>(tablesResponse);
        var orders = tables.Should().ContainSingle(t => t.Name == "Orders").Subject;

        var fieldsResponse = await GetAsync($"/tables/{orders.PublicId}/fields", token);
        var fields = await ReadListData<MinimalFieldDto>(fieldsResponse);
        fields.Should().ContainSingle(f => f.Name == "Total" && f.TypeCode == "Formula");

        var reportsResponse = await GetAsync($"/apps/{report.AppPublicId}/reports", token);
        var reports = await ReadListData<MinimalReportDto>(reportsResponse);
        reports.Should().Contain(r => r.Name == "By Total");
    }

    // Extracted from a real 55,844-line QBL v0.12 export (structure confirmed, not invented):
    // Reference.Relationship/Lookup.Relationship point at the Child relationship node's own
    // key; the Child node's Parent ref gives the parent table; Summary.ReferenceField points at
    // the Reference field's own ref (not the relationship ref).
    private const string QblWithRelationship = """
    Version: '0.12'
    Resources:
      $App_Test:
        Type: QB::Application
        Properties:
          Name: Departments App
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
              $Field_Employee_Count:
                Type: QB::Field::Summary
                Properties:
                  Label: Employee Count
                  Summary:
                    ReferenceField: !Ref
                      Table: $Table_Employees
                      Field: $Field_Related_Department
                    Function: Count
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

    [Fact]
    public async Task Preview_QblWithRelationship_DetectsRelationshipAndFlagsNothing()
    {
        var (token, _) = await SignupAsync();

        var response = await PostFileAsync("/apps/import/preview", QblWithRelationship, token: token, fileName: "sample.qbl");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await ReadData<PreviewDto>(response);
        preview.IsValid.Should().BeTrue();
        preview.AppName.Should().Be("Departments App");
        preview.Relationships.Should().ContainSingle();
        preview.Relationships[0].ReferenceFieldName.Should().Be("Related Department");
        preview.Relationships[0].LookupCount.Should().Be(1);
        preview.Relationships[0].SummaryCount.Should().Be(1);
        preview.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_QblWithRelationship_CreatesReferenceLookupAndSummaryFields()
    {
        var (token, _) = await SignupAsync();

        var response = await PostFileAsync("/apps/import", QblWithRelationship, token: token, fileName: "sample.qbl");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var report = await ReadData<ImportReportDto>(response);
        report.RelationshipsCreated.Should().Be(1);
        report.Skipped.Should().BeEmpty();

        var relationshipsResponse = await GetAsync($"/apps/{report.AppPublicId}/relationships", token);
        relationshipsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tablesResponse = await GetAsync($"/apps/{report.AppPublicId}/tables", token);
        var tables = await ReadListData<MinimalTableDto>(tablesResponse);
        var employees = tables.Should().ContainSingle(t => t.Name == "Employees").Subject;
        var departments = tables.Should().ContainSingle(t => t.Name == "Departments").Subject;

        var employeeFields = await ReadListData<MinimalFieldDto>(await GetAsync($"/tables/{employees.PublicId}/fields", token));
        employeeFields.Should().Contain(f => f.Name == "Related Department" && f.TypeCode == "Reference");
        employeeFields.Should().Contain(f => f.Name == "Department Name" && f.TypeCode == "Lookup");

        var departmentFields = await ReadListData<MinimalFieldDto>(await GetAsync($"/tables/{departments.PublicId}/fields", token));
        departmentFields.Should().Contain(f => f.Name == "Employee Count" && f.TypeCode == "Summary");
    }

    private record PblIssueDto(string Code, string Message, string? ElementRef);
    private record PreviewFieldDto(string LogicalRef, string Name, string TypeCode, bool IsSupported);
    private record PreviewTableDto(string LogicalRef, string Name, List<PreviewFieldDto> Fields, List<string> Reports);
    private record PreviewRelationshipDto(string LogicalRef, string ParentTableRef, string ChildTableRef, string ReferenceFieldName, int LookupCount, int SummaryCount);
    private record PreviewDto(bool IsValid, string AppName, List<PreviewTableDto> Tables, List<PreviewRelationshipDto> Relationships, List<PblIssueDto> Errors, List<PblIssueDto> Warnings);
    private record SkippedDto(string LogicalRef, string Name, string Reason);
    private record FormulaTranslationDto(string LogicalRef, string Name, string Status, List<string> Diagnostics);
    private record ImportReportDto(Guid AppPublicId, string AppName, int TablesCreated, int FieldsCreated, int ReportsCreated, int RelationshipsCreated,
        List<SkippedDto> Skipped, List<FormulaTranslationDto> FormulaTranslations);
    private record MinimalTableDto(Guid PublicId, string Name);
    private record MinimalFieldDto(string Name, string TypeCode);
    private record MinimalReportDto(Guid Id, string Name);
}
