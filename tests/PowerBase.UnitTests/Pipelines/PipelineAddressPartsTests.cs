using System.Data;
using System.Reflection;
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
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The parts of an Address field (Street 1, Street 2, City, State/Region, Postal Code, Country) in PowerFlows: a token
/// <c>{{…fid_N.city}}</c> reads one part of the stored address JSON, and a Create / Update Record mapping to <c>fid_N.city</c>
/// writes one part — on update keeping the record's other parts. Every other token and mapping is unchanged.
/// </summary>
public class PipelineAddressPartsTests
{
    private static readonly AppTable Table = new() { Id = 100, AppId = 1, PublicId = Guid.NewGuid() };
    private const string Pune = "{\"street1\":\"1 MG Road\",\"street2\":\"Wing B\",\"city\":\"Pune\",\"state\":\"MH\",\"zip\":\"411001\",\"country\":\"India\"}";

    private static List<AppField> Fields() => new()
    {
        new() { Id = 1, Fid = 6, Name = "Name", TypeCode = "Text" },
        new() { Id = 2, Fid = 7, Name = "Notes", TypeCode = "Text" },
        new() { Id = 3, Fid = 37, Name = "Address", TypeCode = "Address" },
        new() { Id = 4, Fid = 8, Name = "Amount", TypeCode = "Number" },
    };

    private sealed class Harness
    {
        public IRecordRepository Records { get; } = Substitute.For<IRecordRepository>();
        public IRecordWriteService Writes { get; } = Substitute.For<IRecordWriteService>();
        public IReadOnlyDictionary<long, object?>? Created { get; set; }
        public IReadOnlyDictionary<long, object?>? Updated { get; set; }
    }

