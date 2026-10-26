using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.UpdateAppFromPbl;
using PowerBase.Application.Import.Diff;
using PowerBase.Application.Import.Mapping;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Import;

public class ImportMappingTests
{
    [Fact]
    public void Parse_Json_ReadsDecisionsExclusionsAndStringEnums()
    {
        var mapping = ImportMapping.Parse("""
            { "excludedTableRefs": ["$Table_A"],
              "elements": [ { "elementType": "Field", "sourceLogicalRef": "$Table_B::$Field_X", "action": "overwrite" } ] }
            """);

        mapping.ExcludedTableRefs.Should().Equal("$Table_A");
        mapping.Elements.Should().ContainSingle().Which.Action.Should().Be(ImportElementAction.Overwrite);
    }

    [Fact]
    public void Parse_Blank_ReturnsEmptyMapping() =>
        ImportMapping.Parse(null).Elements.Should().BeEmpty();

    [Fact]
    public void Parse_Malformed_ThrowsValidationException() =>
        FluentActions.Invoking(() => ImportMapping.Parse("{ nope")).Should().Throw<ValidationException>();
}

public class PblDocumentToolsTests
{
    private static PblDocument Doc() => new()
    {
        App = new PblApp { LogicalRef = "$App", Name = "A" },
        Tables =
        [
            new PblTable { LogicalRef = "$T1", Name = "One", Fields = [new PblField { LogicalRef = "$T1::f", Name = "F" }] },
            new PblTable { LogicalRef = "$T2", Name = "Two", Fields = [new PblField { LogicalRef = "$T2::g", Name = "G" }] },
        ],
        Relationships = [new PblRelationship { LogicalRef = "$T2::r", ParentTableRef = "$T1", ChildTableRef = "$T2", ReferenceFieldName = "R" }],
        Forms = [new PblForm { LogicalRef = "$T2::form", TableRef = "$T2", Name = "Main" }],
        Roles = [new PblRole { LogicalRef = "$Role", Name = "R", TablePermissions = [new PblTablePermission { TableRef = "$T1" }, new PblTablePermission { TableRef = "$T2" }] }],
    };

    [Fact]
    public void ExcludeTables_RemovesTableAndEverythingHangingOffIt()
    {
        var doc = Doc();

        PblDocumentTools.ExcludeTables(doc, ["$T2"]);

        doc.Tables.Select(t => t.LogicalRef).Should().Equal("$T1");
        doc.Relationships.Should().BeEmpty();
        doc.Forms.Should().BeEmpty();
        doc.Roles[0].TablePermissions.Select(p => p.TableRef).Should().Equal("$T1");
    }

    [Fact]
    public void Subset_KeepsOnlyApprovedElementsButEveryTable()
    {
        var doc = Doc();
        var approved = new HashSet<string> { "Field|$T2::g", "Form|$T2::form" };

        var subset = PblDocumentTools.Subset(doc, approved);

        subset.Tables.Should().HaveCount(2);
        subset.Tables.Single(t => t.LogicalRef == "$T1").Fields.Should().BeEmpty();
        subset.Tables.Single(t => t.LogicalRef == "$T2").Fields.Should().ContainSingle();
        subset.Forms.Should().ContainSingle();
        subset.Relationships.Should().BeEmpty();
        subset.Roles.Should().BeEmpty();
    }
}

public class ImportUpdatePlannerTests
{
    private static ImportDiffItem Item(string type, string reference, string status, bool canOverwrite = false, string tableRef = "") => new()
    {
        ElementType = type, SourceLogicalRef = reference, Name = reference, Status = status, CanOverwrite = canOverwrite, TableRef = tableRef,
    };

    private static ImportDiffResult Diff(params ImportDiffItem[] items) => new() { Items = [.. items] };

    private static ImportMapping Mapping(params (string Type, string Ref, ImportElementAction Action)[] decisions) => new()
    {
        Elements = decisions.Select(d => new ImportMappingElement { ElementType = d.Type, SourceLogicalRef = d.Ref, Action = d.Action }).ToList(),
    };

    [Fact]
    public void Plan_NewElementWithoutDecision_IsAdded()
    {
        var plan = ImportUpdatePlanner.Plan(Diff(Item("Variable", "$v", ImportDiffStatus.New)), new ImportMapping());

        plan.AddKeys.Should().Contain("Variable|$v");
    }

    [Fact]
    public void Plan_NewElementSkipped_IsNotAdded()
    {
        var plan = ImportUpdatePlanner.Plan(Diff(Item("Variable", "$v", ImportDiffStatus.New)), Mapping(("Variable", "$v", ImportElementAction.Skip)));

        plan.AddKeys.Should().BeEmpty();
        plan.Outcomes.Should().ContainSingle(o => o.Outcome == ImportOutcome.Skipped);
    }

