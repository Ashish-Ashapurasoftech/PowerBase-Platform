using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Records;

/// <summary>The optional "is this value already taken?" answer a caller that checks a whole page of records at once can supply, in place of the
/// per-value database lookup. Every caller that does not supply one must behave exactly as before.</summary>
public class RecordConstraintValidatorDuplicateHookTests
{
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly AppTable _table = new() { Id = 1, PublicId = Guid.NewGuid(), Name = "T" };

    private static AppField Unique(int fid, string typeCode = "Text") =>
        new() { Id = fid, Fid = fid, Name = $"C_f{fid}", Label = $"Field {fid}", TypeCode = typeCode, IsUnique = true };

    private Task<List<RecordConstraintViolation>> Collect(AppField field, object? value, Func<AppField, object, bool?>? hook) =>
        RecordConstraintValidator.CollectViolationsAsync(_table, [field], new Dictionary<long, object?> { [field.Fid!.Value] = value }, _recordRepo,
            isCreate: true, excludeRecordId: null, CancellationToken.None, isDuplicate: hook);

    [Fact]
    public async Task WithoutAHook_TheDatabaseLookupDecides_AsBefore()
    {
        var field = Unique(1);
        _recordRepo.HasValueDuplicateAsync(_table, field, "x", null, null, Arg.Any<CancellationToken>()).Returns(true);

        var violations = await Collect(field, "x", hook: null);

        violations.Should().ContainSingle(v => v.ConstraintType == "Unique" && v.Message.Contains("must be unique"));
        await _recordRepo.Received(1).HasValueDuplicateAsync(_table, field, "x", null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHookAnsweringTrue_ReportsTheDuplicate_WithoutTouchingTheDatabase()
    {
        var violations = await Collect(Unique(1), "x", (_, _) => true);

        violations.Should().ContainSingle(v => v.ConstraintType == "Unique");
        await _recordRepo.DidNotReceiveWithAnyArgs().HasValueDuplicateAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task AHookAnsweringFalse_AcceptsTheValue_WithoutTouchingTheDatabase()
    {
        // Even if the database would say "taken": the caller vouches for its own answer.
        _recordRepo.HasValueDuplicateAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<object>(), Arg.Any<long?>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>()).Returns(true);

        var violations = await Collect(Unique(1), "x", (_, _) => false);

        violations.Should().BeEmpty();
        await _recordRepo.DidNotReceiveWithAnyArgs().HasValueDuplicateAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task AHookAnsweringNull_MeansDoNotKnow_SoTheDatabaseIsAsked()
    {
        var field = Unique(1);
        _recordRepo.HasValueDuplicateAsync(_table, field, "x", null, null, Arg.Any<CancellationToken>()).Returns(true);

        var violations = await Collect(field, "x", (_, _) => null);

        violations.Should().ContainSingle(v => v.ConstraintType == "Unique");
        await _recordRepo.Received(1).HasValueDuplicateAsync(_table, field, "x", null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheHookIsGivenTheFieldAndTheValue()
    {
        var field = Unique(4, "Number");
        AppField? seenField = null; object? seenValue = null;

        await Collect(field, 5.5m, (f, v) => { seenField = f; seenValue = v; return false; });

        seenField.Should().BeSameAs(field);
        seenValue.Should().Be(5.5m);
    }

    [Fact]
    public async Task TheHookIsNotAskedForABlankValue_NorForAFieldThatIsNotUnique()
    {
        var calls = 0;
        bool? Hook(AppField _, object __) { calls++; return true; }

        (await Collect(Unique(1), "   ", Hook)).Should().BeEmpty();   // a blank is never a duplicate
        var notUnique = new AppField { Id = 2, Fid = 2, Name = "C_f2", Label = "Field 2", TypeCode = "Text", IsUnique = false };
        (await Collect(notUnique, "x", Hook)).Should().BeEmpty();

        calls.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAsync_ThrowsTheSameMessageWhetherTheHookOrTheDatabaseAnswers()
    {
        var field = Unique(6);
        field.Label = "name";
        var values = new Dictionary<long, object?> { [6L] = "Raj" };
        _recordRepo.HasValueDuplicateAsync(_table, field, "Raj", null, null, Arg.Any<CancellationToken>()).Returns(true);

        var viaDatabase = await Assert.ThrowsAsync<ValidationException>(() =>
            RecordConstraintValidator.ValidateAsync(_table, [field], values, _recordRepo, true, null, CancellationToken.None));
        var viaHook = await Assert.ThrowsAsync<ValidationException>(() =>
            RecordConstraintValidator.ValidateAsync(_table, [field], values, _recordRepo, true, null, CancellationToken.None, isDuplicate: (_, _) => true));

        viaHook.Message.Should().Be(viaDatabase.Message);
        viaHook.Message.Should().Be("'name' must be unique — this value is already in use.");
    }

    [Fact]
    public async Task OtherViolationsAreStillReportedNextToTheHooksAnswer()
    {
        var unique = Unique(1);
        var required = new AppField { Id = 2, Fid = 2, Name = "C_f2", Label = "Field 2", TypeCode = "Text", IsRequired = true };

        var violations = await RecordConstraintValidator.CollectViolationsAsync(_table, [unique, required],
            new Dictionary<long, object?> { [1L] = "x" }, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, isDuplicate: (_, _) => true);

        violations.Select(v => v.ConstraintType).Should().BeEquivalentTo(new[] { "Unique", "Required" });
    }
}
