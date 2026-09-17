using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.SetKey;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Fields;

public class SetKeyCommandHandlerTests
{
    private const long ChildTableId = 20;

    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    public SetKeyCommandHandlerTests()
    {
        // No relationships to cascade to unless a test configures otherwise.
        _relRepo.ListByParentTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<Relationship>());
        _fieldTypeRepo.GetByCodeAsync("Lookup", Arg.Any<CancellationToken>()).Returns(new FieldType { Id = 99, Code = "Lookup" });
        _nameResolver.GenerateUniqueNameAsync(Arg.Any<long>(), Arg.Any<string>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => "C_" + ci.ArgAt<string>(1).Replace(" ", ""));
        _fieldRepo.GetNextFidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(100);
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>()).Returns((501L, Guid.NewGuid()));
        _fieldRepo.LabelExistsInTableAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(false);
        _formRepo.ListByTableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Form>());
    }

    private SetKeyCommandHandler Sut()
    {
        var fieldFactory = new RelationshipFieldFactory(_fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _nameResolver);
        var carryOver = new RelationshipKeyCarryOverService(_fieldRepo, _relRepo, fieldFactory);
        return new(_tableRepo, _fieldRepo, _recordRepo, _schemaEngine, _relRepo, carryOver, _auditRepo);
    }

    private static AppField Candidate(int fid = 11) =>
        new() { Id = fid, Fid = fid, PublicId = Guid.NewGuid(), Name = $"C_field{fid}", Label = "Code", TypeCode = "Text" };

    /// <summary>The seeded system Record ID# field (Fid 3) — present on every real parent table.</summary>
    private static readonly AppField RecordId = new() { Id = 3, Fid = 3, PublicId = Guid.NewGuid(), Name = "Record ID#", Label = "Record ID#", TypeCode = "Number", IsSystem = true };

    private AppTable Setup(long tableId = 5, long? currentKeyFieldId = null, params AppField[] fields)
    {
        var table = new AppTable { Id = tableId, PublicId = Guid.NewGuid(), Name = "Department", KeyFieldId = currentKeyFieldId };
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>()).Returns(fields.ToList());
        foreach (var f in fields)
            _fieldRepo.GetByIdInTableAsync(f.Id, tableId, Arg.Any<CancellationToken>()).Returns(f);
        return table;
    }

    /// <summary>A relationship with this table as parent, plus a stubbed Employee child table.</summary>
    private Relationship AddRelationship(AppTable parent, long? displayKeyFieldId, params AppField[] childFields)
    {
        var rel = new Relationship
        {
            Id = 1, PublicId = Guid.NewGuid(), ParentTableId = parent.Id, ChildTableId = ChildTableId,
            ReferenceFieldId = 41, ReferenceFid = 8, DisplayKeyFieldId = displayKeyFieldId,
        };
        _relRepo.ListByParentTableAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(new List<Relationship> { rel });
        _tableRepo.GetByIdAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ChildTableId, PublicId = Guid.NewGuid(), Name = "Employee" });
        _fieldRepo.ListByTableAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(childFields.ToList());
        return rel;
    }

    [Fact]
    public async Task Setting_a_key_field_never_rewrites_child_records_and_does_not_conflict()
    {
        var code = Candidate();
        var table = Setup(currentKeyFieldId: null, fields: code);
        _recordRepo.HasDuplicatesAsync(table, code, Arg.Any<CancellationToken>()).Returns(false);
        _recordRepo.HasNullsAsync(table, code, Arg.Any<CancellationToken>()).Returns(false);

        var act = () => Sut().HandleAsync(new SetKeyCommand(table.PublicId, code.Fid));

        await act.Should().NotThrowAsync();
        await _tableRepo.Received(1).SetKeyFieldAsync(table.Id, code.Id, Arg.Any<CancellationToken>());
        await _schemaEngine.Received(1).SetUniqueAsync(table, code, true, Arg.Any<CancellationToken>());
        // The old cascade rewire is gone — no bulk reference-column rewrite exists on the repo any more.
        _recordRepo.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().NotContain("RewriteReferenceColumnAsync");
    }

    [Fact]
    public async Task Rejects_a_field_with_duplicate_values()
    {
        var code = Candidate();
        var table = Setup(fields: code);
        _recordRepo.HasDuplicatesAsync(table, code, Arg.Any<CancellationToken>()).Returns(true);

        var act = () => Sut().HandleAsync(new SetKeyCommand(table.PublicId, code.Fid));

        await act.Should().ThrowAsync<ValidationException>();
        await _tableRepo.DidNotReceive().SetKeyFieldAsync(Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_field_with_empty_values()
    {
        var code = Candidate();
        var table = Setup(fields: code);
        _recordRepo.HasNullsAsync(table, code, Arg.Any<CancellationToken>()).Returns(true);

        var act = () => Sut().HandleAsync(new SetKeyCommand(table.PublicId, code.Fid));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Resetting_to_record_id_clears_the_key_field()
    {
        var code = Candidate();
        var table = Setup(currentKeyFieldId: code.Id, fields: code);

        await Sut().HandleAsync(new SetKeyCommand(table.PublicId, null));

        await _tableRepo.Received(1).SetKeyFieldAsync(table.Id, null, Arg.Any<CancellationToken>());
    }

    // ── Cascade to relationships still on Standard key ─────────────────────────────────────────

    [Fact]
    public async Task Setting_key_carries_over_record_id_for_a_relationship_still_on_standard()
    {
        var code = Candidate();
        var table = Setup(currentKeyFieldId: null, fields: [code, RecordId]);
        _recordRepo.HasDuplicatesAsync(table, code, Arg.Any<CancellationToken>()).Returns(false);
        _recordRepo.HasNullsAsync(table, code, Arg.Any<CancellationToken>()).Returns(false);
        AddRelationship(table, displayKeyFieldId: null);

        await Sut().HandleAsync(new SetKeyCommand(table.PublicId, code.Fid));

        await _fieldRepo.Received(1).CreateAsync(
            Arg.Is<AppField>(f => f.TypeCode == "Lookup" && f.Label == "Department Record ID#"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Setting_key_does_not_affect_a_relationship_with_its_own_override()
    {
        // This relationship already has its own display key override — Set Key on the table must
        // not touch it (the resolver already ignores the table-wide key once an override exists).
        var code = Candidate();
        var otherField = Candidate(fid: 12);
        var table = Setup(currentKeyFieldId: null, fields: [code, otherField, RecordId]);
        _recordRepo.HasDuplicatesAsync(table, code, Arg.Any<CancellationToken>()).Returns(false);
        _recordRepo.HasNullsAsync(table, code, Arg.Any<CancellationToken>()).Returns(false);
        AddRelationship(table, displayKeyFieldId: otherField.Id);

        await Sut().HandleAsync(new SetKeyCommand(table.PublicId, code.Fid));

        await _fieldRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        await _fieldRepo.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task Resetting_to_record_id_removes_the_stale_lookup_the_earlier_set_key_left_behind()
    {
        var code = Candidate();
        var staleLookup = new AppField
        {
            Id = 60, Fid = 13, Name = "C_DepartmentRecordId", Label = "Department Record ID#", TypeCode = "Lookup", PublicId = Guid.NewGuid(),
            Settings = JsonSerializer.Serialize(new LookupSettings { RelationshipId = 1, ReferenceFid = 8, SourceTableId = 5, SourceFid = 3, SourceTypeCode = "Number" },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        };
        var table = Setup(currentKeyFieldId: code.Id, fields: [code, RecordId]);
        AddRelationship(table, displayKeyFieldId: null, staleLookup);

        await Sut().HandleAsync(new SetKeyCommand(table.PublicId, null));

        await _fieldRepo.Received(1).DeleteAsync(staleLookup.PublicId, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
