using FluentAssertions;
using PowerBase.Application.Import.Pbl;

namespace PowerBase.UnitTests.Import;

public class PblValidatorTests
{
    private readonly PblValidator _validator = new();

    private static PblDocument ValidDocument() => new()
    {
        App = new PblApp { LogicalRef = "$App_Test", Name = "Test App" },
        Tables =
        [
            new PblTable
            {
                LogicalRef = "$Table_Clients",
                Name = "Clients",
                Fields =
                [
                    new PblField { LogicalRef = "$Field_Clients_Name", Name = "Name", TypeCode = "Text" },
                    new PblField { LogicalRef = "$Field_Clients_Balance", Name = "Balance", TypeCode = "Currency" },
                ],
            },
        ],
    };

    [Fact]
    public void Validate_WellFormedDocument_IsValidWithNoIssues()
    {
        var result = _validator.Validate(ValidDocument());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingApp_ReturnsError()
    {
        var result = _validator.Validate(new PblDocument { App = null!, Tables = [] });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MISSING_APP");
    }

    [Fact]
    public void Validate_AppMissingName_ReturnsError()
    {
        var doc = ValidDocument();
        doc.App.Name = "";

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MISSING_NAME" && e.ElementRef == "$App_Test");
    }

    [Fact]
    public void Validate_NoTables_IsValidWithWarning()
    {
        var doc = ValidDocument();
        doc.Tables = [];

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Code == "NO_TABLES");
    }

    [Fact]
    public void Validate_DuplicateLogicalRef_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Fields[1].LogicalRef = doc.Tables[0].Fields[0].LogicalRef;

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DUPLICATE_LOGICAL_REF");
    }

    [Fact]
    public void Validate_DuplicateTableName_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables.Add(new PblTable { LogicalRef = "$Table_Clients_2", Name = "Clients", Fields = [] });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DUPLICATE_TABLE_NAME");
    }

    [Fact]
    public void Validate_DuplicateFieldNameWithinTable_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Fields.Add(new PblField { LogicalRef = "$Field_Clients_Name_2", Name = "Name", TypeCode = "Text" });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DUPLICATE_FIELD_NAME");
    }

    [Fact]
    public void Validate_UnsupportedFieldType_ReturnsWarningNotError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Fields.Add(new PblField { LogicalRef = "$Field_Clients_Formula", Name = "Computed", TypeCode = "Formula" });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Code == "UNSUPPORTED_FIELD_TYPE" && w.ElementRef == "$Field_Clients_Formula");
    }

    [Fact]
    public void Validate_FieldMissingTypeCode_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Fields[0].TypeCode = "";

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MISSING_TYPE_CODE");
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("TextMultiLine")]
    [InlineData("RichText")]
    [InlineData("SingleSelect")]
    [InlineData("MultiSelect")]
    [InlineData("Number")]
    [InlineData("Currency")]
    [InlineData("Percent")]
    [InlineData("Rating")]
    [InlineData("Date")]
    [InlineData("DateTime")]
    [InlineData("Time")]
    [InlineData("Duration")]
    [InlineData("Boolean")]
    [InlineData("Email")]
    [InlineData("Phone")]
    [InlineData("Url")]
    [InlineData("Address")]
    public void SupportedFieldTypeCodes_ContainsPhase1ScalarSet(string typeCode)
    {
        PblValidator.SupportedFieldTypeCodes.Should().Contain(typeCode);
    }

    [Theory]
    [InlineData("File")]
    [InlineData("User")]
    [InlineData("Formula")]
    [InlineData("Reference")]
    [InlineData("Lookup")]
    [InlineData("Summary")]
    [InlineData("ReportLink")]
    public void SupportedFieldTypeCodes_ExcludesDeferredTypes(string typeCode)
    {
        PblValidator.SupportedFieldTypeCodes.Should().NotContain(typeCode);
    }

    [Fact]
    public void IsCreatableFieldType_ScalarAndFormula_ReturnsTrue()
    {
        PblValidator.IsCreatableFieldType("Text").Should().BeTrue();
        PblValidator.IsCreatableFieldType("Formula").Should().BeTrue();
    }

    [Fact]
    public void IsCreatableFieldType_StillDeferredType_ReturnsFalse()
    {
        PblValidator.IsCreatableFieldType("Reference").Should().BeFalse();
    }

    [Fact]
    public void Validate_FormulaFieldWithExpressionAndResultType_IsValid()
    {
        var doc = ValidDocument();
        doc.Tables[0].Fields.Add(new PblField
        {
            LogicalRef = "$Field_Clients_Total",
            Name = "Total",
            TypeCode = "Formula",
            ResultType = "Number",
            FormulaExpression = "[Balance] * 2",
        });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_FormulaFieldMissingExpression_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Fields.Add(new PblField
        {
            LogicalRef = "$Field_Clients_Total",
            Name = "Total",
            TypeCode = "Formula",
            ResultType = "Number",
        });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MISSING_FORMULA_EXPRESSION");
    }

    [Fact]
    public void Validate_FormulaFieldInvalidResultType_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Fields.Add(new PblField
        {
            LogicalRef = "$Field_Clients_Total",
            Name = "Total",
            TypeCode = "Formula",
            ResultType = "NotARealType",
            FormulaExpression = "[Balance] * 2",
        });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_FORMULA_RESULT_TYPE");
    }

    [Fact]
    public void Validate_ReportWithValidColumnReferences_IsValid()
    {
        var doc = ValidDocument();
        doc.Tables[0].Reports.Add(new PblReport
        {
            LogicalRef = "$Report_ByBalance",
            Name = "By Balance",
            Columns = ["Name", "Balance"],
            SortFields = [new PblSortSpec { FieldName = "Balance", Desc = true }],
        });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ReportReferencingUnknownField_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Reports.Add(new PblReport
        {
            LogicalRef = "$Report_Bad",
            Name = "Bad Report",
            Columns = ["DoesNotExist"],
        });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_REPORT_FIELD");
    }

    [Fact]
    public void Validate_DuplicateReportName_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Tables[0].Reports.Add(new PblReport { LogicalRef = "$Report_A", Name = "Report", Columns = ["Name"] });
        doc.Tables[0].Reports.Add(new PblReport { LogicalRef = "$Report_B", Name = "Report", Columns = ["Balance"] });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DUPLICATE_REPORT_NAME");
    }
}
