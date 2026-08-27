using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface ISchemaEngineService
{
    Task CreateTableAsync(AppTable table, CancellationToken ct = default);
    Task AddColumnAsync(AppTable table, AppField field, CancellationToken ct = default);
    Task SetUniqueAsync(AppTable table, AppField field, bool enable, CancellationToken ct = default);

    /// <summary>
    /// Widens a field's physical column from INT to DECIMAL(18,4) if (and only if) it is
    /// currently INT — a no-op otherwise. Used solely by the Number/Currency/Percent/Rating
    /// "Display As" type switch to bring a legacy Rating field (created before Rating's
    /// catalog SqlDataType became DECIMAL(18,4)) in line before its FieldTypeId changes.
    /// This is intentionally the only ALTER COLUMN operation in the schema engine — INT to
    /// DECIMAL is always lossless (every integer value is exactly representable), so it never
    /// risks the data truncation a DECIMAL-to-INT narrowing would.
    /// </summary>
    Task WidenIntColumnToDecimalIfNeededAsync(AppTable table, AppField field, CancellationToken ct = default);
}
