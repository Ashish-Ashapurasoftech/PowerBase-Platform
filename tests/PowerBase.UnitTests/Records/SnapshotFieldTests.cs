using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Fields;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Relationships;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Records;

public class SnapshotFieldTests
{
    private static AppField Field(long id, int fid, string typeCode, string name, int? snapshotSource = null, bool editable = false, string? settings = null) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), AppTableId = 5, Fid = fid, TypeCode = typeCode, Name = name, Label = name,
        PhysicalColumnName = $"f_{fid}", SnapshotSourceFid = snapshotSource, SnapshotEditable = editable, Settings = settings,
    };

    private static readonly AppTable Table = new() { Id = 5, PublicId = Guid.NewGuid(), Name = "Orders", AppId = 1 };

    // ── Capability: configuration rules ───────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoSource_IsAlwaysAllowed()
    {
        var dest = Field(1, 10, "Text", "Snap");
        FieldSnapshotCapability.Validate(dest, "Snap", null, false, true, true, true, true, [dest]).Should().BeEmpty();
    }

    [Fact]
    public void Validate_CompatibleStoredSource_IsAllowed()
    {
        var source = Field(1, 10, "Number", "Price");
        var dest = Field(2, 11, "Number", "Price at order");
        FieldSnapshotCapability.Validate(dest, "Price at order", 10, false, false, false, false, false, [source, dest]).Should().BeEmpty();
    }

    [Fact]
    public void Validate_UnsupportedDestinationType_IsRejected()
    {
        var source = Field(1, 10, "Text", "Name");
        var dest = Field(2, 11, "SingleSelect", "Pick");
        FieldSnapshotCapability.Validate(dest, "Pick", 10, false, false, false, false, false, [source, dest]).Should().ContainKey("SnapshotSourceFid");
    }

    [Fact]
    public void Validate_SourceIsItself_IsRejected()
    {
        var dest = Field(2, 11, "Text", "Self");
        FieldSnapshotCapability.Validate(dest, "Self", 11, false, false, false, false, false, [dest]).Should().ContainKey("SnapshotSourceFid");
    }

    [Fact]
    public void Validate_IncompatibleSourceType_IsRejected()
    {
        var source = Field(1, 10, "Boolean", "Flag");
        var dest = Field(2, 11, "Date", "When");
        FieldSnapshotCapability.Validate(dest, "When", 10, false, false, false, false, false, [source, dest]).Should().ContainKey("SnapshotSourceFid");
    }

    [Fact]
    public void Validate_FormulaSource_IsRejected()
    {
        var source = Field(1, 10, "Formula", "Calc");
        var dest = Field(2, 11, "Text", "Snap");
        FieldSnapshotCapability.Validate(dest, "Snap", 10, false, false, false, false, false, [source, dest]).Should().ContainKey("SnapshotSourceFid");
    }

    [Fact]
    public void Validate_LookupSourceWithMatchingResultType_IsAllowed()
    {
        var lookup = Field(1, 10, "Lookup", "Product price", settings: """{"resultTypeCode":"Currency","sourceTableId":9,"referenceFid":8,"sourceFid":7}""");
        var dest = Field(2, 11, "Currency", "Price at order");
        FieldSnapshotCapability.Validate(dest, "Price at order", 10, false, false, false, false, false, [lookup, dest]).Should().BeEmpty();
    }

    [Fact]
    public void Validate_UniqueEncryptedAutoFill_AreRejected()
    {
        var source = Field(1, 10, "Text", "Name");
        var dest = Field(2, 11, "Text", "Snap");
        var errors = FieldSnapshotCapability.Validate(dest, "Snap", 10, false, false, true, true, true, [source, dest]);
        errors.Should().ContainKeys("IsUnique", "IsEncrypted", "IsAutoFill");
    }

    [Fact]
    public void Validate_RequiredOnlyAllowedWhenEditable()
    {
        var source = Field(1, 10, "Text", "Name");
        var dest = Field(2, 11, "Text", "Snap");
        FieldSnapshotCapability.Validate(dest, "Snap", 10, false, true, false, false, false, [source, dest]).Should().ContainKey("IsRequired");
        FieldSnapshotCapability.Validate(dest, "Snap", 10, true, true, false, false, false, [source, dest]).Should().BeEmpty();
    }

    // ── Write guard ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureNotSuppliedOnCreate_ValueForReadOnlySnapshot_Throws()
    {
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10);
        var values = new Dictionary<long, object?> { [11] = "typed by hand" };
        var act = () => SnapshotWriteGuard.EnsureNotSuppliedOnCreate([snap], values);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void EnsureNotSuppliedOnCreate_BlankValueIsDroppedNotRejected()
    {
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10);
        var values = new Dictionary<long, object?> { [11] = "" };
        SnapshotWriteGuard.EnsureNotSuppliedOnCreate([snap], values);
        values.Should().NotContainKey(11);
    }

    [Fact]
    public void EnsureNotSuppliedOnCreate_EditableSnapshot_AcceptsValue()
    {
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10, editable: true);
        var values = new Dictionary<long, object?> { [11] = "typed by hand" };
        SnapshotWriteGuard.EnsureNotSuppliedOnCreate([snap], values);
        values.Should().ContainKey(11);
    }

    [Fact]
    public void EnsureUnchanged_ChangedValue_Throws_UnchangedValue_Passes()
    {
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10);
        var submitted = new Dictionary<long, object?> { [11] = "new" };

        var changed = () => SnapshotWriteGuard.EnsureUnchanged([snap], submitted, (_, _) => false);
        changed.Should().Throw<ValidationException>();

        SnapshotWriteGuard.EnsureUnchanged([snap], submitted, (_, _) => true);
    }

    [Fact]
    public void EnsureNotWritten_ReadOnlySnapshot_Throws_OrdinaryField_Passes()
    {
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10);
        var plain = Field(3, 12, "Text", "Plain");
        var act = () => SnapshotWriteGuard.EnsureNotWritten([snap, plain], [11L]);
        act.Should().Throw<ValidationException>();
        SnapshotWriteGuard.EnsureNotWritten([snap, plain], [12L]);
    }

    // ── Capture ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyOnCreate_StoredSource_CopiesTheValueOnce()
    {
        var source = Field(1, 10, "Text", "Name");
        var snap = Field(2, 11, "Text", "Name at creation", snapshotSource: 10);
        var sut = new SnapshotCaptureService(Substitute.For<IRelationalProjector>());
        var values = new Dictionary<long, object?> { [10] = "Acme" };

        await sut.ApplyOnCreateAsync(Table, [source, snap], values);

        values[11].Should().Be("Acme");
    }

    [Fact]
    public async Task ApplyOnCreate_BlankSource_WritesBlank()
    {
        var source = Field(1, 10, "Text", "Name");
        var snap = Field(2, 11, "Text", "Name at creation", snapshotSource: 10);
        var sut = new SnapshotCaptureService(Substitute.For<IRelationalProjector>());
        var values = new Dictionary<long, object?>();

        await sut.ApplyOnCreateAsync(Table, [source, snap], values);

        values.Should().ContainKey(11);
        values[11].Should().BeNull();
    }

    [Fact]
    public async Task ApplyOnCreate_NumberSourceIntoTextField_ConvertsTheValue()
    {
        var source = Field(1, 10, "Number", "Qty");
        var snap = Field(2, 11, "Text", "Qty text", snapshotSource: 10);
        var sut = new SnapshotCaptureService(Substitute.For<IRelationalProjector>());
        var values = new Dictionary<long, object?> { [10] = 12.5m };

        await sut.ApplyOnCreateAsync(Table, [source, snap], values);

        values[11].Should().Be("12.5");
    }

    [Fact]
    public async Task ApplyOnCreate_LookupSource_ReadsTheProjectedValue()
    {
        var reference = Field(1, 8, "Reference", "Product");
        var lookup = Field(2, 10, "Lookup", "Product price", settings: """{"resultTypeCode":"Number","sourceTableId":9,"referenceFid":8,"sourceFid":7}""");
        var snap = Field(3, 11, "Number", "Price at order", snapshotSource: 10);

        var projector = Substitute.For<IRelationalProjector>();
        projector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // The synthetic row must carry the reference value the new record is about to be written with.
                var rows = call.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(2);
                rows[0]["f_8"].Should().Be(42L);
                return (IReadOnlyList<IReadOnlyDictionary<long, object?>>)new List<IReadOnlyDictionary<long, object?>>
                {
                    new Dictionary<long, object?> { [10] = 99.5m },
                };
            });
        var sut = new SnapshotCaptureService(projector);
        var values = new Dictionary<long, object?> { [8] = 42L };

        await sut.ApplyOnCreateAsync(Table, [reference, lookup, snap], values);

        values[11].Should().Be(99.5m);
    }

    [Fact]
    public async Task ApplyOnCreate_EditableSnapshotWithSuppliedValue_KeepsIt()
    {
        var source = Field(1, 10, "Text", "Name");
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10, editable: true);
        var sut = new SnapshotCaptureService(Substitute.For<IRelationalProjector>());
        var values = new Dictionary<long, object?> { [10] = "Acme", [11] = "Manual" };

        await sut.ApplyOnCreateAsync(Table, [source, snap], values);

        values[11].Should().Be("Manual");
    }

    [Fact]
    public async Task ApplyOnCreate_NoSnapshotFields_LeavesValuesAlone()
    {
        var plain = Field(1, 10, "Text", "Name");
        var sut = new SnapshotCaptureService(Substitute.For<IRelationalProjector>());
        var values = new Dictionary<long, object?> { [10] = "Acme" };

        await sut.ApplyOnCreateAsync(Table, [plain], values);

        values.Should().HaveCount(1);
    }

    [Fact]
    public async Task CaptureForRows_ReadsEachRowsOwnSourceValue()
    {
        var source = Field(1, 10, "Text", "Name");
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10);
        var sut = new SnapshotCaptureService(Substitute.For<IRelationalProjector>());
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["f_10"] = "A" },
            new Dictionary<string, object?> { ["f_10"] = null },
            new Dictionary<string, object?> { ["f_10"] = "C" },
        ];

        var result = await sut.CaptureForRowsAsync(Table, [source, snap], snap, rows);

        result.Should().Equal(new object?[] { "A", null, "C" });
    }

    // ── Source delete guard ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteGuard_SourceOfASnapshot_IsBlocked_UnlessBothAreDeleted()
    {
        var source = Field(1, 10, "Text", "Name");
        var snap = Field(2, 11, "Text", "Snap", snapshotSource: 10);

        var blocked = () => SnapshotSourceDependencyGuard.EnsureNotSource([source, snap], [source]);
        blocked.Should().Throw<ValidationException>();

        SnapshotSourceDependencyGuard.EnsureNotSource([source, snap], [source, snap]);
    }
}
