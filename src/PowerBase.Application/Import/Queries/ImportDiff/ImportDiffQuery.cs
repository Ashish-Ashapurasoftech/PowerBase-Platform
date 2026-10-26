using PowerBase.Application.Import.Mapping;

namespace PowerBase.Application.Import.Queries.ImportDiff;

/// <param name="Content">The uploaded QBL (YAML) or PBL (JSON) text.</param>
/// <param name="TargetAppPublicId">The existing app to compare against.</param>
/// <param name="Mapping">Optional decisions made so far — a mapping override changes which existing element a ref is matched to.</param>
public record ImportDiffQuery(string Content, Guid TargetAppPublicId, ImportMapping? Mapping = null);
