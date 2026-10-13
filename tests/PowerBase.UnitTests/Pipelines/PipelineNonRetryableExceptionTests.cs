using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineNonRetryableExceptionTests
{
    [Fact]
    public void AnOrdinaryFailure_HasNoDetails_AndTheSameCodeAsBefore()
    {
        var ex = new PipelineNonRetryableException("boom");

        Assert.Equal("boom", ex.Message);
        Assert.Equal("PIPELINE_NON_RETRYABLE_ERROR", ex.ErrorCode);
        Assert.Null(ex.FailureDetails);
    }

    [Fact]
    public void Details_AreCarriedAlongsideTheMessage()
    {
        var ex = new PipelineNonRetryableException("boom", new Dictionary<string, object?> { ["ErrorCount"] = 3L });

        Assert.Equal(3L, ex.FailureDetails!["ErrorCount"]);
        Assert.Equal("boom", ex.Message);
    }
}
