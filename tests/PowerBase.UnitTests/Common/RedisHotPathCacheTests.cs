using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.ConnectedTables;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>
/// The hot paths moved onto the metadata cache (Redis L2 when configured): each read goes through the cache under the expected group
/// and key (a cache answer means no SQL), each write drops its group BEFORE touching the database, every newly cached type survives
/// the L2's JSON round trip, and parallel cache uses inside one request share a single shared-version read.
/// </summary>
public class RedisHotPathCacheTests
{
    private const long Tenant = 7;
    private const long User = 3;

    /// <summary>Answers every read with <see cref="Answer"/> (never calls the factory) and records reads and invalidations.</summary>
    private sealed class RecordingCache : IMetadataCache
    {
        public readonly ConcurrentQueue<(long Tenant, string Group, string Key)> Reads = new();
        public readonly ConcurrentQueue<(long Tenant, string Group, bool InTransaction)> Invalidations = new();
        public object? Answer;

        public Task<T> GetOrAddAsync<T>(long tenantId, string group, string key, Func<Task<T>> factory) where T : class
        {
            Reads.Enqueue((tenantId, group, key));
            return Task.FromResult((T)Answer!);
        }

        public void Invalidate(long tenantId, string group, bool inTransaction = false) => Invalidations.Enqueue((tenantId, group, inTransaction));
        public void FlushTenant(long tenantId) { }
    }

    private static IQueryContext Context()
    {
        var ctx = Substitute.For<IQueryContext>();
        ctx.TenantId.Returns(Tenant);
        ctx.UserId.Returns(User);
        return ctx;
    }

    /// <summary>A database that must not be reached: a read answered by the cache never gets here, a write gets here only after
    /// it dropped its cache group.</summary>
    private static ITenantConnectionFactory NoTenantDb()
    {
        var f = Substitute.For<ITenantConnectionFactory>();
        f.CreateAsync(Arg.Any<CancellationToken>()).Returns<Task<SqlConnection>>(_ => throw new InvalidOperationException("no database"));
        return f;
    }

    private static IControlConnectionFactory NoControlDb()
    {
        var f = Substitute.For<IControlConnectionFactory>();
        f.Create().Returns(_ => throw new InvalidOperationException("no database"));
        return f;
    }

    // ── Reads go through the cache ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserRoleNameAsync_IsAnsweredFromTheAccessGroup()
    {
        var cache = new RecordingCache { Answer = new CachedValue<string?>("Administrator") };
        var repo = new AppUserRepository(NoTenantDb(), Context(), cache);

        (await repo.GetUserRoleNameAsync(5, User)).Should().Be("Administrator");
        cache.Reads.Should().ContainSingle().Which.Should().Be((Tenant, MetadataCacheGroups.Access, "rolename:5:3"));
    }

    [Fact]
    public async Task GetUserAppRoleNamesAsync_IsAnsweredFromTheAccessGroup()
    {
        var cache = new RecordingCache { Answer = new List<string> { "A", "B" } };
        var repo = new AppUserRepository(NoTenantDb(), Context(), cache);

        (await repo.GetUserAppRoleNamesAsync(5, User)).Should().Equal("A", "B");
        cache.Reads.Should().ContainSingle().Which.Key.Should().Be("rolenames:5:3");
    }

    [Fact]
    public async Task RolePermissionLists_AreAnsweredFromTheAccessGroup_PerRole()
    {
        var table = Guid.NewGuid();
        var cache = new RecordingCache { Answer = new List<TablePermissionRow>() };
        var repo = new AppRolePermissionRepository(NoTenantDb(), Context(), cache);

        await repo.GetTablePermissionsAsync(9);
        cache.Answer = new List<FieldPermissionScopedRow> { new(table, Guid.NewGuid(), "View") };
        (await repo.GetAllFieldPermissionsAsync(9)).Should().ContainSingle();
        cache.Answer = new List<RecordFilterRow>();
        await repo.GetRecordFiltersAsync(9);

        cache.Reads.Select(r => (r.Group, r.Key)).Should().Equal(
            (MetadataCacheGroups.Access, "tableperms:9"), (MetadataCacheGroups.Access, "fieldperms:9"), (MetadataCacheGroups.Access, "recordfilters:9"));
    }

    [Fact]
    public async Task GetPublicIdsByIdsAsync_ForTheCurrentUser_UsesTheCachedSignInIdentity()
    {
        var publicId = Guid.NewGuid();
        var cache = new RecordingCache { Answer = new User { Id = User, PublicId = publicId, IsActive = true } };
        var repo = new UserRepository(NoControlDb(), Context(), cache);

        (await repo.GetPublicIdsByIdsAsync([User])).Should().Equal(new Dictionary<long, Guid> { [User] = publicId });
        cache.Reads.Should().ContainSingle().Which.Should().Be((MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.Users, "signin:3"));
    }

