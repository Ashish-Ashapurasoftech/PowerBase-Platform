using FluentValidation;

namespace PowerBase.Application.Pipelines.Commands.DeletePipeline;

public class DeletePipelineCommandValidator : AbstractValidator<DeletePipelineCommand>
{
    public DeletePipelineCommandValidator()
    {
        RuleFor(x => x.PublicId).NotEmpty();
    }
}
