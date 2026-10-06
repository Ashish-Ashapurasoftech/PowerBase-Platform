using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

public class ImportNotifyTests
{
    [Fact]
    public void Addresses_are_trimmed_lower_cased_and_de_duplicated()
    {
        ImportNotify.Normalize([" Boss@Example.COM ", "boss@example.com", "", "  ", "a@b.co"]).Should().Equal("boss@example.com", "a@b.co");
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("missing-domain@")]
    [InlineData("@no-user.com")]
    [InlineData("no-dot@localhost")]
    [InlineData("Name <a@b.com>")]
    [InlineData("a@b.com, c@d.com")]
    public void Anything_that_is_not_a_plain_address_is_refused(string address)
    {
        var act = () => ImportNotify.Normalize([address]);

        act.Should().Throw<ValidationException>().Which.Message.Should().Contain("not a valid email");
    }

    [Fact]
    public void Too_many_recipients_are_refused()
    {
        var act = () => ImportNotify.Normalize(Enumerable.Range(0, ImportNotify.MaxRecipients + 1).Select(i => $"u{i}@example.com"));

        act.Should().Throw<ValidationException>().Which.Message.Should().Contain("at most");
    }

    [Fact]
    public void No_list_is_an_empty_list() => ImportNotify.Normalize(null).Should().BeEmpty();
}

public class ImportLinksTests
{
    private static readonly string[] Allowed = ["https://app.example.com"];

    [Fact]
    public void A_configured_address_always_wins_and_loses_any_path() =>
        ImportLinks.TrustedBaseUrl("https://pb.example.com/some/path", "https://evil.example", Allowed).Should().Be("https://pb.example.com");

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("https://APP.example.com")]
    [InlineData("http://localhost:4200")]
    [InlineData("http://127.0.0.1:4200")]
    public void The_callers_origin_is_trusted_only_when_it_is_a_known_frontend(string origin) =>
        ImportLinks.TrustedBaseUrl(null, origin, Allowed).Should().NotBeNull();

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://app.example.com")]
    [InlineData("https://user:pw@app.example.com")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_origin_gives_no_link_rather_than_one_an_attacker_chose(string? origin) =>
        ImportLinks.TrustedBaseUrl(null, origin, Allowed).Should().BeNull();
}

/// <summary>The completion email: who gets it, what it says, and that it can never leak row values or break a run.</summary>
public class ImportCompletionNotifierTests
{
    private static readonly Guid TableId = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppRepository _apps = Substitute.For<IAppRepository>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly List<(string To, string Subject, string Body)> _sent = new();

