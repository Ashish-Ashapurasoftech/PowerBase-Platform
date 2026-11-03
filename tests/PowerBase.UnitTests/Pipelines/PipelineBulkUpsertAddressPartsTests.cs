using System.Data;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Address parts (Street 1, Street 2, City, State/Region, Postal Code, Country) through Prepare Bulk Record Upsert → Add a Bulk
/// Upsert Row → Commit Upsert: a row that sets parts of an Address inserts an address made of them, and updates only those parts of
/// a matched record's address. Rows and columns without address parts commit exactly as before.
/// </summary>
public class PipelineBulkUpsertAddressPartsTests
{
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordWriteService _writes = Substitute.For<IRecordWriteService>();
    private readonly IPipelineTriggerInterceptor _interceptor = Substitute.For<IPipelineTriggerInterceptor>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IDbTransaction _tx = Substitute.For<IDbTransaction>();
    private readonly IPipelineStepIdempotencyRepository _idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
    private readonly PipelineEngine _engine;
    private readonly AppTable _table = new() { Id = 10, AppId = 1, PublicId = Guid.NewGuid(), Name = "Customers" };
    private readonly List<AppField> _fields =
    [
        new() { Id = 1, Fid = 3, Name = "Record ID#", TypeCode = "RecordId", PhysicalColumnName = "Id", IsSystem = true },
        new() { Id = 2, Fid = 6, Name = "Email", TypeCode = "Email" },
        new() { Id = 3, Fid = 7, Name = "FullName", TypeCode = "Text" },
        new() { Id = 4, Fid = 37, Name = "Address", TypeCode = "Address" },
    ];
    private IReadOnlyDictionary<long, object?>? _created;
    private IReadOnlyDictionary<long, object?>? _updated;
    private readonly Dictionary<string, object> _context = new();

