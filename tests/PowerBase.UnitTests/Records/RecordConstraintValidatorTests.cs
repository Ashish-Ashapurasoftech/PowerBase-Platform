using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Records;

public class RecordConstraintValidatorTests
{
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    private static AppTable MakeTable(long id = 1) => new() { Id = id, PublicId = Guid.NewGuid(), Name = "T" };

    private static AppField MakeField(int fid, bool isRequired = false, bool isUnique = false, string typeCode = "Text") =>
        new() { Id = fid, Fid = fid, Name = $"C_field{fid}", Label = $"Field {fid}", TypeCode = typeCode, IsRequired = isRequired, IsUnique = isUnique };

    [Fact]
    public async Task ValidateAsync_RequiredFieldMissingOnCreate_Throws()
    {
        var table = MakeTable();
        var field = MakeField(1, isRequired: true);
        var values = new Dictionary<long, object?>(); // field 1 never submitted, no default resolved

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateAsync_RequiredFieldBlankString_Throws()
    {
        var table = MakeTable();
        var field = MakeField(1, isRequired: true);
        var values = new Dictionary<long, object?> { [1L] = "   " };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateAsync_RequiredFieldPresent_DoesNotThrow()
    {
        var table = MakeTable();
        var field = MakeField(1, isRequired: true);
        var values = new Dictionary<long, object?> { [1L] = "Alice" };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_RequiredFieldFalseBoolean_IsNotTreatedAsBlank()
    {
        var table = MakeTable();
        var field = MakeField(1, isRequired: true, typeCode: "Boolean");
        var values = new Dictionary<long, object?> { [1L] = false };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_UpdateOmitsUntouchedRequiredField_DoesNotThrow()
    {
        var table = MakeTable();
        var field = MakeField(1, isRequired: true);
        var values = new Dictionary<long, object?>(); // field 1 not part of this update

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: false, excludeRecordId: 42, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_UniqueFieldDuplicateValue_Throws()
    {
        var table = MakeTable();
        var field = MakeField(1, isUnique: true);
        var values = new Dictionary<long, object?> { [1L] = "taken@example.com" };
        _recordRepo.HasValueDuplicateAsync(table, field, "taken@example.com", null, Arg.Any<CancellationToken>()).Returns(true);

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateAsync_UniqueFieldNoDuplicate_DoesNotThrow()
    {
        var table = MakeTable();
        var field = MakeField(1, isUnique: true);
        var values = new Dictionary<long, object?> { [1L] = "unique@example.com" };
        _recordRepo.HasValueDuplicateAsync(table, field, "unique@example.com", null, Arg.Any<CancellationToken>()).Returns(false);

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_UniqueFieldExcludesOwnRecordOnUpdate()
    {
        var table = MakeTable();
        var field = MakeField(1, isUnique: true);
        var values = new Dictionary<long, object?> { [1L] = "same@example.com" };
        _recordRepo.HasValueDuplicateAsync(table, field, "same@example.com", 42L, Arg.Any<CancellationToken>()).Returns(false);

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: false, excludeRecordId: 42, CancellationToken.None))
            .Should().NotThrowAsync();

        await _recordRepo.Received(1).HasValueDuplicateAsync(table, field, "same@example.com", 42L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_SystemAndComputedFields_AreSkipped()
    {
        var table = MakeTable();
        var systemField = new AppField { Id = 1, Fid = 1, Name = "S_recordId", IsSystem = true, IsRequired = true, TypeCode = "Number" };
        var formulaField = new AppField { Id = 2, Fid = 2, Name = "C_calc", IsRequired = true, TypeCode = "Formula" };
        var values = new Dictionary<long, object?>(); // neither submitted

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [systemField, formulaField], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    // --- Date/DateTime format validation (appDateFormat) ---
    // Covers the direct-API path: a client that bypasses the UI's own format-aware date input
    // must be held to the same rules — the app's configured Date Formatting, '/' or '-' as an
    // interchangeable separator, and no silent day/month misreading.

    [Fact]
    public async Task ValidateAsync_DateField_SlashSeparator_MatchesConfiguredFormat_DoesNotThrow()
    {
        var table = MakeTable();
        var field = MakeField(1, typeCode: "Date");
        var values = new Dictionary<long, object?> { [1L] = "11/26/2026" };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, appDateFormat: "MM-DD-YYYY"))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_DateField_HyphenSeparator_MatchesConfiguredFormat_DoesNotThrow()
    {
        var table = MakeTable();
        var field = MakeField(1, typeCode: "Date");
        var values = new Dictionary<long, object?> { [1L] = "11-26-2026" };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, appDateFormat: "MM-DD-YYYY"))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_DateField_DayMonthOrder_FollowsAppFormat_NotAmbientCulture()
    {
        // Under a DD-MM-YYYY app format, "05-04-2026" must resolve to April 5th, not May 4th —
        // the exact day/month-swap bug invariant-culture DateTime.TryParse would otherwise cause.
        var table = MakeTable();
        var field = MakeField(1, typeCode: "Date");
        var values = new Dictionary<long, object?> { [1L] = "05-04-2026" };

        var violations = await RecordConstraintValidator.CollectViolationsAsync(
            table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, appDateFormat: "DD-MM-YYYY");

        violations.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_DateField_GarbageText_ThrowsWithClearMessage()
    {
        var table = MakeTable();
        var field = MakeField(1, typeCode: "Date");
        var values = new Dictionary<long, object?> { [1L] = "not-a-date" };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, appDateFormat: "MM-DD-YYYY"))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateAsync_DateField_ImpossibleDate_Throws()
    {
        var table = MakeTable();
        var field = MakeField(1, typeCode: "Date");
        var values = new Dictionary<long, object?> { [1L] = "02-30-2026" }; // Feb 30 doesn't exist

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, appDateFormat: "MM-DD-YYYY"))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateAsync_DateField_IsoString_AcceptedRegardlessOfConfiguredFormat()
    {
        // The UI's own picker serializes a chosen date as ISO 8601 — must be accepted even when
        // it doesn't textually match the app's configured *display* format.
        var table = MakeTable();
        var field = MakeField(1, typeCode: "DateTime");
        var values = new Dictionary<long, object?> { [1L] = "2026-04-05T00:00:00Z" };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, appDateFormat: "DD-MM-YYYY"))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_DateField_TypedDateTimeValue_PassesThroughUnchanged()
    {
        var table = MakeTable();
        var field = MakeField(1, typeCode: "Date");
        var values = new Dictionary<long, object?> { [1L] = new DateTime(2026, 4, 5) };

        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None, appDateFormat: "DD-MM-YYYY"))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_DateField_NoAppDateFormatProvided_FallsBackToDefault()
    {
        var table = MakeTable();
        var field = MakeField(1, typeCode: "Date");
        var values = new Dictionary<long, object?> { [1L] = "11-26-2026" };

        // appDateFormat omitted entirely — existing callers that haven't been updated must still
        // compile and validate against the domain default (MM-DD-YYYY).
        await FluentActions.Invoking(() =>
                RecordConstraintValidator.ValidateAsync(table, [field], values, _recordRepo, isCreate: true, excludeRecordId: null, CancellationToken.None))
            .Should().NotThrowAsync();
    }
}
