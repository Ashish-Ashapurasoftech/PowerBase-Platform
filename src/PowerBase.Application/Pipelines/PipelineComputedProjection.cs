using Microsoft.Extensions.Logging;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>The computed (Formula / Lookup / Summary / Reference-label) values of a set of rows, and the
/// Fids of the relationship fields that could not be computed.</summary>
public sealed record PipelineComputedValues(
    IReadOnlyList<IReadOnlyDictionary<long, object?>> Values,
    IReadOnlySet<long> FailedFids);

/// <summary>
/// Projects compute-on-read values for the rows a pipeline step reads, tolerating a broken relationship
/// field. A Lookup / Summary / Reference stores the numeric id of the table it points at in its settings;
/// after an app is copied to another account or tenant (or a table is removed) that id can name a table
/// the current database does not have, and the relational projection then throws "Table 'N' was not
/// found". One such field must not take the whole step down, nor hide every other computed value — so
/// when the all-at-once projection fails each relationship field is projected on its own, the failing ones
/// are reported in <see cref="PipelineComputedValues.FailedFids"/>, and the caller decides whether it
/// actually needs them (<see cref="ThrowIfNeeded"/>).
/// </summary>
public static class PipelineComputedProjection
{
    /// <summary>A command timeout from the database (SqlClient's "Execution Timeout Expired", Win32 error 258) or a TimeoutException,
    /// anywhere in the exception chain.</summary>
    private static bool IsTimeout(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            if (e is System.ComponentModel.Win32Exception { NativeErrorCode: 258 }) return true;
            if (e.Message.Contains("Execution Timeout Expired", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Timeout expired", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsRelationship(AppField f) => f.Fid.HasValue && f.TypeCode is "Lookup" or "Summary" or "Reference";

    /// <summary>No fixed time limit on the relationship reads (Lookup / Summary / Reference). The data sets here can be
    /// huge (a Summary over millions of child rows), so how long a read takes is up to the data, never to a number of
    /// seconds picked here. The budgets stay as named values so a caller can still pass a limit explicitly
    /// (<c>relationshipBudget</c>), but by default they are <see cref="Timeout.InfiniteTimeSpan"/>.
    ///
    /// What does cut a stuck read short is not a clock: reads made while the step's own write transaction is open use the
    /// write-time projector (<see cref="IPipelineWriteTimeRelationalProjector"/>), which does not wait on locks at all.</summary>
    public static readonly TimeSpan RelationshipBudget = Timeout.InfiniteTimeSpan;
    public static readonly TimeSpan InTransactionBudget = Timeout.InfiniteTimeSpan;
    public static readonly TimeSpan WriteTimeBudget = Timeout.InfiniteTimeSpan;

    public static async Task<PipelineComputedValues?> ProjectAsync(
        IRelationalProjector? relationalProjector,
        IFormulaProjector? formulaProjector,
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        ILogger? logger,
        CancellationToken ct,
        TimeSpan? relationshipBudget = null)
    {
        if (rows.Count == 0 || formulaProjector == null) return null;
        var limit = relationshipBudget ?? RelationshipBudget;

        var failed = new HashSet<long>();
        IReadOnlyList<IReadOnlyDictionary<long, object?>>? relational = null;
        if (relationalProjector != null)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(limit);
            try
            {
                relational = await relationalProjector.ProjectAsync(table, fields, rows, budget.Token);
            }
            catch (Exception) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Out of time: do not start the per-field retries, they would wait on the same lock one by one.
                logger?.LogWarning("Relationship projection for table {TableId} did not finish within its limit of {Limit}; its Lookup / Summary / Reference values are left out.",
                    table.Id, limit);
                foreach (var field in fields.Where(IsRelationship)) failed.Add(field.Fid!.Value);
                relational = rows.Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList();
            }
            catch (Exception ex) when (IsTimeout(ex) && !ct.IsCancellationRequested)
            {
                // The database itself gave up on a read (a command timeout). Retrying field by field would wait the same
                // way once per field, so give up once and leave the relationship values out.
                logger?.LogWarning(ex, "A relationship read for table {TableId} timed out; its Lookup / Summary / Reference values are left out.", table.Id);
                foreach (var field in fields.Where(IsRelationship)) failed.Add(field.Fid!.Value);
                relational = rows.Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Relationship projection failed for table {TableId}; projecting each relationship field on its own.", table.Id);
                var others = fields.Where(f => !IsRelationship(f)).ToList();
                var merged = rows.Select(_ => new Dictionary<long, object?>()).ToArray();
                foreach (var field in fields.Where(IsRelationship))
                {
                    try
                    {
                        var part = await relationalProjector.ProjectAsync(table, others.Append(field).ToList(), rows, budget.Token);
                        for (var i = 0; i < rows.Count && i < part.Count; i++)
                            foreach (var kv in part[i]) merged[i][kv.Key] = kv.Value;
                    }
                    catch (Exception fieldEx) when (!ct.IsCancellationRequested)
                    {
                        failed.Add(field.Fid!.Value);
                        logger?.LogWarning(fieldEx, "Field '{Field}' ({TypeCode}, fid {Fid}) of table {TableId} could not be computed.",
                            field.Name, field.TypeCode, field.Fid, table.Id);
                    }
                }
                relational = merged;
            }
        }

        try
        {
            return new PipelineComputedValues(formulaProjector.Project(fields, rows, relational, table), failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Formula projection failed for table {TableId}.", table.Id);
            if (relational == null) return null;
            return new PipelineComputedValues(relational, failed);
        }
    }

    /// <summary>Fails the step with a message naming the field when a value it reads could not be computed.
    /// Non-retryable: retrying cannot make a missing table appear.</summary>
    public static void ThrowIfNeeded(
        PipelineComputedValues? computed, IReadOnlyList<AppField> fields, IEnumerable<long> neededFids, string stepName)
    {
        if (computed == null || computed.FailedFids.Count == 0) return;
        var broken = neededFids.Where(computed.FailedFids.Contains).Distinct()
            .Select(fid => fields.FirstOrDefault(f => f.Fid == fid))
            .Where(f => f != null)
            .Select(f => $"'{(!string.IsNullOrWhiteSpace(f!.Label) ? f.Label : f.Name)}' ({f.TypeCode})")
            .ToList();
        if (broken.Count == 0) return;
        throw new PipelineNonRetryableException(
            $"{stepName} cannot use {string.Join(", ", broken)}: the field points to a table that is not available in this account " +
            "(its relationship settings refer to a table that does not exist here — for example after the app was copied from another account). " +
            "Recreate the field's relationship, or remove the field from this step.");
    }

    /// <summary>Every field id a filter tree reads (the compared field and, for "the value in the field", the other one).</summary>
    public static HashSet<long> ReferencedFids(FilterGroup? tree)
    {
        var result = new HashSet<long>();
        if (tree == null) return result;
        void Walk(FilterGroup group)
        {
            foreach (var node in group.Nodes)
            {
                if (node.Condition is { } c)
                {
                    result.Add(c.FieldId);
                    if (c.ValueFieldId.HasValue) result.Add(c.ValueFieldId.Value);
                }
                if (node.Group != null) Walk(node.Group);
            }
        }
        Walk(tree);
        return result;
    }
}
