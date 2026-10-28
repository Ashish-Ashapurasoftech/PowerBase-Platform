using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

/// <summary>Quickbase behaviour the formula engine now follows: Hour/Minute/Second of a Time, multi-branch If, UserRoles(), Address fields and parts.</summary>
public class FormulaQuickbaseParityTests
{
    // ---- Hour / Minute / Second of a Time ----

    [Theory]
    [InlineData("Hour(ToTimeOfDay(\"2:45:30 PM\"))", "14")]
    [InlineData("Minute(ToTimeOfDay(\"2:45:30 PM\"))", "45")]
    [InlineData("Second(ToTimeOfDay(\"2:45:30 PM\"))", "30")]
    [InlineData("Hour(ToDate(\"2026-06-11\"))", "0")]
    public void HourMinuteSecond_ReadATimeOfDay(string expression, string expected) =>
        ValueConvertText(FormulaEval.Const(expression)).Should().Be(expected);

    // ---- multi-branch If ----

    [Theory]
    [InlineData("If(1 = 2, \"a\", 2 = 2, \"b\", \"c\")", "b")]
    [InlineData("If(1 = 2, \"a\", 3 = 2, \"b\", \"c\")", "c")]
    [InlineData("If(1 = 1, \"a\", 2 = 2, \"b\", \"c\")", "a")]
    [InlineData("If(1 = 2, \"a\", 2 = 2, \"b\")", "b")]
    [InlineData("If(1 = 2, \"a\", 3 = 2, \"b\")", "")]
    [InlineData("If(1 = 2, 1, 1 = 3, 2, 1 = 4, 3, 1 = 5, 4, 99)", "99")]
    [InlineData("If(1 = 1, \"yes\", \"no\")", "yes")]
    [InlineData("If(1 = 2, \"yes\")", "")]
    public void If_AcceptsAnyNumberOfConditionValuePairs(string expression, string expected) =>
        ValueConvertText(FormulaEval.Const(expression)).Should().Be(expected);

    [Fact]
    public void If_BranchesOfDifferentTypes_AreStillRejected()
    {
        var compiled = new FormulaEngine().Compile("If(1 = 2, \"a\", 2 = 2, 5, \"c\")", new TestSchema());

        compiled.HasErrors.Should().BeTrue();
        compiled.Diagnostics.Should().Contain(d => d.Message.Contains("If branches must have the same type"));
    }

    [Fact]
    public void If_ConditionThatIsNotABoolean_IsRejected()
    {
        var compiled = new FormulaEngine().Compile("If(1, \"a\", 2 = 2, \"b\")", new TestSchema());

        compiled.Diagnostics.Should().Contain(d => d.Message.Contains("If argument 1 expects Bool"));
    }

    // ---- UserRoles() ----

    private static EvaluationOptions Roles() => new()
    {
        CurrentUserRoles = () => [new RoleRef("11", "Service Worker"), new RoleRef("12", "Manager")],
        UserRolesOf = id => id == "7" ? [new RoleRef("99", "Viewer")] : [],
    };

    [Fact]
    public void UserRoles_Name_ListsTheRoleNames() =>
        FormulaEval.Const("UserRoles(\"Name\")", Roles()).AsTextList().Should().Equal("Service Worker", "Manager");

    [Fact]
    public void UserRoles_Id_ListsTheRoleIds() =>
        FormulaEval.Const("UserRoles(\"ID\")", Roles()).AsTextList().Should().Equal("11", "12");

    [Fact]
    public void UserRoles_Empty_ListsIdSlashName() =>
        FormulaEval.Const("UserRoles(\"\")", Roles()).AsTextList().Should().Equal("11/Service Worker", "12/Manager");

    [Fact]
    public void UserRoles_NoArgument_ListsIdSlashName() =>
        FormulaEval.Const("UserRoles()", Roles()).AsTextList().Should().Equal("11/Service Worker", "12/Manager");

    [Fact]
    public void UserRoles_OfAUserId_ReadsThatUsersRoles() =>
        FormulaEval.Const("UserRoles(\"7\")", Roles()).AsTextList().Should().Equal("99/Viewer");

    [Fact]
    public void UserRoles_WithoutAHostLookup_IsBlank() =>
        FormulaEval.Const("UserRoles(\"Name\")").AsTextList().Should().BeEmpty();

    [Fact]
    public void UserRoles_ComparedAsText_WorksLikeQuickbase() =>
        FormulaEval.Const("If(ToText(UserRoles(\"Name\")) = \"Service Worker\", \"Completed By Tech\", \"Closed\")",
            new EvaluationOptions { CurrentUserRoles = () => [new RoleRef("11", "Service Worker")] })
            .AsText().Should().Be("Completed By Tech");

    // ---- Address fields and their parts ----

    private const string Address = """{"street1":"1 Main St","street2":"Apt 4","city":"Albany","state":"NY","zip":"12207","country":"USA"}""";

    private static Bed AddressBed()
    {
        var bed = new Bed();
        bed.AddressField("Address", Address);
        return bed;
    }

    [Fact]
    public void Address_WholeField_IsTheFullAddressAsText() =>
        AddressBed().Eval("[Address]").AsText().Should().Be("1 Main St, Apt 4, Albany, NY, 12207, USA");

    [Theory]
    [InlineData("[Address : Street 1]", "1 Main St")]
    [InlineData("[Address : Street 2]", "Apt 4")]
    [InlineData("[Address : City]", "Albany")]
    [InlineData("[Address : State]", "NY")]
    [InlineData("[Address : Postal Code]", "12207")]
    [InlineData("[Address : Zip]", "12207")]
    [InlineData("[Address : Country]", "USA")]
    [InlineData("[Address: city]", "Albany")]
    public void Address_Part_IsThatPartAsText(string expression, string expected) =>
        AddressBed().Eval(expression).AsText().Should().Be(expected);

    [Fact]
    public void Address_PartInsideAFormula_Works() =>
        AddressBed().Eval("[Address : City] & \", \" & [Address : State]").AsText().Should().Be("Albany, NY");

    [Fact]
    public void Address_UnknownPart_IsReported()
    {
        var schema = new TestSchema();
        schema.AddAddress("Address");

        var compiled = new FormulaEngine().Compile("[Address : Planet]", schema);

        compiled.Diagnostics.Should().Contain(d => d.Message.Contains("no part called Planet"));
    }

    [Fact]
    public void Address_BlankValue_IsEmptyText()
    {
        var bed = new Bed();
        bed.AddressField("Address", null);

        bed.Eval("[Address]").AsText().Should().BeEmpty();
        bed.Eval("[Address : City]").AsText().Should().BeEmpty();
    }

    [Fact]
    public void Address_APartOfANonAddressField_IsStillAnUnknownField()
    {
        var bed = new Bed().Field("Name", FormulaType.Text, "x");
        var compiled = new FormulaEngine().Compile("[Name : City]", new TestSchema().Add("Name", FormulaType.Text));

        compiled.Diagnostics.Should().Contain(d => d.Message.Contains("Unknown field"));
        bed.Should().NotBeNull();
    }

    private static string ValueConvertText(FormulaValue v) => v.IsNull ? string.Empty : v.Type switch
    {
        FormulaType.Number => v.AsNumber().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        FormulaType.Text => v.AsText(),
        _ => v.ToString(),
    };
}
