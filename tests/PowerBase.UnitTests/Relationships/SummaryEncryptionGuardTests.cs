using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class SummaryEncryptionGuardTests
{
    private static readonly App PlainApp = new() { Id = 1, IsEncrypted = false };
    private static readonly App EncryptedApp = new() { Id = 1, IsEncrypted = true };

    private static List<AppField> ChildFields(bool amountEncrypted = false, bool referenceEncrypted = false) =>
    [
        new() { Id = 10, Fid = 10, Name = "Customer", TypeCode = "Reference", IsEncrypted = referenceEncrypted },
        new() { Id = 11, Fid = 11, Name = "Amount", Label = "Amount", TypeCode = "Number", IsEncrypted = amountEncrypted },
        new() { Id = 12, Fid = 12, Name = "Status", TypeCode = "Text" },
        new() { Id = 13, Fid = 13, Name = "Cust Name", TypeCode = "Lookup", Settings = "{\"sourceTableId\":5,\"referenceFid\":10,\"sourceFid\":6,\"sourceTypeCode\":\"Text\"}" },
        new() { Id = 14, Fid = 14, Name = "Double", TypeCode = "Formula_Number" },
        new() { Id = 1, Fid = 1, Name = "Date Created", TypeCode = "DateTime", IsSystem = true },
    ];

    private static FilterGroup Where(long fid, string op = "gt") => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = "5" } }],
    };

    [Fact]
    public void EncryptedApp_Count_IsSupported_InMemory()
    {
        SummaryEncryptionGuard.FindProblem(EncryptedApp, ChildFields(), referenceFid: 10, targetFid: null, sortFid: null, matchingCriteria: null).Should().BeNull();
        SummaryEncryptionGuard.ReadsEncrypted(EncryptedApp, ChildFields(), 10, null, null, null).Should().BeTrue();
    }

    [Fact]
    public void EncryptedApp_SumOfStoredField_WithCriteria_IsSupported() =>
        SummaryEncryptionGuard.FindProblem(EncryptedApp, ChildFields(), 10, targetFid: 11, sortFid: 12, Where(12, "contains")).Should().BeNull();

    [Fact]
    public void PlainApp_NoEncryptedColumns_UsesSql()
    {
        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(), 10, targetFid: 11, sortFid: 12, Where(12)).Should().BeNull();
        SummaryEncryptionGuard.ReadsEncrypted(PlainApp, ChildFields(), 10, 11, 12, Where(12)).Should().BeFalse();
    }

    [Fact]
    public void EncryptedTarget_ReferenceOrCriteria_RunsInMemory()
    {
        SummaryEncryptionGuard.ReadsEncrypted(PlainApp, ChildFields(amountEncrypted: true), 10, 11, null, null).Should().BeTrue();
        SummaryEncryptionGuard.ReadsEncrypted(PlainApp, ChildFields(amountEncrypted: true), 10, 12, null, Where(11)).Should().BeTrue();
        SummaryEncryptionGuard.ReadsEncrypted(PlainApp, ChildFields(referenceEncrypted: true), 10, null, null, null).Should().BeTrue();
        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(amountEncrypted: true), 10, targetFid: 11, null, null).Should().BeNull();
    }

    [Fact]
    public void Encrypted_LookupTarget_WithReadableSource_IsSupported()
    {
        var sources = new Dictionary<long, AppField> { [13] = new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text", IsEncrypted = true } };

        SummaryEncryptionGuard.FindProblem(EncryptedApp, ChildFields(), 10, targetFid: 13, null, null, null, sources).Should().BeNull();
    }

    [Fact]
    public void Encrypted_LookupTarget_WithCalculatedSource_IsRefused_AndNamed()
    {
        var sources = new Dictionary<long, AppField> { [13] = new() { Id = 6, Fid = 6, Name = "Calc", TypeCode = "Formula_Text" } };

        SummaryEncryptionGuard.FindProblem(EncryptedApp, ChildFields(), 10, targetFid: 13, null, null, null, sources)
            .Should().Contain("lookup").And.Contain("'Cust Name'");
    }

    [Fact]
    public void Encrypted_CalculatedTarget_IsSupported() =>
        SummaryEncryptionGuard.FindProblem(EncryptedApp, ChildFields(), 10, targetFid: 14, null, null).Should().BeNull();

    [Fact]
    public void Encrypted_UnknownOperator_IsRefused() =>
        SummaryEncryptionGuard.FindProblem(EncryptedApp, ChildFields(), 10, targetFid: null, null, Where(12, "bogus"))
            .Should().Contain("'bogus'");

    private static FilterGroup Compare(long fid, string valueMode, long valueFid) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = "gte", ValueMode = valueMode, ValueFieldId = valueFid } }],
    };

    [Fact]
    public void Encrypted_ParentFieldComparison_IsSupported_InMemory()
    {
        List<AppField> parentFields = [new() { Id = 6, Fid = 6, Name = "Start Date", TypeCode = "Date", IsEncrypted = true }];

        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(), 10, targetFid: null, null, Compare(1, "parentField", 6), parentFields).Should().BeNull();
        SummaryEncryptionGuard.ReadsEncrypted(PlainApp, ChildFields(), 10, null, null, Compare(1, "parentField", 6), parentFields).Should().BeTrue();
    }

    [Fact]
    public void PlainParentField_ComparedTo_IsFine()
    {
        List<AppField> parentFields = [new() { Id = 6, Fid = 6, Name = "Start Date", TypeCode = "Date" }];

        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(), 10, targetFid: null, null, Compare(1, "parentField", 6), parentFields)
            .Should().BeNull();
    }

    [Fact]
    public void SystemFields_AreNeverTreatedAsEncrypted() =>
        SummaryEncryptionGuard.IsEncrypted(EncryptedApp, ChildFields().Single(f => f.IsSystem)).Should().BeFalse();
}
