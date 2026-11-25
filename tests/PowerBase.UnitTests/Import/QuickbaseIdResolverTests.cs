using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Quickbase;
using Xunit;

namespace PowerBase.UnitTests.Import;

public class QuickbaseIdResolverTests
{
    private sealed class FakeApi(QuickbaseTablesResult result) : IQuickbaseTablesApi
    {
        public string? LastAppId { get; private set; }
        public string? LastToken { get; private set; }
        public Task<QuickbaseTablesResult> GetTablesAsync(string qbAppId, string? userToken, CancellationToken ct = default)
        {
            LastAppId = qbAppId;
            LastToken = userToken;
            return Task.FromResult(result);
        }
    }

    private static IQuickbaseCatalogRepository Catalog(params QuickbaseCatalogAppRow[] apps)
    {
        var repo = Substitute.For<IQuickbaseCatalogRepository>();
        repo.FindAppByLogicalIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => apps.FirstOrDefault(a => string.Equals(a.LogicalId, ci.Arg<string>(), StringComparison.OrdinalIgnoreCase)));
        return repo;
    }

    [Fact]
    public async Task Resolves_app_id_from_catalog_and_matches_tables_on_alias()
    {
        var api = new FakeApi(new(QuickbaseLookupStatus.Ok,
            [new("bunx343yd", "_DBID_CONVERSATIONS", "Conversations"), new("bunx343zz", "_DBID_OTHER", "Other")], null));
        var catalog = Catalog(new QuickbaseCatalogAppRow { QbAppId = "bunx343v9", LogicalId = "$App_HIH_Archive" });

        var result = await new QuickbaseIdResolver(api, catalog).ResolveAsync("$app_hih_archive",
            [new("$Table_Conversations", "Conversations", "_dbid_conversations"), new("$Table_Missing", "Missing", "_DBID_MISSING")]);

        Assert.Equal("bunx343v9", api.LastAppId);
        Assert.Equal("Catalog", result.AppIdSource);
        Assert.Equal("bunx343yd", result.Tables[0].QbTableId);
        Assert.Null(result.Tables[1].QbTableId);
        Assert.Equal(1, result.MatchedTableCount);
        Assert.False(result.NeedsAppId);
        Assert.False(result.NeedsUserToken);
    }

    [Fact]
    public async Task Unknown_app_needs_an_app_id_and_no_token_access_needs_a_token()
    {
        var unknown = await new QuickbaseIdResolver(new FakeApi(new(QuickbaseLookupStatus.Ok, [], null)), Catalog())
            .ResolveAsync("$App_Not_In_List", []);
        Assert.True(unknown.NeedsAppId);
        Assert.Equal("Missing", unknown.AppIdSource);

        var api = new FakeApi(new(QuickbaseLookupStatus.NoAccess, [], "no access"));
        var noAccess = await new QuickbaseIdResolver(api, Catalog()).ResolveAsync("$App_Not_In_List", [], manualAppId: "bxxxxxxx1", userToken: "tok");
        Assert.Equal("Manual", noAccess.AppIdSource);
        Assert.Equal("tok", api.LastToken);
        Assert.True(noAccess.NeedsUserToken);
    }

    [Fact]
    public void Table_without_alias_falls_back_to_unique_name()
    {
        var tables = new List<QuickbaseTableIds> { new() { LogicalRef = "$Table_A", Name = "Clients" } };
        QuickbaseIdResolver.Match(tables, [new("bq1", "_DBID_X", "Clients")]);
        Assert.Equal("bq1", tables[0].QbTableId);
    }
}
