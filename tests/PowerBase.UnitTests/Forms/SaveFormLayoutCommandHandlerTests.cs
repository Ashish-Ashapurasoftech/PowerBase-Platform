using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.SaveFormLayout;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Forms;

public class SaveFormLayoutCommandHandlerTests
{
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private static readonly Guid FormPublicId = Guid.NewGuid();

    private SaveFormLayoutCommandHandler CreateSut()
    {
        _formRepo.GetByPublicIdAsync(FormPublicId, Arg.Any<CancellationToken>())
            .Returns(new Form { Id = 1, PublicId = FormPublicId, AppTableId = 10, Name = "Main Form" });

        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { new() { Id = 100, Fid = 6, Name = "Name", TypeCode = "Text" } });

        return new SaveFormLayoutCommandHandler(_formRepo, _fieldRepo, _queryContext, _auditRepo);
    }

    private static SaveFormLayoutCommand CommandWith(Guid? sectionId, Guid? blockId, Guid? elementId) =>
        new(FormPublicId,
        [
            new FormSectionLayout(sectionId, "Section 1", false,
            [
                new FormBlockLayout(blockId, null, null, null,
                [
                    new FormElementLayout(elementId, 6, "Field", null, "Default", null,
                        true, true, true, "Auto", null, null, false, false, null),
                ]),
            ]),
        ]);

    private async Task<IReadOnlyList<FormSection>> CaptureSavedLayoutAsync(SaveFormLayoutCommand command)
    {
        IReadOnlyList<FormSection>? captured = null;
        await _formRepo.SaveLayoutAsync(
            Arg.Any<long>(),
            Arg.Do<IReadOnlyList<FormSection>>(s => captured = s),
            Arg.Any<IReadOnlyList<FormPage>?>(),
            Arg.Any<string?>(),
            Arg.Any<bool?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<FormSection, Guid>?>(),
            Arg.Any<IReadOnlyDictionary<FormElement, Guid>?>(),
            Arg.Any<CancellationToken>());

        await CreateSut().HandleAsync(command);

        captured.Should().NotBeNull();
        return captured!;
    }

    [Fact]
    public async Task HandleAsync_SuppliedPublicIds_ArePassedThroughToTheRepository()
    {
        // The layout is re-inserted wholesale on every save, so a caller that needs a section /
        // block / element to keep its identity has to supply its PublicId and have that survive
        // the round-trip. Two real callers depend on this: the QBL importer, which matches these
        // ids back to form-rule action targets in a later pass (without it, every rule action
        // target fails to resolve and the rule is dropped), and the form designer, which already
        // sends the existing publicId for rows it is re-saving.
        var sectionId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var elementId = Guid.NewGuid();

        var saved = await CaptureSavedLayoutAsync(CommandWith(sectionId, blockId, elementId));

        var section = saved.Should().ContainSingle().Subject;
        section.PublicId.Should().Be(sectionId);

        var block = section.Blocks.Should().ContainSingle().Subject;
        block.PublicId.Should().Be(blockId);

        var element = block.Elements.Should().ContainSingle().Subject;
        element.PublicId.Should().Be(elementId);
    }

    [Fact]
    public async Task HandleAsync_OmittedPublicIds_ArriveAsEmptyForTheRepositoryToGenerate()
    {
        // Newly-added rows send no PublicId. Guid.Empty is the entity-level stand-in for "absent"
        // (the entities carry a non-nullable Guid), which the repository maps to SQL NULL so the
        // insert generates one instead of writing an all-zero id.
        var saved = await CaptureSavedLayoutAsync(CommandWith(null, null, null));

        var section = saved.Should().ContainSingle().Subject;
        section.PublicId.Should().Be(Guid.Empty);
        section.Blocks[0].PublicId.Should().Be(Guid.Empty);
        section.Blocks[0].Elements[0].PublicId.Should().Be(Guid.Empty);
    }
}
