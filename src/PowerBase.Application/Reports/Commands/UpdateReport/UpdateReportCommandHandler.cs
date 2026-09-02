using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.UpdateReport;

public class UpdateReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly ReportConfigValidatorRegistry _configValidatorRegistry;

    private static readonly HashSet<string> AllowedVisibilities = ["Personal", "Shared", "MyRole", "SpecificRoles", "RoleScoped"];

    public UpdateReportCommandHandler(
        IReportRepository reportRepo,
        IAppFieldRepository fieldRepo,
        IAppAccessService appAccessService,
        IAppUserRepository appUserRepo,
        IAppRoleRepository appRoleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        ReportConfigValidatorRegistry configValidatorRegistry)
    {
        _reportRepo = reportRepo;
        _fieldRepo = fieldRepo;
        _appAccessService = appAccessService;
        _appUserRepo = appUserRepo;
        _appRoleRepo = appRoleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _configValidatorRegistry = configValidatorRegistry;
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

        // Fetched up front (throws NotFoundException if missing) — ReportType is immutable after
        // creation (not part of UpdateReportCommand), but the per-type validator still needs to
        // know it, and AppTableId is needed to load the table's real fields for field-ID/type
        // validation. Fixes a pre-existing gap: Update never validated column/filter field IDs
        // against the table the way Create did.
        var existingReport = await _reportRepo.GetByPublicIdAsync(command.ReportPublicId, ct);
        var tableFields = await _fieldRepo.ListByTableAsync(existingReport.AppTableId, ct);

        var configErrors = _configValidatorRegistry.Validate(existingReport.ReportType, ReportConfigValidationInput.FromUpdate(command), tableFields);
        if (configErrors.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>(configErrors));

        var definition = new ReportDefinition
        {
            Columns = command.Columns,
            ColumnsMode = string.IsNullOrWhiteSpace(command.ColumnsMode) ? "Custom" : command.ColumnsMode,
            SortFields = command.SortFields ?? [],
            TableSortGroup = (command.TableSortGroup ?? []).Select(l => new SortGroupLevel
            {
                FieldId = l.FieldId,
                Desc = l.Desc,
                IsGroup = l.IsGroup,
                GroupByMode = string.IsNullOrWhiteSpace(l.GroupByMode) ? "EqualValues" : l.GroupByMode,
            }).ToList(),
            FilterTree = command.FilterTree,
            GroupByFieldId = command.GroupByFieldId,
            GroupByMode = string.IsNullOrWhiteSpace(command.GroupByMode) ? "EqualValues" : command.GroupByMode,
            HideTotals = command.HideTotals,
            GroupDefaultCollapsed = command.GroupDefaultCollapsed,
            GroupByDescending = command.GroupByDescending,
            Options = command.Options is null ? null : new ReportOptions
            {
                ColumnHeaderText = string.IsNullOrWhiteSpace(command.Options.ColumnHeaderText) ? "Default" : command.Options.ColumnHeaderText,
                ShowEditIcon = command.Options.ShowEditIcon,
                ShowViewIcon = command.Options.ShowViewIcon,
                ShowQuickPeekIcon = command.Options.ShowQuickPeekIcon,
                DisableBulkDelete = command.Options.DisableBulkDelete,
            },
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
            Chart = command.Chart is null ? null : new ChartConfig
            {
                ChartType = string.IsNullOrWhiteSpace(command.Chart.ChartType) ? "Bar" : command.Chart.ChartType,
                SeriesFieldId = command.Chart.SeriesFieldId,
                SeriesMode = string.IsNullOrWhiteSpace(command.Chart.SeriesMode) ? "EqualValues" : command.Chart.SeriesMode,
                AxisLabelX = command.Chart.AxisLabelX,
                AxisLabelY = command.Chart.AxisLabelY,
                YMin = command.Chart.YMin,
                YMax = command.Chart.YMax,
                LogScale = command.Chart.LogScale,
                SortBy = string.IsNullOrWhiteSpace(command.Chart.SortBy) ? "Labels" : command.Chart.SortBy,
                SortDirection = string.IsNullOrWhiteSpace(command.Chart.SortDirection) ? "Asc" : command.Chart.SortDirection,
                GoalValue = command.Chart.GoalValue,
                GoalLabel = command.Chart.GoalLabel,
                DataLabelsVisible = command.Chart.DataLabelsVisible,
                HideMissingCategories = command.Chart.HideMissingCategories,
                DrilldownReportId = command.Chart.DrilldownReportId,
                SecondaryAxisAggregationFieldIds = command.Chart.SecondaryAxisAggregationFieldIds ?? [],
                AxisLabelY2 = command.Chart.AxisLabelY2,
                YMin2 = command.Chart.YMin2,
                YMax2 = command.Chart.YMax2,
                LogScale2 = command.Chart.LogScale2,
                GaugeFieldId = command.Chart.GaugeFieldId,
                GaugeLowMaxPercent = command.Chart.GaugeLowMaxPercent,
                GaugeMediumMaxPercent = command.Chart.GaugeMediumMaxPercent,
                DataLabelDisplayAs = string.IsNullOrWhiteSpace(command.Chart.DataLabelDisplayAs) ? "Value" : command.Chart.DataLabelDisplayAs,
                GaugeGoalType = string.IsNullOrWhiteSpace(command.Chart.GaugeGoalType) ? "Fixed" : command.Chart.GaugeGoalType,
                GaugeGoalFieldId = command.Chart.GaugeGoalFieldId,
                GaugeGoalFunction = command.Chart.GaugeGoalFunction,
            },
        };

        var definitionJson = JsonSerializer.Serialize(definition);

        var affected = await _reportRepo.UpdateAsync(
            command.ReportPublicId, command.Name, command.Description,
            command.Visibility, definitionJson, ct);

        if (affected == 0)
            throw new NotFoundException("Report", command.ReportPublicId);

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
            await _reportRepo.SetReportRolesAsync(existingReport.Id, reportRolesToSave, ct);
        }
        else
        {
            // Clear mappings if visibility changed to something else
            await _reportRepo.SetReportRolesAsync(existingReport.Id, [], ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Report, command.ReportPublicId.ToString(), $"Report name changed to {command.Name}", appId: appId, ct: ct);
    }
}
