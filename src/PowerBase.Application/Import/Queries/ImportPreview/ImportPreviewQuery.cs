namespace PowerBase.Application.Import.Queries.ImportPreview;

/// <summary>Raw PBL JSON text (already converted from QBL, in later phases) to preview.</summary>
public record ImportPreviewQuery(string PblJson);
