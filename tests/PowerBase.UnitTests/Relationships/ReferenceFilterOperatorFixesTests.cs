using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>A Reference's "Filter dropdown values" on the operators and field types that did not work:
/// "ends with" and address parts judged in memory (encrypted fields), relative dates, and User conditions.</summary>
public class ReferenceFilterOperatorFixesTests
{
    // ── In memory (encrypted fields): ends with, address parts ─────────────────────────────────────────────────

    private static readonly List<AppField> ProductFields =
    [
        new() { Id = 60, Fid = 6, Name = "Name", TypeCode = "Text" },
        new() { Id = 61, Fid = 7, Name = "Address", TypeCode = "Address" },
    ];

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Products() =>
    [
        Row(1, "Apple Pie", Address("Ahmedabad", "380001", "India")),
        Row(2, "Cherry Tart", Address("Surat", "395001", "India")),
        Row(3, "Banana Split", Address("New York", "10001", "USA")),
        Row(4, null, null),
    ];

    private static Dictionary<string, object?> Row(long id, string? name, string? address) =>
        new() { ["Id"] = id, ["f_6"] = name, ["f_7"] = address };

    private static string Address(string city, string zip, string country) =>
        JsonSerializer.Serialize(new { street1 = "1 Main St", city, zip, country });

    private static FilterGroup One(long fid, string op, string? value = null, string? subField = null) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value, SubField = subField } }],
    };

    [Fact]
    public void EndsWith_InMemory_MatchesTheEndOfTheText() =>
        EncryptedRowFilter.MatchingIds(Products(), ProductFields, One(6, "endsWith", "TART")).Should().Equal(2L);

    [Fact]
    public void NotEndsWith_InMemory_KeepsTheOthers() =>
        EncryptedRowFilter.MatchingIds(Products(), ProductFields, One(6, "notEndsWith", "tart")).Should().Equal(1L, 3L, 4L);

    [Fact]
    public void AddressPart_Equals_ReadsThatPartOnly() =>
        EncryptedRowFilter.MatchingIds(Products(), ProductFields, One(7, "eq", "ahmedabad", "city")).Should().Equal(1L);

    [Fact]
    public void AddressPart_NotEqual_ComparesThePartNotTheWholeAddress() =>
        EncryptedRowFilter.MatchingIds(Products(), ProductFields, One(7, "ne", "Ahmedabad", "city")).Should().Equal(2L, 3L, 4L);

    [Fact]
    public void AddressPart_Wildcard_And_Blank()
    {
        EncryptedRowFilter.MatchingIds(Products(), ProductFields, One(7, "wildcard", "380*", "zip")).Should().Equal(1L);
        EncryptedRowFilter.MatchingIds(Products(), ProductFields, One(7, "isEmpty", null, "city")).Should().Equal(4L);
    }

    /// <summary>Notes: a multi-line text, a Rich Text and a File, to check "ends with" across line breaks and types.</summary>
    private static readonly List<AppField> NoteFields =
    [
        new() { Id = 80, Fid = 6, Name = "Note", TypeCode = "TextMultiLine" },
        new() { Id = 81, Fid = 7, Name = "Body", TypeCode = "RichText" },
        new() { Id = 82, Fid = 8, Name = "Attachment", TypeCode = "File" },
        new() { Id = 83, Fid = 9, Name = "Address", TypeCode = "Address" },
    ];

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Notes() =>
    [
        new Dictionary<string, object?>
        {
            ["Id"] = 1L, ["f_6"] = "Sweet apple\r\ndessert", ["f_7"] = "<p>First line</p><p>last word</p>",
            ["f_8"] = "[{\"name\":\"report.pdf\"}]", ["f_9"] = JsonSerializer.Serialize(new { city = "New\nYork" }),
        },
        new Dictionary<string, object?>
        {
            ["Id"] = 2L, ["f_6"] = "Fresh juice", ["f_7"] = "<p>nothing here</p>",
            ["f_8"] = "[{\"name\":\"photo.png\"}]", ["f_9"] = JsonSerializer.Serialize(new { city = "Surat" }),
        },
        new Dictionary<string, object?> { ["Id"] = 3L },
    ];

    [Fact]
    public void EndsWith_AcrossALineBreak_MatchesTheLastLine()
    {
        EncryptedRowFilter.MatchingIds(Notes(), NoteFields, One(6, "endsWith", "dessert")).Should().Equal(1L);
        EncryptedRowFilter.MatchingIds(Notes(), NoteFields, One(6, "endsWith", "JUICE")).Should().Equal(2L);
    }

    [Fact]
    public void NotEndsWith_AcrossALineBreak_LeavesOutTheMatch() =>
        EncryptedRowFilter.MatchingIds(Notes(), NoteFields, One(6, "notEndsWith", "dessert")).Should().Equal(2L, 3L);

    [Fact]
    public void EndsWith_OnRichText_ReadsTheTextItShows() =>
        EncryptedRowFilter.MatchingIds(Notes(), NoteFields, One(7, "endsWith", "last word")).Should().Equal(1L);

    [Fact]
    public void EndsWith_OnAFile_StillMatchesTheFileName() =>
        EncryptedRowFilter.MatchingIds(Notes(), NoteFields, One(8, "endsWith", ".pdf")).Should().Equal(1L);

    [Fact]
    public void EndsWith_OnAnAddressPart_ReadsThatPartAsOneLine() =>
        EncryptedRowFilter.MatchingIds(Notes(), NoteFields, One(9, "endsWith", "york", "city")).Should().Equal(1L);

    [Fact]
    public void EndsWith_TextWithWildcardCharacters_IsMatchedLiterally() =>
        EncryptedRowFilter.MatchingIds(Products(), ProductFields, One(6, "endsWith", "*Pie")).Should().BeEmpty();

    // ── Resolved for SQL: relative dates and User conditions ───────────────────────────────────────────────────

    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IAppUserRepository _appUsers = Substitute.For<IAppUserRepository>();

    private static readonly Guid John = Guid.NewGuid();
    private static readonly Guid Emily = Guid.NewGuid();

    /// <summary>Tasks (the dropdown's records): Due Date (7), Owner (8, User), Team (9, Multi-user).</summary>
    private static readonly List<AppField> TaskFields =
    [
        new() { Id = 70, Fid = 7, Name = "Due_Date", TypeCode = "Date" },
        new() { Id = 71, Fid = 8, Name = "Owner", TypeCode = "User" },
        new() { Id = 72, Fid = 9, Name = "Team", TypeCode = "MultiUser" },
    ];

    /// <summary>The form's table: Assigned (20, User), Note (21, Text).</summary>
    private static readonly List<AppField> FormFields =
    [
        new() { Id = 120, Fid = 20, Name = "Assigned", TypeCode = "User" },
        new() { Id = 121, Fid = 21, Name = "Note", TypeCode = "Text" },
    ];

    public ReferenceFilterOperatorFixesTests()
    {
        _tables.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 5, AppId = 77 });
        _users.GetByPublicIdAsync(John, Arg.Any<CancellationToken>()).Returns(new User { Id = 21, PublicId = John });
        _users.GetByPublicIdAsync(Emily, Arg.Any<CancellationToken>()).Returns(new User { Id = 22, PublicId = Emily });
        _appUsers.ListByAppIdAsync(77, Arg.Any<CancellationToken>()).Returns(new List<AppUserDetail>
        {
            AppUser(John, "John Smith", "john.smith@mailinator.com"),
            AppUser(Emily, "Emily Davis", "emily.davis@mailinator.com"),
        });
    }

    private static AppUserDetail AppUser(Guid user, string name, string email) =>
        new(Guid.NewGuid(), user, name, email, Guid.NewGuid(), "User", "Active", true, DateTime.UtcNow, false, false, null);

    private static ReferenceSettings Filter(params FilterCondition[] conditions) => new()
    {
        ParentTableId = 5,
        FilterTree = JsonSerializer.Serialize(new FilterGroup
        {
            Logic = "and",
            Nodes = conditions.Select(c => new FilterNode { Condition = c }).ToList(),
        }),
    };

    private async Task<FilterGroup> Resolve(ReferenceSettings settings, Dictionary<int, string?>? form = null, long currentUserId = 0)
    {
        var clauses = await ReferenceFilterResolver.BuildAsync(settings, FormFields, TaskFields, form ?? new(), _tables, _fields, default,
            currentUserId, users: new ReferenceFilterUsers(_users, _appUsers));
        return clauses.Should().ContainSingle().Which.Tree!;
    }

    private static FilterCondition Only(FilterGroup tree) => tree.Nodes.Should().ContainSingle().Which.Condition!;

    [Fact]
    public async Task DaysInThePast_BecomesARealDate()
    {
        var c = Only(await Resolve(Filter(new FilterCondition { FieldId = 7, Operator = "lt", Value = "30", ValueMode = "pastDays" })));

        c.Value.Should().Be(DateTime.UtcNow.Date.AddDays(-30).ToString("yyyy-MM-dd"));
        c.ValueMode.Should().Be("literal");
    }

    [Fact]
    public async Task Today_BecomesTodaysDate() =>
        Only(await Resolve(Filter(new FilterCondition { FieldId = 7, Operator = "date_eq", ValueMode = "today" })))
            .Value.Should().Be(DateTime.UtcNow.Date.ToString("yyyy-MM-dd"));

    [Fact]
    public async Task DuringTheCurrentYear_BecomesADateRange()
    {
        var tree = await Resolve(Filter(new FilterCondition { FieldId = 7, Operator = "during", Value = "1:year", ValueMode = "duringCurrent" }));

        tree.Nodes.Should().ContainSingle().Which.Group.Should().NotBeNull("a period resolves to its start and end dates");
    }

    [Fact]
    public async Task PickedUser_BecomesTheStoredUserId()
    {
        var c = Only(await Resolve(Filter(new FilterCondition { FieldId = 8, Operator = "eq", Value = John.ToString() })));

        c.Operator.Should().Be("eq");
        c.Value.Should().Be("21");
    }

    [Fact]
    public async Task PickedUsers_OnAList_BecomeTheirIds() =>
        Only(await Resolve(Filter(new FilterCondition { FieldId = 9, Operator = "includes", Value = $"{John},{Emily}" })))
            .Value.Should().Be("21,22");

    [Fact]
    public async Task FormUser_AsItsPickerSendsIt_IsComparedAsTheUsersId()
    {
        var c = Only(await Resolve(
            Filter(new FilterCondition { FieldId = 8, Operator = "eq", ValueMode = "parentField", ValueFieldId = 20 }),
            new() { [20] = John.ToString() }));

        c.Operator.Should().Be("eq");
        c.Value.Should().Be("21");
    }

    [Fact]
    public async Task ContainsTheUserInTheField_OnASingleUser_IsThatUser()
    {
        var c = Only(await Resolve(
            Filter(new FilterCondition { FieldId = 8, Operator = "contains", ValueMode = "parentField", ValueFieldId = 20 }),
            new() { [20] = John.ToString() }));

        c.Operator.Should().Be("eq");
        c.Value.Should().Be("21");
    }

    [Fact]
    public async Task ContainsTheUserInTheField_OnAList_Includes()
    {
        var c = Only(await Resolve(
            Filter(new FilterCondition { FieldId = 9, Operator = "contains", ValueMode = "parentField", ValueFieldId = 20 }),
            new() { [20] = John.ToString() }));

        c.Operator.Should().Be("includes");
        c.Value.Should().Be("21");
    }

    [Fact]
    public async Task TypedName_MatchesTheAppsUsers()
    {
        var c = Only(await Resolve(Filter(new FilterCondition { FieldId = 8, Operator = "contains", Value = "emily" })));

        c.Operator.Should().Be("in");
        c.Value.Should().Be("[\"22\"]");
    }

    [Fact]
    public async Task TypedNameInATextFormField_MatchesTheAppsUsers()
    {
        var c = Only(await Resolve(
            Filter(new FilterCondition { FieldId = 9, Operator = "contains", ValueMode = "parentField", ValueFieldId = 21 }),
            new() { [21] = "mailinator" }));

        c.Operator.Should().Be("includes");
        c.Value.Should().Be("21,22");
    }

    [Fact]
    public async Task IsTheCurrentUser_OnAList_MeansTheListHoldsThem()
    {
        var c = Only(await Resolve(Filter(new FilterCondition { FieldId = 9, Operator = "isCurrentUser" }), currentUserId: 21));

        c.Operator.Should().Be("includes");
        c.Value.Should().Be("21");
    }

    [Fact]
    public async Task IsTheCurrentUser_OnASingleUser_StillEquals() =>
        Only(await Resolve(Filter(new FilterCondition { FieldId = 8, Operator = "isCurrentUser" }), currentUserId: 21))
            .Operator.Should().Be("eq");
}
