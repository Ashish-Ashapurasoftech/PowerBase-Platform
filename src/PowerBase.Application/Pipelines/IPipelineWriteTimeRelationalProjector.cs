using PowerBase.Application.Relationships;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// A <see cref="IRelationalProjector"/> for the reads a pipeline makes while its own write transaction is still
/// open — the output of a Create / Update Record step and the trigger event of the write.
///
/// The ordinary projector reads on connections of its own. Right after the step has written a record, those
/// reads of the same table (a Summary's child rows with a Lookup back to the parent, a Lookup's parent row) sit
/// behind the step's own uncommitted lock until SQL Server's 30 s command timeout — for every record, which is
/// what made one record take 90 s. This projector reads without waiting on locks, so it also sees the step's
/// own uncommitted write, which is exactly the value being asked for. Resolved by the engine and the trigger
/// interceptor; absent (e.g. in unit tests) they fall back to the ordinary projector.
/// </summary>
public interface IPipelineWriteTimeRelationalProjector : IRelationalProjector
{
}
