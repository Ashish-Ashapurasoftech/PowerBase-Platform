using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Validates the type-specific portion of a report's configuration. Implementations are
/// registered in <see cref="ReportConfigValidatorRegistry"/>, keyed by <see cref="ReportType"/>.
/// Mirrors the shape of PowerBase.Application.Fields.Settings.IFieldSettingsValidator, adapted:
/// report validation needs the table's actual field list (to check field IDs/types referenced
/// by columns/filters/aggregations), which the field-settings pattern's self-contained JSON blob
/// never needed, so this takes typed data instead of a raw JSON string.
/// </summary>
public interface IReportConfigValidator
{
    /// <summary>The ReportType (e.g. "Table", "Summary", "Chart") this validator handles.</summary>
    string ReportType { get; }

    /// <summary>
    /// Validates the incoming config against the table's real fields. Returns a dictionary of
    /// field-path → error messages, or an empty dictionary on success. The caller converts this
    /// to a <see cref="PowerBase.Domain.Exceptions.ValidationException"/>.
    /// </summary>
    IDictionary<string, string[]> Validate(ReportConfigValidationInput input, IReadOnlyList<AppField> tableFields);
}
