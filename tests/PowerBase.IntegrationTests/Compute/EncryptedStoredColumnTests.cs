using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.IntegrationTests.Compute;

/// <summary>
/// An ENCRYPTED app (Customers / Orders, text columns hold real AES-GCM ciphertext under the app's DEK): the worker decrypts what each
/// stored field reads and keeps the computed value PLAIN in c_ — a lookup of the customer's (encrypted) name, a count of the customer's
/// orders whose (encrypted) status is "Open", and the sum of their (plain, never encrypted) amounts with the same criteria. Checked after
/// the first build and after live changes through the outbox, against values computed in C# from the plain text.
/// </summary>
public class EncryptedStoredColumnTests : IClassFixture<ComputeSqlFixture>, IAsyncLifetime
{
    private const long App = 2, Cust = 30, Ord = 31;
    private readonly ComputeSqlFixture _db;
    private readonly long _tenantId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
    // Two fields built side by side (two background slots of three), the lookup's table first.
    private readonly ComputeOptions _options = new()
    {
        PauseBetweenBatchesMs = 0, SummaryBatchSize = 7, LookupBatchSize = 50, MaxSessionsPerDatabase = 3, BackgroundSessionsPerDatabase = 2,
    };
    private readonly IEncryptionService _encryption;
    private string _dek = "";
    private long _nameLookupId, _openCountId, _openSumId;

    // Plain truth the stored values are compared with.
    private readonly Dictionary<long, string> _customerNames = new();
    private readonly Dictionary<long, (long Customer, decimal Amount, string Status)> _orders = new();

