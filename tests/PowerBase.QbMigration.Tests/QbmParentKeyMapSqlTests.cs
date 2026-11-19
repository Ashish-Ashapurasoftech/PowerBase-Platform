using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

/// <summary>In an encrypted app every non-system field is stored as ciphertext, so a child's text key ("G-17") can only be matched
/// to its parent after the parent's key column is decrypted. On CFTSS the keys were compared as ciphertext, nothing matched and
/// millions of links were stored empty.</summary>
public sealed class QbmParentKeyMapSqlTests : IAsyncLifetime
{
    private const long PlainApp = 7_101, EncryptedApp = 7_102, Tenant = 1;
    private readonly long _parentId = 920_000 + Random.Shared.Next(70_000);
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private string DataTable => $"data.t_{_parentId}";

    public async Task InitializeAsync()
    {
        _encryption.DecryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<string>(0).StartsWith("ENC(") ? ci.ArgAt<string>(0)[4..^1] : throw new FormatException("not ciphertext"));
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync($"""
            IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
            IF SCHEMA_ID('data') IS NULL EXEC('CREATE SCHEMA data');
            IF OBJECT_ID('meta.App') IS NULL CREATE TABLE meta.App (Id BIGINT PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, SecurityOptions NVARCHAR(MAX) NULL, IsEncrypted BIT NOT NULL DEFAULT 0);
            DELETE FROM meta.App WHERE Id IN ({PlainApp}, {EncryptedApp});
            INSERT INTO meta.App (Id, IsEncrypted) VALUES ({PlainApp}, 0);
            CREATE TABLE {DataTable} (
                Id BIGINT NOT NULL PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, f_6 NVARCHAR(MAX) NULL, f_7 DECIMAL(18,4) NULL);
            """);
        await c.ExecuteAsync("INSERT INTO meta.App (Id, IsEncrypted, SecurityOptions) VALUES (@app, 1, @options)",
            new { app = EncryptedApp, options = "{\"WrappedDek\":\"wrapped-dek\"}" });
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync($"DROP TABLE IF EXISTS {DataTable}; DELETE FROM meta.App WHERE Id IN ({PlainApp}, {EncryptedApp});");
    }

    private QbmPowerBaseReader Reader(IEncryptionService? encryption = null) =>
        new(new SqlDb.Factory(), () => new SqlConnection(SqlDb.ConnectionString), encryption);

    private async Task Parents(params (long Id, string? Key, bool Deleted)[] rows)
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        foreach (var (id, key, deleted) in rows)
            await c.ExecuteAsync($"INSERT INTO {DataTable} (Id, IsDeleted, f_6) VALUES (@id, @deleted, @key)", new { id, deleted, key });
    }

    [SqlFact]
    public async Task An_encrypted_apps_parent_keys_are_matched_by_their_decrypted_text()
    {
        await Parents((10, "ENC(G-1)", false), (11, "ENC(G-2)", false), (12, "ENC(G-3)", true), (13, null, false));
        var map = await Reader(_encryption).ParentKeyMapAsync(_parentId, Pb(6, "Text", "Goal ID"), Tenant, EncryptedApp);
        map.Should().BeEquivalentTo(new Dictionary<string, long> { ["G-1"] = 10, ["G-2"] = 11 });
    }

    [SqlFact]
    public async Task A_plain_apps_parent_keys_are_used_as_stored_and_need_no_encryption_service()
    {
        await Parents((10, "G-1", false), (11, "G-2", false), (12, "G-2", false));
        var map = await Reader().ParentKeyMapAsync(_parentId, Pb(6, "Text", "Goal ID"), Tenant, PlainApp);
        map.Should().BeEquivalentTo(new Dictionary<string, long> { ["G-1"] = 10, ["G-2"] = 12 }); // a duplicated key: the highest Id wins
    }

    [SqlFact]
    public async Task A_number_key_in_an_encrypted_app_is_not_encrypted_so_it_is_read_as_stored()
    {
        await using (var c = new SqlConnection(SqlDb.ConnectionString))
            await c.ExecuteAsync($"INSERT INTO {DataTable} (Id, f_7) VALUES (10, 17), (11, 18)");
        var map = await Reader(_encryption).ParentKeyMapAsync(_parentId, Pb(7, "Number", "Code"), Tenant, EncryptedApp);
        map.Keys.Should().BeEquivalentTo("17.0000", "18.0000");
    }

    [SqlFact]
    public async Task Keys_that_cannot_be_decrypted_stop_the_load_instead_of_matching_nothing()
    {
        await Parents((10, "not-ciphertext-1", false), (11, "not-ciphertext-2", false));
        var act = () => Reader(_encryption).ParentKeyMapAsync(_parentId, Pb(6, "Text", "Goal ID"), Tenant, EncryptedApp);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*could not be decrypted*");
    }

    [SqlFact]
    public async Task An_encrypted_key_without_an_encryption_service_stops_the_load()
    {
        await Parents((10, "ENC(G-1)", false));
        var act = () => Reader().ParentKeyMapAsync(_parentId, Pb(6, "Text", "Goal ID"), Tenant, EncryptedApp);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
