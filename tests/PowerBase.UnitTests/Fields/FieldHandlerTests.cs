using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Fields;

public class FieldHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly FieldSettingsValidatorRegistry _settingsRegistry = new(Array.Empty<IFieldSettingsValidator>());
    private readonly IFieldNameResolver _fieldNameResolver = Substitute.For<IFieldNameResolver>();

    private static AppTable MakeTable(long id = 5) => new() { Id = id, PublicId = Guid.NewGuid(), Name = "T" };
    private static FieldType MakeFieldType() => new() { Id = 1, Code = "Text", SqlDataType = "NVARCHAR(500)" };

    public FieldHandlerTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _formRepo.ListByTableAsync(Arg.Any<Guid>()).Returns(Array.Empty<Form>());
        // Default next FID for new user fields
        _fieldRepo.GetNextFidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(6);
    }

    private CreateFieldCommandHandler MakeSut() =>
        new(_tableRepo, _fieldRepo, _fieldTypeRepo, _schemaEngine, _queryContext, _auditRepo, _formRepo, _settingsRegistry, _fieldNameResolver);

    // --- CreateFieldCommandHandler ---

    [Fact]
    public async Task CreateField_ValidCommand_CreatesFieldAndUpdatesPhysicalName()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.LabelExistsInTableAsync(table.Id, "Email", Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldTypeRepo.GetByCodeAsync("Text").Returns(MakeFieldType());
        _fieldRepo.CreateAsync(Arg.Any<AppField>()).Returns((100L, Guid.NewGuid()));
        _fieldNameResolver.GenerateUniqueNameAsync(table.Id, "Email", false, Arg.Any<CancellationToken>()).Returns("C_email");

        var result = await MakeSut().HandleAsync(new CreateFieldCommand(table.PublicId, "Text", "Email", null, false));

        result.Id.Should().Be(100L);
        result.Name.Should().Be("C_email");
        result.Label.Should().Be("Email");
        // Physical column name is based on FID (6), not DB identity (100)
        result.PhysicalColumnName.Should().Be(PhysicalNaming.ColumnName(6));
        result.TypeCode.Should().Be("Text");
        await _fieldRepo.Received(1).UpdatePhysicalColumnNameAsync(100L, PhysicalNaming.ColumnName(6), Arg.Any<CancellationToken>());
        await _schemaEngine.Received(1).AddColumnAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateField_DuplicateLabel_ThrowsDuplicateException()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.LabelExistsInTableAsync(table.Id, "Email", Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(true);

        await MakeSut().Invoking(s => s.HandleAsync(new CreateFieldCommand(table.PublicId, "Text", "Email", null, false)))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task CreateField_InvalidTypeCode_ThrowsNotFoundException()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.LabelExistsInTableAsync(table.Id, "File", Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _fieldTypeRepo.GetByCodeAsync("Blob", Arg.Any<CancellationToken>()).Returns((FieldType?)null);

        await MakeSut().Invoking(s => s.HandleAsync(new CreateFieldCommand(table.PublicId, "Blob", "File", null, false)))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CreateField_EmptyLabel_ThrowsValidationException()
    {
        await MakeSut().Invoking(s => s.HandleAsync(new CreateFieldCommand(Guid.NewGuid(), "Text", "", null, false)))
            .Should().ThrowAsync<ValidationException>();
    }

    // --- ListFieldsQueryHandler ---

    [Fact]
    public async Task ListFields_ReturnsFieldsForTable()
    {
        var table = MakeTable();
        var fields = new List<PowerBase.Application.Common.Models.AppFieldListItemDto>
        {
            new() { Id = 1, Name = "C_name", Label = "Name" },
            new() { Id = 2, Name = "C_age", Label = "Age" },
        };
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTablePagedAsync(table.Id, 1, 20, null, "name", false, null, Arg.Any<CancellationToken>()).Returns(fields);
        _fieldRepo.CountByTableAsync(table.Id, null, null, Arg.Any<CancellationToken>()).Returns(2);
        var sut = new ListFieldsQueryHandler(_tableRepo, _fieldRepo);

        var result = await sut.HandleAsync(new ListFieldsQuery(table.PublicId));

        result.Items.Should().HaveCount(2);
        result.Total.Should().Be(2);
    }
}
