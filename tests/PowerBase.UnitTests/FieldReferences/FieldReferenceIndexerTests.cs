using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.FieldReferences;

public class FieldReferenceIndexerTests
{
    private const long TableId = 7;

    private readonly IFieldReferenceRepository _references = Substitute.For<IFieldReferenceRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();
    private readonly IFormRepository _forms = Substitute.For<IFormRepository>();
    private readonly IFormRuleRepository _rules = Substitute.For<IFormRuleRepository>();
    private readonly Guid _tablePublicId = Guid.NewGuid();

    public FieldReferenceIndexerTests()
    {
        _fields.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 1001, Fid = 1, AppTableId = TableId, Name = "Qty", Label = "Qty", TypeCode = "Number" },
            new() { Id = 1002, Fid = 2, AppTableId = TableId, Name = "Price", Label = "Price", TypeCode = "Number" },
        });
        _tables.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = TableId, PublicId = _tablePublicId });
    }

    private FieldReferenceIndexer NewIndexer() =>
        new(_references, _fields, _tables, _reports, _forms, _rules, new FormulaEngine(), NullLogger<FieldReferenceIndexer>.Instance);

    private static string Definition(params long[] columns) => JsonSerializer.Serialize(new ReportDefinition { Columns = [.. columns] });

    [Fact]
    public async Task ReindexReport_replaces_the_reports_rows_with_what_its_definition_references()
    {
        var reportId = Guid.NewGuid();
        _reports.GetByPublicIdAsync(reportId, Arg.Any<CancellationToken>())
            .Returns(new Report { Id = 55, PublicId = reportId, AppTableId = TableId, Definition = Definition(2) });

        await NewIndexer().ReindexReportAsync(reportId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.Report, 55,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows =>
                rows.Single().TargetFieldId == 1002 && rows.Single().Usage == FieldReferenceUsages.Column && rows.Single().SourceTableId == TableId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReindexReport_of_a_deleted_report_removes_its_rows_instead()
    {
        var reportId = Guid.NewGuid();
        _reports.GetByPublicIdAsync(reportId, Arg.Any<CancellationToken>()).ThrowsAsync(new NotFoundException("Report", reportId));

        await NewIndexer().ReindexReportAsync(reportId);

        await _references.Received(1).DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Report, reportId, Arg.Any<CancellationToken>());
        await _references.DidNotReceiveWithAnyArgs().ReplaceForSourceAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Indexing_failures_are_swallowed_so_a_save_never_fails_because_of_the_index()
    {
        var reportId = Guid.NewGuid();
        _reports.GetByPublicIdAsync(reportId, Arg.Any<CancellationToken>())
            .Returns(new Report { Id = 55, PublicId = reportId, AppTableId = TableId, Definition = Definition(1) });
        _references.ReplaceForSourceAsync(default!, default, default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("db down")));

        var act = () => NewIndexer().ReindexReportAsync(reportId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReindexFormRule_resolves_action_targets_through_the_forms_layout()
    {
        var ruleId = Guid.NewGuid();
        _rules.GetByPublicIdAsync(ruleId, Arg.Any<CancellationToken>()).Returns(new FormRule
        {
            Id = 70, PublicId = ruleId, FormId = 3,
            Actions = [new FormRuleAction { ActionType = "Show", TargetElementId = 500 }],
        });
        _forms.GetTableIdByFormIdAsync(3, Arg.Any<CancellationToken>()).Returns(TableId);
        _forms.GetLayoutAsync(3, Arg.Any<CancellationToken>()).Returns(new List<FormSection>
        {
            new() { Blocks = [new FormSectionBlock { Elements = [new FormElement { Id = 500, ElementType = "Field", AppFieldId = 2 }] }] },
        });

        await NewIndexer().ReindexFormRuleAsync(ruleId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.FormRule, 70,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows =>
                rows.Single().TargetFieldId == 1002 && rows.Single().Usage == FieldReferenceUsages.RuleTarget),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReindexForm_indexes_the_elements_and_every_rule_on_the_form()
    {
        var formId = Guid.NewGuid();
        _forms.GetByPublicIdAsync(formId, Arg.Any<CancellationToken>()).Returns(new Form { Id = 3, PublicId = formId, AppTableId = TableId });
        _forms.GetLayoutAsync(3, Arg.Any<CancellationToken>()).Returns(new List<FormSection>
        {
            new() { Blocks = [new FormSectionBlock { Elements = [new FormElement { Id = 500, ElementType = "Field", AppFieldId = 1 }] }] },
        });
        _rules.ListByFormAsync(3, Arg.Any<CancellationToken>()).Returns(new List<FormRule>
        {
            new() { Id = 70, Conditions = [new FormRuleCondition { AppFieldId = 2 }] },
        });

        await NewIndexer().ReindexFormAsync(formId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.Form, 3,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Single().TargetFieldId == 1001),
            Arg.Any<CancellationToken>());
        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.FormRule, 70,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Single().TargetFieldId == 1002),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RebuildTable_clears_reindexes_every_source_and_then_marks_the_table_indexed()
    {
        var reportId = Guid.NewGuid();
        _references.ListReportSourcesAsync(TableId, Arg.Any<CancellationToken>())
            .Returns(new List<Report> { new() { Id = 55, PublicId = reportId, AppTableId = TableId, Definition = Definition(1) } });
        _forms.ListByTableAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(new List<Form>());

        await NewIndexer().RebuildTableAsync(TableId);

        Received.InOrder(() =>
        {
            _references.ClearTableAsync(TableId, Arg.Any<CancellationToken>());
            _references.ReplaceForSourceAsync(FieldReferenceSourceTypes.Report, 55, Arg.Any<IReadOnlyCollection<FieldReferenceRow>>(), Arg.Any<CancellationToken>());
            _references.MarkTableIndexedAsync(TableId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task EnsureTableIndexed_backfills_only_a_table_that_was_never_indexed()
    {
        _references.IsTableIndexedAsync(TableId, Arg.Any<CancellationToken>()).Returns(true);
        await NewIndexer().EnsureTableIndexedAsync(TableId);
        await _references.DidNotReceive().ClearTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());

        _references.IsTableIndexedAsync(TableId, Arg.Any<CancellationToken>()).Returns(false);
        _references.ListReportSourcesAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<Report>());
        _forms.ListByTableAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(new List<Form>());
        await NewIndexer().EnsureTableIndexedAsync(TableId);
        await _references.Received(1).MarkTableIndexedAsync(TableId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveForm_drops_the_forms_rows_and_the_rows_of_its_rules()
    {
        var formId = Guid.NewGuid();

        await NewIndexer().RemoveFormAsync(formId);

        await _references.Received(1).DeleteRuleRowsOfFormAsync(formId, Arg.Any<CancellationToken>());
        await _references.Received(1).DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Form, formId, Arg.Any<CancellationToken>());
    }
}
