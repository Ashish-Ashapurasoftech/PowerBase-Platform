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
}
