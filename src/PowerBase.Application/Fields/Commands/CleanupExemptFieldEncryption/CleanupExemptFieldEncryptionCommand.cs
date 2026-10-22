namespace PowerBase.Application.Fields.Commands.CleanupExemptFieldEncryption;

/// <summary>
/// Scans apps and tables for fields whose TypeCode is now in the encryption-exempt list
/// (Number, Currency, Date, Boolean, etc.) but still have IsEncrypted = true in metadata.
/// If AllTenants is true, scans all active tenants in the system.
/// Otherwise, scans the specified TenantId (or the current scoped tenant).
/// </summary>
public record CleanupExemptFieldEncryptionCommand(long? TenantId = null, bool AllTenants = false);
