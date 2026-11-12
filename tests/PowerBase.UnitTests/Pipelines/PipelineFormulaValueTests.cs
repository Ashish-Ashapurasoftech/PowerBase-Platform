using System.Linq;
using System;
using System.Collections.Generic;
using FluentAssertions;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineFormulaValueTests
{
    private const string Id = "{{steps.ref_aa.fid_3}}";
    private const string Name = "{{steps.ref_aa.fid_20}}";
    private const string When = "{{steps.ref_aa.fid_1}}";
    private const string Done = "{{steps.ref_aa.fid_9}}";

    private static string Formula(string expression, params (string Token, string Type)[] fields) =>
        PipelineFormulaValue.Create(new PipelineFormulaValue.Definition(expression, fields.Select(f => new PipelineFormulaValue.Field(f.Token, f.Type)).ToList()));

    private static string Run(string value, Dictionary<string, string?> values) =>
        PipelineFormulaValue.Evaluate(value, token => values.TryGetValue(token, out var v) ? v : null, Engine,
            fields => new PowerBase.Formula.Evaluation.EvaluationOptions { UtcNow = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc) });

    private static readonly PowerBase.Formula.FormulaEngine Engine = new();

    [Theory]
    [InlineData("plain text")]
    [InlineData("{{steps.ref_aa.fid_3}}")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("@formula")]
    public void IsFormula_OrdinaryValue_IsNot(string? value) => PipelineFormulaValue.IsFormula(value).Should().BeFalse();

    [Fact]
    public void CreateAndParse_RoundTrip()
    {
        var value = Formula("\"CST000\" & ToText([" + Id + "])", (Id, "number"));

        value.Should().StartWith(PipelineFormulaValue.Prefix);
        var parsed = PipelineFormulaValue.Parse(value)!;
        parsed.Expression.Should().Be("\"CST000\" & ToText([" + Id + "])");
        parsed.Fields.Should().ContainSingle().Which.Should().Be(new PipelineFormulaValue.Field(Id, "number"));
        PipelineFormulaValue.Tokens(value).Should().Equal(Id);
    }

    [Fact]
    public void Evaluate_TextWithANumberField_AsInTheTableFormula()
    {
        var value = Formula("\"CST000\" & ToText([" + Id + "])", (Id, "number"));

        Run(value, new() { [Id] = "42" }).Should().Be("CST00042");
    }

    [Fact]
    public void Evaluate_NumberArithmetic()
    {
        Run(Formula("[" + Id + "] * 2 + 1", (Id, "number")), new() { [Id] = "20" }).Should().Be("41");
    }

    [Fact]
    public void Evaluate_DateField_IsADate()
    {
        Run(Formula("Year([" + When + "])", (When, "date")), new() { [When] = "2026-07-04" }).Should().Be("2026");
        Run(Formula("AdjustMonth([" + When + "], 1)", (When, "date")), new() { [When] = "2026-01-15" }).Should().Be("2026-02-15");
    }

    [Fact]
    public void Evaluate_CheckboxField_IsABoolean()
    {
        Run(Formula("If([" + Done + "], \"Yes\", \"No\")", (Done, "boolean")), new() { [Done] = "true" }).Should().Be("Yes");
        Run(Formula("If([" + Done + "], \"Yes\", \"No\")", (Done, "boolean")), new() { [Done] = "false" }).Should().Be("No");
    }

    [Fact]
    public void Evaluate_BlankField_IsBlank()
    {
        Run(Formula("If([" + Name + "] = \"\", \"none\", [" + Name + "])", (Name, "text")), new() { [Name] = "" }).Should().Be("none");
        Run(Formula("If([" + Name + "] = \"\", \"none\", [" + Name + "])", (Name, "text")), new() { [Name] = "Ann" }).Should().Be("Ann");
    }

    [Fact]
    public void Evaluate_SeveralFields()
    {
        var value = Formula("[" + Name + "] & \"-\" & ToText([" + Id + "])", (Name, "text"), (Id, "number"));

        Run(value, new() { [Name] = "Ann", [Id] = "7" }).Should().Be("Ann-7");
    }

    [Fact]
    public void Evaluate_NoFields_StillRuns()
    {
        Run(Formula("\"A\" & \"B\""), new()).Should().Be("AB");
    }

    [Fact]
    public void Compile_UnknownField_IsAnError()
    {
        var (compiled, _, diagnostics) = PipelineFormulaValue.Compile(new PipelineFormulaValue.Definition("[Nope] & \"x\"", []), Engine);

        compiled.Should().BeNull();
        diagnostics.Should().NotBeEmpty();
    }

    [Fact]
    public void Evaluate_BrokenFormula_FailsTheStepWithTheReason()
    {
        var value = Formula("ToText(", (Id, "number"));

        FluentActions.Invoking(() => Run(value, new())).Should().Throw<PipelineStepException>().WithMessage("*formula*");
    }

    [Fact]
    public void Parse_UnreadableValue_IsNull() =>
        PipelineFormulaValue.Parse(PipelineFormulaValue.Prefix + "{not json").Should().BeNull();
}
