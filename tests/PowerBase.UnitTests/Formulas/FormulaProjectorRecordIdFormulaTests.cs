using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Formulas;

/// <summary>A formula over the Record ID# ("Order no" = "CST000" &amp; ToText([Record ID#])) must evaluate on the rows the
/// pipeline trigger re-reads, with the field list exactly as the table stores it.</summary>
public class FormulaProjectorRecordIdFormulaTests
{
    private static FormulaProjector NewProjector()
    {
        var qc = Substitute.For<IQueryContext>();
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns(string.Empty);
        return new FormulaProjector(new FormulaEngine(), qc, runtime,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IAppRepository>());
    }

    // Exactly the "Ai Search" table: system fields with their physical columns, encrypted text fields, and the formula (PhysicalColumnName NULL).
    private static List<AppField> AiSearchFields() => new()
    {
        new() { Id = 101, Fid = 3, Name = "S_recordId", Label = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
        new() { Id = 102, Fid = 1, Name = "S_dateCreated", Label = "Date Created", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn" },
        new() { Id = 103, Fid = 2, Name = "S_dateModified", Label = "Date Modified", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "ModifiedOn" },
        new() { Id = 104, Fid = 4, Name = "S_recordOwner", Label = "Record Owner", TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy" },
        new() { Id = 105, Fid = 5, Name = "S_lastModifiedBy", Label = "Last Modified By", TypeCode = "User", IsSystem = true, PhysicalColumnName = "ModifiedBy" },
        new() { Id = 106, Fid = 6, Name = "C_name", Label = "Name", TypeCode = "Text", IsEncrypted = true, PhysicalColumnName = "f_6" },
        new() { Id = 107, Fid = 7, Name = "C_number", Label = "Number", TypeCode = "Number", IsEncrypted = true, PhysicalColumnName = "f_7" },
        new() { Id = 108, Fid = 8, Name = "C_city", Label = "City", TypeCode = "Text", IsEncrypted = true, PhysicalColumnName = "f_8" },
        new() { Id = 109, Fid = 9, Name = "C_orderNo", Label = "Order no", TypeCode = "Formula_Text", IsEncrypted = true, PhysicalColumnName = null,
                Settings = "{\"expression\":\"\\\"CST000\\\" & ToText([Record ID#])\"}" },
    };

    [Fact]
    public void OrderNo_FromTheRecordId_IsComputedOnTheReReadRow()
    {
        var row = new Dictionary<string, object?>
        {
            ["Id"] = 5L, ["PublicId"] = Guid.NewGuid(), ["CreatedOn"] = DateTime.UtcNow, ["CreatedBy"] = 40021L,
            ["ModifiedOn"] = DateTime.UtcNow, ["ModifiedBy"] = 40021L, ["f_6"] = "AiUser00002", ["f_7"] = "700002", ["f_8"] = "Ra"
        };

        var computed = NewProjector().Project(AiSearchFields(), new[] { (IReadOnlyDictionary<string, object?>)row });

        Assert.Equal("CST0005", computed[0][9]);
    }
}
