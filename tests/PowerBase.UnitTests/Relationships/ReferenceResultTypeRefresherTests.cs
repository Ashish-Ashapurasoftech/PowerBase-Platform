using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

/// <summary>A Reference made as a new field is shown as the type of the parent key it shows; when that key changes its
/// saved type follows. One converted from an existing field keeps its own type.</summary>
public class ReferenceResultTypeRefresherTests
{
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly List<(long Id, string? Settings)> _writes = [];

    private const long DeptId = 6, EmpId = 7;
    private readonly AppTable _dept = new() { Id = DeptId, Name = "Departments" };

    private static readonly AppField RecordId = new() { Id = 3, Fid = 3, AppTableId = DeptId, Name = "Record ID#", TypeCode = "Number", IsSystem = true };
    private static readonly AppField DeptName = new() { Id = 60, Fid = 6, AppTableId = DeptId, Name = "Department Name", TypeCode = "Text", IsUnique = true };
    private static readonly AppField Budget = new() { Id = 70, Fid = 7, AppTableId = DeptId, Name = "Budget", TypeCode = "Currency", IsUnique = true };

    public ReferenceResultTypeRefresherTests()
    {
        _tableRepo.GetByIdAsync(DeptId, Arg.Any<CancellationToken>()).Returns(_ => _dept);
        _fieldRepo.ListByTableAsync(DeptId, Arg.Any<CancellationToken>()).Returns(new List<AppField> { RecordId, DeptName, Budget });
        _fieldRepo.When(r => r.UpdateSettingsAsync(Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(ci => _writes.Add((ci.ArgAt<long>(0), ci.ArgAt<string?>(1))));
    }

    private static AppField Reference(long id, string? resultTypeCode) => new()
    {
        Id = id, Fid = (int)id, AppTableId = EmpId, Name = $"Ref{id}", TypeCode = "Reference",
        Settings = resultTypeCode is null ? """{"relationshipId":1}""" : $$"""{"relationshipId":1,"resultTypeCode":"{{resultTypeCode}}"}""",
    };

    private void Given(params (Relationship Rel, AppField Reference)[] rels)
    {
        _relRepo.ListByParentTableAsync(DeptId, Arg.Any<CancellationToken>()).Returns(rels.Select(r => r.Rel).ToList());
        _fieldRepo.ListByTableAsync(EmpId, Arg.Any<CancellationToken>()).Returns(rels.Select(r => r.Reference).ToList());
    }

    private static Relationship Rel(long id, long refFieldId, bool existing, long? displayKeyFieldId = null) => new()
    {
        Id = id, ParentTableId = DeptId, ChildTableId = EmpId, ReferenceFieldId = refFieldId,
        ReferenceFieldIsExisting = existing, DisplayKeyFieldId = displayKeyFieldId,
    };

    [Fact]
    public async Task SetKeyChangedToText_NewFieldReferenceSavedAsNumber_BecomesText()
    {
        _dept.KeyFieldId = DeptName.Id;
        Given((Rel(1, 100, existing: false), Reference(100, "Number")));

        var updated = await ReferenceResultTypeRefresher.RefreshForParentAsync(DeptId, _relRepo, _tableRepo, _fieldRepo);

        updated.Should().Be(1);
        _writes.Should().ContainSingle(w => w.Id == 100 && w.Settings!.Contains("\"resultTypeCode\":\"Text\"") && w.Settings.Contains("\"relationshipId\":1"));
    }

    [Fact]
    public async Task KeyResetToRecordId_IsNumber_AndADisplayKeyWinsOverTheSetKey()
    {
        _dept.KeyFieldId = DeptName.Id;
        Given((Rel(1, 100, existing: false), Reference(100, "Text")),                                // Set Key Text: unchanged
              (Rel(2, 101, existing: false, displayKeyFieldId: Budget.Id), Reference(101, "Text")));  // its own Display Key

        await ReferenceResultTypeRefresher.RefreshForParentAsync(DeptId, _relRepo, _tableRepo, _fieldRepo);

        _writes.Should().ContainSingle(w => w.Id == 101 && w.Settings!.Contains("\"resultTypeCode\":\"Currency\""));

        _writes.Clear();
        _dept.KeyFieldId = null;   // back to Record ID#
        await ReferenceResultTypeRefresher.RefreshForParentAsync(DeptId, _relRepo, _tableRepo, _fieldRepo);
        _writes.Should().ContainSingle(w => w.Id == 100 && w.Settings!.Contains("\"resultTypeCode\":\"Number\""));
    }

    [Fact]
    public async Task ConvertedFromAnExistingField_KeepsItsOwnType()
    {
        _dept.KeyFieldId = Budget.Id;
        Given((Rel(1, 100, existing: true), Reference(100, "Text")));

        var updated = await ReferenceResultTypeRefresher.RefreshForParentAsync(DeptId, _relRepo, _tableRepo, _fieldRepo);

        updated.Should().Be(0);
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AlreadyTheKeysType_WritesNothing()
    {
        Given((Rel(1, 100, existing: false), Reference(100, "Number")));

        (await ReferenceResultTypeRefresher.RefreshForParentAsync(DeptId, _relRepo, _tableRepo, _fieldRepo)).Should().Be(0);
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task NoSavedTypeYet_IsFilledIn()
    {
        _dept.KeyFieldId = DeptName.Id;
        Given((Rel(1, 100, existing: false), Reference(100, null)));

        await ReferenceResultTypeRefresher.RefreshForParentAsync(_dept, _relRepo, _fieldRepo);

        _writes.Should().ContainSingle(w => w.Id == 100 && w.Settings!.Contains("\"resultTypeCode\":\"Text\""));
    }
}
