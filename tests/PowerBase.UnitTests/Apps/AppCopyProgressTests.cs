using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using PowerBase.Application.Apps.Queries.GetAppCopyProgress;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Apps;

/// <summary>Copy App progress bar: the procedure's messages, the in-memory store and the query that reads it.</summary>
public class AppCopyProgressTests
{
    private const long USER_ID = 7;
    private const long OTHER_USER_ID = 8;

    private static AppCopyProgressStore NewStore() => new(new MemoryCache(new MemoryCacheOptions()));

    [Theory]
    [InlineData("copy-progress:52:Copying forms and rules", 52, "Copying forms and rules")]
    [InlineData("copy-progress:13:Creating tables 0 of 13", 13, "Creating tables 0 of 13")]
    [InlineData("copy-progress:1:Starting", 1, "Starting")]
    public void TryParseProgress_ProcedureMessage_ReturnsPercentAndStage(string message, int percent, string stage)
    {
        AppCopyRepository.TryParseProgress(message, out var p, out var s).Should().BeTrue();
        p.Should().Be(percent);
        s.Should().Be(stage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Warning! The maximum key length for a nonclustered index is 1700 bytes.")]
    [InlineData("copy-progress:")]
    [InlineData("copy-progress:abc:Starting")]
    [InlineData("copy-progress::Starting")]
    public void TryParseProgress_OtherMessage_ReturnsFalse(string? message)
    {
        AppCopyRepository.TryParseProgress(message, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Get_NothingReported_ReturnsNull()
    {
        NewStore().Get(Guid.NewGuid(), USER_ID).Should().BeNull();
    }

    [Fact]
    public void Report_ThenGet_ReturnsTheLatestStep()
    {
        var store = NewStore();
        var id = Guid.NewGuid();

        store.Report(id, USER_ID, 13, "Creating tables 0 of 13");
        store.Report(id, USER_ID, 25, "Creating tables 4 of 13");

        var progress = store.Get(id, USER_ID)!;
        progress.Percent.Should().Be(25);
        progress.Stage.Should().Be("Creating tables 4 of 13");
        progress.IsDone.Should().BeFalse();
    }

    [Fact]
    public void Report_LowerPercent_KeepsTheHigherOneButTheNewStage()
    {
        var store = NewStore();
        var id = Guid.NewGuid();

        store.Report(id, USER_ID, 52, "Creating tables 13 of 13");
        store.Report(id, USER_ID, 40, "Copying forms and rules");

        var progress = store.Get(id, USER_ID)!;
        progress.Percent.Should().Be(52);
        progress.Stage.Should().Be("Copying forms and rules");
    }

    [Fact]
    public void Report_NeverReaches100BeforeTheCopyIsSaved()
    {
        var store = NewStore();
        var id = Guid.NewGuid();

        store.Report(id, USER_ID, 150, "Finishing");

        store.Get(id, USER_ID)!.Percent.Should().Be(99);
    }

    [Fact]
    public void Complete_Success_Is100AndDone()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        store.Report(id, USER_ID, 98, "Finishing");

        store.Complete(id, USER_ID, failed: false);

        var progress = store.Get(id, USER_ID)!;
        progress.Percent.Should().Be(100);
        progress.IsDone.Should().BeTrue();
        progress.IsFailed.Should().BeFalse();
    }

    [Fact]
    public void Complete_Failure_KeepsThePercentAndIsFailed()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        store.Report(id, USER_ID, 52, "Copying forms and rules");

        store.Complete(id, USER_ID, failed: true);

        var progress = store.Get(id, USER_ID)!;
        progress.Percent.Should().Be(52);
        progress.IsDone.Should().BeTrue();
        progress.IsFailed.Should().BeTrue();
    }

    [Fact]
    public void Report_AfterComplete_IsIgnored()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        store.Complete(id, USER_ID, failed: false);

        store.Report(id, USER_ID, 13, "Creating tables 0 of 13");

        store.Get(id, USER_ID)!.Percent.Should().Be(100);
    }

    [Fact]
    public void AnotherUser_CannotReadOrChangeTheProgress()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        store.Report(id, USER_ID, 25, "Creating tables 4 of 13");

        store.Get(id, OTHER_USER_ID).Should().BeNull();
        store.Report(id, OTHER_USER_ID, 90, "Linking settings to the copy");
        store.Complete(id, OTHER_USER_ID, failed: true);

        var progress = store.Get(id, USER_ID)!;
        progress.Percent.Should().Be(25);
        progress.IsDone.Should().BeFalse();
    }

    [Fact]
    public async Task QueryHandler_ReadsTheCallersProgressOnly()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        store.Report(id, USER_ID, 25, "Creating tables 4 of 13");
        var caller = Substitute.For<IQueryContext>();
        var copyRepo = Substitute.For<IAppCopyRepository>();

        caller.UserId.Returns(USER_ID);
        (await new GetAppCopyProgressQueryHandler(store, copyRepo, caller).HandleAsync(id))!.Percent.Should().Be(25);

        caller.UserId.Returns(OTHER_USER_ID);
        (await new GetAppCopyProgressQueryHandler(store, copyRepo, caller).HandleAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task QueryHandler_CopyWithData_ReadsTheRunFromTheDatabaseFirst()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        // The structure step reported in memory; the run in the database has moved on since.
        store.Report(id, USER_ID, 3, "Saving the copy");
        var caller = Substitute.For<IQueryContext>();
        caller.UserId.Returns(USER_ID);
        var copyRepo = Substitute.For<IAppCopyRepository>();
        var newApp = Guid.NewGuid();
        copyRepo.GetDataCopyProgressAsync(id, USER_ID, Arg.Any<CancellationToken>())
            .Returns(new AppCopyProgress(100, "Done", IsDone: true, IsFailed: false, NewAppPublicId: newApp, RecordsCopied: 1506503));

        var progress = await new GetAppCopyProgressQueryHandler(store, copyRepo, caller).HandleAsync(id);

        progress!.IsDone.Should().BeTrue();
        progress.NewAppPublicId.Should().Be(newApp);
        progress.RecordsCopied.Should().Be(1506503);
    }
}
