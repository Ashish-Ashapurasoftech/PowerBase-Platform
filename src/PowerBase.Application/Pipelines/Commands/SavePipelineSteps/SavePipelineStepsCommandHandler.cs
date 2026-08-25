using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Commands.SavePipelineSteps;

public class SavePipelineStepsCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly ITenantUnitOfWork _uow;
    private readonly ITenantRepository _tenantRepo;
    private readonly IQueryContext _queryContext;
    private readonly IServiceProvider _serviceProvider;

    public SavePipelineStepsCommandHandler(
        IPipelineRepository pipelineRepo,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppAccessService appAccessService,
        ITenantUnitOfWork uow,
        ITenantRepository tenantRepo,
        IQueryContext queryContext,
        IServiceProvider serviceProvider)
    {
        _pipelineRepo = pipelineRepo;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _appAccessService = appAccessService;
        _uow = uow;
        _tenantRepo = tenantRepo;
        _queryContext = queryContext;
        _serviceProvider = serviceProvider;
    }

    public async Task HandleAsync(SavePipelineStepsCommand command, CancellationToken ct = default)
    {
        var validator = new SavePipelineStepsCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        // Run authoritative DB validations.
        // The targetScopeFactory creates a fresh DI scope for the target tenant so that
        // cross-tenant App/Table/Field validation never queries the owner-tenant's database.
        Func<long, Task<TargetTenantRepos>> targetScopeFactory = async (targetTenantId) =>
        {
            var scope = _serviceProvider.CreateAsyncScope();
            try
            {
                // Set the scoped IQueryContext to the target tenant BEFORE any DB call.
                // TenantConnectionFactory caches its connection string on first use,
                // so we must set TenantId before any repository method is invoked.
                // Also copy the requesting user's identity so authorization checks
                // (AppAccessService.RequirePermissionByAppPublicIdAsync) use the correct
                // userId when querying the target tenant's AppUser permissions.
                var scopedContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                scopedContext.SetTenantId(targetTenantId);
                scopedContext.SetUserIdentity(
                    _queryContext.UserId,
                    _queryContext.IsSuperAdmin,
                    _queryContext.UserName,
                    _queryContext.UserEmail,
                    _queryContext.Permissions,
                    _queryContext.TenantRole);

                var appRepo          = scope.ServiceProvider.GetRequiredService<IAppRepository>();
                var tableRepo        = scope.ServiceProvider.GetRequiredService<IAppTableRepository>();
                var fieldRepo        = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();
                var appAccessService = scope.ServiceProvider.GetRequiredService<IAppAccessService>();

                return new TargetTenantRepos(appRepo, tableRepo, fieldRepo, appAccessService, scope);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        };

        var stepValidator = new PipelineStepValidator(
            _pipelineRepo, _appRepo, _tableRepo, _fieldRepo,
            _appAccessService, _tenantRepo, _queryContext,
            targetScopeFactory,
            // Optional (GetService, not GetRequiredService): when absent the validator simply has
            // no saved-account support, which is what test doubles for IServiceProvider give us.
            _serviceProvider.GetService<Connections.Common.ConnectionScopeResolver>(),
            _serviceProvider.GetService<IServiceScopeFactory>());
        await ValidateStepsConfigAsync(command.Steps, stepValidator, ct);

        var pipelineId = await _pipelineRepo.GetIdByPublicIdAsync(command.PipelinePublicId, ct);
        var pipeline = await _pipelineRepo.GetByPublicIdAsync(command.PipelinePublicId, ct);
        if (pipeline.IsActive)
        {
            bool HasInvalidStep(List<SavePipelineStepDto> dtoSteps)
            {
                foreach (var dto in dtoSteps)
                {
                    if (!dto.IsValidated) return true;
                    if (dto.Children != null && HasInvalidStep(dto.Children)) return true;
                    if (dto.ElseChildren != null && HasInvalidStep(dto.ElseChildren)) return true;
                    if (dto.SuccessChildren != null && HasInvalidStep(dto.SuccessChildren)) return true;
                    if (dto.ErrorChildren != null && HasInvalidStep(dto.ErrorChildren)) return true;
                }
                return false;
            }

            if (HasInvalidStep(command.Steps))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    { "Steps", new[] { "Cannot save incomplete steps to an Active PowerFlow." } }
                });
            }
        }

        // Flatten the hierarchy tree
        var flatList = new List<PipelineStep>();
        var rootOrder = 0;
        FlattenSteps(command.Steps, null, null, ref rootOrder, flatList);

        await _uow.BeginAsync(ct);
        try
        {
            await _pipelineRepo.SaveStepsAsync(pipelineId, flatList, command.RowVersion, _uow.Transaction, ct);
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ValidateStepsConfigAsync(List<SavePipelineStepDto> list, PipelineStepValidator stepValidator, CancellationToken ct)
    {
        if (list == null) return;
        foreach (var dto in list)
        {
            if (!string.IsNullOrEmpty(dto.ConfigJson))
            {
                await stepValidator.ValidateStepConnectionAndTenantAccessAsync(dto.ConfigJson, ct);
            }

            if (dto.Type == "trigger" && (dto.Subtype == "new-event" || dto.Subtype == "new-bulk-event"))
            {
                await stepValidator.ValidateNewEventStepAsync(dto.ConfigJson ?? string.Empty, ct);
            }
            if (dto.Children != null) await ValidateStepsConfigAsync(dto.Children, stepValidator, ct);
            if (dto.ElseChildren != null) await ValidateStepsConfigAsync(dto.ElseChildren, stepValidator, ct);
            if (dto.SuccessChildren != null) await ValidateStepsConfigAsync(dto.SuccessChildren, stepValidator, ct);
            if (dto.ErrorChildren != null) await ValidateStepsConfigAsync(dto.ErrorChildren, stepValidator, ct);
        }
    }

    private void FlattenSteps(List<SavePipelineStepDto> dtoSteps, Guid? parentPublicId, string? parentBranch, ref int order, List<PipelineStep> flatList)
    {
        foreach (var dto in dtoSteps)
        {
            var stepPublicId = dto.PublicId ?? Guid.NewGuid();
            var step = new PipelineStep
            {
                PublicId = stepPublicId,
                ParentPublicId = parentPublicId,
                ParentBranch = parentBranch,
                RefId = dto.RefId,
                Label = dto.Label ?? string.Empty,
                Notes = dto.Notes,
                IsValidated = dto.IsValidated,
                DisplayOrder = order++,
                Type = dto.Type,
                Subtype = dto.Subtype,
                ConfigJson = dto.ConfigJson
            };
            flatList.Add(step);

            if (dto.Children != null && dto.Children.Any())
            {
                var childOrder = 0;
                FlattenSteps(dto.Children, stepPublicId, "children", ref childOrder, flatList);
            }
            if (dto.ElseChildren != null && dto.ElseChildren.Any())
            {
                var elseOrder = 0;
                FlattenSteps(dto.ElseChildren, stepPublicId, "elseChildren", ref elseOrder, flatList);
            }
            if (dto.SuccessChildren != null && dto.SuccessChildren.Any())
            {
                var successOrder = 0;
                FlattenSteps(dto.SuccessChildren, stepPublicId, "successChildren", ref successOrder, flatList);
            }
            if (dto.ErrorChildren != null && dto.ErrorChildren.Any())
            {
                var errorOrder = 0;
                FlattenSteps(dto.ErrorChildren, stepPublicId, "errorChildren", ref errorOrder, flatList);
            }
        }
    }
}
