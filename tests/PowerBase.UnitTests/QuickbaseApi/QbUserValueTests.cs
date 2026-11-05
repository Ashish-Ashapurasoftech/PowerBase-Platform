using FluentAssertions;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbUserValueTests
{
    [Theory]
    [InlineData(5L, new long[] { 5 })]
    [InlineData("[1,2]", new long[] { 1, 2 })]
    [InlineData("[\"3\",\"4\"]", new long[] { 3, 4 })]
    [InlineData("7, 8", new long[] { 7, 8 })]
    [InlineData("", new long[0])]
    [InlineData(null, new long[0])]
    public void ParseIds_StoredForms(object? value, long[] expected)
    {
        QbUserValueResolver.ParseIds(value).Should().Equal(expected);
    }

    [Fact]
    public void UserObject_FillsEmailIdName()
    {
        var g = Guid.NewGuid();
        var o = QbUserValueResolver.UserObject(5,
            new Dictionary<long, UserIdentity> { [5] = new("Jamie Stein", "jamie@x.com") },
            new Dictionary<long, Guid> { [5] = g });
        o["email"].Should().Be("jamie@x.com");
        o["id"].Should().Be(g.ToString());
        o["name"].Should().Be("Jamie Stein");
        o["userName"].Should().Be("jamie@x.com"); // Quickbase user objects carry userName (ours: the sign-in e-mail)
    }

    [Fact]
    public void UserObject_UnknownUser_KeepsKeysWithNulls()
    {
        var o = QbUserValueResolver.UserObject(9, new Dictionary<long, UserIdentity>(), new Dictionary<long, Guid>());
        o.Keys.Should().BeEquivalentTo(new[] { "email", "id", "name", "userName" });
        o.Values.Should().AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void IsStoredUserField_UserMultiUserAndSystemUserColumns()
    {
        QbUserValueResolver.IsStoredUserField(new AppField { TypeCode = "User" }).Should().BeTrue();
        QbUserValueResolver.IsStoredUserField(new AppField { TypeCode = "MultiUser" }).Should().BeTrue();
        QbUserValueResolver.IsStoredUserField(new AppField { TypeCode = "User", IsSystem = true, PhysicalColumnName = "CreatedBy" }).Should().BeTrue();
        QbUserValueResolver.IsStoredUserField(new AppField { TypeCode = "Formula_User" }).Should().BeFalse();
        QbUserValueResolver.IsStoredUserField(new AppField { TypeCode = "Text" }).Should().BeFalse();
    }
}
