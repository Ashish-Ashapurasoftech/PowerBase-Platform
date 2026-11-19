using FluentAssertions;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

/// <summary>A QuickBase user field that is the foreign key of a relationship (CFTSS "Related User Setting"): PowerBase
/// stores it as a Reference to the parent record, so the user is translated to the parent record that names that user.</summary>
public class QbUserReferenceTests
{
    private const string AnnJson = "{\"email\":\"Ann@x.com\",\"id\":\"1.a\",\"name\":\"Ann\"}";

    [Fact]
    public void A_user_field_stored_as_a_reference_is_loadable_but_a_user_field_in_any_other_type_still_blocks()
    {
        var qb = new QbField(6, "Related User Setting", "user", null, []);
        QbFieldMapper.Map([qb], [.. SystemFields(), Pb(6, "Reference", "Related User Setting")]).Single().Action.Should().Be(QbFieldAction.Load);
        QbFieldMapper.Map([qb], [.. SystemFields(), Pb(6, "Number", "Related User Setting")]).Single().Action.Should().Be(QbFieldAction.Block);
    }

    private static (QbTransformedRow Row, List<QbIssue> Issues) Transform(TestData.Lookups lookups, params string[] jsons)
    {
        var qb = new QbField(6, "Related User Setting", "user", null, []);
        var maps = QbFieldMapper.Map([.. QbSystemFields(), qb], [.. SystemFields(), Pb(6, "Reference", "Related User Setting")]);
        var t = new QbValueTransformer(maps, lookups, new QbTransformOptions { FallbackUserId = 99 });
        var issues = new List<QbIssue>();
        QbTransformedRow last = null!;
        var id = 7;
        foreach (var json in jsons) last = t.Transform(Row(id++, (6, json)), issues);
        return (last, issues);
    }

    [Fact]
    public void The_user_becomes_the_parent_record_that_names_that_user()
    {
        var (row, issues) = Transform(new TestData.Lookups(userParents: new() { [(6, "1.a")] = 301 }), AnnJson);
        row.Values[6].Should().Be(301L);
        issues.Should().BeEmpty();
    }

    [Fact]
    public void A_user_without_a_parent_is_stored_empty_and_reported_once_per_chunk()
    {
        var (row, issues) = Transform(new TestData.Lookups(), AnnJson, AnnJson, AnnJson);
        row.Values[6].Should().BeNull();
        issues.Should().ContainSingle(i => i.Code == "QBM_REFERENCE_NOT_FOUND" && i.Severity == "warning" && i.QbFid == 6);
    }

    [Fact]
    public void An_empty_user_is_stored_empty_without_an_issue()
    {
        var (row, issues) = Transform(new TestData.Lookups(), "\"\"");
        row.Values[6].Should().BeNull();
        issues.Should().BeEmpty();
    }
}

