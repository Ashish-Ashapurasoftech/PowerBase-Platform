using FluentAssertions;
using PowerBase.API.Attributes;
using PowerBase.API.Controllers;
using PowerBase.Domain.Constants;

namespace PowerBase.UnitTests.Apps;

/// <summary>Copy App is allowed by its own tenant-level permission, apps:copy, not by apps:create.</summary>
public class AppCopyPermissionTests
{
    [Theory]
    [InlineData(nameof(AppCopyController.Copy))]
    [InlineData(nameof(AppCopyController.Progress))]
    public void Endpoints_NeedAppsCopy(string action)
    {
        var permission = typeof(AppCopyController).GetMethod(action)!.GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(RequirePermissionAttribute))
            .ConstructorArguments.Single().Value;

        permission.Should().Be(PermissionCodes.AppsCopy);
    }

    [Fact]
    public void AppsCopy_IsNotGivenToTheDefaultUserRole()
    {
        // Like apps:create: new tenants' "User" role cannot copy apps until an administrator allows it.
        PermissionCodes.AppsCopy.Should().Be("apps:copy");
        PermissionCodes.DefaultUserPermissions.Should().NotContain(PermissionCodes.AppsCopy);
    }
}
