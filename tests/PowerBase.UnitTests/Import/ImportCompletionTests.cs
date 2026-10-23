using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields;
using PowerBase.Application.Import.Jobs;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>Tests for the "complete the partially implemented import" work (timeouts, exact fids, summaries, formulas, rules…).</summary>
public class ImportCompletionTests
{
    // ---- WP1: import scope ----

    [Fact]
    public void ImportRuntime_Begin_SetsLongerCommandTimeoutOnlyInsideTheScope()
    {
        ImportRuntime.CommandTimeoutSeconds.Should().BeNull();

        using (ImportRuntime.Begin())
            ImportRuntime.CommandTimeoutSeconds.Should().Be(ImportRuntime.ConfiguredCommandTimeoutSeconds);

        ImportRuntime.CommandTimeoutSeconds.Should().BeNull();
        ImportRuntime.Fids.Should().BeNull();
    }

    // ---- WP2: exact fids ----

    private static IAppFieldRepository Repo(params int[] takenFids)
    {
        var repo = Substitute.For<IAppFieldRepository>();
        repo.GetByFidInTableAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => takenFids.Contains(ci.ArgAt<int>(1)) ? new AppField() : null);
        repo.GetNextFidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(500);
        return repo;
    }

    [Fact]
    public async Task Fid_OutsideAnImport_IsAlwaysTheNextFreeNumber()
    {
        var fid = await FieldFidPolicy.ResolveAsync(Repo(), 1, "Name", explicitFid: 42, CancellationToken.None);

        // Outside an import an explicit number is still honoured when free (callers only pass it during imports).
        fid.Should().Be(42);
        (await FieldFidPolicy.ResolveAsync(Repo(), 1, "Name", null, CancellationToken.None)).Should().Be(500);
    }

    [Fact]
    public async Task Fid_ExplicitAndFree_IsKept()
    {
        (await FieldFidPolicy.ResolveAsync(Repo(), 1, "Name", 17, CancellationToken.None)).Should().Be(17);
    }

    [Theory]
    [InlineData(3)]   // system field number
    [InlineData(5)]
    public async Task Fid_ReservedSystemNumber_FallsBackToNextFreeAndIsReported(int source)
    {
        using var scope = ImportRuntime.Begin();

        var fid = await FieldFidPolicy.ResolveAsync(Repo(), 1, "Odd", source, CancellationToken.None);

        fid.Should().Be(500);
        ImportRuntime.Fids!.Fallbacks.Should().ContainSingle(m => m.Contains("Odd") && m.Contains(source.ToString()));
    }

    [Fact]
    public async Task Fid_AlreadyTaken_FallsBack()
    {
        using var scope = ImportRuntime.Begin();

        (await FieldFidPolicy.ResolveAsync(Repo(17), 1, "Name", 17, CancellationToken.None)).Should().Be(500);
        ImportRuntime.Fids!.DrainFallbacks().Should().HaveCount(1);
        ImportRuntime.Fids.Fallbacks.Should().BeEmpty();
    }

    [Fact]
    public async Task Fid_ExpectedByLabel_IsUsedOnceForTheRegisteredTable()
    {
        using var scope = ImportRuntime.Begin();
        ImportRuntime.Fids!.Expect(7, "Related Client", 123);

        (await FieldFidPolicy.ResolveAsync(Repo(), 7, "related client", null, CancellationToken.None)).Should().Be(123);
        // consumed: the same label again gets a normal number
        (await FieldFidPolicy.ResolveAsync(Repo(), 7, "Related Client", null, CancellationToken.None)).Should().Be(500);
        // other table: not affected
        ImportRuntime.Fids.Expect(7, "X", 9);
        (await FieldFidPolicy.ResolveAsync(Repo(), 8, "X", null, CancellationToken.None)).Should().Be(500);
    }

    [Fact]
    public async Task Fid_AutoCreatedFieldsDoNotConsumeExpectedNumbers()
    {
        using var scope = ImportRuntime.Begin();
        ImportRuntime.Fids!.Expect(7, "Add Files", 55);

        (await FieldFidPolicy.ResolveAsync(Repo(), 7, "Add Files", null, CancellationToken.None, useExpected: false)).Should().Be(500);
        ImportRuntime.Fids.TryTake(7, "Add Files", out var fid).Should().BeTrue();
        fid.Should().Be(55);
    }

    // ---- WP9: restricting one table must not wipe the role's other tables ----

    [Fact]
    public void MergeRestriction_KeepsOtherTablesAndRestrictsTheFailedOne()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var existing = new[]
        {
            new TablePermissionRow(a, "A", "AllRecords", "AllRecords", true, true, false, false, "FullAccess"),
            new TablePermissionRow(b, "B", "AllRecords", "None", false, false, false, false, "FullAccess"),
        };

        var merged = PowerBase.Application.Import.Services.PblPermissionsApplier.MergeRestriction(existing, [new AppTable { Id = 2, PublicId = b }]);

        merged.Should().HaveCount(2);
        merged.Single(m => m.TablePublicId == a).ViewScope.Should().Be("AllRecords");
        merged.Single(m => m.TablePublicId == a).CanAdd.Should().BeTrue();
        var restricted = merged.Single(m => m.TablePublicId == b);
        restricted.ViewScope.Should().Be("None");
        restricted.ModifyScope.Should().Be("None");
    }

    [Fact]
    public void MergeRestriction_ATableTheRoleHadNoRowFor_IsStillRestricted()
    {
        var c = Guid.NewGuid();

        var merged = PowerBase.Application.Import.Services.PblPermissionsApplier.MergeRestriction([], [new AppTable { Id = 3, PublicId = c }]);

        merged.Should().ContainSingle().Which.ViewScope.Should().Be("None");
    }

    [Fact]
    public void FidPlan_Floor_OnlyGrows()
    {
        using var scope = ImportRuntime.Begin();
        var plan = ImportRuntime.Fids!;

        plan.SetFloor(3, 400);
        plan.SetFloor(3, 120);

        plan.FloorOf(3).Should().Be(400);
        plan.FloorOf(4).Should().Be(0);
    }
}
