namespace PowerBase.Formula.Querying;

/// <summary>How adjacent query clauses combine. QuickBase evaluates left-to-right.</summary>
public enum QueryConnector { And, Or }

/// <summary>
/// One QuickBase query clause: <c>{fid.op.'value'}</c>. <see cref="Op"/> is kept as the
/// raw QuickBase operator code (EX, XEX, GT, GTE, LT, LTE, CT, XCT, SW, BF, AF, …) — the
/// engine does not interpret it; the host <see cref="Evaluation.IRecordContext"/> applies it.
/// </summary>
public sealed record RecordQueryClause(long Fid, string Op, string Value);

/// <summary>
/// A parsed QuickBase query string: a sequence of clauses joined by AND/OR connectors.
/// <c>Connectors[i]</c> joins <c>Clauses[i]</c> to <c>Clauses[i+1]</c>.
/// </summary>
public sealed record RecordQuery(IReadOnlyList<RecordQueryClause> Clauses, IReadOnlyList<QueryConnector> Connectors)
{
    public static readonly RecordQuery Empty = new(System.Array.Empty<RecordQueryClause>(), System.Array.Empty<QueryConnector>());
}
