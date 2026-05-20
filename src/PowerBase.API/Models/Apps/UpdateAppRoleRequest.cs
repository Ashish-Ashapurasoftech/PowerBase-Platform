namespace PowerBase.API.Models.Apps;

public record UpdateAppRoleRequest(
    bool CanViewRecords,
    bool CanAddRecords,
    bool CanEditRecords,
    bool CanDeleteRecords);
