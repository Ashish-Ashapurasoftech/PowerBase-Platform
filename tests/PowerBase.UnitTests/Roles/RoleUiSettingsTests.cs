using FluentAssertions;
using PowerBase.Application.Apps.RoleUi;
using System;
using System.Linq;
using Xunit;

namespace PowerBase.UnitTests.Roles;

public class RoleUiSettingsTests
{
    private static readonly Guid T1 = Guid.NewGuid();
    private static readonly Guid T2 = Guid.NewGuid();

    [Fact]
    public void Merge_hides_only_what_every_role_hides()
    {
        var a = new RoleUiSettings([new RoleUiTableSettings(T1, HideInTableBar: true, HideAdd: true)], HideSearch: true, HideUsers: true);
        var b = new RoleUiSettings([new RoleUiTableSettings(T1, HideInTableBar: true)], HideSearch: true);

        var merged = RoleUiSettings.Merge([a, b]);

        merged.HideSearch.Should().BeTrue();
        merged.HideUsers.Should().BeFalse();
        merged.Tables.Should().ContainSingle();
        merged.Tables[0].HideInTableBar.Should().BeTrue();
        merged.Tables[0].HideAdd.Should().BeFalse();
    }

    [Fact]
    public void Merge_shows_a_table_when_one_role_has_no_row_for_it()
    {
        var a = new RoleUiSettings([new RoleUiTableSettings(T1, HideInTableBar: true), new RoleUiTableSettings(T2, HideAdd: true)]);
        var b = new RoleUiSettings([new RoleUiTableSettings(T2, HideAdd: true)]);

        var merged = RoleUiSettings.Merge([a, b]);

        merged.Tables.Select(t => t.TablePublicId).Should().Equal(T2);
    }

    [Fact]
    public void Serialize_returns_null_when_nothing_is_hidden_and_roundtrips_otherwise()
    {
        RoleUiSettings.Serialize(new RoleUiSettings([new RoleUiTableSettings(T1)])).Should().BeNull();

        var json = RoleUiSettings.Serialize(new RoleUiSettings([new RoleUiTableSettings(T1, HideMultiRecord: true)], HideAppearance: true));
        json.Should().NotBeNull();
        json.Should().NotContain("hidesAnything");
        var back = RoleUiSettings.Parse(json);
        back.HideAppearance.Should().BeTrue();
        back.Tables.Should().ContainSingle(t => t.TablePublicId == T1 && t.HideMultiRecord);
    }

    [Fact]
    public void Parse_treats_bad_json_as_nothing_hidden()
    {
        RoleUiSettings.Parse("{not json").HidesAnything.Should().BeFalse();
        RoleUiSettings.Parse(null).HidesAnything.Should().BeFalse();
    }
}
