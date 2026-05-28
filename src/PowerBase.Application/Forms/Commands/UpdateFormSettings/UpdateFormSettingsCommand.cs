namespace PowerBase.Application.Forms.Commands.UpdateFormSettings;

public record UpdateFormSettingsCommand(
    Guid FormPublicId,
    string Name,
    bool AutoAddNewFields,
    bool ShowBuiltInFields,
    string SaveOptions,
    byte[] RowVersion);
