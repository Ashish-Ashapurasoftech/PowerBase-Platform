using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>The import templates the Import with CSV step lists: the import feature's own PowerFlow list (file imports only),
/// with the merge field each one matches records on.</summary>
public class PipelineImportTemplatesTests
{
    private readonly Guid _appId = Guid.NewGuid(), _tableId = Guid.NewGuid();
    private readonly IAppRepository _apps = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IImportDefinitionRepository _definitions = Substitute.For<IImportDefinitionRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();

    public PipelineImportTemplatesTests()
    {
        _apps.GetIdByPublicIdAsync(_appId, Arg.Any<CancellationToken>()).Returns(5L);
        _tables.GetByPublicIdAsync(_tableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 7, AppId = 5, PublicId = _tableId });
        _definitions.ListEntitiesByAppAsync(5, Arg.Any<CancellationToken>()).Returns(new List<ImportDefinition>());
        _fields.ListByTableAsync(7, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Fid = 6, Name = "C_email", Label = "Email" },
            new() { Fid = 8, Name = "C_code", Label = "" }
        });
        _services.GetService(typeof(ListPowerFlowImportTemplatesHandler))
            .Returns(new ListPowerFlowImportTemplatesHandler(_apps, _tables, Substitute.For<IAppAccessService>(), _definitions));
        _services.GetService(typeof(IImportDefinitionRepository)).Returns(_definitions);
        _services.GetService(typeof(IAppFieldRepository)).Returns(_fields);
    }

    private ImportDefinitionListItem Item(Guid id, string name, string type, string kind) =>
        new(id, name, type, Guid.Empty, "", 3, false, null, null, null, [], null, null, kind, 1, _tableId, "People");

    private void Saved(Guid id, int? mergeFid) =>
        _definitions.GetByPublicIdAsync(id, Arg.Any<CancellationToken>()).Returns(new ImportDefinition { PublicId = id, DestinationTableId = 7, MergeKeyFid = mergeFid, ImportType = mergeFid is null ? "copy" : "merge" });

    [Fact]
    public async Task ListsOnlyFileImportsWithTheirMergeField()
    {
        Guid merge = Guid.NewGuid(), copy = Guid.NewGuid(), fromTable = Guid.NewGuid(), unnamed = Guid.NewGuid(), gone = Guid.NewGuid();
        _definitions.ListByAppAsync(5, Arg.Any<CancellationToken>()).Returns(new List<ImportDefinitionListItem>
        {
            Item(merge, "Contacts", "merge", "file"),
            Item(copy, "Add rows", "copy", "file"),
            Item(fromTable, "From table", "merge", "table"),
            Item(unnamed, "Codes", "merge", "file"),
            Item(gone, "Old key", "merge", "file")
        });
        Saved(merge, 6); Saved(copy, null); Saved(unnamed, 8); Saved(gone, 99);

        var templates = await PipelineImportTemplates.ListAsync(_services, _appId, _tableId, default);

        templates.Select(t => t.TemplateId).Should().Equal(merge, copy, unnamed, gone);
        templates[0].Should().Match<PipelineImportTemplateDto>(t => t.MergeFieldId == 6 && t.MergeFieldLabel == "Email" && t.TableName == "People");
        templates[1].Should().Match<PipelineImportTemplateDto>(t => t.MergeFieldId == null && t.MergeFieldLabel == null && t.ImportType == "copy");
        templates[2].MergeFieldLabel.Should().Be("C_code", "a field without a label shows its name");
        templates[3].MergeFieldLabel.Should().Be("Field 99", "a merge key whose field is gone still says which one");
        await _fields.Received(1).ListByTableAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoFileImports_GivesAnEmptyList()
    {
        _definitions.ListByAppAsync(5, Arg.Any<CancellationToken>()).Returns(new List<ImportDefinitionListItem> { Item(Guid.NewGuid(), "T", "merge", "table") });
        (await PipelineImportTemplates.ListAsync(_services, _appId, null, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task NeedsAttention_IsPassedOn()
    {
        var id = Guid.NewGuid();
        _definitions.ListByAppAsync(5, Arg.Any<CancellationToken>()).Returns(new List<ImportDefinitionListItem>
        {
            new(id, "Broken", "copy", Guid.Empty, "", 3, true, "A mapped field was deleted.", null, null, [], null, null, "file", 1, _tableId, "People")
        });
        Saved(id, null);

        var template = (await PipelineImportTemplates.ListAsync(_services, _appId, _tableId, default)).Single();

        template.NeedsAttention.Should().BeTrue();
        template.AttentionReason.Should().Be("A mapped field was deleted.");
    }
}
