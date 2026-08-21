using FluentValidation;

namespace PowerBase.Application.Pipelines.Queries.GetPipeline;

public class GetPipelineQueryValidator : AbstractValidator<GetPipelineQuery>
{
    public GetPipelineQueryValidator()
    {
        RuleFor(x => x.PublicId).NotEmpty();
    }
}
