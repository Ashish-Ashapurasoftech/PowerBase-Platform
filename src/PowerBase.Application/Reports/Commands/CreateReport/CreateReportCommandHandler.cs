using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.CreateReport;

public class CreateReportCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly ReportConfigValidatorRegistry _configValidatorRegistry;
    private readonly CreateReportCommandValidator _validator;

    public CreateReportCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IReportRepository reportRepo,
        IAppUserRepository appUserRepo,
        IAppRoleRepository appRoleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        ReportConfigValidatorRegistry configValidatorRegistry)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _appUserRepo = appUserRepo;
        _appRoleRepo = appRoleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _configValidatorRegistry = configValidatorRegistry;
        _validator = new CreateReportCommandValidator();
    }

    public async Task<ReportDetailResult> HandleAsync(CreateReportCommand command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        // Fail fast on a nonsense ReportType before any repo call (matches the original
        // AllowedReportTypes check's fail-fast position — the field-aware per-type checks below
        // still need the table/fields loaded, but "is this even a real report type" doesn't).
        if (!_configValidatorRegistry.IsSupported(command.ReportType))
            throw new ValidationException(new Dictionary<string, string[]>
                { ["ReportType"] = [$"Report type must be one of: {string.Join(", ", _configValidatorRegistry.SupportedReportTypes.OrderBy(t => t))}"] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        // Report columns/filters/aggregations carry per-table field IDs (AppField.Fid), matching
        // what the client sends and how RunReport/ExportReport resolve them — not the global
        // AppField.Id. Always loaded (not just when some field looks populated) because the
        // per-type validator below independently re-derives which fields are/aren't applicable —
        // it needs the full field list regardless of what the client happened to send.
        var tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var configErrors = _configValidatorRegistry.Validate(command.ReportType, ReportConfigValidationInput.FromCreate(command), tableFields);
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

        var report = new Report
        {
            AppTableId = table.Id,
            OwnerId = _queryContext.UserId,
            Name = command.Name,
            Description = command.Description,
            ReportType = command.ReportType,
            Visibility = command.Visibility,
            Definition = JsonSerializer.Serialize(definition),
            IsDefault = false,
            DisplayOrder = 0,
        };

        var (reportId, publicId) = await _reportRepo.CreateAsync(report, ct);

        var reportRolesToSave = new List<long>();
        if (command.Visibility == Domain.Enums.Visibility.MyRole.ToString())
        {
            var appUser = await _appUserRepo.GetByAppAndUserAsync(table.AppId, _queryContext.UserId, ct);
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

        if (reportRolesToSave.Count > 0)
        {
            await _reportRepo.SetReportRolesAsync(reportId, reportRolesToSave, ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Report, publicId.ToString(), $"Report added: {command.Name}", appId: table.AppId, ct: ct);

        return new ReportDetailResult
        {
            Id = publicId,
            Name = report.Name,
            Description = report.Description,
            ReportType = report.ReportType,
            Visibility = report.Visibility,
            Definition = definition,
            IsDefault = report.IsDefault,
            DisplayOrder = report.DisplayOrder,
            ViewEditFormId = report.ViewEditFormPublicId,
            CreatedOn = DateTime.UtcNow,
        };
    }

}
