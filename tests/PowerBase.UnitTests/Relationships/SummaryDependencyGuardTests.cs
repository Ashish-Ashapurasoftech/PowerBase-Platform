using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

public class SummaryDependencyGuardTests
{
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();

    private const long ClientTableId = 5;
    private const long TaskTableId = 77;

    // Task.Amount (fid 12), linked to Client through reference fid 10.
    private static readonly AppField Amount = new() { Id = 12, Fid = 12, AppTableId = TaskTableId, Name = "Amount", TypeCode = "Number" };

    private static AppField Summary(long id, string label, string function, int? targetFid = 12, long childTableId = TaskTableId, int referenceFid = 10) => new()
    {
        Id = id, Fid = (int)id, AppTableId = ClientTableId, Name = label, Label = label, TypeCode = "Summary",
        Settings = $"{{\"childTableId\":{childTableId},\"referenceFid\":{referenceFid},\"function\":\"{function}\""
                   + (targetFid is int t ? $",\"targetFid\":{t}" : "") + "}",
    };

    public SummaryDependencyGuardTests()
    {
        _relRepo.ListByChildTableAsync(TaskTableId, Arg.Any<CancellationToken>())
            .Returns(new List<Relationship> { new() { Id = 1, ParentTableId = ClientTableId, ChildTableId = TaskTableId, ReferenceFid = 10 } });
        _tableRepo.GetByIdAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ClientTableId, Name = "Client" });
    }

    private Task Run(string newTypeCode) =>
        SummaryDependencyGuard.EnsureTypeChangeKeepsSummariesValidAsync(Amount, newTypeCode, _relRepo, _tableRepo, _fieldRepo, CancellationToken.None);

    [Fact]
    public async Task Change_that_breaks_a_summary_is_blocked_and_lists_it()
    {
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Summary(30, "Sum of Amount", "Sum"), Summary(31, "Max of Amount", "Max") });

        var act = () => Run("Reference");

        var ex = (await act.Should().ThrowAsync<ConflictException>()).Which;
        ex.Message.Should().Contain("Client › Sum of Amount (Sum)").And.Contain("Client › Max of Amount (Maximum)").And.Contain("2 summary field(s)");
    }

    [Fact]
    public async Task Change_that_every_summary_still_supports_is_allowed()
    {
        // Number → Currency: still numeric, so Sum/Max keep working.
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Summary(30, "Sum of Amount", "Sum"), Summary(31, "Max of Amount", "Max") });

        await FluentActions.Invoking(() => Run("Currency")).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Summaries_that_work_on_any_type_are_not_affected()
    {
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Summary(32, "Distinct Amounts", "DistinctCount"),
            Summary(34, "# of Tasks", "Count", targetFid: null),
        });

        await FluentActions.Invoking(() => Run("Reference")).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Combined_text_blocks_a_change_to_a_type_with_no_text_form()
    {
        // A Reference value is a bare record id — Combined Text can't show it, so the change is refused.
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Summary(33, "All Amounts", "CombinedText") });

        (await FluentActions.Invoking(() => Run("Reference")).Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("Client › All Amounts (Combined Text)");
    }

    [Fact]
    public async Task Lowercase_function_name_from_an_old_import_is_still_checked()
    {
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Summary(30, "Sum of Amount", "sum") });

        await FluentActions.Invoking(() => Run("Reference")).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Summaries_over_other_fields_or_relationships_are_ignored()
    {
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            Summary(35, "Sum of Hours", "Sum", targetFid: 13),              // a different child field
            Summary(36, "Sum via other link", "Sum", referenceFid: 11),     // another relationship's reference
        });

        await FluentActions.Invoking(() => Run("Reference")).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Same_type_is_a_noop()
    {
        await Run("Number");

        await _relRepo.DidNotReceive().ListByChildTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
