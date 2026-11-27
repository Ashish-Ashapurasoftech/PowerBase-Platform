using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using Xunit;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// A form rule's conditions are judged against the record's stored values with the submitted ones laid over
/// them. Before this, an update that sent only the edited fields made every other field (and the system fields
/// Date Created / Record ID / Record Owner, which are never sent) look blank, so "Date Created is blank" fired
/// on a record that has a Date Created.
/// </summary>
public class FormRuleServerValidatorStoredValuesTests
{
    private const long DateCreatedFid = 1;
    private const long NameFid = 6;
    private const long StatusFid = 7;

    private static readonly AppTable Table = new() { Id = 5, AppId = 1 };
    private static readonly List<AppField> Fields = new()
    {
        new AppField { Id = 101, Fid = (int)DateCreatedFid, Name = "Date Created", TypeCode = "DateTime", IsSystem = true },
        new AppField { Id = 106, Fid = (int)NameFid, Name = "Name", TypeCode = "Text" },
        new AppField { Id = 107, Fid = (int)StatusFid, Name = "Status", TypeCode = "Text" },
    };

    private readonly IFormRuleRepository _rules = Substitute.For<IFormRuleRepository>();
    private readonly IFormRepository _forms = Substitute.For<IFormRepository>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();

    private static FormRule PreventSaveWhen(long fieldId, string op, string? value = null) => new()
    {
        Id = 1,
        FormId = 9,
        Name = "Test rule",
        IsActive = true,
        ConditionLogic = "all",
        Conditions = { new FormRuleCondition { ConditionKind = "field", AppFieldId = fieldId, Operator = op, Value = value } },
        Actions = { new FormRuleAction { ActionType = "PreventSave", ActionValue = "Rule fired" } },
    };

    private Task<List<RecordConstraintViolation>> RunAsync(
        FormRule rule,
        Dictionary<long, object?> submitted,
        Dictionary<long, object?>? stored,
        bool gridEdit = false)
    {
        _rules.ListActiveByTableIdAsync(Table.Id, Arg.Any<CancellationToken>()).Returns(new[] { rule });
        _forms.GetLayoutAsync(rule.FormId, Arg.Any<CancellationToken>()).Returns(Array.Empty<FormSection>());
        _forms.GetPagesAsync(rule.FormId, Arg.Any<CancellationToken>()).Returns(Array.Empty<FormPage>());

        var reportId = Guid.NewGuid();
        if (gridEdit)
        {
            _reports.GetAppTableIdByPublicIdAsync(reportId, Arg.Any<CancellationToken>()).Returns(Table.Id);
            _reports.GetAppliedGridEditRulesAsync(reportId, Arg.Any<CancellationToken>()).Returns(new[] { rule });
        }

        return FormRuleServerValidator.CollectViolationsAsync(
            Table, Fields, submitted, stored, currentUserId: 1,
            _rules, _forms,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(),
            Substitute.For<IUserRepository>(), Substitute.For<IAppUserRepository>(),
            new FormulaEngine(), CancellationToken.None,
            reportRepo: _reports, reportId: gridEdit ? reportId : null, isGridEditSave: gridEdit);
    }

    [Fact]
    public async Task Update_RuleOnAFieldTheWriteDidNotSend_SeesTheStoredValue()
    {
        // "Date Created is blank" must not fire: the record has one, it just was not part of the save.
        var violations = await RunAsync(
            PreventSaveWhen(DateCreatedFid, "isEmpty"),
            submitted: new() { [NameFid] = "New name" },
            stored: new() { [DateCreatedFid] = new DateTime(2026, 9, 11, 7, 21, 13), [NameFid] = "Old name" });

        violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_RuleStillFires_WhenTheStoredValueReallyIsBlank()
    {
        var violations = await RunAsync(
            PreventSaveWhen(StatusFid, "isEmpty"),
            submitted: new() { [NameFid] = "New name" },
            stored: new() { [StatusFid] = null, [NameFid] = "Old name" });

        violations.Should().ContainSingle().Which.Message.Should().Be("Rule fired");
    }

    [Fact]
    public async Task Update_ASubmittedValue_WinsOverTheStoredOne()
    {
        // Stored Status is "Open", the save sets it to blank: the rule must see the blank.
        var violations = await RunAsync(
            PreventSaveWhen(StatusFid, "isEmpty"),
            submitted: new() { [StatusFid] = null },
            stored: new() { [StatusFid] = "Open" });

        violations.Should().ContainSingle();
    }

    [Fact]
    public async Task GridEdit_OneCell_RuleOnAnotherField_SeesItsStoredValue()
    {
        // Grid Edit sends only the edited cell (Name). A rule about Status must judge the stored Status.
        var violations = await RunAsync(
            PreventSaveWhen(StatusFid, "isEmpty"),
            submitted: new() { [NameFid] = "Edited in grid" },
            stored: new() { [StatusFid] = "Open", [NameFid] = "Old name" },
            gridEdit: true);

        violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_HasNoStoredValues_SoAnUnsentFieldIsBlank()
    {
        var violations = await RunAsync(
            PreventSaveWhen(StatusFid, "isEmpty"),
            submitted: new() { [NameFid] = "New record" },
            stored: null);

        violations.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_RequireStillIgnoresAFieldTheWriteDidNotTouch()
    {
        // The Require check is unchanged: a field this write never touched is not demanded, even if its stored value is blank.
        var rule = new FormRule
        {
            Id = 2, FormId = 9, Name = "Require status", IsActive = true, ConditionLogic = "all",
            Actions = { new FormRuleAction { ActionType = "Require", TargetType = "Field", TargetFieldId = StatusFid } },
        };
        var violations = await RunAsync(
            rule,
            submitted: new() { [NameFid] = "New name" },
            stored: new() { [StatusFid] = null });

        violations.Should().BeEmpty();
    }
}
