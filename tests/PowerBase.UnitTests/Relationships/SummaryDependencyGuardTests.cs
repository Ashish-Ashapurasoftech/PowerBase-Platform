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
            .Returns(new List<AppField> { Summary(30, "Avg of Amount", "Avg"), Summary(31, "Avg of Hours", "Avg") });

        var act = () => Run("Reference");

        var ex = (await act.Should().ThrowAsync<ConflictException>()).Which;
        ex.Message.Should().Contain("Client › Avg of Amount (Average)").And.Contain("Client › Avg of Hours (Average)").And.Contain("2 summary field(s)");
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
    public async Task Sum_min_and_max_are_allowed_on_a_reference_as_its_record_id()
    {
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Summary(30, "Sum of Amount", "Sum"), Summary(31, "Max of Amount", "Max"), Summary(37, "Min of Amount", "Min") });

        await FluentActions.Invoking(() => Run("Reference")).Should().NotThrowAsync();
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
    public async Task Combined_text_allows_a_change_to_any_type_with_a_value()
    {
        // Combined Text shows any value as the grid does — a Reference as its Record ID#, a user by name.
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Summary(33, "All Amounts", "CombinedText") });

        await FluentActions.Invoking(() => Run("Reference")).Should().NotThrowAsync();
        await FluentActions.Invoking(() => Run("User")).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Lowercase_function_name_from_an_old_import_is_still_checked()
    {
        _fieldRepo.ListByTableAsync(ClientTableId, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { Summary(30, "Avg of Amount", "avg") });

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