    [Fact]
    public async Task GetSignInIdentityAsync_NeverHandsOutTheCachedInstanceOrAPasswordHash()
    {
        var cached = new User { Id = User, Name = "Nirav", HashedPassword = "should-not-be-here", IsActive = true };
        var repo = new UserRepository(NoControlDb(), Context(), new RecordingCache { Answer = cached });

        var user = await repo.GetSignInIdentityAsync(User);
        user.Should().NotBeSameAs(cached);
        user!.Name.Should().Be("Nirav");
        user.HashedPassword.Should().BeEmpty();
    }

    [Fact]
    public async Task TenantReads_AreAnsweredFromTheGlobalGroups()
    {
        var cache = new RecordingCache { Answer = new Tenant { Id = Tenant, CodePagesEnabled = true } };
        var repo = new TenantRepository(NoControlDb(), Context(), cache);

        (await repo.GetByIdAsync(Tenant)).CodePagesEnabled.Should().BeTrue();
        cache.Answer = new CachedValue<string?>("Owner");
        (await repo.GetUserRoleNameInTenantAsync(User, Tenant)).Should().Be("Owner");

        cache.Reads.Select(r => (r.Tenant, r.Group, r.Key)).Should().Equal(
            (MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.Tenants, "id:7"),
            (MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.UserPermissions, "tenantrole:3:7"));
    }

    [Fact]
    public async Task UserTokenByHash_IsAnsweredFromTheUserTokensGroup_AsACopy()
    {
        var token = new UserToken { Id = 11, UserId = User, TenantId = Tenant, IsActive = true, TokenHash = "abc" };
        var cache = new RecordingCache { Answer = token };
        var repo = new UserTokenRepository(NoControlDb(), NoTenantDb(), Substitute.For<ITenantConnectionResolver>(), Context(), cache);

        var found = await repo.GetByHashAsync("abc", default);
        found.Should().NotBeSameAs(token);
        found!.Id.Should().Be(11);
        cache.Reads.Should().ContainSingle().Which.Should().Be((MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.UserTokens, "hash:abc"));
    }

    // ── Writes drop their group BEFORE the database ─────────────────────────────────────────────

    private static async Task<RecordingCache> WriteAsync(Func<RecordingCache, Task> write)
    {
        var cache = new RecordingCache();
        await FluentActions.Awaiting(() => write(cache)).Should().ThrowAsync<InvalidOperationException>().WithMessage("no database");
        return cache;
    }

    [Fact]
    public async Task RevokingAToken_DropsTheTokenCache_First()
    {
        var cache = await WriteAsync(c => new UserTokenRepository(NoControlDb(), NoTenantDb(), Substitute.For<ITenantConnectionResolver>(), Context(), c)
            .RevokeAsync(Guid.NewGuid(), Tenant, default));
        cache.Invalidations.Should().Contain(i => i.Tenant == MetadataCacheGroups.GlobalTenant && i.Group == MetadataCacheGroups.UserTokens);
    }

    [Fact]
    public async Task RenamingARole_DropsAccess_First() =>
        (await WriteAsync(c => new AppRoleRepository(NoTenantDb(), Context(), c).UpdateNameAsync(4, "New")))
            .Invalidations.Should().Contain(i => i.Tenant == Tenant && i.Group == MetadataCacheGroups.Access);

    [Fact]
    public async Task DeactivatingAUser_DropsTheSignInIdentities_First() =>
        (await WriteAsync(c => new AdminRepository(NoControlDb(), Context(), c).UpdateUserActiveStateAsync(Guid.NewGuid(), false)))
            .Invalidations.Should().Contain(i => i.Group == MetadataCacheGroups.Users);

    [Fact]
    public async Task ChangingTheCodePagesSwitch_DropsTheTenantCache_First() =>
        (await WriteAsync(c => new AdminRepository(NoControlDb(), Context(), c).UpdateTenantCodePagesEnabledAsync(Tenant, true)))
            .Invalidations.Should().Contain(i => i.Group == MetadataCacheGroups.Tenants);

    [Fact]
    public async Task UpdatingAVariable_DropsVariables_First() =>
        (await WriteAsync(c => new AppVariableRepository(NoTenantDb(), Context(), c).UpdateAsync(1, Guid.NewGuid(), "n", "v", null)))
            .Invalidations.Should().Contain(i => i.Tenant == Tenant && i.Group == MetadataCacheGroups.Variables);

    [Fact]
    public async Task DeletingAForm_DropsForms_InTransactionMode_First() =>
        (await WriteAsync(c => new FormRepository(NoTenantDb(), Context(), c).DeleteAsync(Guid.NewGuid())))
            .Invalidations.Should().Contain((Tenant, MetadataCacheGroups.Forms, true));

    [Fact]
    public async Task DeletingPages_DropsAccess_BecauseTheNavListIsPerUser() =>
        (await WriteAsync(c => new PageRepository(NoTenantDb(), Context(), c).SoftDeleteManyAsync([Guid.NewGuid()])))
            .Invalidations.Should().Contain(i => i.Tenant == Tenant && i.Group == MetadataCacheGroups.Access);

    [Fact]
    public async Task RemovingAConnectedTable_DropsTheConnectedList_First() =>
        (await WriteAsync(c => new ConnectedTableRepository(NoTenantDb(), Context(), c).SoftDeleteAsync(1, "Removed")))
            .Invalidations.Should().Contain(i => i.Tenant == Tenant && i.Group == MetadataCacheGroups.ConnectedTables);

