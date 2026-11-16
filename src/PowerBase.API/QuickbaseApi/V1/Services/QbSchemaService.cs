using System.Globalization;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>Read-only schema endpoints of the Quickbase-compatible v1 API: getAppTables and getFields.</summary>
public sealed class QbSchemaService
{
    private readonly IAppRepository _apps;
    private readonly IAppTableRepository _tables;
    private readonly IAppFieldRepository _fields;
    private readonly IAppAccessService _access;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAppRoleRepository _roles;
    private readonly IAppRolePermissionRepository _rolePermissions;
    private readonly QbReferenceTypes _referenceTypes;

    public QbSchemaService(IAppRepository apps, IAppTableRepository tables, IAppFieldRepository fields, IAppAccessService access,
        IRolePermissionEnforcer enforcer, IAppRoleRepository roles, IAppRolePermissionRepository rolePermissions, QbReferenceTypes referenceTypes)
    {
        _referenceTypes = referenceTypes;
        _apps = apps;
        _tables = tables;
        _fields = fields;
        _access = access;
        _enforcer = enforcer;
        _roles = roles;
        _rolePermissions = rolePermissions;
    }

    /// <summary>getAppTables: every table of the app the caller's role can view (unpaged, like Quickbase).</summary>
    public async Task<IReadOnlyList<QbTableResponse>> GetAppTablesAsync(Guid appPublicId, CancellationToken ct)
    {
        await _access.RequirePermissionByAppPublicIdAsync(appPublicId, PermissionCodes.TablesRead, ct);
        var appId = await _apps.GetIdByPublicIdAsync(appPublicId, ct);
        var tables = await _tables.ListByAppAsync(appId, ct);

        var result = new List<QbTableResponse>();
        foreach (var table in tables.OrderBy(t => t.DisplayOrder).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var fields = table.Fields.Count > 0 ? table.Fields : await _fields.ListByTableAsync(table.Id, ct);
            var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
            if (!access.CanView) continue;
            result.Add(await ToTableAsync(table, fields, ct));
        }
        return result;
    }

    private async Task<QbTableResponse> ToTableAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        long? keyFid = QbIds.RECORD_ID_FID;
        if (table.KeyFieldId is { } keyId)
        {
            // The app-wide table listing may carry only part of each table's fields; fall back to the field itself.
            var keyField = fields.FirstOrDefault(f => f.Id == keyId && f.Fid.HasValue);
            if (keyField is null)
            {
                try { keyField = await _fields.GetByIdInTableAsync(keyId, table.Id, ct); }
                catch (PowerBase.Domain.Exceptions.NotFoundException) { keyField = null; }
            }
            keyFid = keyField?.Fid;
        }

        return new QbTableResponse
        {
            Name = table.Name,
            Id = QbIds.Format(table.PublicId),
            Alias = QbReferenceTypes.TableTag(table),                // Quickbase always has one: "_DBID_TABLE_NAME"
            Description = table.Description,
            Created = Iso(table.CreatedOn),
            Updated = Iso(table.ModifiedOn ?? table.CreatedOn),
            NextRecordId = null,
            NextFieldId = await _fields.GetNextFidAsync(table.Id, ct),
            DefaultSortFieldId = null,
            DefaultSortOrder = null,
            KeyFieldId = keyFid,
            SingleRecordName = table.SingularLabel,
            PluralRecordName = table.PluralLabel,
            SizeLimit = null,
            SpaceUsed = null,
            SpaceRemaining = null,
        };
    }

    /// <summary>getFields: the fields of a table the caller's role can see.</summary>
    public async Task<IReadOnlyList<QbFieldResponse>> GetFieldsAsync(Guid tablePublicId, bool includeFieldPerms, CancellationToken ct)
    {
        await _access.RequirePermissionByTablePublicIdAsync(tablePublicId, PermissionCodes.FieldsRead, ct);
        var table = await _tables.GetByPublicIdAsync(tablePublicId, ct);
        var allFields = await _fields.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, allFields, ct);
        if (!access.CanView) throw QbApiException.AccessDenied("You do not have access to this table.");

        var visible = HelperFields.Visible(access.Unrestricted ? allFields : access.VisibleFields)
            .Where(f => f.Fid.HasValue)
            .OrderBy(f => f.Fid)
            .ToList();

        Dictionary<long, List<QbFieldPermission>>? perms = null;
        if (includeFieldPerms)
        {
            await _access.RequirePermissionByTablePublicIdAsync(tablePublicId, PermissionCodes.RolesManage, ct);
            perms = await BuildPermissionsAsync(table, visible, ct);
        }

        var types = await _referenceTypes.ForAsync(visible, ct);
        var contexts = new Dictionary<long, QbFieldContext?>();
        foreach (var f in visible.Where(f => f.TypeCode is "Summary" or "Lookup" or "Reference")) contexts[f.Id] = await _referenceTypes.ContextAsync(f, allFields, ct);
        return visible.Select(f => ToField(f, perms, types, contexts.GetValueOrDefault(f.Id))).ToList();
    }

    public static QbFieldResponse ToField(AppField f, IReadOnlyDictionary<long, List<QbFieldPermission>>? perms,
        IReadOnlyDictionary<long, QbFieldType>? types = null, QbFieldContext? context = null)
    {
        var type = QbFieldPropertiesBuilder.TypeOf(f, types);
        return new QbFieldResponse
        {
            Id = f.Fid!.Value,
            FieldType = type.FieldType,
            Mode = type.Mode,
            Label = string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label!,
            NoWrap = QbFieldShape.NoWrap(f, type),
            Bold = QbFieldPropertiesBuilder.Bold(f),
            Required = f.IsRequired,
            AppearsByDefault = QbFieldShape.AppearsByDefault(f),
            FindEnabled = QbFieldShape.FindEnabled(f, type),
            Unique = f.IsUnique || f.Fid == QbIds.RECORD_ID_FID,
            DoesDataCopy = QbFieldShape.DoesDataCopy(f, type),
            FieldHelp = f.HelpText ?? "",
            Audited = QbFieldShape.Audited(f, type),
            Properties = QbFieldShape.Properties(f, type, context),
            Permissions = perms is null ? null : perms.TryGetValue(f.Id, out var list) ? list : new List<QbFieldPermission>(),
        };
    }

    private async Task<Dictionary<long, List<QbFieldPermission>>> BuildPermissionsAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        var result = fields.ToDictionary(f => f.Id, _ => new List<QbFieldPermission>());
        var roles = await _roles.ListDetailsByAppIdAsync(table.AppId, ct);
        foreach (var role in roles.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var map = await _rolePermissions.GetFieldAccessMapAsync(role.Id, table.Id, ct);
            foreach (var field in fields)
            {
                // No entry = default access (Modify) — see IAppRolePermissionRepository.GetFieldAccessMapAsync.
                var access = map.TryGetValue(field.Id, out var a) ? a : FieldAccessLevels.Modify;
                result[field.Id].Add(new QbFieldPermission(role.Name, access, QbIds.Format(role.PublicId)));
            }
        }
        return result;
    }

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind)
            .ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
