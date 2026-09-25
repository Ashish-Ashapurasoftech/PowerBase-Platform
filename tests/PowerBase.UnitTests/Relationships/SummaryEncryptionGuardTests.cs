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
        new() { Id = 1, Fid = 1, Name = "Date Created", TypeCode = "DateTime", IsSystem = true },
    ];

    private static FilterGroup Where(long fid) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = "gt", Value = "5" } }],
    };

    [Fact]
    public void EncryptedApp_BlocksEverySummary_EvenCount() =>
        SummaryEncryptionGuard.FindProblem(EncryptedApp, ChildFields(), referenceFid: 10, targetFid: null, sortFid: null, matchingCriteria: null)
            .Should().Contain("encrypted app");

    [Fact]
    public void PlainApp_NoEncryptedColumns_IsFine() =>
        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(), 10, targetFid: 11, sortFid: 12, Where(12)).Should().BeNull();

    [Fact]
    public void EncryptedTarget_IsBlocked_AndNamed() =>
        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(amountEncrypted: true), 10, targetFid: 11, null, null)
            .Should().Contain("'Amount'");

    [Fact]
    public void EncryptedField_UsedOnlyAsSortOrFilter_IsBlocked()
    {
        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(amountEncrypted: true), 10, targetFid: 12, sortFid: 11, null).Should().NotBeNull();
        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(amountEncrypted: true), 10, targetFid: 12, null, Where(11)).Should().NotBeNull();
    }

    [Fact]
    public void EncryptedReference_BlocksCount() =>
        SummaryEncryptionGuard.FindProblem(PlainApp, ChildFields(referenceEncrypted: true), 10, targetFid: null, null, null)
            .Should().Contain("reference field");

    [Fact]
    public void SystemFields_AreNeverTreatedAsEncrypted() =>
        SummaryEncryptionGuard.IsEncrypted(EncryptedApp, ChildFields().Single(f => f.IsSystem)).Should().BeFalse();
}
