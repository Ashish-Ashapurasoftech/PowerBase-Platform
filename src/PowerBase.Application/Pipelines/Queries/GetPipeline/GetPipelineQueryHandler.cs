using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.GetPipeline;

public class GetPipelineQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppRepository _appRepo;

    public GetPipelineQueryHandler(IPipelineRepository pipelineRepo, IAppRepository appRepo)
    {
        _pipelineRepo = pipelineRepo;
        _appRepo = appRepo;
    }

    public async Task<PipelineResult> HandleAsync(GetPipelineQuery query, CancellationToken ct = default)
    {
        var validator = new GetPipelineQueryValidator();
        var validation = await validator.ValidateAsync(query, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var pipeline = await _pipelineRepo.GetByPublicIdAsync(query.PublicId, ct);
        var appPublicId = await _appRepo.GetPublicIdByIdAsync(pipeline.AppId, ct);

        var flatSteps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipeline.Id, ct);

        // Map to result DTOs
        var stepDtos = flatSteps.ToDictionary(
            s => s.Id,
            s => new PipelineStepResult
            {
                PublicId = s.PublicId,
                RefId = s.RefId,
                Label = s.Label,
                Notes = s.Notes,
                IsValidated = s.IsValidated,
                LastTriggeredOn = s.LastTriggeredOn,
                DisplayOrder = s.DisplayOrder,
                Type = s.Type,
                Subtype = s.Subtype,
                ConfigJson = s.ConfigJson,
                ParentBranch = s.ParentBranch,
                RowVersion = s.RowVersion ?? Array.Empty<byte>()
            });

        var rootSteps = new List<PipelineStepResult>();

        // Reconstruct hierarchy tree
        foreach (var step in flatSteps)
        {
            var dto = stepDtos[step.Id];
            if (step.ParentStepId.HasValue && stepDtos.TryGetValue(step.ParentStepId.Value, out var parentDto))
            {
                var branch = step.ParentBranch?.ToLowerInvariant();
                if (branch == "elsechildren")
                {
                    parentDto.ElseChildren.Add(dto);
                }
                else if (branch == "successchildren")
                {
                    parentDto.SuccessChildren.Add(dto);
                }
                else if (branch == "errorchildren")
                {
                    parentDto.ErrorChildren.Add(dto);
                }
                else
                {
                    parentDto.Children.Add(dto);
                }
            }
            else
            {
                rootSteps.Add(dto);
            }
        }

        // Sort all child and root collections by DisplayOrder
        SortSteps(rootSteps);

        return new PipelineResult
        {
            PublicId = pipeline.PublicId,
            AppPublicId = appPublicId,
            Name = pipeline.Name,
            Description = pipeline.Description,
            VariablesJson = pipeline.VariablesJson,
            IsActive = pipeline.IsActive,
            RowVersion = pipeline.RowVersion ?? Array.Empty<byte>(),
            Steps = rootSteps
        };
    }

    private void SortSteps(List<PipelineStepResult> dtos)
    {
        dtos.Sort((a, b) => a.DisplayOrder.CompareTo(b.DisplayOrder));
        foreach (var dto in dtos)
        {
            if (dto.Children.Any()) SortSteps(dto.Children);
            if (dto.ElseChildren.Any()) SortSteps(dto.ElseChildren);
            if (dto.SuccessChildren.Any()) SortSteps(dto.SuccessChildren);
            if (dto.ErrorChildren.Any()) SortSteps(dto.ErrorChildren);
        }
    }
}
