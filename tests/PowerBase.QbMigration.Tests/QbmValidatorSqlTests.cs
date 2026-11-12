using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

public class QbmValidatorPickTests
{
    private static readonly List<QbRange> Ten = Enumerable.Range(0, 10).Select(i => new QbRange(i * 10 + 1, i * 10 + 11)).ToList();

    [Fact]
    public void Sample_one_checks_every_chunk() => QbmValidator.Pick(Ten, 1).Should().HaveCount(10);

    [Fact]
    public void A_sample_checks_an_even_spread_including_the_last_chunk()
    {
        var picked = QbmValidator.Pick(Ten, 0.25);
        picked.Select(r => r.From).Should().Equal(1, 41, 81, 91);
    }
}

/// <summary>Validation against tempdb: load a chunk through the real processor, then break things on purpose.</summary>
public sealed class QbmValidatorSqlTests : IAsyncLifetime
{
    private const long App = 7_201, Tenant = 78;
    private readonly long _parentId = 980_000 + Random.Shared.Next(4_000);
    private long ChildId => _parentId + 5_000;
    private readonly SqlDb.Factory _factory = new();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private QbmStateStore _state = null!;

    private sealed class FakeQuickBase(List<QbRow> rows) : IQuickBaseClient
    {
        public List<QbRow> Rows { get; } = rows;
        public Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default)
        {
            var page = Rows.Skip(skip).Take(2).ToList();
            return Task.FromResult(new QbQueryPage(page, Rows.Count, page.Count, skip));
        }
    }

    public async Task InitializeAsync()
    {
        // Ciphertext must look real for gate 7: Base64 that only this fake key can open.
        _encryption.GenerateAndWrapDekAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns("dek");
        _encryption.EncryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci => Convert.ToBase64String(Encoding.UTF8.GetBytes("E:" + ci.ArgAt<string>(0))));
        _encryption.DecryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(ci.ArgAt<string>(0)));
                return text.StartsWith("E:") ? text[2..] : throw new InvalidOperationException("wrong key");
            });
        _state = new QbmStateStore(_factory);

        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
            IF SCHEMA_ID('data') IS NULL EXEC('CREATE SCHEMA data');
            IF SCHEMA_ID('core') IS NULL EXEC('CREATE SCHEMA core');
            IF OBJECT_ID('meta.App') IS NULL CREATE TABLE meta.App (Id BIGINT PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, SecurityOptions NVARCHAR(MAX) NULL, IsEncrypted BIT NOT NULL DEFAULT 0);
            IF OBJECT_ID('meta.AppTable') IS NULL CREATE TABLE meta.AppTable (Id BIGINT PRIMARY KEY, RecordCount INT NOT NULL DEFAULT 0);
            IF COL_LENGTH('meta.AppTable', 'KeyFieldId') IS NULL ALTER TABLE meta.AppTable ADD KeyFieldId BIGINT NULL;
            IF OBJECT_ID('meta.Relationship') IS NULL CREATE TABLE meta.Relationship (AppId BIGINT, ParentTableId BIGINT, ChildTableId BIGINT, ReferenceFid INT, DisplayKeyFieldId BIGINT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            IF OBJECT_ID('core.FieldType') IS NULL CREATE TABLE core.FieldType (Id INT PRIMARY KEY, Code NVARCHAR(50));
            IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Id = 6) INSERT INTO core.FieldType VALUES (6, 'Number');
            IF OBJECT_ID('meta.AppField') IS NULL CREATE TABLE meta.AppField (Id BIGINT IDENTITY PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
                AppTableId BIGINT, FieldTypeId INT, Name NVARCHAR(200), Label NVARCHAR(200) NULL, Description NVARCHAR(MAX) NULL, PhysicalColumnName NVARCHAR(100) NULL,
                IsSystem BIT NOT NULL DEFAULT 0, IsUnique BIT NOT NULL DEFAULT 0, IsEncrypted BIT NOT NULL DEFAULT 0, IsHidden BIT NOT NULL DEFAULT 0,
                IsSearchable BIT NOT NULL DEFAULT 0, IsFilterable BIT NOT NULL DEFAULT 0, Fid INT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            DELETE FROM meta.App WHERE Id = 7201;
            INSERT INTO meta.App (Id, IsEncrypted) VALUES (7201, 1);
            """);
        await _state.InitAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "001_qbm_state.sql")));
        foreach (var id in new[] { _parentId, ChildId })
            await c.ExecuteAsync($"""
                CREATE TABLE data.t_{id} (
                    Id BIGINT IDENTITY(1,1) NOT NULL, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(), IsDeleted BIT NOT NULL DEFAULT 0,
                    CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CreatedBy BIGINT NOT NULL DEFAULT 0, ModifiedOn DATETIME2(3) NULL,
                    ModifiedBy BIGINT NULL, RowVersion ROWVERSION NOT NULL, CONSTRAINT PK_t_{id} PRIMARY KEY CLUSTERED (Id),
                    f_6 VARCHAR(MAX) NULL, f_7 DECIMAL(18,4) NULL, f_8 BIGINT NULL);
                INSERT INTO meta.AppTable (Id) VALUES ({id});
                INSERT INTO meta.AppField (AppTableId, FieldTypeId, Name, Fid, IsSystem, PhysicalColumnName) VALUES ({id}, 6, 'Record ID#', 3, 1, 'Id');
                """);
        await c.ExecuteAsync("INSERT INTO meta.Relationship (AppId, ParentTableId, ChildTableId, ReferenceFid) VALUES (@App, @p, @ch, 8)",
            new { App, p = _parentId, ch = ChildId });
        await c.ExecuteAsync($"SET IDENTITY_INSERT data.t_{_parentId} ON; INSERT INTO data.t_{_parentId} (Id) VALUES (1), (2); SET IDENTITY_INSERT data.t_{_parentId} OFF;");
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync($"""
            DROP TABLE IF EXISTS data.t_{_parentId}; DROP TABLE IF EXISTS data.t_{ChildId};
            DELETE FROM meta.AppTable WHERE Id IN ({_parentId}, {ChildId});
            DELETE FROM meta.AppField WHERE AppTableId IN ({_parentId}, {ChildId});
            DELETE FROM meta.Relationship WHERE AppId = 7201;
            DELETE FROM qbm.Chunk WHERE TableMapId = {ChildId};
            DELETE FROM meta.App WHERE Id = 7201;
            """);
    }

    private QbmTableContext Context()
    {
        List<AppField> pb = [.. SystemFields(), Pb(6, "Text", "Name"), Pb(7, "Number", "Qty"), Pb(8, "Reference", "Parent")];
        List<QbField> qb = [.. QbSystemFields(), new(6, "Name", "text", null, []), new(7, "Qty", "numeric", null, []), new(8, "Parent", "numeric", null, [])];
        return new QbmTableContext(Tenant, 1, ChildId, "bqchild", new AppTable { Id = ChildId, AppId = App, Name = "Child" }, pb,
            QbFieldMapper.Map(qb, pb), new Dictionary<int, QbmPowerBaseReader.PbReference>(),
            new QbTransformOptions { FallbackUserId = 5 });
    }

    private static List<QbRow> Records() =>
    [
        Row(11, (6, "\"secret one\""), (7, "1.5"), (8, "1"), (1, "\"2020-01-01T00:00:00Z\"")),
        Row(12, (6, "\"secret two\""), (7, "2"), (8, "2"), (1, "\"2020-01-01T00:00:00Z\"")),
        Row(15, (6, "\"secret three\""), (7, "-3"), (8, "1"), (1, "\"2020-01-01T00:00:00Z\"")),
    ];

    private QbmPowerBaseReader Reader() => new(_factory, () => new SqlConnection(SqlDb.ConnectionString));

    private async Task<FakeQuickBase> LoadAsync()
    {
        var qb = new FakeQuickBase(Records());
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var chunk = await c.ExecuteScalarAsync<long>(
            "INSERT INTO qbm.Chunk (RunId, TableMapId, FromRecordId, ToRecordId) OUTPUT INSERTED.Id VALUES (1, @t, 10, 20)", new { t = ChildId });
        await new QbmChunkProcessor(qb, new QbmChunkLoader(_factory, _encryption), _state, Reader(), NullLogger<QbmChunkProcessor>.Instance)
            .ProcessAsync(Context(), chunk, new QbRange(10, 20));
        return qb;
    }

    private QbmValidator Validator(IQuickBaseClient qb) => new(qb, _factory, _encryption, Reader());

    private Task<QbmValidator.TableResult> CompareAsync(IQuickBaseClient qb) => Validator(qb).CompareTableAsync(Context(), [new QbRange(10, 20)], 1);

    private async Task SqlAsync(string sql)
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync(sql);
    }

    [SqlFact]
    public async Task A_clean_load_passes_every_gate()
    {
        var qb = await LoadAsync();
        var result = await CompareAsync(qb);
        result.Should().Match<QbmValidator.TableResult>(r => r.Records == 3 && r.Compared == 3 && r.Gate3Passed && r.Gate7Passed);
        (await Validator(qb).OrphanReferencesAsync(App, ChildId)).Should().BeEmpty();
    }

    [SqlFact]
    public async Task A_changed_value_is_found_and_named()
    {
        var qb = await LoadAsync();
        await SqlAsync($"UPDATE data.t_{ChildId} SET f_7 = 99 WHERE Id = 12");
        var result = await CompareAsync(qb);
        result.Gate3Passed.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == "QBM_GATE3_MISMATCH" && i.QbRecordId == 12 && i.Message.Contains("QuickBase '2', PowerBase '99'"));
    }

    [SqlFact]
    public async Task An_encrypted_value_that_differs_is_reported_without_showing_it()
    {
        var qb = await LoadAsync();
        await SqlAsync($"UPDATE data.t_{ChildId} SET f_6 = '{Convert.ToBase64String(Encoding.UTF8.GetBytes("E:tampered"))}' WHERE Id = 11");
        var issue = (await CompareAsync(qb)).Issues.Single(i => i.Code == "QBM_GATE3_MISMATCH");
        issue.Message.Should().Contain("values not shown").And.NotContain("tampered").And.NotContain("secret");
    }

    [SqlFact]
    public async Task Plain_text_in_an_encrypted_column_fails_gate_7()
    {
        var qb = await LoadAsync();
        await SqlAsync($"UPDATE data.t_{ChildId} SET f_6 = 'secret one' WHERE Id = 11");
        var result = await CompareAsync(qb);
        result.Gate7Passed.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Code == "QBM_GATE7_NOT_ENCRYPTED" && i.QbRecordId == 11);
    }

    [SqlFact]
    public async Task Missing_and_extra_records_are_found()
    {
        var qb = await LoadAsync();
        qb.Rows.RemoveAt(0); // record 11 deleted in QuickBase after the load
        var result = await CompareAsync(qb);
        result.Missing.Should().Be(0);
        result.Extra.Should().Be(1);
        result.Issues.Should().Contain(i => i.Code == "QBM_GATE3_EXTRA_RECORD" && i.QbRecordId == 11);

        qb.Rows.Insert(0, Records()[0]);
        await SqlAsync($"DELETE FROM data.t_{ChildId} WHERE Id = 15");
        result = await CompareAsync(qb);
        result.Extra.Should().Be(0);
        result.Missing.Should().Be(1);
        result.Issues.Should().Contain(i => i.Code == "QBM_GATE3_MISSING_RECORD" && i.QbRecordId == 15);
    }

    [SqlFact]
    public async Task A_reference_to_a_missing_parent_fails_gate_4()
    {
        var qb = await LoadAsync();
        await SqlAsync($"DELETE FROM data.t_{_parentId} WHERE Id = 2");
        var orphans = await Validator(qb).OrphanReferencesAsync(App, ChildId);
        orphans.Should().ContainSingle(i => i.Code == "QBM_GATE4_ORPHAN_REFERENCE" && i.QbRecordId == 12 && i.QbFid == 8);
    }
}
