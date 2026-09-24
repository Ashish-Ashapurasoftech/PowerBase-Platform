using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class SelfRelationshipTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();

    private readonly CreateRelationshipCommandHandler _handler;

    private static readonly Guid AppPublicId = Guid.NewGuid();
    private static readonly Guid DeptPublicId = Guid.NewGuid();
    private const long DeptTableId = 100;
    private const long AppId = 10;

    public SelfRelationshipTests()
    {
        var fieldFactory = new RelationshipFieldFactory(
            _fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _nameResolver);

        _handler = new CreateRelationshipCommandHandler(
            _tableRepo, _fieldRepo, _fieldTypeRepo, _relRepo, fieldFactory, _auditRepo, _appRepo);

        var deptTable = new AppTable { Id = DeptTableId, PublicId = DeptPublicId, AppId = AppId, Name = "Department" };
        _tableRepo.GetByPublicIdAsync(DeptPublicId, Arg.Any<CancellationToken>()).Returns(deptTable);
        _tableRepo.GetByIdAsync(DeptTableId, Arg.Any<CancellationToken>()).Returns(deptTable);
        _appRepo.GetPublicIdByIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(AppPublicId);

        var recordIdField = new AppField { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, IsUnique = true };
        var deptNameField = new AppField { Id = 10, Fid = 6, Name = "Department Name", TypeCode = "Text" };
        _fieldRepo.ListByTableAsync(DeptTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { recordIdField, deptNameField });

        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new FieldType { Id = 1, Code = callInfo.Arg<string>() });

        _nameResolver.GenerateUniqueNameAsync(DeptTableId, Arg.Any<string>(), false, Arg.Any<CancellationToken>())
            .Returns(callInfo => "C_" + callInfo.ArgAt<string>(1).Replace(" ", ""));

        _fieldRepo.GetNextFidAsync(DeptTableId, Arg.Any<CancellationToken>()).Returns(15);
        _fieldRepo.LabelExistsInTableAsync(DeptTableId, Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);

        _relRepo.CreateAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns((1L, Guid.NewGuid()));

        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>())
            .Returns((99L, Guid.NewGuid()));

        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Form>());
    }

    [Fact]
    public async Task Can_create_self_relationship_on_same_table()
    {
        var command = new CreateRelationshipCommand(
            AppPublicId: AppPublicId,
            ParentTablePublicId: DeptPublicId,
            ChildTablePublicId: DeptPublicId,
            ReferenceFieldLabel: "Parent Department",
            IsReferenceRequired: false,
            Lookups: [],
            Summaries: [],
            ReferenceFieldFid: null,
            DisplayKeyFieldFid: null);

        var result = await _handler.HandleAsync(command);

        result.Should().NotBeNull();
        result.ParentTablePublicId.Should().Be(DeptPublicId);
        result.ChildTablePublicId.Should().Be(DeptPublicId);
        result.ParentTableName.Should().Be("Department");
        result.ChildTableName.Should().Be("Department");

        // Verify relationship created with parent = child = DeptTableId
        await _relRepo.Received(1).CreateAsync(
            Arg.Is<Relationship>(r => r.ParentTableId == DeptTableId && r.ChildTableId == DeptTableId),
            Arg.Any<CancellationToken>());

        // Verify ReportLink auto-created with standard Department naming
        await _fieldRepo.Received().CreateAsync(
            Arg.Is<AppField>(f => f.TypeCode == "ReportLink" && f.Name.Contains("Department")),
            Arg.Any<CancellationToken>());
    }
}
