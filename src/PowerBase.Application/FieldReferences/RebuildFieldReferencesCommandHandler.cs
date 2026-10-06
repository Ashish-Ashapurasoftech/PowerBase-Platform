using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.FieldReferences;

public record RebuildFieldReferencesCommand(Guid TablePublicId);

/// <summary>Repairs a table's field-reference index by re-reading every report, form, rule and field
/// on it. Unlike the save-time hooks it surfaces errors, so a failing rebuild is visible to the caller.</summary>
public class RebuildFieldReferencesCommandHandler
{
    private readonly IAppTableRepository _tables;
    private readonly IFieldReferenceIndexer _indexer;

    public RebuildFieldReferencesCommandHandler(IAppTableRepository tables, IFieldReferenceIndexer indexer)
    {
        _tables = tables;
        _indexer = indexer;
    }

    public async Task HandleAsync(RebuildFieldReferencesCommand command, CancellationToken ct = default)
    {
        var table = await _tables.GetByPublicIdAsync(command.TablePublicId, ct);
        await _indexer.RebuildTableAsync(table.Id, ct);
    }
}
