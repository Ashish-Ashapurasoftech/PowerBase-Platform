using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Tenants;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

// powerflowextraRd: Connection picker shows only the logged-in tenant (+ explicitly referenced accessible tenants)
public class PipelineConnectionTenantFilterTests
{
    private static readonly TenantItem A = new(Guid.NewGuid(), "A", "a", true);
    private static readonly TenantItem B = new(Guid.NewGuid(), "B", "b", false);
    private static readonly TenantItem C = new(Guid.NewGuid(), "C", "c", false);
    private static readonly IReadOnlyList<TenantItem> All = new[] { A, B, C };

    [Fact]
    public void Returns_only_current_tenant()
        => PipelineConnectionTenantFilter.Filter(All, B.PublicId).Should().Equal(B);

    [Fact]
    public void Includes_referenced_accessible_tenant_after_current()
        => PipelineConnectionTenantFilter.Filter(All, C.PublicId, new[] { A.PublicId }).Select(t => t.Name).Should().Equal("C", "A");

    [Fact]
    public void Never_returns_tenant_the_user_cannot_access()
        => PipelineConnectionTenantFilter.Filter(All, A.PublicId, new[] { Guid.NewGuid() }).Should().Equal(A);

    [Fact]
    public void No_current_tenant_returns_empty()
        => PipelineConnectionTenantFilter.Filter(All, null).Should().BeEmpty();
}
