using FluentValidation;

namespace PowerBase.Application.Pipelines.Queries.ListPipelines;

public class ListPipelinesQueryValidator : AbstractValidator<ListPipelinesQuery>
{
    public ListPipelinesQueryValidator()
    {
        RuleFor(x => x.AppPublicId).NotEmpty();
        RuleFor(x => x.Page).GreaterThan(0);
        RuleFor(x => x.PageSize).GreaterThan(0).LessThanOrEqualTo(100);
    }
}
