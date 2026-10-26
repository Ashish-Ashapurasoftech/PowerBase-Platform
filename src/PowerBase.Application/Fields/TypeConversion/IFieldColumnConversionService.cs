using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>
/// Physical-column side of Change Type (the data.t_{table}.f_{fid} column). Kept apart from ISchemaEngineService so
/// the type-conversion module stays isolated; like the schema engine it is the only place this module writes DDL.
/// Every write method also applies the field's new type <see cref="FieldTypeMetadata"/> to meta.AppField in the
/// SAME transaction, so the stored data and the field's declared type can never disagree.
/// Every row is converted, soft-deleted rows included, so a restored record never comes back with an old-type value.
/// </summary>
public interface IFieldColumnConversionService
{
    /// <summary>Dry run: reads the current column and runs every value through <paramref name="convert"/> without
    /// writing anything. Returns the counts and the first <paramref name="sampleSize"/> non-empty before/after pairs.</summary>
    Task<ColumnConversionPreview> PreviewAsync(
        AppTable table, AppField field, Func<object?, ConvertedValue> convert, int sampleSize, CancellationToken ct = default);

    /// <summary>Up to <paramref name="max"/> distinct non-null stored values (used to resolve users up front).</summary>
    Task<IReadOnlyList<object>> ReadDistinctValuesAsync(AppTable table, AppField field, int max, CancellationToken ct = default);

    /// <summary>Scalar → Scalar: replaces the column with one of the new type's SQL type holding the converted values
    /// (old column renamed → new column added → batched copy → old column dropped). Indexes on the old column (e.g.
    /// the Unique index) are dropped and reported so the caller can rebuild them.</summary>
    Task<ColumnConversionResult> ConvertAsync(
        AppTable table, AppField field, FieldTypeMetadata metadata, Func<object?, ConvertedValue> convert, CancellationToken ct = default);

    /// <summary>Formula → Scalar: adds the field's column (the formula had none) and stores the already-evaluated,
    /// already-converted <paramref name="valuesByRowId"/>.</summary>
    Task<ColumnConversionResult> CreateFromValuesAsync(
        AppTable table, AppField field, FieldTypeMetadata metadata, IReadOnlyDictionary<long, object?> valuesByRowId, CancellationToken ct = default);

    /// <summary>Scalar → Formula: drops the field's column together with any index on it. Data is discarded.</summary>
    Task<ColumnConversionResult> DropAsync(AppTable table, AppField field, FieldTypeMetadata metadata, CancellationToken ct = default);

    /// <summary>Formula → Formula: no column exists; only the field's metadata changes.</summary>
    Task ApplyMetadataAsync(AppField field, FieldTypeMetadata metadata, CancellationToken ct = default);
}

/// <summary>What meta.AppField looks like after the change. The physical SQL type comes from the target FieldType.</summary>
public sealed record FieldTypeMetadata(int FieldTypeId, string SqlDataType, string? Settings, string? DefaultValue, bool IsUnique, bool IsRequired);

public sealed record ColumnConversionSample(long RowId, object? Before, object? After);

public sealed record ColumnConversionPreview(
    int TotalRows,
    int NonEmpty,
    int WillBecomeBlank,
    IReadOnlyList<ColumnConversionSample> Samples);

/// <param name="RowsScanned">Every row of the physical table, soft-deleted included.</param>
/// <param name="ValuesKept">Rows whose new value is not blank.</param>
/// <param name="ValuesLost">Rows that had a value the new type couldn't keep.</param>
/// <param name="DroppedIndexes">Indexes removed from the old column (rebuild e.g. Unique afterwards).</param>
public sealed record ColumnConversionResult(int RowsScanned, int ValuesKept, int ValuesLost, IReadOnlyList<string> DroppedIndexes);
