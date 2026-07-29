using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Commands.InvokeButtonAction;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Records;

public class InvokeButtonActionHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IRecordWriteService _writeService = Substitute.For<IRecordWriteService>();
    private readonly IActionButtonValueResolver _valueResolver = Substitute.For<IActionButtonValueResolver>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AppTable _table;
    private readonly Guid _recordId = Guid.NewGuid();

    private const int ButtonFid = 10;
    private const int TargetFid = 20;
    private const int BoolGateFid = 30;
    private const int LocationFid = 40;
    private const int IpFid = 50;

    public InvokeButtonActionHandlerTests()
    {
        _table = new AppTable { Id = 1, PublicId = Guid.NewGuid(), Name = "T", AppId = 1 };
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);

        // Allow by default — individual gate tests override the row/settings, not this.
        _enforcer.EnsureButtonWriteAllowedAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlySet<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Echo whatever was written so tests can assert on it.
        _writeService.ApplyAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyDictionary<long, object?>>()));

        _queryContext.IpAddress.Returns("203.0.113.7");

        // Default resolver behavior: 'data' kind returns the literal Data string, matching
        // ActionButtonValueResolver's real behavior — no formula/field resolution needed here.
        _valueResolver.ResolveAsync(
            Arg.Any<ValueSource?>(), Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<FormulaType>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var vs = ci.ArgAt<ValueSource?>(0);
                return Task.FromResult<object?>(vs?.Kind == ValueSourceKinds.Data ? vs.Data : null);
            });
    }

    private InvokeButtonActionCommandHandler CreateSut() => new(
        _tableRepo, _fieldRepo, _recordRepo, _enforcer, _writeService, _valueResolver, _queryContext);

    private AppField ButtonField(ActionButtonSettings settings) => new()
    {
        Id = 100, Fid = ButtonFid, TypeCode = "ActionButton", Name = "Btn", Label = "Btn",
        Settings = JsonSerializer.Serialize(settings, JsonOpts),
    };

    private static AppField PlainField(int fid, string typeCode = "Text") =>
        new() { Id = fid, Fid = fid, TypeCode = typeCode, Name = $"F{fid}" };

    private void SetupFields(AppField button, params AppField[] others)
    {
        var fields = new List<AppField> { button };
        fields.AddRange(others);
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(fields);
    }

    private void SetupRow(IReadOnlyDictionary<string, object?>? row = null)
    {
        _recordRepo.GetByPublicIdAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), _recordId, Arg.Any<CancellationToken>())
            .Returns(row ?? new Dictionary<string, object?>());
    }

    private InvokeButtonActionCommand MakeCommand(
        string? promptValue = null, string? capturedFileRef = null, string? password = null,
        double? geoLat = null, double? geoLng = null, string? geoState = null) =>
        new(_table.PublicId, _recordId, ButtonFid, promptValue, capturedFileRef, password, geoLat, geoLng, geoState, null);

    // ── Link Expiration ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LinkExpiration_PastWindow_ThrowsLinkExpiredException()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            LinkExpiration = new LinkExpirationSettings { Start = new ValueSource { Kind = ValueSourceKinds.Data, Data = "unused" }, Minutes = 60 },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        // Resolver returns a start time 2 hours ago — well past the 60-minute window.
        _valueResolver.ResolveAsync(
            Arg.Is<ValueSource?>(v => v != null && v.Kind == ValueSourceKinds.Data),
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<string, object?>>(),
            FormulaType.DateTime, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<object?>(DateTime.UtcNow.AddHours(-2).ToString("O")));

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(MakeCommand()))
            .Should().ThrowAsync<LinkExpiredException>();

        await _writeService.DidNotReceive().ApplyAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkExpiration_WithinWindow_Succeeds()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            LinkExpiration = new LinkExpirationSettings { Start = new ValueSource { Kind = ValueSourceKinds.Data, Data = "unused" }, Minutes = 60 },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        _valueResolver.ResolveAsync(
            Arg.Is<ValueSource?>(v => v != null && v.Kind == ValueSourceKinds.Data),
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<string, object?>>(),
            FormulaType.DateTime, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<object?>(DateTime.UtcNow.AddMinutes(-5).ToString("O")));

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand());

        result.UpdatedFields[TargetFid].Should().Be("x");
    }

    [Fact]
    public async Task LinkExpiration_StartNotConfigured_FailsClosed_ThrowsLinkExpiredException()
    {
        // Regression: Start left blank (kind='data', no data typed in) must NOT be treated
        // as "no expiration" — that would let a configured expiration silently never expire.
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            LinkExpiration = new LinkExpirationSettings { Start = new ValueSource { Kind = ValueSourceKinds.Data, Data = null }, Minutes = 1 },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        // Resolver behaves exactly like the real ActionButtonValueResolver for an empty
        // 'data' ValueSource: returns null (see constructor default — no override needed).

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(MakeCommand()))
            .Should().ThrowAsync<LinkExpiredException>();

        await _writeService.DidNotReceive().ApplyAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkExpiration_FormulaStartOfNow_NeverExpires_ByDesignOfFreshEvaluation()
    {
        // Reproduces the user's report: Start = formula "Now()", Minutes = 2, clicked well
        // past that window — and it STILL succeeds. This is not a bug in the comparison;
        // it's the mathematical consequence of resolving Start fresh on every click. A
        // formula's Now() maps to EvaluationOptions.UtcNow, captured at THIS click's
        // resolution — so Start is always "this instant" and can never be more than
        // `minutes` in the past relative to itself. Start must be an anchor fixed at some
        // earlier point (a static timestamp, or a stored field) for expiration to mean
        // anything; a live Now() formula is inherently non-expiring under fresh-per-click
        // evaluation. This test locks in that documented behavior so it isn't "fixed" by
        // accident later without deciding on a capture-once mechanism first.
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            LinkExpiration = new LinkExpirationSettings
            {
                Start = new ValueSource { Kind = ValueSourceKinds.Formula, Formula = "Now()" },
                Minutes = 2,
            },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        // Simulate the real resolver's Now() behavior: resolves to "right now", fresh,
        // every time it's asked — exactly like EvaluationOptions.UtcNow = DateTime.UtcNow.
        _valueResolver.ResolveAsync(
            Arg.Is<ValueSource?>(v => v != null && v.Kind == ValueSourceKinds.Formula && v.Formula == "Now()"),
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<string, object?>>(),
            FormulaType.DateTime, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<object?>(DateTime.UtcNow.ToString("O")));

        var sut = CreateSut();

        // "Clicked" three separate times with real wall-clock gaps — still never expires.
        for (var i = 0; i < 3; i++)
        {
            var result = await sut.HandleAsync(MakeCommand());
            result.UpdatedFields[TargetFid].Should().Be("x");
            await Task.Delay(50);
        }
    }

    // ── Bool-Field Gate ────────────────────────────────────────────────────────

    [Fact]
    public async Task BoolGate_False_ThrowsActionGateException()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            BoolGateFid = BoolGateFid,
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid), PlainField(BoolGateFid, "Boolean"));
        SetupRow(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(BoolGateFid)] = false });

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(MakeCommand()))
            .Should().ThrowAsync<ActionGateException>();
    }

    [Fact]
    public async Task BoolGate_True_Succeeds()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            BoolGateFid = BoolGateFid,
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid), PlainField(BoolGateFid, "Boolean"));
        SetupRow(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(BoolGateFid)] = true });

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand());

        result.UpdatedFields[TargetFid].Should().Be("x");
    }

    // ── Type coercion (AddData targeting typed fields) ──────────────────────────

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("False", false)]
    [InlineData("no", false)]
    [InlineData("0", false)]
    public async Task AddData_TargetingBooleanField_CoercesToRealBool(string typed, bool expected)
    {
        // Reproduces the report: admin types "True" into a Data-kind Add Data value for a
        // Boolean target field. Before the fix, the literal string "True" was written AND
        // echoed back in UpdatedFields — which downstream display code (case-sensitive
        // 'true' checks) rendered as unchecked/"No" even though SQL Server's implicit
        // string->bit conversion happened to store it correctly. Now the write, and what
        // we tell the client we wrote, must be a real bool that matches the stored value.
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = typed } }],
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid, "Boolean"));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand());

        result.UpdatedFields[TargetFid].Should().BeOfType<bool>().And.Be(expected);
    }

    [Fact]
    public async Task AddData_TargetingNumberField_CoercesToDecimal()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "42.5" } }],
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid, "Number"));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand());

        result.UpdatedFields[TargetFid].Should().Be(42.5m);
    }

    [Fact]
    public async Task AddData_TargetingTextField_LeavesStringUncoerced()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "Approved" } }],
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid, "Text"));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand());

        result.UpdatedFields[TargetFid].Should().Be("Approved");
    }

    // ── Location Capture ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LocationCapture_WritesLatLngToTargetField()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            LocationCapture = new LocationCaptureSettings { TargetFid = LocationFid },
        };
        SetupFields(ButtonField(settings), PlainField(LocationFid));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand(geoLat: 40.7128, geoLng: -74.0060));

        result.UpdatedFields[LocationFid].Should().Be("40.7128,-74.006");
    }

    [Fact]
    public async Task LocationCapture_RestrictToState_Mismatch_ThrowsActionGateException()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            LocationCapture = new LocationCaptureSettings { TargetFid = LocationFid, RestrictToState = "NY" },
        };
        SetupFields(ButtonField(settings), PlainField(LocationFid));
        SetupRow();

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(MakeCommand(geoLat: 34.0522, geoLng: -118.2437, geoState: "CA")))
            .Should().ThrowAsync<ActionGateException>();
    }

    [Fact]
    public async Task LocationCapture_RestrictToState_Match_Succeeds()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            LocationCapture = new LocationCaptureSettings { TargetFid = LocationFid, RestrictToState = "NY" },
        };
        SetupFields(ButtonField(settings), PlainField(LocationFid));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand(geoLat: 40.7128, geoLng: -74.0060, geoState: "NY"));

        result.UpdatedFields[LocationFid].Should().Be("40.7128,-74.006");
    }

    // ── IP Capture ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IpCapture_WritesQueryContextIpToTargetField()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            IpCaptureFid = IpFid,
        };
        SetupFields(ButtonField(settings), PlainField(IpFid));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand());

        result.UpdatedFields[IpFid].Should().Be("203.0.113.7");
    }

    // ── Password Gate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PasswordGate_Mismatch_ThrowsActionGateException()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            PasswordGate = new ValueSource { Kind = ValueSourceKinds.Data, Data = "correct-horse" },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(MakeCommand(password: "wrong-password")))
            .Should().ThrowAsync<ActionGateException>();
    }

    [Fact]
    public async Task PasswordGate_Match_Succeeds()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            PasswordGate = new ValueSource { Kind = ValueSourceKinds.Data, Data = "correct-horse" },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand(password: "correct-horse"));

        result.UpdatedFields[TargetFid].Should().Be("x");
    }

    [Fact]
    public async Task PasswordGate_ConfiguredButExpectedResolvesBlank_TreatedAsNoGate_Succeeds()
    {
        // Reproduces the report: PasswordGate object exists (kind='data') but its Data was
        // left blank — this must mean "no gate", not "the password must be blank/typed".
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            PasswordGate = new ValueSource { Kind = ValueSourceKinds.Data, Data = null },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        var sut = CreateSut();
        var result = await sut.HandleAsync(MakeCommand()); // no password supplied

        result.UpdatedFields[TargetFid].Should().Be("x");
    }

    [Fact]
    public async Task PasswordGate_NoPasswordSupplied_ThrowsActionGateException()
    {
        var settings = new ActionButtonSettings
        {
            Variant = ActionButtonVariants.Data,
            AddData = [new AddDataItem { TargetFid = TargetFid, Value = new ValueSource { Kind = ValueSourceKinds.Data, Data = "x" } }],
            PasswordGate = new ValueSource { Kind = ValueSourceKinds.Data, Data = "correct-horse" },
        };
        SetupFields(ButtonField(settings), PlainField(TargetFid));
        SetupRow();

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(MakeCommand()))
            .Should().ThrowAsync<ActionGateException>();
    }
}