    [Fact]
    public void Plan_ChangedElementWithoutDecision_IsRejected_NoSilentOverwrite()
    {
        var diff = Diff(Item("Field", "$f", ImportDiffStatus.Changed, canOverwrite: true));

        FluentActions.Invoking(() => ImportUpdatePlanner.Plan(diff, new ImportMapping()))
            .Should().Throw<BadRequestException>().Which.Message.Should().Contain("explicit Overwrite or Skip");
    }

    [Fact]
    public void Plan_ChangedElementOverwrite_IsPlannedAsOverwrite()
    {
        var diff = Diff(Item("Field", "$f", ImportDiffStatus.Changed, canOverwrite: true));

        var plan = ImportUpdatePlanner.Plan(diff, Mapping(("Field", "$f", ImportElementAction.Overwrite)));

        plan.Overwrites.Should().ContainSingle();
    }

    [Fact]
    public void Plan_OverwriteOfConflict_IsRejected()
    {
        var diff = Diff(Item("Field", "$f", ImportDiffStatus.Conflict));

        FluentActions.Invoking(() => ImportUpdatePlanner.Plan(diff, Mapping(("Field", "$f", ImportElementAction.Overwrite))))
            .Should().Throw<ValidationException>();
    }

    [Fact]
    public void Plan_AddOfExistingElement_IsRejected()
    {
        var diff = Diff(Item("Field", "$f", ImportDiffStatus.Identical));

        FluentActions.Invoking(() => ImportUpdatePlanner.Plan(diff, Mapping(("Field", "$f", ImportElementAction.Add))))
            .Should().Throw<ValidationException>();
    }

    [Fact]
    public void Plan_UnknownElement_IsRejected() =>
        FluentActions.Invoking(() => ImportUpdatePlanner.Plan(Diff(), Mapping(("Field", "$nope", ImportElementAction.Skip))))
            .Should().Throw<ValidationException>();

    [Fact]
    public void Plan_ConflictAndIdentical_AreLeftUntouched()
    {
        var diff = Diff(
            Item("Field", "$c", ImportDiffStatus.Conflict),
            Item("Field", "$i", ImportDiffStatus.Identical));

        var plan = ImportUpdatePlanner.Plan(diff, new ImportMapping());

        plan.AddKeys.Should().BeEmpty();
        plan.Overwrites.Should().BeEmpty();
        plan.Outcomes.Select(o => o.Outcome).Should().BeEquivalentTo([ImportOutcome.Skipped, ImportOutcome.Unchanged]);
    }

    [Fact]
    public void Plan_ChildrenOfASkippedNewTable_AreNotAdded()
    {
        var diff = Diff(
            Item("Table", "$T", ImportDiffStatus.New),
            Item("Field", "$T::f", ImportDiffStatus.New, tableRef: "$T"));

        var plan = ImportUpdatePlanner.Plan(diff, Mapping(("Table", "$T", ImportElementAction.Skip)));

        plan.AddKeys.Should().BeEmpty();
        plan.Outcomes.Should().Contain(o => o.SourceLogicalRef == "$T::f" && o.Outcome == ImportOutcome.Skipped);
    }
}

