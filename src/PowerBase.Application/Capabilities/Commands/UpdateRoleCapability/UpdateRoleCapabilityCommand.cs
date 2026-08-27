namespace PowerBase.Application.Capabilities.Commands.UpdateRoleCapability;

public record UpdateRoleCapabilityCommand(Guid RolePublicId, string CapabilityCode, bool Enabled);