    public PipelineBulkUpsertAddressPartsTests()
    {
        _uow.Transaction.Returns(_tx);
        _tables.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(_fields);
        _records.CreateAsync(_table, _fields, Arg.Do<IReadOnlyDictionary<long, object?>>(v => _created = new Dictionary<long, object?>(v)), _tx,
            Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>?>()).Returns(Guid.NewGuid());
        _records.GetActiveRecordIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), _tx, Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(1).ToDictionary(g => g, _ => 88L));
        _writes.ApplyAsync(_table, _fields, Arg.Any<Guid>(), Arg.Do<IReadOnlyDictionary<long, object?>>(v => _updated = new Dictionary<long, object?>(v)),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>(), Arg.Any<bool>(),
                Arg.Any<Action<SearchIndexMessage>?>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<Guid?>(), Arg.Any<bool>())
            .Returns(call => call.ArgAt<IReadOnlyDictionary<long, object?>>(3));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IPipelineRecordSearchService)).Returns(Substitute.For<IPipelineRecordSearchService>());
        _engine = new PipelineEngine(Substitute.For<IPipelineRepository>(), _records, _writes, _tables, _fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(),
            _interceptor, _uow, Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(),
            services, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), _idempotency);
    }

    private async Task<string> RunAsync(string refId, string subtype, object config, string payload = "{}")
    {
        var step = new PipelineStep { RefId = refId, Type = "action", Subtype = subtype, ConfigJson = JsonSerializer.Serialize(config) };
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return await (Task<string>)method.Invoke(_engine, new object[]
        {
            step, payload, _context, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(),
            new List<PipelineEngine.RawStepAuditSnapshot>(), "step_test", _records, _tables, _fieldRepo, _writes, _interceptor, _uow, _idempotency,
            Substitute.For<IFileStorageService>(), Substitute.For<IPipelineRecordSearchService>(), CancellationToken.None
        })!;
    }

    private Task PrepareAsync(params string[] fields) =>
        RunAsync("prep", "prepare-bulk-upsert", new { tableLabel = _table.PublicId.ToString(), mergeKeyFid = "fid_6", fields });

    private Task AddRowAsync(Dictionary<string, object?> rowValues, string payload = "{}") =>
        RunAsync("row", "add-bulk-upsert-row", new { bulkRecordSetStepId = "prep", rowValues }, payload);

    private Task<string> CommitAsync() => RunAsync("commit", "commit-upsert", new { bulkRecordSetStepId = "prep" });

    private void ExistingRecord(string? address) =>
        _records.GetBulkUpsertRowsByColumnValuesAsync(_table, _fields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _tx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>
            {
                ["a@x.test"] = new Dictionary<string, object?> { ["Id"] = 77L, ["publicId"] = Guid.NewGuid(), ["f_6"] = "a@x.test", ["f_7"] = "Old Name", ["f_37"] = address },
            });

    private static JsonElement Json(object? value) => JsonDocument.Parse(value!.ToString()!).RootElement;

    [Fact]
    public async Task InsertedRow_WritesTheAddressFromItsParts()
    {
        await PrepareAsync("fid_6", "fid_37.city", "fid_37.zip");
        await AddRowAsync(new() { ["fid_6"] = "new@x.test", ["fid_37.city"] = "{{trigger.city}}", ["fid_37.zip"] = "411001" }, "{\"trigger\":{\"city\":\"Pune\"}}");
        _records.GetBulkUpsertRowsByColumnValuesAsync(_table, _fields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _tx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        var result = JsonSerializer.Deserialize<JsonElement>(await CommitAsync());

        result.GetProperty("inserted_count").GetInt32().Should().Be(1);
        _created![6].Should().Be("new@x.test");
        var address = Json(_created[37]);
        address.GetProperty("city").GetString().Should().Be("Pune");
        address.GetProperty("zip").GetString().Should().Be("411001");
        address.GetProperty("street1").GetString().Should().BeEmpty();
        address.GetProperty("country").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatedRow_ChangesOnlyItsPartsAndKeepsTheRecordsOtherParts()
    {
        await PrepareAsync("fid_6", "fid_37.city");
        await AddRowAsync(new() { ["fid_6"] = "a@x.test", ["fid_37.city"] = "Rajkot" });
        ExistingRecord("{\"street1\":\"A-12\",\"street2\":\"\",\"city\":\"Surat\",\"state\":\"GJ\",\"zip\":\"395007\",\"country\":\"India\",\"lat\":21.2}");

        var result = JsonSerializer.Deserialize<JsonElement>(await CommitAsync());

        result.GetProperty("updated_count").GetInt32().Should().Be(1);
        var address = Json(_updated![37]);
        address.GetProperty("city").GetString().Should().Be("Rajkot");
        address.GetProperty("street1").GetString().Should().Be("A-12");
        address.GetProperty("state").GetString().Should().Be("GJ");
        address.GetProperty("zip").GetString().Should().Be("395007");
        address.GetProperty("country").GetString().Should().Be("India");
        address.GetProperty("lat").GetDouble().Should().Be(21.2);
    }

    [Fact]
    public async Task SelectedPartTheRowLeavesOut_IsWrittenBlank_AndTheOtherPartsAreKept()
    {
        // The editor drops blank values from a row; a selected column is still written for every row.
        await PrepareAsync("fid_6", "fid_37.city", "fid_37.zip");
        await AddRowAsync(new() { ["fid_6"] = "a@x.test", ["fid_37.zip"] = "360001" });
        ExistingRecord("{\"street1\":\"A-12\",\"street2\":\"\",\"city\":\"Surat\",\"state\":\"GJ\",\"zip\":\"395007\",\"country\":\"India\"}");

        await CommitAsync();

        var address = Json(_updated![37]);
        address.GetProperty("zip").GetString().Should().Be("360001");
        address.GetProperty("city").GetString().Should().BeEmpty();
        address.GetProperty("street1").GetString().Should().Be("A-12");
        address.GetProperty("country").GetString().Should().Be("India");
    }

    [Fact]
    public async Task WholeAddressAndAPartInOneRow_ThePartWinsOverTheMappedAddress()
    {
        await PrepareAsync("fid_6", "fid_37", "fid_37.city");
        await AddRowAsync(new()
        {
            ["fid_6"] = "a@x.test", ["fid_37"] = "{\"street1\":\"B-7\",\"city\":\"Pune\",\"country\":\"India\"}", ["fid_37.city"] = "Mumbai",
        });
        ExistingRecord("{\"street1\":\"OLD\",\"city\":\"Old\"}");

        await CommitAsync();

        var address = Json(_updated![37]);
        address.GetProperty("city").GetString().Should().Be("Mumbai");
        address.GetProperty("street1").GetString().Should().Be("B-7");
    }

    [Fact]
    public async Task RowsWithoutAddressParts_CommitExactlyAsBefore()
    {
        await PrepareAsync("fid_6", "fid_7");
        await AddRowAsync(new() { ["fid_6"] = "a@x.test", ["fid_7"] = "New Name" });
        ExistingRecord("{\"city\":\"Surat\"}");

        await CommitAsync();

        _updated!.Keys.Should().BeEquivalentTo(new long[] { 6, 7 });
        _updated[7].Should().Be("New Name");
    }

    [Fact]
    public async Task InsertedRow_WithAllSelectedPartsBlank_LeavesTheAddressEmpty()
    {
        await PrepareAsync("fid_6", "fid_37.city");
        await AddRowAsync(new() { ["fid_6"] = "new@x.test" });
        _records.GetBulkUpsertRowsByColumnValuesAsync(_table, _fields, "f_6", Arg.Any<IReadOnlyCollection<object>>(), _tx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, IReadOnlyDictionary<string, object?>>());

        await CommitAsync();

        _created!.TryGetValue(37, out var address).Should().BeTrue();
        address.Should().BeNull();
    }
}
