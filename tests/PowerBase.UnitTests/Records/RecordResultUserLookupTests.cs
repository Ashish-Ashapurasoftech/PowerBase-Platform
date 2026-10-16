using FluentAssertions;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// A Lookup of a parent User-typed field (e.g. Employees' "Departments - HOD") pulls down the
/// parent's raw user id. It must resolve to the user's name like the source field does, not show
/// the bare id.
/// </summary>
public class RecordResultUserLookupTests
{
    private static readonly IReadOnlyDictionary<long, string> UserNames = new Dictionary<long, string>
    {
        [21] = "john.smith@mailinator.com",
        [22] = "jane.doe@mailinator.com",
    };

    private static AppField Lookup(int fid, string sourceTypeCode) => new()
    {
        Id = fid, Fid = fid, Name = $"Lookup {fid}", TypeCode = "Lookup",
        Settings = $$"""{"referenceFid":7,"sourceTableId":2,"sourceFid":9,"sourceTypeCode":"{{sourceTypeCode}}"}""",
    };

    private static Dictionary<string, object?> Row() => new()
    {
        ["PublicId"] = Guid.NewGuid(),
        ["CreatedOn"] = DateTime.UtcNow,
        ["CreatedBy"] = 21L,
    };

    [Fact]
    public void FromRow_LookupOfUserField_ResolvesIdToName()
    {
        var result = RecordResult.FromRow(Row(), [Lookup(10, "User")], UserNames,
            new Dictionary<long, object?> { [10] = 21L });

        result.Fields["10"].Should().Be("john.smith@mailinator.com");
    }

    [Fact]
    public void FromRow_LookupOfMultiUserField_ResolvesEveryId()
    {
        var result = RecordResult.FromRow(Row(), [Lookup(10, "MultiUser")], UserNames,
            new Dictionary<long, object?> { [10] = "[21,22]" });

        result.Fields["10"].Should().BeEquivalentTo(new List<string> { "john.smith@mailinator.com", "jane.doe@mailinator.com" });
    }

    [Fact]
    public void FromRow_LookupOfTextField_KeepsValue()
    {
        var result = RecordResult.FromRow(Row(), [Lookup(10, "Text")], UserNames,
            new Dictionary<long, object?> { [10] = "21" });

        result.Fields["10"].Should().Be("21");
    }
}
