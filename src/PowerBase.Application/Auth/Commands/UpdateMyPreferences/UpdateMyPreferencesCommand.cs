using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Auth.Commands.UpdateMyPreferences;

public record UpdateMyPreferencesCommand(UserPreferencesSettings Preferences);
