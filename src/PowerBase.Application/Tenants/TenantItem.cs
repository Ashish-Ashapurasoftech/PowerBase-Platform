namespace PowerBase.Application.Tenants;

public record TenantItem(Guid PublicId, string Name, string Slug, bool IsOwner);
