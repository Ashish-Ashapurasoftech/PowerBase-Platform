using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Records.Commands.BulkCreateRecords;

/// <summary>
/// Saves Grid Edit's new rows: every row is prepared and checked exactly like a single-record create
/// (<see cref="RecordCreatePreparer"/>), all failures of all rows are reported together, and only when
/// every row passes are they written — in one transaction, so a save never leaves half the rows behind.
/// </summary>
public class BulkCreateRecordsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IReferenceRelinkService? _relink;
    private readonly RecordCreatePreparer _preparer;

    public BulkCreateRecordsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRelationshipRepository relRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IQueryContext queryContext,
        IUserRepository userRepo,
        IMessagePublisher messagePublisher,
        FormulaEngine engine,
        IAppRepository appRepo,
        IFormRuleRepository formRuleRepo,
        IFormRepository formRepo,
        IAppUserRepository appUserRepo,
        IReportRepository reportRepo,
        IReferenceRelinkService? relink = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
        _triggerInterceptor = triggerInterceptor;
        _uow = uow;
        _queryContext = queryContext;
        _messagePublisher = messagePublisher;
        _relink = relink;
        _preparer = new RecordCreatePreparer(
            tableRepo, fieldRepo, recordRepo, relRepo, enforcer, queryContext, userRepo, appRepo, engine, formRuleRepo, formRepo, appUserRepo, reportRepo);
    }

    /// <returns>The created records, in the same order as <see cref="BulkCreateRecordsCommand.Rows"/>.</returns>
    public async Task<IReadOnlyList<RecordResult>> HandleAsync(BulkCreateRecordsCommand command, CancellationToken ct = default)
    {
        if (command.Rows.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["records"] = ["At least one record is required."] });
        if (command.Rows.Count > RecordLimits.MaxBulkCreate)
            throw new ValidationException(new Dictionary<string, string[]> { ["records"] = [$"Cannot add more than {RecordLimits.MaxBulkCreate} records at once."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        // Field ids and permissions are checked once, for every field any row submits: an unknown or
        // read-only field, or a user who can't add records, rejects the request as a whole — it isn't
        // something the user can fix by changing one row's value.
        await _preparer.EnsureWritableAsync(table, fields, command.Rows.SelectMany(r => r.Keys).Distinct().ToList(), ct);

        var dateFormat = await _preparer.GetAppDateFormatAsync(table, ct);
        var violations = new List<BulkCreateRowViolation>();

        // A row whose values can't be prepared (e.g. a Reference to a record that doesn't exist) stays
        // null: its other checks would only run against half-resolved values.
        var prepared = new Dictionary<long, object?>?[command.Rows.Count];
        for (var row = 0; row < command.Rows.Count; row++)
        {
            try
            {
                prepared[row] = await _preparer.BuildEffectiveValuesAsync(table, fields, command.Rows[row], ct);
            }
            catch (ValidationException e)
            {
                violations.AddRange(ToRowViolations(row, e, fields));
            }
        }

        var inBatchDuplicates = CollectInBatchDuplicates(fields, prepared, violations);

        for (var row = 0; row < prepared.Length; row++)
        {
            if (prepared[row] is not { } values) continue;
            var rowViolations = await _preparer.CollectViolationsAsync(
                table, fields, values, dateFormat, ct, command.ReportId, command.IsGridEditSave);
            // A Unique value two rows of this save share is already reported above, with a clearer
            // message than "already in use" — don't report the same cell twice.
            violations.AddRange(rowViolations
                .Where(v => !(v.ConstraintType == "Unique" && inBatchDuplicates.Contains((row, v.FieldId))))
                .Select(v => new BulkCreateRowViolation(row, v.FieldId, v.ConstraintType, v.Message)));
        }

        if (violations.Count > 0)
            throw new BulkCreateValidationException(violations.OrderBy(v => v.RowIndex).ToList());

        var rows = prepared.Select(values => values!).ToList();
        var indexMessages = new List<SearchIndexMessage>();
        IReadOnlyList<Guid> publicIds;
        var createdOn = DateTime.UtcNow;

        await _uow.BeginAsync(ct);
        try
        {
            publicIds = await _recordRepo.CreateManyAsync(table, fields, WithSameColumns(rows), _uow.Transaction!, ct, indexMessages.Add);
            if (publicIds.Count != rows.Count)
                throw new InvalidOperationException("The batch insert returned an unexpected number of records.");

            var recordIds = await _recordRepo.GetActiveRecordIdsByPublicIdsAsync(table, publicIds, _uow.Transaction, ct);

            // Record ID#, Date Created and Record Owner aren't submitted; filled in like a single-record
            // create does, so pipeline triggers and the response see them.
            var createdOnField = fields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedOn" && f.Fid.HasValue);
            var createdByField = fields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedBy" && f.Fid.HasValue);
            for (var row = 0; row < rows.Count; row++)
            {
                rows[row][3] = recordIds.TryGetValue(publicIds[row], out var recordId)
                    ? recordId : throw new InvalidOperationException("A record of the batch insert is missing.");
                if (createdOnField != null) rows[row][createdOnField.Fid!.Value] = createdOn;
                if (createdByField != null) rows[row][createdByField.Fid!.Value] = _queryContext.UserId;
            }

            // Child records waiting for a parent with one of these keys are linked by a background job,
            // queued here to run once this save commits.
            if (_relink is not null)
                await _relink.QueueAddedAsync(table, rows.Select(values => new RelinkParent((long)values[3]!, values)).ToList(), ct);

            await _auditRepo.LogActivitiesAsync(
                AuditActions.Created, AuditEntityTypes.Record, publicIds.Select(id => id.ToString()).ToList(),
                $"Record added in {table.Name} from Grid Edit", table.AppId, _uow.Transaction, ct);

            await _tableRepo.IncrementRecordCountByAsync(table.Id, rows.Count, _uow.Transaction, ct);

            if (await _triggerInterceptor.HasActiveSubscriptionsAsync(table, ct))
                await _triggerInterceptor.InterceptAddedManyAsync(table, fields,
                    rows.Select((values, row) => (publicIds[row], (IReadOnlyDictionary<long, object?>)values)).ToList(), ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (indexMessages.Count > 0)
            _ = _messagePublisher.PublishBatchAsync(indexMessages, default);

        return rows.Select((values, row) => new RecordResult
        {
            Id = publicIds[row],
            CreatedOn = createdOn,
            ModifiedOn = null,
            CreatedBy = _queryContext.UserId,
            Fields = fields
                .Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value))
                .ToDictionary(f => f.Fid!.Value.ToString(), f => values[(long)f.Fid!.Value]),
        }).ToList();
    }

    /// <summary>Flags every row of this save that gives a Unique field the same value as another row of
    /// the same save — all of them, not only the later ones, since either could be the one the user meant
    /// to change. Values compare the way the database's own duplicate check does: numbers by value, text
    /// ignoring case and trailing spaces.</summary>
    /// <returns>The (row, Fid) cells flagged, so the per-row database check doesn't report them again.</returns>
    private static HashSet<(int Row, long Fid)> CollectInBatchDuplicates(
        IReadOnlyList<AppField> fields, IReadOnlyList<Dictionary<long, object?>?> rows, List<BulkCreateRowViolation> violations)
    {
        var flagged = new HashSet<(int Row, long Fid)>();
        var uniqueFields = fields.Where(f => f.IsUnique && f.Fid.HasValue && !f.IsSystem
            && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && !PhysicalNaming.IsRangeTypeCode(f.TypeCode));

        foreach (var field in uniqueFields)
        {
            var fid = (long)field.Fid!.Value;
            var label = !string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name;
            var rowsByValue = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var row = 0; row < rows.Count; row++)
            {
                if (rows[row] is not { } values || !values.TryGetValue(fid, out var value) || value is null
                    || PhysicalNaming.IsRequiredMissing(field.TypeCode, value)) continue;
                var key = UniqueValueKey(field, value);
                if (!rowsByValue.TryGetValue(key, out var sameValue)) rowsByValue[key] = sameValue = [];
                sameValue.Add(row);
            }

            foreach (var sameValue in rowsByValue.Values.Where(g => g.Count > 1))
            {
                foreach (var row in sameValue)
                {
                    flagged.Add((row, fid));
                    var others = sameValue.Where(r => r != row).Select(r => (r + 1).ToString(CultureInfo.InvariantCulture)).ToList();
                    var where = others.Count == 1 ? $"row {others[0]} of this save has" : $"rows {string.Join(", ", others)} of this save have";
                    violations.Add(new BulkCreateRowViolation(row, fid, "Unique", $"'{label}' must be unique — {where} the same value."));
                }
            }
        }

        return flagged;
    }

    private static string UniqueValueKey(AppField field, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (field.TypeCode is "Number" or "Currency" or "Percent"
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);   // 5 and 5.00 are the same value
        return text.TrimEnd();
    }

    /// <summary>Gives every row the same set of columns: a field one row leaves out is written as NULL,
    /// which is what leaving it out stores (data columns have no database defaults; default values were
    /// already applied while preparing). The rows then share one INSERT shape, so they are inserted — and
    /// get their Record ID# — in the order the user entered them, instead of grouped by which cells they filled.</summary>
    private static IReadOnlyList<IReadOnlyDictionary<long, object?>> WithSameColumns(IReadOnlyList<Dictionary<long, object?>> rows)
    {
        var allFids = rows.SelectMany(r => r.Keys).ToHashSet();
        return rows.Select(values =>
        {
            var padded = new Dictionary<long, object?>(values);
            foreach (var fid in allFids) padded.TryAdd(fid, null);
            return (IReadOnlyDictionary<long, object?>)padded;
        }).ToList();
    }

    /// <summary>A row's <see cref="ValidationException"/> (e.g. "The referenced Customer record does not
    /// exist.") as row violations. Its keys are a field's Name or Fid; anything else belongs to the row.</summary>
    private static IEnumerable<BulkCreateRowViolation> ToRowViolations(int row, ValidationException e, IReadOnlyList<AppField> fields)
    {
        foreach (var (key, messages) in e.Errors)
        {
            var fid = long.TryParse(key, out var parsed) ? parsed
                : fields.FirstOrDefault(f => f.Fid.HasValue && string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase))?.Fid ?? 0;
            foreach (var message in messages)
                yield return new BulkCreateRowViolation(row, fid, "Invalid", message);
        }
    }
}