    private static async Task<Harness> RunAsync(string subtype, object[] mappings, string payload, string? existingAddress = null)
    {
        var h = new Harness();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        var idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
        var interceptor = Substitute.For<IPipelineTriggerInterceptor>();
        tableRepo.GetByPublicIdAsync(Table.PublicId, Arg.Any<CancellationToken>()).Returns(Table);
        fieldRepo.ListByTableAsync(Table.Id, Arg.Any<CancellationToken>()).Returns(Fields());
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        idempotency.GetByExecutionKeyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<byte[]>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var created = Guid.NewGuid();
        h.Records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Do<IReadOnlyDictionary<long, object?>>(v => h.Created = v),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>>()).Returns(created);
        h.Records.GetActiveRecordIdByPublicIdAsync(Arg.Any<AppTable>(), Arg.Any<Guid>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(19L);
        var target = Guid.NewGuid();
        h.Records.GetByPublicIdAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), target, Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["f_37"] = existingAddress, ["f_6"] = "Ronak" });
        h.Writes.ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), target, Arg.Do<IReadOnlyDictionary<long, object?>>(v => h.Updated = v),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>(), Arg.Any<bool>(),
                Arg.Any<Action<SearchIndexMessage>?>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<Guid?>(), Arg.Any<bool>())
            .Returns(call => call.ArgAt<IReadOnlyDictionary<long, object?>>(3));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), h.Records, h.Writes, tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), interceptor, uow, Substitute.For<IPipelineAuditFormatter>(),
            Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), idempotency);

        var trigger = new PipelineStep { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "record-added", ConfigJson = $"{{\"tableId\":\"{Table.PublicId}\"}}" };
        var step = new PipelineStep { Id = 2, RefId = "ref_step", Type = "action", Subtype = subtype,
            ConfigJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["TableId"] = Table.PublicId, ["TargetRecordId"] = target.ToString(), ["FieldMappings"] = mappings,
            }) };
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task<string>)method.Invoke(engine, new object[]
        {
            step, payload, new Dictionary<string, object> { ["_MessageId"] = Guid.NewGuid() }, new List<PipelineStep> { trigger, step },
            new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            h.Records, tableRepo, fieldRepo, h.Writes, interceptor, uow, idempotency,
            Substitute.For<IFileStorageService>(), Substitute.For<IPipelineRecordSearchService>(), CancellationToken.None
        })!;
        return h;
    }

    private static object Map(string field, string value) => new { Field = field, Value = value };

    private static string TriggerWithAddress(string? address = Pune) =>
        JsonSerializer.Serialize(new { trigger = new Dictionary<string, object?> { ["fid_37"] = address, ["fid_6"] = "Ronak" } });

    private static JsonElement Address(object? stored) => JsonDocument.Parse(stored!.ToString()!).RootElement;

    // ───────────────────────── reading a part: {{…fid_N.part}} ─────────────────────────

    [Theory]
    [InlineData("street1", "1 MG Road")]
    [InlineData("street2", "Wing B")]
    [InlineData("city", "Pune")]
    [InlineData("state", "MH")]
    [InlineData("zip", "411001")]
    [InlineData("country", "India")]
    public async Task PartToken_ReadsThatPartOfThePreviousStepsAddress(string part, string expected)
    {
        var h = await RunAsync("create-record", [Map("fid_7", $"{{{{trigger.fid_37.{part}}}}}")], TriggerWithAddress());

        h.Created![7].Should().Be(expected);
    }

    [Fact]
    public async Task PartToken_OfAStepOutput_InText_IsReplacedInPlace()
    {
        var payload = JsonSerializer.Serialize(new { trigger = new { fid_6 = "x" }, steps = new Dictionary<string, object> { ["ref_look"] = new Dictionary<string, object?> { ["fid_37"] = Pune } } });

        var h = await RunAsync("create-record", [Map("fid_7", "{{steps.ref_look.fid_37.city}}, {{steps.ref_look.fid_37.country}}")], payload);

        h.Created![7].Should().Be("Pune, India");
    }

    [Fact]
    public async Task PartToken_OfABlankAddress_WritesNothing()
    {
        var h = await RunAsync("create-record", [Map("fid_7", "{{trigger.fid_37.city}}")], TriggerWithAddress(null));

        h.Created!.Should().NotContainKey(7);
    }

    [Fact]
    public async Task WholeAddressAndOtherTokens_AreUnchanged()
    {
        var h = await RunAsync("create-record", [Map("fid_7", "{{trigger.fid_6}}"), Map("fid_37", "{{trigger.fid_37}}")], TriggerWithAddress());

        h.Created![7].Should().Be("Ronak");
        Address(h.Created[37]).GetProperty("city").GetString().Should().Be("Pune");
    }

    // ───────────────────────── writing a part: fid_N.part ─────────────────────────

    [Fact]
    public async Task CreateRecord_WritesTheMappedParts_AndLeavesTheOthersEmpty()
    {
        var h = await RunAsync("create-record", [Map("fid_37.city", "{{trigger.fid_6}}"), Map("fid_37.zip", "395007"), Map("fid_6", "Name")], TriggerWithAddress());

        var address = Address(h.Created![37]);
        address.GetProperty("city").GetString().Should().Be("Ronak");
        address.GetProperty("zip").GetString().Should().Be("395007");
        address.GetProperty("street1").GetString().Should().BeEmpty();
        address.GetProperty("country").GetString().Should().BeEmpty();
        h.Created[6].Should().Be("Name");
    }

    [Fact]
    public async Task CreateRecord_WholeAddressAndAPart_ThePartWinsAndTheRestIsKept()
    {
        var h = await RunAsync("create-record", [Map("fid_37", "{{trigger.fid_37}}"), Map("fid_37.city", "Surat")], TriggerWithAddress());

        var address = Address(h.Created![37]);
        address.GetProperty("city").GetString().Should().Be("Surat");
        address.GetProperty("street1").GetString().Should().Be("1 MG Road");
        address.GetProperty("country").GetString().Should().Be("India");
    }

    [Fact]
    public async Task UpdateRecord_ChangesOnlyTheMappedPart_AndKeepsTheRecordsOtherParts()
    {
        var existing = "{\"street1\":\"A-12\",\"street2\":\"\",\"city\":\"Old City\",\"state\":\"GJ\",\"zip\":\"360001\",\"country\":\"India\",\"lat\":22.3,\"lng\":70.8}";

        var h = await RunAsync("update-record", [Map("fid_37.city", "Rajkot")], TriggerWithAddress(), existing);

        var address = Address(h.Updated![37]);
        address.GetProperty("city").GetString().Should().Be("Rajkot");
        address.GetProperty("street1").GetString().Should().Be("A-12");
        address.GetProperty("state").GetString().Should().Be("GJ");
        address.GetProperty("zip").GetString().Should().Be("360001");
        address.GetProperty("country").GetString().Should().Be("India");
        address.GetProperty("lat").GetDouble().Should().Be(22.3);
        h.Updated.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateRecord_BlankPartValue_LeavesTheAddressAlone()
    {
        var h = await RunAsync("update-record", [Map("fid_37.city", ""), Map("fid_6", "New")], TriggerWithAddress(), Pune);

        h.Updated.Should().NotContainKey(37);
        h.Updated![6].Should().Be("New");
    }

    [Fact]
    public async Task UpdateRecord_OrdinaryMappings_AreUnchanged()
    {
        var h = await RunAsync("update-record", [Map("fid_6", "{{trigger.fid_6}}"), Map("fid_8", "12.5")], TriggerWithAddress(), Pune);

        h.Updated![6].Should().Be("Ronak");
        Convert.ToDecimal(h.Updated[8]).Should().Be(12.5m);
        h.Updated.Should().NotContainKey(37);
        await h.Records.DidNotReceive().GetByPublicIdAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartOfAFieldThatIsNotAnAddress_FailsTheStep()
    {
        var act = () => RunAsync("create-record", [Map("fid_6.city", "x")], TriggerWithAddress());

        (await act.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(e =>
            e is PipelineNonRetryableException || e.InnerException is PipelineNonRetryableException);
    }

    // ───────────────────────── merge rules ─────────────────────────

    [Fact]
    public void Merge_InvalidOrEmptyAddress_StartsFromTheSixParts()
    {
        var merged = JsonDocument.Parse(PipelineAddressMappings.Merge("not json", new Dictionary<string, string> { ["city"] = "Pune" })).RootElement;

        merged.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["street1", "street2", "city", "state", "zip", "country"]);
        merged.GetProperty("city").GetString().Should().Be("Pune");
    }

    [Theory]
    [InlineData("fid_37.city", true)]
    [InlineData("fid_37.street1", true)]
    [InlineData("fid_37.ZIP", true)]
    [InlineData("fid_37", false)]
    [InlineData("fid_37.lat", false)]
    [InlineData("fid_37.city.x", false)]
    [InlineData("Address.city", false)]
    public void IsPartReference_AcceptsOnlyFidAndAKnownPart(string reference, bool expected) =>
        PipelineAddressMappings.IsPartReference(reference).Should().Be(expected);

    // ───────────────────────── save validation ─────────────────────────

    private static SavePipelineStepsCommand Save(string subtype, object config) => new(Guid.NewGuid(),
        [
            new SavePipelineStepDto { RefId = "ref_aa", Type = "trigger", Subtype = "new-event", IsValidated = true, ConfigJson = "{}" },
            new SavePipelineStepDto { RefId = "ref_ab", Type = "action", Subtype = subtype, IsValidated = true, ConfigJson = JsonSerializer.Serialize(config) },
        ], [1]);

    [Fact]
    public void SaveValidator_AcceptsAddressPartMappingsAndFields()
    {
        var result = new SavePipelineStepsCommandValidator().Validate(Save("update-record", new
        {
            targetRecordId = "{{trigger.RecordPublicId}}",
            fieldMappings = new[] { new { field = "fid_37.city", value = "x" }, new { field = "fid_6", value = "y" } },
            subsequentFields = new[] { "fid_37.zip", "fid_6" },
        }));

        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains("stable FID"));
    }

    [Fact]
    public void SaveValidator_RejectsAnUnknownAddressPart()
    {
        var result = new SavePipelineStepsCommandValidator().Validate(Save("update-record", new
        {
            targetRecordId = "{{trigger.RecordPublicId}}",
            fieldMappings = new[] { new { field = "fid_37.lat", value = "x" } },
        }));

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("fid_37.lat"));
    }

    private static PipelineStepValidator StepValidator(IAppTableRepository tables, IAppFieldRepository fields) =>
        new(Substitute.For<IPipelineRepository>(), Substitute.For<IAppRepository>(), tables, fields, Substitute.For<IAppAccessService>(),
            Substitute.For<ITenantRepository>(), Substitute.For<IQueryContext>());

    private static (IAppTableRepository, IAppFieldRepository) Repos()
    {
        var tables = Substitute.For<IAppTableRepository>();
        var fields = Substitute.For<IAppFieldRepository>();
        tables.GetByPublicIdAsync(Table.PublicId, Arg.Any<CancellationToken>()).Returns(Table);
        fields.ListByTableAsync(Table.Id, Arg.Any<CancellationToken>()).Returns(Fields());
        return (tables, fields);
    }

    private static string StepConfig(params string[] fields) => JsonSerializer.Serialize(new
    {
        tableId = Table.PublicId.ToString(),
        fieldMappings = fields.Select(f => new { field = f, value = "x" }),
    });

    [Fact]
    public async Task StepValidator_PartOfAnAddressField_Passes()
    {
        var (tables, fields) = Repos();

        await StepValidator(tables, fields).ValidateAddressPartMappingsAsync(StepConfig("fid_37.city", "fid_6"), CancellationToken.None);
    }

    [Theory]
    [InlineData("fid_6.city")]   // a Text field
    [InlineData("fid_99.city")]  // no such field
    [InlineData("fid_37.lat")]   // not a part
    public async Task StepValidator_InvalidAddressPart_IsRejected(string reference)
    {
        var (tables, fields) = Repos();

        var act = () => StepValidator(tables, fields).ValidateAddressPartMappingsAsync(StepConfig(reference), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task StepValidator_StepsWithoutAddressParts_AreNotRead()
    {
        var (tables, fields) = Repos();

        await StepValidator(tables, fields).ValidateAddressPartMappingsAsync(StepConfig("fid_6", "fid_7"), CancellationToken.None);

        await tables.DidNotReceive().GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
