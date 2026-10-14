using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>Starting an import from a PowerFlow in one call: a table import needs only its id, a file import also needs its file, which is uploaded and run
/// together and thrown away again if the run is not started.</summary>
public class ImportPowerFlowRunTests
{
    private static readonly Guid AppPublicId = Guid.NewGuid(), FileImport = Guid.NewGuid(), TableImport = Guid.NewGuid();
    private static readonly Guid DestinationTable = Guid.NewGuid(), UploadId = Guid.NewGuid(), RunId = Guid.NewGuid();

    private readonly IAppRepository _apps = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IImportDefinitionRepository _definitions = Substitute.For<IImportDefinitionRepository>();
    private readonly IPowerFlowImportActions _actions = Substitute.For<IPowerFlowImportActions>();
    private readonly StartPowerFlowImportHandler _sut;

    public ImportPowerFlowRunTests()
    {
        _sut = new StartPowerFlowImportHandler(_apps, _tables, _access, _definitions, _actions);
        _apps.GetIdByPublicIdAsync(AppPublicId, Arg.Any<CancellationToken>()).Returns(7L);
        _tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 7, PublicId = DestinationTable });
        _tables.GetByIdAsync(12, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 12, AppId = 99, PublicId = Guid.NewGuid() }); // another app's
        _definitions.GetByPublicIdAsync(FileImport, Arg.Any<CancellationToken>()).Returns(new ImportDefinition { PublicId = FileImport, DestinationTableId = 11, SourceKind = "file" });
        _definitions.GetByPublicIdAsync(TableImport, Arg.Any<CancellationToken>()).Returns(new ImportDefinition { PublicId = TableImport, DestinationTableId = 11, SourceKind = "table" });
        _actions.UploadAsync(DestinationTable, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(UploadId);
        _actions.StartAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), AppPublicId, Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ImportRunStart(RunId, false));
    }

    private static Stream File() => new MemoryStream([1, 2, 3]);

    private Task<ImportRunStart> Run(Guid? import = null, Stream? file = null, string? emails = null, string? token = null, Guid? app = null, long length = 3) =>
        _sut.HandleAsync(app ?? AppPublicId, import ?? TableImport, file, file is null ? null : "data.csv", length, emails, token, "https://app", default);

    // ---- a table import ----

    [Fact]
    public async Task A_table_import_starts_with_just_its_id()
    {
        var started = await Run();

        started.Should().Be(new ImportRunStart(RunId, false));
        await _actions.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, default!, default, default);
        await _actions.Received(1).StartAsync(TableImport, null, "https://app", AppPublicId, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_sent_to_a_table_import_is_refused_before_anything_is_stored()
    {
        var act = () => Run(file: File());

        (await act.Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("file");
        await _actions.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, default!, default, default);
        await _actions.DidNotReceiveWithAnyArgs().StartAsync(default, default, default, default, default, default, default);
    }

    // ---- a file import ----

    [Fact]
    public async Task A_file_import_uploads_the_file_to_the_imports_first_table_and_runs_it_with_the_upload()
    {
        var started = await Run(FileImport, File(), token: " retry-1 ");

        started.RunId.Should().Be(RunId);
        await _actions.Received(1).UploadAsync(DestinationTable, Arg.Any<Stream>(), "data.csv", 3, Arg.Any<CancellationToken>());
        await _actions.Received(1).StartAsync(FileImport, null, "https://app", AppPublicId, "retry-1", UploadId, Arg.Any<CancellationToken>());
        await _actions.DidNotReceiveWithAnyArgs().DiscardAsync(default, default);
    }

    [Fact]
    public async Task A_file_import_with_no_file_is_refused_and_says_where_to_send_it()
    {
        var act = () => Run(FileImport);

        var error = await act.Should().ThrowAsync<ValidationException>();
        error.Which.Errors["file"].Single().Should().Contain("'file'");
        await _actions.DidNotReceiveWithAnyArgs().StartAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task A_file_that_cannot_be_stored_stops_the_call_before_any_run_is_started()
    {
        _actions.UploadAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ValidationException(new Dictionary<string, string[]> { ["file"] = ["The file is empty."] }));

        var act = () => Run(FileImport, File(), length: 0);

        await act.Should().ThrowAsync<ValidationException>();
        await _actions.DidNotReceiveWithAnyArgs().StartAsync(default, default, default, default, default, default, default);
        await _actions.DidNotReceiveWithAnyArgs().DiscardAsync(default, default);
    }

    [Fact]
    public async Task When_the_run_is_refused_the_uploaded_file_is_thrown_away()
    {
        _actions.StartAsync(FileImport, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), AppPublicId, Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("This import is already running."));

        var act = () => Run(FileImport, File());

        await act.Should().ThrowAsync<ConflictException>("the real reason is what the caller sees");
        await _actions.Received(1).DiscardAsync(UploadId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failure_to_throw_the_file_away_never_hides_the_real_error()
    {
        _actions.StartAsync(FileImport, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), AppPublicId, Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("This import is already running."));
        _actions.DiscardAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));

        var act = () => Run(FileImport, File());

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_repeated_call_returns_the_first_run_and_the_file_sent_again_is_thrown_away()
    {
        _actions.StartAsync(FileImport, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), AppPublicId, "t", UploadId, Arg.Any<CancellationToken>()).Returns(new ImportRunStart(RunId, true));

        var started = await Run(FileImport, File(), token: "t");

        started.Replayed.Should().BeTrue();
        await _actions.Received(1).DiscardAsync(UploadId, Arg.Any<CancellationToken>());
    }

    // ---- what was sent ----

    [Theory]
    [InlineData("a@x.com, b@y.org;c@z.net", new[] { "a@x.com", "b@y.org", "c@z.net" })]
    [InlineData("  A@X.com  ", new[] { "a@x.com" })]
    [InlineData("a@x.com a@x.com", new[] { "a@x.com" })]
    public async Task The_people_to_tell_are_split_cleaned_and_passed_on(string raw, string[] expected)
    {
        await Run(emails: raw);

        await _actions.Received(1).StartAsync(TableImport, Arg.Is<IReadOnlyList<string>?>(l => l != null && l.SequenceEqual(expected)), Arg.Any<string?>(), AppPublicId,
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ; , ")]
    public async Task No_people_means_the_ones_saved_on_the_import_are_kept(string? raw)
    {
        await Run(emails: raw);

        await _actions.Received(1).StartAsync(TableImport, null, Arg.Any<string?>(), AppPublicId, Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Name <a@x.com>")]
    public async Task A_bad_address_is_refused_before_anything_is_stored(string raw)
    {
        var act = () => Run(FileImport, File(), emails: raw);

        await act.Should().ThrowAsync<ValidationException>();
        await _actions.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task An_overlong_email_list_or_client_token_is_refused()
    {
        (await ((Func<Task>)(() => Run(emails: new string('a', 4001)))).Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("notifyEmails");
        (await ((Func<Task>)(() => Run(token: new string('t', 201)))).Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("X-PowerBase-Client-Token");
        await _actions.DidNotReceiveWithAnyArgs().StartAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task A_blank_client_token_is_no_token()
    {
        await Run(token: "   ");

        await _actions.Received(1).StartAsync(TableImport, Arg.Is<IReadOnlyList<string>?>(l => l == null), Arg.Any<string?>(), AppPublicId, Arg.Is<string?>(t => t == null), Arg.Is<Guid?>(g => g == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_ids_are_refused()
    {
        (await ((Func<Task>)(() => Run(app: Guid.Empty))).Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("appId");
        (await ((Func<Task>)(() => _sut.HandleAsync(AppPublicId, Guid.Empty, null, null, 0, null, null, null, default))).Should().ThrowAsync<ValidationException>())
            .Which.Errors.Should().ContainKey("templateId");
    }

    // ---- access ----

    [Fact]
    public async Task Only_a_member_of_the_app_may_start_an_import_and_a_stranger_is_not_told_whether_the_request_is_valid()
    {
        _access.RequireMembershipByAppPublicIdAsync(AppPublicId, Arg.Any<CancellationToken>()).Returns(Task.FromException(new UnauthorizedActionException("read the app")));

        var act = () => Run(FileImport, File(), emails: "not-an-email");

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await _definitions.DidNotReceiveWithAnyArgs().GetByPublicIdAsync(default, default);
        await _actions.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task An_import_that_does_not_exist_or_belongs_to_another_app_is_a_404_and_nothing_is_stored()
    {
        _definitions.GetByPublicIdAsync(Arg.Is<Guid>(g => g != FileImport && g != TableImport), Arg.Any<CancellationToken>()).Returns((ImportDefinition?)null);
        var other = Guid.NewGuid();
        _definitions.GetByPublicIdAsync(other, Arg.Any<CancellationToken>()).Returns(new ImportDefinition { PublicId = other, DestinationTableId = 12, SourceKind = "file" });

        await ((Func<Task>)(() => Run(Guid.NewGuid(), File()))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => Run(other, File()))).Should().ThrowAsync<NotFoundException>();

        await _actions.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, default!, default, default);
    }
}