    public ImportCompletionNotifierTests()
    {
        _users.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new User { Id = 5, Email = "Starter@Example.com" });
        _tables.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = TableId });
        _apps.GetPublicIdByIdAsync(1, Arg.Any<CancellationToken>()).Returns(AppId);
        _email.When(e => e.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()))
            .Do(c => _sent.Add((c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<string>(2))));
    }

    private ImportCompletionNotifier Notifier() => new(_users, _tables, _apps, _email, NullLogger<ImportCompletionNotifier>.Instance);

    private static ImportRun Run(string status = ImportRunStatus.Partial) => new()
    {
        PublicId = Guid.NewGuid(), TriggeredByUserId = 5, Status = status, RowsRead = 1500, Inserted = 1000, Updated = 400, Skipped = 60, Errored = 40,
        StartedOn = new DateTime(2026, 3, 9, 10, 0, 0), CompletedOn = new DateTime(2026, 3, 9, 10, 2, 5), ErrorDetail = null
    };

    private static ImportRunSnapshot Snapshot(string name = "Nightly sync", string? baseUrl = "https://app.example.com", params string[] extra) =>
        new(TableId, new ImportDefinitionConfig { Name = name, NotifyEmails = [.. extra] }, baseUrl);

    [Fact]
    public async Task The_person_who_started_the_run_and_everyone_listed_are_emailed_once_each()
    {
        await Notifier().NotifyAsync(Run(), Snapshot(extra: ["boss@example.com", "starter@example.com"]));

        _sent.Select(s => s.To).Should().Equal("starter@example.com", "boss@example.com"); // the starter is not emailed twice
    }

    [Fact]
    public async Task The_email_gives_the_counts_and_a_link_to_the_run_page()
    {
        var run = Run();

        await Notifier().NotifyAsync(run, Snapshot());

        var (_, subject, body) = _sent[0];
        subject.Should().Be("Import \"Nightly sync\" finished with some rows not imported");
        body.Should().Contain("1,500").And.Contain("1,400").And.Contain("1,000 added, 400 updated").And.Contain("60").And.Contain("40").And.Contain("2 min 5 s");
        body.Should().Contain($"https://app.example.com/app/{AppId}/tables/{TableId}/settings/imports/runs/{run.PublicId}");
    }

    [Fact]
    public async Task Without_a_trusted_address_the_email_has_no_link_but_still_says_where_to_look()
    {
        await Notifier().NotifyAsync(Run(), Snapshot(baseUrl: null));

        _sent[0].Body.Should().NotContain("href").And.Contain("Open the run in PowerBase");
    }

    [Theory]
    [InlineData(ImportRunStatus.Success, "finished")]
    [InlineData(ImportRunStatus.Partial, "finished with some rows not imported")]
    [InlineData(ImportRunStatus.Failed, "failed")]
    [InlineData(ImportRunStatus.Cancelled, "was cancelled")]
    public async Task The_subject_says_how_the_run_ended(string status, string headline)
    {
        await Notifier().NotifyAsync(Run(status), Snapshot());

        _sent[0].Subject.Should().EndWith(headline);
    }

    [Fact]
    public async Task A_hostile_import_name_cannot_inject_markup_or_mail_headers()
    {
        await Notifier().NotifyAsync(Run(), Snapshot(name: "<script>x</script>\r\nBcc: evil@example.com"));

        var (_, subject, body) = _sent[0];
        subject.Should().NotContain("\r").And.NotContain("\n");
        body.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task A_failure_detail_is_shown_encoded()
    {
        var run = Run(ImportRunStatus.Failed);
        run.ErrorDetail = "Field <b>X</b> is missing";

        await Notifier().NotifyAsync(run, Snapshot());

        _sent[0].Body.Should().Contain("Field &lt;b&gt;X&lt;/b&gt; is missing");
    }

    [Fact]
    public async Task One_recipient_that_cannot_be_reached_does_not_stop_the_others()
    {
        _email.When(e => e.SendEmailAsync("boss@example.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("mailbox full"));

        await Notifier().NotifyAsync(Run(), Snapshot(extra: ["boss@example.com", "other@example.com"]));

        // The failed attempt was still made (and recorded by the fake); what matters is that the next recipient was reached.
        _sent.Select(s => s.To).Should().Equal("starter@example.com", "boss@example.com", "other@example.com");
    }

    [Fact]
    public async Task Whatever_goes_wrong_the_notifier_never_throws()
    {
        _users.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns<User>(_ => throw new InvalidOperationException("database down"));

        var act = () => Notifier().NotifyAsync(Run(), Snapshot());

        await act.Should().NotThrowAsync();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_starter_without_an_address_and_no_one_else_to_tell_sends_nothing()
    {
        _users.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new User { Id = 5, Email = "" });

        await Notifier().NotifyAsync(Run(), Snapshot());

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mail_server_that_refuses_the_message_is_reported_in_a_sentence_for_the_run()
    {
        _email.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new System.Net.Mail.SmtpException("Wrapper", new InvalidOperationException("5.7.1 Sender address not verified" + (char)13 + (char)10 + "noreply@example.com")));

        var note = await Notifier().NotifyAsync(Run(), Snapshot());

        note.Should().Be("The completion email could not be sent: 5.7.1 Sender address not verifiednoreply@example.com");
        note.Should().NotContain(((char)13).ToString()).And.NotContain(((char)10).ToString());
    }

    [Fact]
    public async Task A_very_long_server_message_is_cut_short()
    {
        _email.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException(new string('x', 5000)));

        var note = await Notifier().NotifyAsync(Run(), Snapshot());

        note!.Length.Should().BeLessThan(320);
    }

    [Fact]
    public async Task When_the_mail_goes_out_there_is_nothing_to_report()
    {
        (await Notifier().NotifyAsync(Run(), Snapshot())).Should().BeNull();
    }

    [Fact]
    public async Task One_recipient_failing_is_reported_even_though_the_others_got_the_mail()
    {
        _email.SendEmailAsync("bad@example.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("mailbox unavailable"));

        var note = await Notifier().NotifyAsync(Run(), Snapshot(extra: ["bad@example.com"]));

        note.Should().Contain("mailbox unavailable");
        _sent.Should().ContainSingle(m => m.To == "starter@example.com");
    }

    [Fact]
    public async Task No_address_at_all_is_said_so_nobody_wonders_why_no_mail_came()
    {
        _users.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new User { Id = 5, Email = "" });

        (await Notifier().NotifyAsync(Run(), Snapshot())).Should().Contain("no email address");
    }

    [Fact]
    public async Task The_email_never_carries_row_values()
    {
        var run = Run();
        run.ErrorDetail = null;

        await Notifier().NotifyAsync(run, Snapshot());

        _sent[0].Body.Should().Contain("Row values are not included");
        await _email.DidNotReceiveWithAnyArgs().SendEmailAsync(default!, default!, default!, null, null, null, null, default);
    }
}
