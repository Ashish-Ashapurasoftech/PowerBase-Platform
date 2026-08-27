using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Resolves the correct <see cref="IReportConfigValidator"/> for a given ReportType and runs it.
/// Mirrors PowerBase.Application.Fields.Settings.FieldSettingsValidatorRegistry. An unrecognized
/// ReportType is itself a validation error here (this is what replaces the old hardcoded
/// AllowedReportTypes HashSet check that used to live only in CreateReportCommandHandler).
/// </summary>
public sealed class ReportConfigValidatorRegistry
{
    private readonly Dictionary<string, IReportConfigValidator> _map;

    public ReportConfigValidatorRegistry(IEnumerable<IReportConfigValidator> validators)
    {
        _map = new Dictionary<string, IReportConfigValidator>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in validators)
            _map[v.ReportType] = v;
    }

    public IReadOnlyCollection<string> SupportedReportTypes => _map.Keys;

    /// <summary>Cheap check with no field/table dependency — lets callers fail fast on a
    /// nonsense ReportType before doing any repo work to load the table's fields.</summary>
    public bool IsSupported(string reportType) => _map.ContainsKey(reportType);

    public IDictionary<string, string[]> Validate(string reportType, ReportConfigValidationInput input, IReadOnlyList<AppField> tableFields)
    {
        if (!_map.TryGetValue(reportType, out var validator))
        {
            return new Dictionary<string, string[]>
            {
                ["ReportType"] = [$"Report type must be one of: {string.Join(", ", _map.Keys.OrderBy(k => k))}"],
            };
        }

        return validator.Validate(input, tableFields);
    }
}
