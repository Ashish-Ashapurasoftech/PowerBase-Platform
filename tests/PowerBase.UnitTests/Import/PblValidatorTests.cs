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
        doc.Tables[0].Fields.Add(new PblField { LogicalRef = "$Field_Clients_Reference", Name = "Ref", TypeCode = "Reference" });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Code == "UNSUPPORTED_FIELD_TYPE" && w.ElementRef == "$Field_Clients_Reference");
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
    [InlineData("File")]
    [InlineData("User")]
    [InlineData("MultiUser")]
    public void SupportedFieldTypeCodes_ContainsPhase1ScalarSet(string typeCode)
    {
        PblValidator.SupportedFieldTypeCodes.Should().Contain(typeCode);
    }

    [Theory]
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

    private static PblDocument ValidDocumentWithRelationship()
    {
        var doc = ValidDocument();
        doc.Tables.Add(new PblTable
        {
            LogicalRef = "$Table_Orders",
            Name = "Orders",
            Fields = [new PblField { LogicalRef = "$Field_Orders_Total", Name = "Total", TypeCode = "Currency" }],
        });
        doc.Relationships.Add(new PblRelationship
        {
            LogicalRef = "$Relationship_to_Clients",
            ParentTableRef = "$Table_Clients",
            ChildTableRef = "$Table_Orders",
            ReferenceFieldName = "Related Client",
            Lookups = [new PblLookupField { LogicalRef = "$Field_Client_Name_Lookup", Name = "Client Name", SourceFieldName = "Name" }],
            Summaries = [new PblSummaryField { LogicalRef = "$Field_Order_Count", Name = "Order Count", Function = "Count" }],
        });
        return doc;
    }

    [Fact]
    public void Validate_WellFormedRelationship_IsValidWithNoIssues()
    {
        var result = _validator.Validate(ValidDocumentWithRelationship());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ChainedLookupAcrossRelationships_ResolvesRegardlessOfListOrder()
    {
        // A chained lookup/summary (a real, confirmed Quickbase pattern): Timesheets' Lookup
        // sources from Employees' "Department Name" field, which only exists because of the
        // Employees->Departments relationship. Relationship processing order isn't guaranteed to
        // respect that dependency - listing the dependent relationship (Timesheets->Employees)
        // BEFORE the one it depends on (Employees->Departments) must still validate cleanly,
        // since a real import's relationship order isn't controlled by this constraint.
        var doc = ValidDocument();
        doc.Tables.Add(new PblTable
        {
            LogicalRef = "$Table_Employees",
            Name = "Employees",
            Fields = [],
        });
        doc.Tables.Add(new PblTable
        {
            LogicalRef = "$Table_Timesheets",
            Name = "Timesheets",
            Fields = [],
        });

        doc.Relationships.Add(new PblRelationship
        {
            LogicalRef = "$Relationship_Timesheets_to_Employees",
            ParentTableRef = "$Table_Employees",
            ChildTableRef = "$Table_Timesheets",
            ReferenceFieldName = "Related Employee",
            Lookups = [new PblLookupField { LogicalRef = "$Field_Dept_Name_Chained", Name = "Employee Dept Name", SourceFieldName = "Department Name" }],
        });
        doc.Relationships.Add(new PblRelationship
        {
            LogicalRef = "$Relationship_Employees_to_Clients",
            ParentTableRef = "$Table_Clients",
            ChildTableRef = "$Table_Employees",
            ReferenceFieldName = "Related Department",
            Lookups = [new PblLookupField { LogicalRef = "$Field_Dept_Name_Lookup", Name = "Department Name", SourceFieldName = "Name" }],
        });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_RelationshipUnknownParentTable_ReturnsError()
    {
        var doc = ValidDocumentWithRelationship();
        doc.Relationships[0].ParentTableRef = "$Table_DoesNotExist";

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_RELATIONSHIP_PARENT_TABLE");
    }

    [Fact]
    public void Validate_RelationshipReferenceFieldNameCollidesWithExistingField_ReturnsError()
    {
        var doc = ValidDocumentWithRelationship();
        doc.Relationships[0].ReferenceFieldName = "Total"; // already a field on the child table

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DUPLICATE_FIELD_NAME");
    }

    [Fact]
    public void Validate_LookupUnknownSourceField_ReturnsError()
    {
        var doc = ValidDocumentWithRelationship();
        doc.Relationships[0].Lookups[0].SourceFieldName = "NoSuchField";

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_LOOKUP_SOURCE_FIELD");
    }

    [Fact]
    public void Validate_SummaryUnsupportedFunction_ReturnsError()
    {
        var doc = ValidDocumentWithRelationship();
        doc.Relationships[0].Summaries[0].Function = "StdDeviation";

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNSUPPORTED_SUMMARY_FUNCTION");
    }

    [Fact]
    public void Validate_SummaryUnknownTargetField_ReturnsError()
    {
        var doc = ValidDocumentWithRelationship();
        doc.Relationships[0].Summaries[0].TargetFieldName = "NoSuchField";

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_SUMMARY_TARGET_FIELD");
    }

    [Fact]
    public void Validate_ReportReferencingLookupField_ResolvesAgainstRelationshipDerivedName()
    {
        // A report column referencing a Lookup/Summary field name must resolve even though
        // that field never appears in PblTable.Fields - it only exists via the relationship.
        var doc = ValidDocumentWithRelationship();
        doc.Tables.First(t => t.LogicalRef == "$Table_Orders").Reports.Add(new PblReport
        {
            LogicalRef = "$Report_Orders_List",
            Name = "Order List",
            Columns = ["Total", "Client Name"],
        });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
    }

    private static PblForm ValidForm() => new()
    {
        LogicalRef = "$Form_Clients_Main",
        TableRef = "$Table_Clients",
        Name = "Main Form",
        Sections =
        [
            new PblFormSection
            {
                LogicalRef = "$Section_1",
                Name = "Section 1",
                Blocks =
                [
                    new PblFormBlock
                    {
                        LogicalRef = "$Block_1",
                        Elements = [new PblFormElement { LogicalRef = "$Element_1", ElementType = "Field", FieldName = "Name" }],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Validate_WellFormedForm_IsValidWithNoIssues()
    {
        var doc = ValidDocument();
        doc.Forms.Add(ValidForm());

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_FormUnknownTable_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        form.TableRef = "$Table_DoesNotExist";
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_FORM_TABLE");
    }

    [Fact]
    public void Validate_FormFieldElementUnknownField_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        form.Sections[0].Blocks[0].Elements[0].FieldName = "NoSuchField";
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_FORM_ELEMENT_FIELD");
    }

    [Fact]
    public void Validate_FormSectionWithNoBlocks_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        form.Sections[0].Blocks.Clear();
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_FORM_SECTION_BLOCK_COUNT");
    }

    [Fact]
    public void Validate_FormElementInvalidElementType_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        form.Sections[0].Blocks[0].Elements[0].ElementType = "NotAThing";
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_FORM_ELEMENT_TYPE");
    }

    private static PblFormRule ValidFormRule() => new()
    {
        LogicalRef = "$Rule_1",
        Name = "Require Name",
        RunTrigger = "Save",
        ConditionLogic = "all",
        Conditions = [new PblFormRuleCondition { FieldName = "Name", Operator = "eq", Value = "1" }],
        Actions = [new PblFormRuleAction { ActionType = "Require", TargetType = "Field", TargetElementRef = "$Element_1" }],
    };

    [Fact]
    public void Validate_WellFormedFormRule_IsValidWithNoIssues()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        form.Rules.Add(ValidFormRule());
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_FormRuleUnknownConditionField_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        var rule = ValidFormRule();
        rule.Conditions[0].FieldName = "NoSuchField";
        form.Rules.Add(rule);
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_FORM_RULE_CONDITION_FIELD");
    }

    [Fact]
    public void Validate_FormRuleInvalidOperator_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        var rule = ValidFormRule();
        rule.Conditions[0].Operator = "notAnOperator";
        form.Rules.Add(rule);
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_FORM_RULE_OPERATOR");
    }

    [Fact]
    public void Validate_FormRuleActionTargetNotOnForm_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        var rule = ValidFormRule();
        rule.Actions[0].TargetElementRef = "$Element_DoesNotExist";
        form.Rules.Add(rule);
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_FORM_RULE_ACTION_TARGET");
    }

    [Fact]
    public void Validate_FormRuleExpressionModeWithoutExpressionText_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        var rule = ValidFormRule();
        rule.IsExpressionMode = true;
        rule.ExpressionText = null;
        form.Rules.Add(rule);
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MISSING_FORM_RULE_EXPRESSION");
    }

    [Fact]
    public void Validate_FormRuleInvalidActionType_ReturnsError()
    {
        var doc = ValidDocument();
        var form = ValidForm();
        var rule = ValidFormRule();
        rule.Actions[0].ActionType = "NotAnAction";
        form.Rules.Add(rule);
        doc.Forms.Add(form);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_FORM_RULE_ACTION_TYPE");
    }

    private static PblRole ValidRole() => new()
    {
        LogicalRef = "$Role_Viewer",
        Name = "Viewer",
        TablePermissions =
        [
            new PblTablePermission { TableRef = "$Table_Clients", ViewScope = "AllRecords", ModifyScope = "None" },
        ],
    };

    [Fact]
    public void Validate_WellFormedRole_IsValidWithNoIssues()
    {
        var doc = ValidDocument();
        doc.Roles.Add(ValidRole());

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_DuplicateRoleName_ReturnsError()
    {
        var doc = ValidDocument();
        doc.Roles.Add(ValidRole());
        doc.Roles.Add(new PblRole { LogicalRef = "$Role_Viewer_2", Name = "Viewer" });

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DUPLICATE_ROLE_NAME");
    }

    [Fact]
    public void Validate_RoleUnknownTable_ReturnsError()
    {
        var doc = ValidDocument();
        var role = ValidRole();
        role.TablePermissions[0].TableRef = "$Table_DoesNotExist";
        doc.Roles.Add(role);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_ROLE_TABLE");
    }

    [Fact]
    public void Validate_RoleInvalidViewScope_ReturnsError()
    {
        var doc = ValidDocument();
        var role = ValidRole();
        role.TablePermissions[0].ViewScope = "NotAScope";
        doc.Roles.Add(role);

        var result = _validator.Validate(doc);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "INVALID_VIEW_SCOPE");
    }
}
