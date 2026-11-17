using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.ImportNotes;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Fields;

public class FieldImportNotesHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppFieldImportNoteRepository _noteRepo = Substitute.For<IAppFieldImportNoteRepository>();
    private readonly IQueryContext _ctx = Substitute.For<IQueryContext>();

    private readonly AppTable _table = new() { Id = 5, PublicId = Guid.NewGuid(), Name = "Tasks" };
    private readonly AppField _field = new() { Id = 11, PublicId = Guid.NewGuid(), AppTableId = 5, Name = "Total" };

    private FieldImportNotesHandler Sut()
    {
        _ctx.UserId.Returns(42L);
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        _fieldRepo.GetByPublicIdAsync(_field.PublicId, Arg.Any<CancellationToken>()).Returns(_field);
        _fieldRepo.ListByTableAsync(5L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)[_field]);
        return new FieldImportNotesHandler(_tableRepo, _fieldRepo, _noteRepo, _ctx);
    }

    [Fact]
    public async Task SummarizeTable_ReportsOnlyFieldsWithUnresolvedNotes_WithTheWorstSeverity()
    {
        var sut = Sut();
        _noteRepo.SummarizeByTableAsync(5L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppFieldImportNoteSummary>)
        [
            new AppFieldImportNoteSummary(11, 3, 2, 0, true),
            new AppFieldImportNoteSummary(999, 1, 1, 0, false), // a field that is no longer there
        ]);

        var result = await sut.SummarizeTableAsync(_table.PublicId);

        var summary = result.Should().ContainSingle().Subject;
        summary.FieldPublicId.Should().Be(_field.PublicId);
        summary.ActiveCount.Should().Be(3);
        summary.Severity.Should().Be(ImportNoteSeverities.Warning);
        summary.HasPlaceholder.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0, "Information")]
    [InlineData(1, 0, "Warning")]
    [InlineData(1, 1, "Error")]
    public async Task SummarizeTable_SeverityIsTheWorstOne(int warnings, int errors, string expected)
    {
        var sut = Sut();
        _noteRepo.SummarizeByTableAsync(5L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppFieldImportNoteSummary>)
            [new AppFieldImportNoteSummary(11, 1 + warnings + errors, warnings, errors, false)]);

        (await sut.SummarizeTableAsync(_table.PublicId)).Single().Severity.Should().Be(expected);
    }

    [Fact]
    public async Task ListForField_ReturnsTheNotesWithTheCompleteOriginalDefinition()
    {
        var sut = Sut();
        var huge = new string('x', 9000);
        _noteRepo.ListByFieldAsync(11L, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppFieldImportNote>)
            [new AppFieldImportNote { PublicId = Guid.NewGuid(), Severity = "Warning", Message = "m", IsPlaceholder = true, OriginalDefinition = huge }]);

        var notes = await sut.ListForFieldAsync(_table.PublicId, _field.PublicId);

        notes.Single().OriginalDefinition.Should().HaveLength(9000);
        notes.Single().IsPlaceholder.Should().BeTrue();
    }

    [Fact]
    public async Task ListForField_FieldOfAnotherTable_IsNotFound()
    {
        var sut = Sut();
        _field.AppTableId = 6;

        var act = () => sut.ListForFieldAsync(_table.PublicId, _field.PublicId);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Resolve_WithoutANote_ResolvesAllOfTheFieldsNotesAsTheCurrentUser()
    {
        var sut = Sut();
        _noteRepo.ResolveByFieldAsync(11L, 42L, Arg.Any<CancellationToken>()).Returns(2);

        (await sut.ResolveAsync(_table.PublicId, _field.PublicId, null)).Should().Be(2);
    }

    [Fact]
    public async Task Resolve_OneNote_ResolvesIt_AndAnUnknownOneIsNotFound()
    {
        var sut = Sut();
        var known = Guid.NewGuid();
        _noteRepo.ResolveAsync(11L, known, 42L, Arg.Any<CancellationToken>()).Returns(true);

        (await sut.ResolveAsync(_table.PublicId, _field.PublicId, known)).Should().Be(1);

        var act = () => sut.ResolveAsync(_table.PublicId, _field.PublicId, Guid.NewGuid());
        await act.Should().ThrowAsync<NotFoundException>();
    }
}
