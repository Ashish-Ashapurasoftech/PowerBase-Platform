using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>Where the unmatched rows' original values go when a column is converted to a Reference.
/// <paramref name="PlainTextByRowId"/> is null for a stored column (its raw — possibly encrypted — value is
/// copied as-is, so the target field must carry the same encryption); a Formula has no column, so the
/// caller supplies its evaluated text instead.</summary>
public sealed record PreservedOriginal(AppField Target, IReadOnlyDictionary<long, string>? PlainTextByRowId = null);

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

    /// <summary>
    /// Re-types an existing field's physical column (and a range field's end column) when its
    /// IsEncrypted flag is toggled, so the column matches what <see cref="AddColumnAsync"/> would
    /// have created: ciphertext storage (VARCHAR(MAX), or the enclave-encrypted catalog type) when
    /// <paramref name="isEncrypted"/> is true, the field type's catalog SQL type otherwise. A no-op
    /// for computed and encryption-exempt fields, and in an encrypted App (every column there
    /// already holds ciphertext). Only safe while the table has no live records — the caller
    /// (UpdateField, behind ValidateEncryptionTransitionAsync) guarantees that. A unique index on
    /// the column is dropped first; the caller re-creates it with <see cref="SetUniqueAsync"/>.
    /// </summary>
    Task SetColumnEncryptionAsync(AppTable table, AppField field, bool isEncrypted, CancellationToken ct = default);

    /// <summary>
    /// Repurposes a non-numeric field's physical column as a Reference column (BIGINT, parent Record IDs).
    /// <paramref name="parentIdByRowId"/> maps each child row Id to the parent Record ID its value
    /// resolved to (the caller reads the values decrypted, so encrypted columns work too). Every other
    /// row (blank or soft-deleted) becomes NULL. Values are replaced and the column becomes BIGINT in
    /// one transaction; if the column can't be converted nothing changes.
    /// When <paramref name="preserveUnmatched"/> is given, the original value of every row that did NOT
    /// resolve to a parent is first saved into that (text) field's column, in the same transaction — so
    /// those rows get a NULL reference without losing what the user had typed.
    /// After the commit the column gets the same reference index a Reference created as such has (a failure to build it is
    /// logged and does not undo the conversion).
    /// </summary>
    Task ConvertColumnToReferenceAsync(
        AppTable table, AppField field, IReadOnlyDictionary<long, long> parentIdByRowId,
        CancellationToken ct = default, PreservedOriginal? preserveUnmatched = null);

    /// <summary>
    /// The reverse of <see cref="ConvertColumnToReferenceAsync"/>: turns a Reference column (BIGINT) back into the column
    /// type of <paramref name="restoredField"/> (whose FieldTypeId/TypeCode already name the type to restore), filling it
    /// from <paramref name="textByRowId"/> (row Id → value as text; rows not listed become NULL). One transaction.
    /// Returns false — changing nothing — when the column can't be restored here: the app stores ciphertext (the text would
    /// have to be encrypted), or the table has an index/constraint on the column that blocks replacing it.
    /// </summary>
    Task<bool> RestoreColumnFromReferenceAsync(
        AppTable table, AppField restoredField, IReadOnlyDictionary<long, string> textByRowId, CancellationToken ct = default);

    /// <summary>Drops a field's physical column (and its reference index) if it exists. Only for a column that holds
    /// nothing worth keeping — e.g. the BIGINT column a Formula was given when it was converted to a Reference.</summary>
    Task DropColumnIfExistsAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>
    /// Rescales every stored value of a numeric field by a factor of 100 — multiplies when
    /// <paramref name="toPercent"/> is true, divides otherwise. Used by the Number/Currency/
    /// Percent/Rating "Display As" type switch so that a fraction (1.15) becomes a whole
    /// percentage (115) when the field turns into Percent, and back again when it leaves Percent.
    /// NULLs are left alone; the physical column type is unchanged.
    /// </summary>
    Task ScaleNumericColumnForPercentAsync(AppTable table, AppField field, bool toPercent, CancellationToken ct = default);

    /// <summary>
    /// Makes sure the stored cache column c_{fid} of a Lookup / Summary / Formula field exists with <paramref name="sqlType"/> (one of
    /// the few types a stored column may have). A column of another type (the field's output type changed) is dropped and added again,
    /// empty. Adding an empty NULL column is a metadata-only change. Never touches f_ columns.
    /// Returns false — changing nothing — when the table is too close to SQL Server's 1,024-column limit.
    /// </summary>
    Task<bool> EnsureMaterializedColumnAsync(long tableId, int fid, string sqlType, CancellationToken ct = default);

    /// <summary>Drops the stored cache column c_{fid} (and any index on it) if it exists.</summary>
    Task DropMaterializedColumnAsync(long tableId, int fid, CancellationToken ct = default);

    /// <summary>
    /// The filtered UNIQUE index that enforces a unique formula (design section 5):
    /// <c>UX_t_X_c_N ON data.t_X (c_N) WHERE IsDeleted = 0 AND c_N IS NOT NULL</c>. Returns up to five values that already appear more than
    /// once (then nothing is created); empty when the index exists.
    /// </summary>
    Task<IReadOnlyList<string>> EnsureMaterializedUniqueIndexAsync(long tableId, int fid, CancellationToken ct = default);

    /// <summary>Drops that UNIQUE index if it exists (the formula is no longer unique).</summary>
    Task DropMaterializedUniqueIndexAsync(long tableId, int fid, CancellationToken ct = default);

    /// <summary>
    /// The sort / filter index of a stored column: IX_t_X_c_N ON data.t_X (c_N) WHERE IsDeleted = 0, built ONLINE where the SQL edition
    /// allows it so the table stays usable. False when it already existed.
    /// </summary>
    Task<bool> EnsureMaterializedIndexAsync(long tableId, int fid, CancellationToken ct = default);

    /// <summary>Drops IX_t_X_c_N (no error when it is not there).</summary>
    Task DropMaterializedIndexAsync(long tableId, int fid, CancellationToken ct = default);

    /// <summary>The Fids of the stored columns of the table that have an IX_t_X_c_N index now.</summary>
    Task<IReadOnlySet<int>> ListMaterializedIndexesAsync(long tableId, CancellationToken ct = default);
}
