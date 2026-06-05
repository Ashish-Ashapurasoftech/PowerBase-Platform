using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.UpdateReport;

public class UpdateReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    private static readonly HashSet<string> AllowedOperators = ["eq", "ne", "contains", "startsWith", "gt", "gte", "lt", "lte"];
    private static readonly HashSet<string> AllowedFunctions = ["Count", "Sum", "Avg", "Min", "Max"];
    private static readonly HashSet<string> AllowedVisibilities = ["Personal", "Shared", "MyRole", "SpecificRoles", "RoleScoped"];

    public UpdateReportCommandHandler(
        IReportRepository reportRepo,
        IAppAccessService appAccessService,
        IAppUserRepository appUserRepo,
        IAppRoleRepository appRoleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _reportRepo = reportRepo;
        _appAccessService = appAccessService;
        _appUserRepo = appUserRepo;
        _appRoleRepo = appRoleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateReportCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });
        if (!AllowedVisibilities.Contains(command.Visibility))
            throw new ValidationException(new Dictionary<string, string[]>
                { ["Visibility"] = [$"Visibility must be one of: {string.Join(", ", AllowedVisibilities)}"] });

        // Validate filter tree operators
        if (command.FilterTree is not null)
            ValidateFilterGroup(command.FilterTree, AllowedOperators);

        // Validate aggregations
        foreach (var agg in command.Aggregations)
        {
            if (!AllowedFunctions.Contains(agg.Function))
                throw new ValidationException(new Dictionary<string, string[]>
                    { ["aggregations"] = [$"Invalid function '{agg.Function}'. Allowed: {string.Join(", ", AllowedFunctions)}"] });
        }

        var definition = new ReportDefinition
        {
            Columns = command.Columns,
            SortFields = command.SortFields ?? [],
            FilterTree = command.FilterTree,
            GroupByFieldId = command.GroupByFieldId,
            GroupByMode = string.IsNullOrWhiteSpace(command.GroupByMode) ? "EqualValues" : command.GroupByMode,
            HideTotals = command.HideTotals,
            GroupDefaultCollapsed = command.GroupDefaultCollapsed,
            GroupByDescending = command.GroupByDescending,
            Aggregations = command.Aggregations.Select(a => new SummaryAggregation
            {
                FieldId = a.FieldId,
                Function = a.Function,
                DisplayAs = string.IsNullOrWhiteSpace(a.DisplayAs) ? "Normal" : a.DisplayAs,
            }).ToList(),
            DynamicFilterType = string.IsNullOrWhiteSpace(command.DynamicFilterType) ? "Default" : command.DynamicFilterType,
            CustomDynamicFilterFields = command.CustomDynamicFilterFields ?? [],
            CustomDynamicFilterItems = command.CustomDynamicFilterItems ?? [],
            AllowQuickSearch = command.AllowQuickSearch,
        };

        var definitionJson = JsonSerializer.Serialize(definition);

        var affected = await _reportRepo.UpdateAsync(
            command.ReportPublicId, command.Name, command.Description,
            command.Visibility, definitionJson, ct);

        if (affected == 0)
            throw new NotFoundException("Report", command.ReportPublicId);

        var reportId = await _reportRepo.GetByPublicIdAsync(command.ReportPublicId, ct);
        var appId = await _reportRepo.GetAppIdByPublicIdAsync(command.ReportPublicId, ct);

        var reportRolesToSave = new List<long>();
        if (command.Visibility == Domain.Enums.Visibility.MyRole.ToString())
        {
            var appUser = await _appUserRepo.GetByAppAndUserAsync(appId, _queryContext.UserId, ct);
            if (appUser?.AppRoleId is not null)
            {
                reportRolesToSave.Add(appUser.AppRoleId);
            }
        }
        else if (command.Visibility == Domain.Enums.Visibility.SpecificRoles.ToString() && command.VisibleToRoleIds?.Count > 0)
        {
            foreach (var rolePubId in command.VisibleToRoleIds)
            {
                var role = await _appRoleRepo.GetByPublicIdAsync(rolePubId, ct);
                if (role is not null)
                {
                    reportRolesToSave.Add(role.Id);
                }
            }
        }

        if (command.Visibility == Domain.Enums.Visibility.MyRole.ToString() || command.Visibility == Domain.Enums.Visibility.SpecificRoles.ToString())
        {
            await _reportRepo.SetReportRolesAsync(reportId.Id, reportRolesToSave, ct);
        }
        else
        {
            // Clear mappings if visibility changed to something else
            await _reportRepo.SetReportRolesAsync(reportId.Id, [], ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Report, command.ReportPublicId.ToString(), $"Report name changed to {command.Name}", appId: appId, ct: ct);
    }

    private static void ValidateFilterGroup(FilterGroup group, HashSet<string> allowedOperators)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } cond && !allowedOperators.Contains(cond.Operator))
                throw new ValidationException(new Dictionary<string, string[]>
                    { ["filterTree"] = [$"Invalid operator '{cond.Operator}'. Allowed: {string.Join(", ", allowedOperators)}"] });

            if (node.Group is { } sub)
                ValidateFilterGroup(sub, allowedOperators);
        }
    }
}
