using System;
using FluentAssertions;
using PowerBase.Application.Pipelines.Commands.CreatePipeline;
using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineDescriptionLengthTests
{
    private static readonly string LongDescription = new('d', 5000);

    [Fact]
    public void Validate_CreateWithLongDescription_IsValid() =>
        new CreatePipelineCommandValidator().Validate(new CreatePipelineCommand(Guid.NewGuid(), "Flow", LongDescription))
            .IsValid.Should().BeTrue();

    [Fact]
    public void Validate_UpdateWithLongDescription_IsValid() =>
        new UpdatePipelineCommandValidator().Validate(new UpdatePipelineCommand(Guid.NewGuid(), "Flow", LongDescription, false, [1]))
            .IsValid.Should().BeTrue();
}
