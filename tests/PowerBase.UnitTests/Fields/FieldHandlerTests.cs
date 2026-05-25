using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Queries.ListFields;
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

    private static AppTable MakeTable(long id = 5) => new() { Id = id, PublicId = Guid.NewGuid(), Name = "T" };
    private static FieldType MakeFieldType() => new() { Id = 1, Code = "Text", SqlDataType = "NVARCHAR(500)" };

    public FieldHandlerTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
    }

    // --- CreateFieldCommandHandler ---

    [Fact]
    public async Task CreateField_ValidCommand_CreatesFieldAndUpdatesPhysicalName()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.NameExistsInTableAsync(table.Id, "Email").Returns(false);
        _fieldTypeRepo.GetByCodeAsync("Text").Returns(MakeFieldType());
        _fieldRepo.CreateAsync(Arg.Any<AppField>()).Returns((100L, Guid.NewGuid()));
        var sut = new CreateFieldCommandHandler(_tableRepo, _fieldRepo, _fieldTypeRepo, _schemaEngine, _queryContext, _auditRepo);

        var result = await sut.HandleAsync(new CreateFieldCommand(table.PublicId, "Text", "Email", null, null, false));

        result.Id.Should().Be(100L);
        result.Name.Should().Be("Email");
        result.PhysicalColumnName.Should().Be(PhysicalNaming.ColumnName(100L));
        result.TypeCode.Should().Be("Text");
        await _fieldRepo.Received(1).UpdatePhysicalColumnNameAsync(100L, PhysicalNaming.ColumnName(100L), Arg.Any<CancellationToken>());
        await _schemaEngine.Received(1).AddColumnAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateField_DuplicateName_ThrowsDuplicateException()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.NameExistsInTableAsync(table.Id, "Email").Returns(true);
        var sut = new CreateFieldCommandHandler(_tableRepo, _fieldRepo, _fieldTypeRepo, _schemaEngine, _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new CreateFieldCommand(table.PublicId, "Text", "Email", null, null, false)))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task CreateField_InvalidTypeCode_ThrowsValidationException()
    {
        var sut = new CreateFieldCommandHandler(_tableRepo, _fieldRepo, _fieldTypeRepo, _schemaEngine, _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new CreateFieldCommand(Guid.NewGuid(), "Blob", "File", null, null, false)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateField_EmptyName_ThrowsValidationException()
    {
        var sut = new CreateFieldCommandHandler(_tableRepo, _fieldRepo, _fieldTypeRepo, _schemaEngine, _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new CreateFieldCommand(Guid.NewGuid(), "Text", "", null, null, false)))
            .Should().ThrowAsync<ValidationException>();
    }

    // --- ListFieldsQueryHandler ---

    [Fact]
    public async Task ListFields_ReturnsFieldsForTable()
    {
        var table = MakeTable();
        var fields = new List<AppField> { new() { Id = 1, Name = "Name" }, new() { Id = 2, Name = "Age" } };
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(fields);
        var sut = new ListFieldsQueryHandler(_tableRepo, _fieldRepo);

        var result = await sut.HandleAsync(new ListFieldsQuery(table.PublicId));

        result.Should().HaveCount(2);
    }
}