    // ── Every newly cached type survives the Redis L2 (JSON) and the deep copy ───────────────────

    public static TheoryData<object> CachedValues() => new()
    {
        new CachedValue<string?>(null),
        new CachedValue<string?>("Administrator"),
        new List<TablePermissionRow> { new(Guid.NewGuid(), "Orders", "AllRecords", "None", true, false, true, false, "CustomAccess") },
        new List<FieldPermissionScopedRow> { new(Guid.NewGuid(), Guid.NewGuid(), "View") },
        new List<RecordFilterRow> { new(Guid.NewGuid(), "AND", """{"logic":"and"}""", "Modify") },
        new User { Id = 3, PublicId = Guid.NewGuid(), Email = "a@b.c", Name = "N", SystemRoleCode = "SuperAdmin", IsActive = true },
        new Tenant { Id = 7, PublicId = Guid.NewGuid(), Name = "T", Status = "Active", CodePagesEnabled = true, RowVersion = [1, 2] },
        new UserToken { Id = 11, PublicId = Guid.NewGuid(), TenantId = 7, UserId = 3, TokenHash = "h", IsActive = true, AccessAllApps = false },
        new List<AppVariable> { new() { Id = 1, PublicId = Guid.NewGuid(), AppId = 2, Name = "Rate", Value = "12" } },
        new List<FieldType> { new() { Id = 1, Code = "Text", DisplayName = "Text", Category = "Basic", SqlDataType = "NVARCHAR(500)", Icon = "t" } },
        new List<FormSection>
        {
            new()
            {
                Id = 1, FormId = 2, Name = "Main",
                Blocks = [new FormSectionBlock { Id = 5, FormSectionId = 1, DisplayOrder = 1, Elements = [new FormElement { Id = 9, FormSectionId = 1, AppFieldId = 6 }] }],
            },
        },
        new List<FormRule>
        {
            new()
            {
                Id = 4, PublicId = Guid.NewGuid(), FormId = 2, Name = "R", IsActive = true,
                Conditions = [new FormRuleCondition { Id = 1, FormRuleId = 4, AppFieldId = 6, Operator = "eq", Value = "x" }],
                Actions = [new FormRuleAction { Id = 2, FormRuleId = 4, ActionType = "Hide" }],
            },
        },
        new List<GridEditRuntimeRule> { new() { Id = Guid.NewGuid(), Name = "G", ElementFieldMap = new() { [9] = 6 } } },
        new List<Page> { new() { Id = 1, PublicId = Guid.NewGuid(), AppId = 2, Name = "Home", PageType = "Dashboard" } },
    };

    [Theory]
    [MemberData(nameof(CachedValues))]
    public void CachedTypes_RoundTripThroughL2AndTheDeepCopy(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.General));
        var fromL2 = typeof(MetadataCache).GetMethod(nameof(MetadataCache.FromL2), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(value.GetType()).Invoke(null, [bytes]);
        fromL2.Should().BeEquivalentTo(value, o => o.PreferringRuntimeMemberTypes());

        var copy = typeof(CacheCopy).GetMethod(nameof(CacheCopy.Deep))!.MakeGenericMethod(value.GetType()).Invoke(null, [value]);
        copy.Should().NotBeSameAs(value);
        copy.Should().BeEquivalentTo(value, o => o.PreferringRuntimeMemberTypes());
    }

    // ── One shared-version read per request, also for parallel uses ─────────────────────────────

    private sealed class CountingVersions : ICacheVersionStore
    {
        public int Reads;
        public async Task<IReadOnlyDictionary<string, long>?> ReadAsync(long tenantId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            await Task.Delay(50, ct); // a slow read: parallel callers must wait for it, not answer from older versions
            return new Dictionary<string, long>();
        }
        public bool Bump(long tenantId, string group) => true;
    }

    [Fact]
    public async Task ParallelCacheUsesInOneRequest_ShareOneVersionRead_AndAllWaitForIt()
    {
        var versions = new CountingVersions();
        var cache = new MetadataCache(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MetadataCacheOptions { UseInMemoryCache = true, TransactionQuarantineSeconds = 0, WriteQuarantineSeconds = 0 }),
            NullLogger<MetadataCache>.Instance, versions);

        using var scope = CacheVersionScope.Begin();
        var stats = CacheRequestStats.Begin();
        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            cache.GetOrAddAsync(Tenant, MetadataCacheGroups.Access, $"k{i}", () => Task.FromResult(new CachedId(i)))));

        answers.Select(a => a.Value).Should().Equal(0, 1, 2, 3, 4);
        versions.Reads.Should().Be(1);
        stats.SyncCount.Should().Be(1);
        stats.SqlCount.Should().Be(5);
    }

    [Fact]
    public void ServerTiming_CarriesADescription_WithoutQuotes()
    {
        var timing = new ServerTiming();
        timing.Add("cache-sql", TimeSpan.FromMilliseconds(4), "3\"x");
        timing.ToHeaderValue().Should().Be("cache-sql;dur=4.0;desc=\"3x\"");
    }
}
