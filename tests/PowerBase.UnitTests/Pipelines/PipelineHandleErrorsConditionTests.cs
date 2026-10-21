using System.Reflection;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// A Condition inside Handle Errors' "On Error" branch reads the error the step failed with: {{steps.ERROR.error_message}} (and its
/// reference_id, name, step, channel). ERROR is no step of the PowerFlow, so working out the token's type looked for one, failed with
/// "Source step 'ERROR' … could not be resolved", and the error branch failed in turn. The error's details are text.
/// </summary>
public class PipelineHandleErrorsConditionTests
{
    private static readonly List<PipelineStep> Steps =
    [
        new() { Id = 1, RefId = "ref_1", Type = "trigger", Subtype = "schedule", ConfigJson = "{}" },
        new() { Id = 2, RefId = "ref_2", Type = "action", Subtype = "handle-errors", ConfigJson = "{\"fallbackAction\":\"handle\"}" },
        new() { Id = 3, RefId = "ref_3", Type = "action", Subtype = "create-record", ConfigJson = "{\"tableId\":\"" + Guid.NewGuid() + "\"}" },
    ];

    private static Task<string> Category(string token)
    {
        var resolve = typeof(PipelineEngine).GetMethod("ResolveRuleTypeCategoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<string>)resolve.Invoke(PipelineFilterSqlParityTests.Engine(),
            [token, "{}", Steps, Substitute.For<IAppFieldRepository>(), Substitute.For<IAppTableRepository>(), CancellationToken.None])!;
    }

    [Theory]
    [InlineData("{{steps.ERROR.error_message}}")]
    [InlineData("{{steps.ERROR.reference_id}}")]
    [InlineData("{{steps.ERROR.name}}")]
    [InlineData("{{steps.ERROR.step}}")]
    [InlineData("{{steps.ERROR.channel}}")]
    [InlineData("{{ERROR.error_message}}")]
    public async Task TheErrorsDetails_AreText(string token) =>
        Assert.Equal("TEXT", await Category(token));

    [Fact]
    public async Task AStepThatIsNotThere_IsStillReported() =>
        await Assert.ThrowsAsync<PowerBase.Domain.Exceptions.PipelineStepException>(() => Category("{{steps.ref_99.fid_6}}"));
}
