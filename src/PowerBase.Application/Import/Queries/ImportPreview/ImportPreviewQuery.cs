using PowerBase.Application.Import.Commands.ImportAppFromPbl;

namespace PowerBase.Application.Import.Queries.ImportPreview;

/// <summary>Raw PBL JSON / QBL text to preview. In <see cref="ImportMode.UpdateExistingApp"/> the file must hold exactly one application.</summary>
public record ImportPreviewQuery(string PblJson, ImportMode Mode = ImportMode.CreateNewApp);