public class ImportDiffBuilderTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IAppRoleRepository _roleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IAppVariableRepository _variableRepo = Substitute.For<IAppVariableRepository>();
    private readonly IPageRepository _pageRepo = Substitute.For<IPageRepository>();
    private readonly IImportSourceRefRepository _refRepo = Substitute.For<IImportSourceRefRepository>();
    private readonly Guid _appId = Guid.NewGuid();

    private ImportDiffBuilder Sut()
    {
        _appRepo.GetIdByPublicIdAsync(_appId, Arg.Any<CancellationToken>()).Returns(1L);
        _appRepo.GetByPublicIdAsync(_appId, Arg.Any<CancellationToken>()).Returns(new App { Id = 1, PublicId = _appId, Name = "Target" });
        _refRepo.ListByAppAsync(1L, Arg.Any<CancellationToken>()).Returns(Array.Empty<ImportSourceRef>());
        _roleRepo.ListDetailsByAppIdAsync(1L, Arg.Any<CancellationToken>()).Returns(Array.Empty<AppRoleDetail>());
        _variableRepo.ListAsync(1L, Arg.Any<CancellationToken>()).Returns(Array.Empty<AppVariable>());
        return new ImportDiffBuilder(_appRepo, _tableRepo, _fieldRepo, _reportRepo, _formRepo, _roleRepo, _variableRepo, _pageRepo, _refRepo);
    }

    private static PblDocument Doc(params PblField[] fields) => new()
    {
        App = new PblApp { LogicalRef = "$App", Name = "A" },
        Tables = [new PblTable { LogicalRef = "$T", Name = "Clients", Fields = [.. fields] }],
    };

    private void ExistingTable(params AppField[] fields)
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid(), AppId = 1, Name = "Clients" };
        _tableRepo.ListByAppAsync(1L, Arg.Any<CancellationToken>()).Returns(new List<AppTable> { table });
        _fieldRepo.ListByTableAsync(5L, Arg.Any<CancellationToken>()).Returns(fields);
    }

    [Fact]
    public async Task Build_TableAndFieldsNotInApp_AreNew()
    {
        _tableRepo.ListByAppAsync(1L, Arg.Any<CancellationToken>()).Returns(new List<AppTable>());

        var diff = await Sut().BuildAsync(Doc(new PblField { LogicalRef = "$T::f", Name = "Name", TypeCode = "Text" }), _appId, null, default);

        diff.Items.Select(i => (i.ElementType, i.Status)).Should().BeEquivalentTo([("Table", "New"), ("Field", "New")]);
    }

    [Fact]
    public async Task Build_FieldWithSameNameAndLabel_IsIdentical_AndAppOnlyFieldIsPreserved()
    {
        ExistingTable(
            new AppField { Id = 10, PublicId = Guid.NewGuid(), AppTableId = 5, Name = "Name", Label = "Name", TypeCode = "Text" },
            new AppField { Id = 11, PublicId = Guid.NewGuid(), AppTableId = 5, Name = "Extra", Label = "Extra", TypeCode = "Text" });

        var diff = await Sut().BuildAsync(Doc(new PblField { LogicalRef = "$T::f", Name = "Name", TypeCode = "Text" }), _appId, null, default);

        diff.Items.Single(i => i.ElementType == "Field").Status.Should().Be(ImportDiffStatus.Identical);
        diff.Items.Single(i => i.ElementType == "Field").MatchedBy.Should().Be(ImportMatchedBy.Name);
        diff.OnlyInApp.Should().ContainSingle(o => o.ElementType == "Field" && o.Name == "Extra");
    }

    [Fact]
    public async Task Build_FieldWithDifferentLabel_IsChangedAndOverwritable()
    {
        ExistingTable(new AppField { Id = 10, PublicId = Guid.NewGuid(), AppTableId = 5, Name = "Name", Label = "Old", TypeCode = "Text" });

        var diff = await Sut().BuildAsync(Doc(new PblField { LogicalRef = "$T::f", Name = "Name", Label = "New", TypeCode = "Text" }), _appId, null, default);

        var item = diff.Items.Single(i => i.ElementType == "Field");
        item.Status.Should().Be(ImportDiffStatus.Changed);
        item.CanOverwrite.Should().BeTrue();
        item.Changes.Should().ContainSingle(c => c.Property == "Label" && c.Before == "Old" && c.After == "New");
    }

    [Fact]
    public async Task Build_FieldWithDifferentType_IsConflictAndNotOverwritable()
    {
        ExistingTable(new AppField { Id = 10, PublicId = Guid.NewGuid(), AppTableId = 5, Name = "Name", Label = "Name", TypeCode = "Number" });

        var diff = await Sut().BuildAsync(Doc(new PblField { LogicalRef = "$T::f", Name = "Name", TypeCode = "Text" }), _appId, null, default);

        var item = diff.Items.Single(i => i.ElementType == "Field");
        item.Status.Should().Be(ImportDiffStatus.Conflict);
        item.CanOverwrite.Should().BeFalse();
    }

    [Fact]
    public async Task Build_RenamedFieldMatchedThroughRecordedSourceRef_NotThroughName()
    {
        ExistingTable(new AppField { Id = 10, PublicId = Guid.NewGuid(), AppTableId = 5, Name = "renamed", Label = "Renamed", TypeCode = "Text" });
        var sut = Sut();
        _refRepo.ListByAppAsync(1L, Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ImportSourceRef { AppId = 1, ElementType = "Field", SourceLogicalRef = "$T::f", ElementId = 10 },
        });

        var diff = await sut.BuildAsync(Doc(new PblField { LogicalRef = "$T::f", Name = "Name", Label = "Renamed", TypeCode = "Text" }), _appId, null, default);

        var item = diff.Items.Single(i => i.ElementType == "Field");
        item.MatchedBy.Should().Be(ImportMatchedBy.SourceRef);
        item.Status.Should().Be(ImportDiffStatus.Identical);
    }

    [Fact]
    public async Task Build_MappingOverride_PointsTheRefAtTheChosenExistingField()
    {
        var chosen = Guid.NewGuid();
        ExistingTable(new AppField { Id = 10, PublicId = chosen, AppTableId = 5, Name = "other", Label = "Name", TypeCode = "Text" });
        var mapping = new ImportMapping
        {
            Elements = [new ImportMappingElement { ElementType = "Field", SourceLogicalRef = "$T::f", Action = ImportElementAction.Skip, TargetElementPublicId = chosen }],
        };

        var diff = await Sut().BuildAsync(Doc(new PblField { LogicalRef = "$T::f", Name = "Name", TypeCode = "Text" }), _appId, mapping, default);

        diff.Items.Single(i => i.ElementType == "Field").MatchedBy.Should().Be(ImportMatchedBy.Mapping);
    }
}
