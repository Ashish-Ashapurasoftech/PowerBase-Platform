namespace PowerBase.Application.Capabilities.Commands.SaveRoleCapabilities;

public record SaveRoleCapabilitiesCommand(Guid RolePublicId, IReadOnlyList<string> Capabilities);