    public EncryptedStoredColumnTests(ComputeSqlFixture db)
    {
        _db = db;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Encryption:IsEncryptionLocal"] = "true",
            ["Encryption:MasterKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
        }).Build();
        _encryption = new CachedDekEncryptionService(config, new AesEncryptionService(config));
    }

    private sealed class Factory(string cs) : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(cs));
    }

    private IQueryContext Context()
    {
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(_tenantId);
        return context;
    }

    private ComputeOutboxRepository Outbox()
    {
        var settings = Substitute.For<IComputeSettings>();
        settings.IsComputeEnabledAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        return new ComputeOutboxRepository(new Factory(_db.ConnectionString), Context(), settings);
    }

    private (MaterializeBuilder Builder, ComputeTableProcessor Processor) Worker(bool withDecryptor = true)
    {
        var factory = new Factory(_db.ConnectionString);
        var context = Context();
        var decryptor = withDecryptor ? new ComputeDecryptor(_encryption, context) : null;
        var cache = new ComputeModelCache();
        var processor = new ComputeTableProcessor(factory, context, cache, decryptor: decryptor);
        return (new MaterializeBuilder(factory, context, new SchemaEngineService(factory), cache, processor, decryptor: decryptor), processor);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private Task<string> Enc(string plain) => _encryption.EncryptDataAsync(plain, _dek, _tenantId, App);

    public async Task InitializeAsync()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync("IF COL_LENGTH('meta.App', 'SecurityOptions') IS NULL ALTER TABLE meta.App ADD SecurityOptions NVARCHAR(MAX) NULL;");
        await conn.ExecuteAsync($"""
            IF OBJECT_ID('data.t_{Cust}') IS NOT NULL DROP TABLE data.t_{Cust};
            IF OBJECT_ID('data.t_{Ord}') IS NOT NULL DROP TABLE data.t_{Ord};
            DELETE FROM meta.AppField WHERE AppTableId IN ({Cust}, {Ord});
            DELETE FROM meta.AppTable WHERE Id IN ({Cust}, {Ord});
            DELETE FROM meta.App WHERE Id = {App};
            DELETE FROM meta.ComputeOutbox WHERE TableId IN ({Cust}, {Ord});
            """);

        _dek = await _encryption.GenerateAndWrapDekAsync(_tenantId, App);
        await conn.ExecuteAsync("INSERT INTO meta.App (Id, IsEncrypted, SecurityOptions) VALUES (@App, 1, @opts)",
            new { App, opts = System.Text.Json.JsonSerializer.Serialize(new { WrappedDek = _dek }) });
        await conn.ExecuteAsync($"""
            INSERT INTO meta.AppTable (Id, AppId) VALUES ({Cust}, {App}), ({Ord}, {App});
            CREATE TABLE data.t_{Cust} (Id BIGINT IDENTITY PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), IsDeleted BIT NOT NULL DEFAULT 0,
                CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CreatedBy BIGINT NULL, ModifiedOn DATETIME2(3) NULL, ModifiedBy BIGINT NULL,
                f_6 NVARCHAR(MAX) NULL);
            CREATE TABLE data.t_{Ord} (Id BIGINT IDENTITY PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), IsDeleted BIT NOT NULL DEFAULT 0,
                CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CreatedBy BIGINT NULL, ModifiedOn DATETIME2(3) NULL, ModifiedBy BIGINT NULL,
                f_6 BIGINT NULL, f_7 DECIMAL(18,4) NULL, f_8 NVARCHAR(MAX) NULL);
            CREATE INDEX IX_t_{Ord}_f_6 ON data.t_{Ord}(f_6) WHERE IsDeleted = 0;
            INSERT INTO meta.AppField (AppTableId, FieldTypeId, Fid, Name) VALUES
                ({Cust}, 1, 6, 'Name'), ({Ord}, 3, 6, 'Customer'), ({Ord}, 2, 7, 'Amount'), ({Ord}, 1, 8, 'Status');
            """);

        var openFilter = System.Text.Json.JsonSerializer.Serialize(new
        {
            Logic = "and",
            Nodes = new[] { new { Condition = new { FieldId = 8, Operator = "eq", Value = "Open" } } }
        });
        _nameLookupId = await AddFieldAsync(conn, Ord, 4, 9, "Customer Name", $"{{\"SourceTableId\":{Cust},\"ReferenceFid\":6,\"SourceFid\":6}}");
        _openCountId = await AddFieldAsync(conn, Cust, 5, 8, "Open Orders",
            System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = Ord, ReferenceFid = 6, Function = "Count", FilterTree = openFilter }));
        _openSumId = await AddFieldAsync(conn, Cust, 5, 9, "Open Amount",
            System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = Ord, ReferenceFid = 6, Function = "Sum", TargetFid = 7, FilterTree = openFilter }));

        // 20 customers (encrypted names), 150 orders (plain amounts, encrypted statuses); customers 19 and 20 have no orders.
        for (var i = 1; i <= 20; i++)
        {
            var name = $"Customer {i:00}";
            var id = await conn.ExecuteScalarAsync<long>($"INSERT INTO data.t_{Cust} (f_6) OUTPUT inserted.Id VALUES (@v)", new { v = await Enc(name) });
            _customerNames[id] = name;
        }
        for (var i = 1; i <= 150; i++)
        {
            var customer = (long)(i % 18) + 1;
            var status = i % 3 == 0 ? "Open" : "Closed";
            var amount = i * 10m;
            var id = await conn.ExecuteScalarAsync<long>($"INSERT INTO data.t_{Ord} (f_6, f_7, f_8) OUTPUT inserted.Id VALUES (@customer, @amount, @status)",
                new { customer, amount, status = await Enc(status) });
            _orders[id] = (customer, amount, status);
        }
        await conn.ExecuteAsync("UPDATE meta.AppField SET MaterializeMode = 1 WHERE Id IN @ids", new { ids = new[] { _nameLookupId, _openCountId, _openSumId } });
    }

    private static Task<long> AddFieldAsync(SqlConnection conn, long table, int type, int fid, string name, string settings) =>
        conn.ExecuteScalarAsync<long>("INSERT INTO meta.AppField (AppTableId, FieldTypeId, Fid, Name, Settings) OUTPUT inserted.Id VALUES (@table, @type, @fid, @name, @settings)",
            new { table, type, fid, name, settings });

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Stored values that differ from the plain truth: (lookup, open count, open sum).</summary>
    private async Task<(int Lookup, int Count, int Sum)> WrongValuesAsync()
    {
        await using var conn = await OpenAsync();
        var lookups = (await conn.QueryAsync<(long Id, string? Value)>($"SELECT Id, c_9 FROM data.t_{Ord} WHERE IsDeleted = 0")).ToList();
        var wrongLookup = lookups.Count(r => r.Value != _customerNames.GetValueOrDefault(_orders[r.Id].Customer));

        var summaries = (await conn.QueryAsync<(long Id, int? Count, decimal? Sum)>($"SELECT Id, c_8, c_9 FROM data.t_{Cust} WHERE IsDeleted = 0")).ToList();
        int wrongCount = 0, wrongSum = 0;
        foreach (var (id, count, sum) in summaries)
        {
            var open = _orders.Values.Where(o => o.Customer == id && o.Status == "Open").ToList();
            if (count != open.Count) wrongCount++;
            if (sum != (open.Count == 0 ? null : open.Sum(o => o.Amount))) wrongSum++;
        }
        return (wrongLookup, wrongCount, wrongSum);
    }

    private async Task<(byte Mode, byte State)> StateAsync(long fieldId)
    {
        await using var conn = await OpenAsync();
        return await conn.QuerySingleAsync<(byte, byte)>("SELECT MaterializeMode, MaterializeState FROM meta.AppField WHERE Id = @fieldId", new { fieldId });
    }

    [Fact]
    public async Task Encrypted_inputs_are_decrypted_and_the_values_stored_plain_then_kept_exact_on_changes()
    {
        var (builder, processor) = Worker();
        // A busy database (the guard) stops a round; the worker simply continues on its next poll, and so does the test.
        var outcomes = new List<MaterializeBuilder.FieldOutcome>();
        for (var round = 0; round < 5; round++)
        {
            var r = await builder.BuildPendingAsync(_options, CancellationToken.None);
            outcomes.AddRange(r.Where(o => o.Outcome != "busy"));
            if (r.All(o => o.Outcome != "busy")) break;
        }
        outcomes.Where(o => o.Outcome == "ready").Select(o => o.FieldId).Should().BeEquivalentTo([_nameLookupId, _openCountId, _openSumId],
            string.Join("; ", outcomes.Select(o => $"{o.FieldId}={o.Outcome} {o.Reason}")));
        (await WrongValuesAsync()).Should().Be((0, 0, 0));

        await using var conn = await OpenAsync();
        // The stored lookup is the PLAIN name (sortable in SQL), while the source column still holds ciphertext.
        (await conn.ExecuteScalarAsync<string>($"SELECT TOP 1 c_9 FROM data.t_{Ord} WHERE f_6 = 1")).Should().Be(_customerNames[1]);
        (await conn.ExecuteScalarAsync<string>($"SELECT f_6 FROM data.t_{Cust} WHERE Id = 1")).Should().NotBe(_customerNames[1]);

        var outbox = Outbox();
        // An order becomes Open (encrypted status), another moves to customer 19, one is deleted.
        var reopened = _orders.First(o => o.Value.Status == "Closed").Key;
        await conn.ExecuteAsync($"UPDATE data.t_{Ord} SET f_8 = @v WHERE Id = @reopened", new { v = await Enc("Open"), reopened });
        _orders[reopened] = _orders[reopened] with { Status = "Open" };
        await outbox.AddAsync([new ComputeChange(Ord, ComputeChangeReason.Update, RecordId: reopened, ChangedFids: [8])], null);

        var moved = _orders.First(o => o.Value.Status == "Open" && o.Key != reopened).Key;
        var oldCustomer = await conn.ExecuteScalarAsync<long>($"UPDATE data.t_{Ord} SET f_6 = 19 OUTPUT deleted.f_6 WHERE Id = @moved", new { moved });
        _orders[moved] = _orders[moved] with { Customer = 19 };
        await outbox.AddAsync([new ComputeChange(Ord, ComputeChangeReason.Update, RecordId: moved, ChangedFids: [6],
            OldRefs: new Dictionary<int, long?> { [6] = oldCustomer })], null);

        var deleted = _orders.Keys.Last();
        await conn.ExecuteAsync($"UPDATE data.t_{Ord} SET IsDeleted = 1 WHERE Id = @deleted", new { deleted });
        _orders.Remove(deleted);
        await outbox.AddAsync([new ComputeChange(Ord, ComputeChangeReason.Delete, RecordId: deleted)], null);

        await processor.DrainAsync(Ord, _options, CancellationToken.None);
        (await WrongValuesAsync()).Should().Be((0, 0, 0));

        // A customer is renamed (encrypted name): every order's stored lookup follows.
        var renamed = await conn.ExecuteScalarAsync<Guid>($"UPDATE data.t_{Cust} SET f_6 = @v OUTPUT inserted.PublicId WHERE Id = 2", new { v = await Enc("Acme Renamed") });
        _customerNames[2] = "Acme Renamed";
        await outbox.AddAsync([new ComputeChange(Cust, ComputeChangeReason.Update, RecordPublicId: renamed, ChangedFids: [6])], null);
        await processor.DrainAsync(Cust, _options, CancellationToken.None);
        (await WrongValuesAsync()).Should().Be((0, 0, 0));
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Ord} WHERE f_6 = 2 AND IsDeleted = 0 AND c_9 = N'Acme Renamed'")).Should().BeGreaterThan(0);

        // The nightly check finds nothing to fix.
        var checks = await builder.ReconcileAsync(_options, CancellationToken.None);
        checks.Sum(c => c.Differences).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_decryptor_a_field_reading_ciphertext_fails_and_keeps_the_dynamic_path()
    {
        var (builder, _) = Worker(withDecryptor: false);
        var outcomes = await builder.BuildPendingAsync(_options, CancellationToken.None);
        outcomes.Should().Contain(o => o.FieldId == _nameLookupId && o.Outcome != "ready");
        (await StateAsync(_nameLookupId)).State.Should().NotBe(MaterializeStates.Ready);
    }
}
