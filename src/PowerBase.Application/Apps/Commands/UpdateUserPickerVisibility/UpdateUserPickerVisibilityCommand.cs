namespace PowerBase.Application.Apps.Commands.UpdateUserPickerVisibility;

public record UpdateUserPickerVisibilityCommand(Guid AppPublicId, Guid UserPublicId, bool ShowInUserPickers);
