using FluentAssertions;
using PowerBase.Application.ConnectedTables;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.ConnectedTables;

public class ConnectedSupportTests
{
    [Theory]
    [InlineData("https://acme.powerbase.app/app/123", "acme")]
    [InlineData("acme.powerbase.app", "acme")]
    [InlineData("ACME", "acme")]
    [InlineData(" acme/app/1 ", "acme")]
    public void RealmSlug_AcceptsUrlsHostsAndNames(string input, string expected)
    {
        ConnectedTokens.RealmSlug(input).Should().Be(expected);
    }

    [Fact]
    public void Hash_IsLowercaseSha256OfTrimmedToken()
    {
        var hash = ConnectedTokens.Hash("  pb_ut_secret ");
        hash.Should().HaveLength(64).And.Be(hash.ToLowerInvariant());
        ConnectedTokens.Hash("pb_ut_secret").Should().Be(hash);
    }

    [Fact]
    public void Prefix_NeverRevealsTheWholeToken()
    {
        ConnectedTokens.Prefix("pb_ut_0123456789abcdef").Should().Be("pb_ut_0123…");
        ConnectedTokens.Prefix("short").Should().Be("•••••");
    }

    [Fact]
    public void RowVersion_RoundTrips_AndRejectsGarbage()
    {
        var bytes = new byte[] { 0, 0, 0, 0, 0, 0, 7, 209 };
        ConnectedTokens.RowVersion(ConnectedTokens.RowVersion(bytes)).Should().Equal(bytes);
        FluentActions.Invoking(() => ConnectedTokens.RowVersion("%%%")).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ConnectedTokens.RowVersion("")).Should().Throw<ValidationException>();
    }

    private static ConnectedSourceTable Source(params int[] visibleFids) =>
        new(new AppTable { Id = 1 }, [], visibleFids.Select(f => new AppField { Id = f, Fid = f, TypeCode = "Text" }).ToList(), new TableAccessContext { Unrestricted = true });

    [Fact]
    public void Filters_Validate_RejectsFieldsTheUserCannotSee()
    {
        var filter = new FilterGroup { Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 99, Operator = "eq", Value = "x" } }] };
        FluentActions.Invoking(() => ConnectedFilters.Validate(filter, Source(6, 7))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Filters_Validate_AcceptsNestedVisibleFields_AndEmptyMeansNone()
    {
        var filter = new FilterGroup
        {
            Logic = "or",
            Nodes = [new FilterNode { Group = new FilterGroup { Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "eq" } }] } }]
        };
        ConnectedFilters.Validate(filter, Source(7)).Should().BeSameAs(filter);
        ConnectedFilters.Validate(new FilterGroup(), Source(7)).Should().BeNull();
    }

    [Fact]
    public void Filters_And_CombinesOnlyNonEmptyGroups()
    {
        var a = new FilterGroup { Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 1 } }] };
        ConnectedFilters.And(a, null).Should().BeSameAs(a);
        ConnectedFilters.And(null, a).Should().BeSameAs(a);
        ConnectedFilters.And(a, a)!.Nodes.Should().HaveCount(2);
        ConnectedFilters.And(null, new FilterGroup()).Should().BeNull();
    }

    [Fact]
    public void CreateConnectionValidator_RequiresTokenAndRealmForUserToken()
    {
        var validator = new CreateConnectionCommandValidator();
        validator.Validate(new CreateConnectionCommand("c", "user_token", null, Guid.NewGuid(), null, null)).IsValid.Should().BeFalse();
        validator.Validate(new CreateConnectionCommand("c", "user_token", "acme", Guid.NewGuid(), "pb_ut_x", null)).IsValid.Should().BeTrue();
        validator.Validate(new CreateConnectionCommand("c", "same_realm_user", null, Guid.NewGuid(), null, null)).IsValid.Should().BeTrue();
        validator.Validate(new CreateConnectionCommand("c", "password", null, Guid.NewGuid(), null, null)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateConnectedTableValidator_RejectsDuplicateFieldsAndBadMode()
    {
        var validator = new CreateConnectedTableCommandValidator();
        CreateConnectedTableCommand Command(IReadOnlyList<int> fids, string mode = "mirror", string? tz = null) =>
            new(Guid.NewGuid(), "T", null, null, null, null, Guid.NewGuid(), Guid.NewGuid(), fids, null, mode, false, null, null, tz, true);

        validator.Validate(Command([6, 7])).IsValid.Should().BeTrue();
        validator.Validate(Command([])).IsValid.Should().BeFalse();
        validator.Validate(Command([6, 6])).IsValid.Should().BeFalse();
        validator.Validate(Command([6], "replace")).IsValid.Should().BeFalse();
        validator.Validate(Command([6], tz: "Not/AZone")).IsValid.Should().BeFalse();
    }
}
