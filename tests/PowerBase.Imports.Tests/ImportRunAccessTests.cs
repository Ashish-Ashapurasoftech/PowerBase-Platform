using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>Who may see a run's rejected rows and download its details file: the rows carry values from the source table,
/// read with the access of whoever started the run.</summary>
public class ImportRunAccessTests
{
    private const long Initiator = 5;
    private readonly IImportRunRepository _runs = Substitute.For<IImportRunRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService, IFileStorageReadService>();
    private readonly ImportRun _run;

    public ImportRunAccessTests()
    {
        _run = new ImportRun
        {
            Id = 1, PublicId = Guid.NewGuid(), TriggeredByUserId = Initiator, Status = ImportRunStatus.Partial, Errored = 2,
            FeedbackFileUrl = "/files/f.csv", CompletedOn = new DateTime(2026, 3, 9, 14, 5, 0),
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(Guid.NewGuid(), new ImportDefinitionConfig()))
        };
        _runs.GetByPublicIdAsync(_run.PublicId, Arg.Any<CancellationToken>()).Returns(_run);
        _runs.ListIssuesAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
            new List<ImportRunIssueItem> { new(5, 6, "errored", ImportReason.RequiredMissing, "'Name' is required.", null) });
    }

    private static IQueryContext User(long id, bool superAdmin = false, bool tenantAdmin = false)
    {
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(id);
        user.IsSuperAdmin.Returns(superAdmin);
        user.IsTenantAdmin.Returns(tenantAdmin);
        return user;
    }

    private Task<ImportRunDetail> Detail(IQueryContext user) => new GetImportRunHandler(_access, _runs, user).HandleAsync(_run.PublicId, default);

    [Fact]
    public async Task The_person_who_started_the_run_sees_the_rejected_rows_and_can_download_the_file()
    {
        var detail = await Detail(User(Initiator));

        detail.Issues.Should().HaveCount(1);
        detail.HasFeedback.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task An_admin_sees_them_too(bool superAdmin, bool tenantAdmin)
    {
        var detail = await Detail(User(99, superAdmin, tenantAdmin));

        detail.Issues.Should().HaveCount(1);
        detail.HasFeedback.Should().BeTrue();
    }

    [Fact]
    public async Task Another_member_of_the_app_sees_the_status_and_counts_but_not_the_rows()
    {
        var detail = await Detail(User(99));

        detail.Run.Errored.Should().Be(2);
        detail.Issues.Should().BeEmpty();
        detail.HasFeedback.Should().BeFalse();
        await _runs.DidNotReceive().ListIssuesAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Another_member_cannot_download_the_details_file()
    {
        var download = () => new GetImportFeedbackHandler(_access, _runs, _storage, User(99)).HandleAsync(_run.PublicId, default);

        await download.Should().ThrowAsync<UnauthorizedActionException>();
        await ((IFileStorageReadService)_storage).DidNotReceive().OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_initiator_downloads_the_file_under_a_dated_name_and_the_storage_path_stays_server_side()
    {
        ((IFileStorageReadService)_storage).OpenReadAsync("/files/f.csv", Arg.Any<CancellationToken>()).Returns(new MemoryStream([1, 2, 3]));

        var file = await new GetImportFeedbackHandler(_access, _runs, _storage, User(Initiator)).HandleAsync(_run.PublicId, default);

        file.FileName.Should().Be("import-feedback-20260309-1405.csv");
        file.Content.Length.Should().Be(3);
    }

    [Fact]
    public async Task A_file_that_is_gone_from_storage_is_reported_as_not_found_not_as_a_crash()
    {
        ((IFileStorageReadService)_storage).OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<Stream>>(_ => throw new FileNotFoundException());

        var download = () => new GetImportFeedbackHandler(_access, _runs, _storage, User(Initiator)).HandleAsync(_run.PublicId, default);

        await download.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_run_with_no_feedback_file_is_not_found()
    {
        _run.FeedbackFileUrl = null;

        var download = () => new GetImportFeedbackHandler(_access, _runs, _storage, User(Initiator)).HandleAsync(_run.PublicId, default);

        await download.Should().ThrowAsync<NotFoundException>();
    }
}
