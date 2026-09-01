using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// The Custom Data Rule save gate — the formula counterpart to
/// <see cref="RecordConstraintValidatorTests"/>'s Required/Unique coverage. A rule is only ever
/// evaluated against the record's own effective values; a non-blank Text result blocks the save.
/// </summary>
public class CustomDataRuleValidatorTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly FormulaEngine _engine = new();

    public CustomDataRuleValidatorTests()
    {
        // AppTableAliasSchema.BuildAsync always calls this, even for a rule with no cross-table
        // reference — an empty listing is fine for every test here.
        _tableRepo.ListByAppAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<AppTable>());
    }

    private static AppTable MakeTable(string? customDataRule, bool ruleEnabled = true, long appId = 1) =>
        new() { Id = 1, AppId = appId, PublicId = Guid.NewGuid(), Name = "T", Alias = "_DBID_T", CustomDataRule = customDataRule, IsCustomDataRuleEnabled = ruleEnabled };

    private static AppField MakeField(int fid, string name, string typeCode = "Number") =>
        new() { Id = fid, Fid = fid, Name = name, Label = name, TypeCode = typeCode };

    private Task ValidateAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values) =>
        CustomDataRuleValidator.ValidateAsync(table, fields, values, _tableRepo, _fieldRepo, _recordRepo, _engine, CancellationToken.None);

    [Fact]
    public async Task Blank_rule_is_a_no_op()
    {
        var table = MakeTable(customDataRule: null);
        var values = new Dictionary<long, object?> { [1L] = 15000m };

        await FluentActions.Invoking(() => ValidateAsync(table, [MakeField(1, "Amount")], values))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Rule_returning_a_message_blocks_the_save()
    {
        var table = MakeTable(customDataRule: """If([Amount] > 10000, "Amounts over $10,000 require approval.")""");
        var values = new Dictionary<long, object?> { [1L] = 15000m };

        var thrown = await FluentActions.Invoking(() => ValidateAsync(table, [MakeField(1, "Amount")], values))
            .Should().ThrowAsync<ValidationException>();
        thrown.Which.Errors["customDataRule"].Should().Contain("Amounts over $10,000 require approval.");
    }

    [Fact]
    public async Task Rule_returning_blank_allows_the_save()
    {
        var table = MakeTable(customDataRule: """If([Amount] > 10000, "Amounts over $10,000 require approval.")""");
        var values = new Dictionary<long, object?> { [1L] = 500m };

        await FluentActions.Invoking(() => ValidateAsync(table, [MakeField(1, "Amount")], values))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Disabled_toggle_is_a_no_op_even_with_a_violating_rule()
    {
        // "Turn custom data rules on?" off — a stored (even otherwise-violating) rule must not
        // block saves until the toggle is switched on.
        var table = MakeTable(customDataRule: """If([Amount] > 10000, "Amounts over $10,000 require approval.")""", ruleEnabled: false);
        var values = new Dictionary<long, object?> { [1L] = 15000m };

        await FluentActions.Invoking(() => ValidateAsync(table, [MakeField(1, "Amount")], values))
            .Should().NotThrowAsync();
    }
}