public class QbUserParentIndexTests
{
    private sealed class OneTableClient(params QbRow[] rows) : IQuickBaseClient
    {
        public int Queries { get; private set; }
        public Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default)
        {
            Queries++;
            var after = long.Parse(where![(where.LastIndexOf("{3.GT.", StringComparison.Ordinal) + 6)..^1]);
            var page = rows.Where(r => r.RecordId > after).OrderBy(r => r.RecordId).ToList();
            return Task.FromResult(new QbQueryPage(page, page.Count, page.Count, 0));
        }
    }

    private static QbRow User(long id, string json) => Row(id, (6, json));

    [Fact]
    public async Task Users_are_found_by_quickbase_user_id_then_by_email_ignoring_case()
    {
        var client = new OneTableClient(User(10, "{\"email\":\"ann@x.com\",\"id\":\"1.a\"}"), User(11, "{\"email\":\"bo@x.com\",\"id\":\"2.b\"}"));
        var index = await QbUserParentIndex.BuildAsync(client, new("bqparent", 6));
        index.Find("1.a", "other@x.com").Should().Be(10);
        index.Find(null, "BO@X.COM").Should().Be(11);
        index.Find("9.z", "nobody@x.com").Should().BeNull();
        index.Find(null, null).Should().BeNull();
    }

    [Fact]
    public async Task A_user_named_by_several_parent_records_resolves_to_the_lowest_record_id_and_is_counted()
    {
        var client = new OneTableClient(User(30, "{\"email\":\"ann@x.com\",\"id\":\"1.a\"}"), User(12, "{\"email\":\"ann@x.com\",\"id\":\"1.a\"}"));
        var index = await QbUserParentIndex.BuildAsync(client, new("bqparent", 6));
        index.Find("1.a", null).Should().Be(12);
        index.DuplicateUsers.Should().Be(1);
    }

    [Fact]
    public async Task Parent_records_without_a_user_are_ignored()
    {
        var client = new OneTableClient(Row(10), Row(11, (6, "\"\"")), User(12, "{\"email\":\"ann@x.com\",\"id\":\"1.a\"}"));
        var index = await QbUserParentIndex.BuildAsync(client, new("bqparent", 6));
        index.Find("1.a", null).Should().Be(12);
        index.DuplicateUsers.Should().Be(0);
    }

    [Fact]
    public async Task The_cache_reads_a_parent_table_once_however_many_chunks_ask()
    {
        var client = new OneTableClient(User(10, "{\"email\":\"ann@x.com\",\"id\":\"1.a\"}"));
        var cache = new QbUserParentIndexes(client);
        var spec = new QbUserReferenceSpec("bqparent", 6);
        var indexes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync(spec)));
        indexes.Distinct().Should().HaveCount(1);
        client.Queries.Should().Be(1);
    }
}

public class QbUserReferenceResolverTests
{
    private static QbUserReferenceResolver.Parent UserSettings(params AppField[] fields) => new("bqusers", "User Settings", [.. SystemFields(), .. fields]);

    private static List<QbFieldMapping> Child(string pbType = "Reference") =>
        [.. QbFieldMapper.Map([new QbField(10, "Related User", "user", null, [])], [.. SystemFields(), Pb(10, pbType, "Related User")])];

    [Fact]
    public void The_parents_only_user_field_is_the_key_the_users_are_matched_on()
    {
        var (specs, problems) = QbUserReferenceResolver.Resolve(Child(), [(10, 250)], _ => UserSettings(Pb(6, "User", "User"), Pb(7, "Text", "Name")));
        problems.Should().BeEmpty();
        specs.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, QbUserReferenceSpec>(10, new("bqusers", 6)));
    }

    [Fact]
    public void A_parent_with_no_user_field_or_several_blocks_instead_of_guessing()
    {
        var (_, none) = QbUserReferenceResolver.Resolve(Child(), [(10, 250)], _ => UserSettings(Pb(7, "Text", "Name")));
        none.Should().ContainSingle(p => p.Code == "QBM_USER_REFERENCE_PARENT_KEY" && p.Severity == "error");
        var (_, several) = QbUserReferenceResolver.Resolve(Child(), [(10, 250)], _ => UserSettings(Pb(6, "User", "User"), Pb(8, "User", "Manager")));
        several.Should().ContainSingle(p => p.Code == "QBM_USER_REFERENCE_PARENT_KEY" && p.Message.Contains("User") && p.Message.Contains("Manager"));
    }

    [Fact]
    public void A_user_reference_without_a_relationship_or_a_parent_table_in_the_run_blocks()
    {
        QbUserReferenceResolver.Resolve(Child(), [], _ => null).Problems.Should().ContainSingle(p => p.Code == "QBM_USER_REFERENCE_NO_RELATIONSHIP");
        QbUserReferenceResolver.Resolve(Child(), [(10, 250)], _ => null).Problems.Should().ContainSingle(p => p.Code == "QBM_USER_REFERENCE_PARENT_KEY");
    }

    [Fact]
    public void Tables_without_user_references_resolve_to_nothing()
    {
        var plain = QbFieldMapper.Map([new QbField(6, "Name", "text", null, [])], [.. SystemFields(), Pb(6, "Text", "Name")]);
        var (specs, problems) = QbUserReferenceResolver.Resolve(plain, [], _ => null);
        specs.Should().BeEmpty();
        problems.Should().BeEmpty();
    }
}
