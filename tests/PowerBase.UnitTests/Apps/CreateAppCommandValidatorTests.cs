using FluentAssertions;
using PowerBase.Application.Apps.Commands.CreateApp;

namespace PowerBase.UnitTests.Apps;

public class CreateAppCommandValidatorTests
{
    private readonly CreateAppCommandValidator _sut = new();

    [Fact]
    public async Task Validate_DuplicateTableNames_IsInvalid_WithClearMessage()
    {
        var command = new CreateAppCommand("My App", null, null, null,
            new[] { new TableSpec("Firts Table"), new TableSpec("Firts Table") });

        var result = await _sut.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage == "Table name already exists. Duplicate table names are not allowed.");
    }

    [Fact]
    public async Task Validate_DuplicateTableNames_CaseInsensitiveAndWhitespaceTrimmed_IsInvalid()
    {
        var command = new CreateAppCommand("My App", null, null, null,
            new[] { new TableSpec("Clients"), new TableSpec(" clients ") });

        var result = await _sut.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_UniqueTableNames_IsValid()
    {
        var command = new CreateAppCommand("My App", null, null, null,
            new[] { new TableSpec("Table A"), new TableSpec("Table B") });

        var result = await _sut.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DuplicateFieldNamesWithinSameTable_IsInvalid_WithClearMessage()
    {
        var command = new CreateAppCommand("My App", null, null, null, new[]
        {
            new TableSpec("Firts Table", Fields: new[]
            {
                new AppFieldSpec("Name", "Text"),
                new AppFieldSpec("Name", "Text"),
            })
        });

        var result = await _sut.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage == "Field name already exists in this table. Duplicate field names are not allowed.");
    }

    [Fact]
    public async Task Validate_SameFieldNameAcrossDifferentTables_IsValid()
    {
        var command = new CreateAppCommand("My App", null, null, null, new[]
        {
            new TableSpec("Table A", Fields: new[] { new AppFieldSpec("Name", "Text") }),
            new TableSpec("Table B", Fields: new[] { new AppFieldSpec("Name", "Text") }),
        });

        var result = await _sut.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_DuplicateFieldNames_CaseInsensitiveAndWhitespaceTrimmed_IsInvalid()
    {
        var command = new CreateAppCommand("My App", null, null, null, new[]
        {
            new TableSpec("Table A", Fields: new[]
            {
                new AppFieldSpec("Email", "Text"),
                new AppFieldSpec(" email ", "Text"),
            })
        });

        var result = await _sut.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }
}
