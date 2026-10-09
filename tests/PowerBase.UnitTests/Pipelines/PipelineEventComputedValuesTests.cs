using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;
using Xunit;
using IMessagePublisherAlias = PowerBase.Application.Common.Interfaces.IMessagePublisher;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>When the event of a write must carry computed values, and that the write-time projector can be built by DI.</summary>
public class PipelineEventComputedValuesTests
{
    private static readonly AppField Name = new() { Id = 6, Fid = 6, Name = "Order no", TypeCode = "Text" };
    private static readonly AppField Sum = new() { Id = 10, Fid = 10, Name = "Sum of Amount", TypeCode = "Summary" };
    private static readonly AppField Look = new() { Id = 12, Fid = 12, Name = "Customer Name", TypeCode = "Lookup" };
    private static readonly AppField Frm = new() { Id = 13, Fid = 13, Name = "Code", TypeCode = "Formula_Text" };

    private static PipelineEventListener Listener(bool bulk = false, bool deleted = false, bool otherTenant = false, params string?[] filters) =>
        new(bulk, deleted, otherTenant, filters);

    private static bool Required(IReadOnlyList<AppField> fields, params PipelineEventListener[] listeners) =>
        PipelineEventComputedValues.Required(listeners, fields);

    [Fact]
    public void PlainFilterOnAStoredField_NeedsNothingNow()
        => Assert.False(Required([Name, Sum], Listener(filters: "[{\"Field\":\"fid_6\",\"Operator\":\"is\",\"Value\":\"x\"}]")));

    [Fact]
    public void NoFilterAtAll_NeedsNothingNow()
        => Assert.False(Required([Name, Sum, Look, Frm], Listener()));

    [Fact]
    public void NoListeners_NeedsNothing()
        => Assert.False(Required([Name, Sum]));

    [Fact]
    public void TableWithoutComputedFields_NeverNeedsThem()
        => Assert.False(Required([Name], Listener(bulk: true), Listener(deleted: true), Listener(otherTenant: true)));

    [Theory]
    [InlineData("[{\"Field\":\"fid_10\",\"Operator\":\"greater_than\",\"Value\":\"100\"}]")]            // by fid token
    [InlineData("[{\"Field\":\"Sum of Amount\",\"Operator\":\"greater_than\",\"Value\":\"100\"}]")]     // by name
    [InlineData("{10.GT.'100'}")]                                                                      // advanced query id
    public void FilterThatMentionsAComputedField_NeedsItNow(string filter)
        => Assert.True(Required([Name, Sum], Listener(filters: filter)));

    [Fact]
    public void FilterMentioningALookupOrFormula_NeedsItNow()
    {
        Assert.True(Required([Name, Look], Listener(filters: "fid_12")));
        Assert.True(Required([Name, Frm], Listener(filters: "fid_13")));
    }

    [Fact]
    public void Matching_IsConservative_AComputedFilterIsNeverReportedAsNotNeeded()
        // "fid_10" contains "fid_1": answering true is allowed (a little extra work); the reverse is not.
        => Assert.True(Required([new AppField { Id = 1, Fid = 1, Name = "A", TypeCode = "Summary" }], Listener(filters: "fid_10")));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void BulkDeleteOrAnotherTenantsFlow_NeedsComputedValuesInTheEvent(bool bulk, bool deleted, bool otherTenant)
        => Assert.True(Required([Name, Sum], Listener(bulk, deleted, otherTenant)));

    [Fact]
    public void OneListenerThatNeedsThem_IsEnough()
        => Assert.True(Required([Name, Sum], Listener(), Listener(filters: "fid_6"), Listener(otherTenant: true)));

    [Fact]
    public void IsComputed_CoversLookupSummaryAndEveryFormulaVariant_ButNotDeletedOrStoredFields()
    {
        foreach (var type in new[] { "Lookup", "Summary", "Formula", "Formula_Text", "Formula_Number", "Formula_Date", "Formula_Bool" })
            Assert.True(PipelineEventComputedValues.IsComputed(new AppField { Id = 1, Fid = 1, Name = "x", TypeCode = type }), type);
        Assert.False(PipelineEventComputedValues.IsComputed(Name));
        Assert.False(PipelineEventComputedValues.IsComputed(new AppField { Id = 1, Fid = 1, Name = "x", TypeCode = "Summary", IsDeleted = true }));
        Assert.False(PipelineEventComputedValues.IsComputed(new AppField { Id = 1, Name = "x", TypeCode = "Summary" }));
        Assert.False(PipelineEventComputedValues.IsComputed(new AppField { Id = 1, Fid = 1, Name = "x", TypeCode = "Reference" }));
    }

    // ───────────────────────── dependency injection ─────────────────────────

    [Fact]
    public async Task WriteTimeProjector_IsBuiltByTheContainer_AndProjectsATableWithoutRelationships()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IControlConnectionFactory>());
        services.AddScoped(_ => Substitute.For<ITenantConnectionFactory>());
        services.AddScoped(_ => Substitute.For<IQueryContext>());
        services.AddScoped(_ => Substitute.For<IMessagePublisherAlias>());
        services.AddScoped(_ => Substitute.For<IEncryptionService>());
        services.AddScoped(_ => Substitute.For<IAppTableRepository>());
        services.AddScoped(_ => Substitute.For<IAppFieldRepository>());
        services.AddScoped(_ => Substitute.For<IRelationshipRepository>());
        services.AddScoped(_ => Substitute.For<IAppRepository>());
        services.AddScoped(_ => Substitute.For<IUserRepository>());
        services.AddScoped(_ => Substitute.For<IFormulaProjector>());
        services.AddScoped<IPipelineWriteTimeRelationalProjector, PipelineWriteTimeRelationalProjector>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var projector = scope.ServiceProvider.GetRequiredService<IPipelineWriteTimeRelationalProjector>();

        // A table with no Reference / Lookup / Summary builds the inner projector but runs no query.
        var result = await projector.ProjectAsync(new AppTable { Id = 1 }, [Name],
            [new Dictionary<string, object?> { ["Id"] = 1L }], CancellationToken.None);

        Assert.Single(result);
        Assert.Empty(result[0]);
    }
}
