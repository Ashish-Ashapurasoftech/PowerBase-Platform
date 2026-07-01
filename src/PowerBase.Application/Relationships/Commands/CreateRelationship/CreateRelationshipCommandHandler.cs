using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships.Commands.CreateRelationship;

public class CreateRelationshipCommandHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IFormRepository _formRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRepository _appRepo;

    public CreateRelationshipCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        IRelationshipRepository relRepo,
        ISchemaEngineService schemaEngine,
        IFormRepository formRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IAppRepository appRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _relRepo = relRepo;
        _schemaEngine = schemaEngine;
        _formRepo = formRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _appRepo = appRepo;
    }

    public async Task<RelationshipDto> HandleAsync(CreateRelationshipCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.ReferenceFieldName))
            throw new ValidationException(new Dictionary<string, string[]> { ["referenceFieldName"] = ["Reference field name is required."] });

        var parent = await _tableRepo.GetByPublicIdAsync(command.ParentTablePublicId, ct);
        var child = await _tableRepo.GetByPublicIdAsync(command.ChildTablePublicId, ct);
        if (parent.AppId != child.AppId)
            throw new ValidationException(new Dictionary<string, string[]> { ["tables"] = ["Both tables must belong to the same app."] });

        var refType = await _fieldTypeRepo.GetByCodeAsync(FieldTypeCodeNames.Reference, ct) ?? throw new NotFoundException("FieldType", "Reference");
        var lookupType = await _fieldTypeRepo.GetByCodeAsync(FieldTypeCodeNames.Lookup, ct) ?? throw new NotFoundException("FieldType", "Lookup");
        var summaryType = await _fieldTypeRepo.GetByCodeAsync(FieldTypeCodeNames.Summary, ct) ?? throw new NotFoundException("FieldType", "Summary");
        var reportLinkType = await _fieldTypeRepo.GetByCodeAsync(FieldTypeCodeNames.ReportLink, ct) ?? throw new NotFoundException("FieldType", "ReportLink");

        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);

        // 1. Reference field on the child (physical FK column). Settings get the relationship id after creation.
        var refField = await CreateFieldAsync(
            child, refType, command.ReferenceFieldName.Trim(), command.ReferenceFieldLabel?.Trim(),
            command.IsReferenceRequired,
            new ReferenceSettings { ParentTableId = parent.Id }, ct);

        // 2. Relationship row.
        var (relId, relPublicId) = await _relRepo.CreateAsync(new Relationship
        {
            AppId = child.AppId,
            ParentTableId = parent.Id,
            ChildTableId = child.Id,
            ReferenceFieldId = refField.Id,
            ReferenceFid = refField.Fid!.Value,
            ProxyFieldId = null,
        }, ct);

        await _fieldRepo.UpdateSettingsAsync(refField.Id,
            Serialize(new ReferenceSettings { RelationshipId = relId, ParentTableId = parent.Id }), ct);

        // 3. Lookup fields on the child (first one becomes the proxy).
        var createdFields = new List<RelationshipFieldDto> { new(refField.PublicId, refField.Fid!.Value, refField.Name, "reference") };
        var childAddFids = new List<int> { refField.Fid!.Value };
        AppField? firstLookup = null;
        foreach (var spec in command.Lookups)
        {
            var src = parentFields.FirstOrDefault(f => f.Fid == spec.SourceFid)
                ?? throw new NotFoundException("Field", spec.SourceFid);
            var lookup = await CreateFieldAsync(child, lookupType, spec.Name.Trim(), spec.Label?.Trim(), false,
                new LookupSettings
                {
                    RelationshipId = relId,
                    ReferenceFid = refField.Fid!.Value,
                    SourceTableId = parent.Id,
                    SourceFid = spec.SourceFid,
                    SourceTypeCode = src.TypeCode,
                }, ct);
            firstLookup ??= lookup;
            createdFields.Add(new(lookup.PublicId, lookup.Fid!.Value, lookup.Name, "lookup"));
            childAddFids.Add(lookup.Fid!.Value);
        }

        if (firstLookup is not null)
            await _relRepo.UpdateProxyFieldAsync(relId, firstLookup.Id, ct);

        // 4. Summary fields on the parent.
        var parentAddFids = new List<int>();
        foreach (var spec in command.Summaries)
        {
            var target = spec.TargetFid.HasValue ? childFields.FirstOrDefault(f => f.Fid == spec.TargetFid) : null;
            var summary = await CreateFieldAsync(parent, summaryType, spec.Name.Trim(), spec.Label?.Trim(), false,
                new SummarySettings
                {
                    RelationshipId = relId,
                    ChildTableId = child.Id,
                    ReferenceFid = refField.Fid!.Value,
                    Function = spec.Function,
                    TargetFid = spec.TargetFid,
                    TargetTypeCode = target?.TypeCode,
                }, ct);
            createdFields.Add(new(summary.PublicId, summary.Fid!.Value, summary.Name, "summary"));
            parentAddFids.Add(summary.Fid!.Value);
        }

        // 5. Auto-create a Report Link on the parent: "See {child.Name}" — navigates to filtered child records.
        var appPublicId = await _appRepo.GetPublicIdByIdAsync(parent.AppId, ct);
        var reportLinkName = $"{child.Name} records";
        if (!await _fieldRepo.NameExistsInTableAsync(parent.Id, reportLinkName, ct))
        {
            var reportLink = await CreateFieldAsync(parent, reportLinkType, reportLinkName, null, false,
                new ReportLinkSettings
                {
                    RelationshipId = relId,
                    TargetAppPublicId = appPublicId.ToString(),
                    TargetTablePublicId = child.PublicId.ToString(),
                    TargetFid = refField.Fid!.Value,
                    SourceFid = null, // null = use Record ID# (Fid 3)
                    LinkText = $"See related {child.Name}",
                    OpenInNewWindow = false,
                }, ct);
            createdFields.Add(new(reportLink.PublicId, reportLink.Fid!.Value, reportLink.Name, "reportlink", reportLink.TypeCode));
            parentAddFids.Add(reportLink.Fid!.Value);
        }

        // 6. Auto-append new fields to forms that opt in.
        await AppendToAutoAddFormsAsync(child.PublicId, childAddFids, ct);
        await AppendToAutoAddFormsAsync(parent.PublicId, parentAddFids, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, relId.ToString(),
            $"Relationship created: {child.Name} → {parent.Name}", appId: child.AppId, ct: ct);

        return new RelationshipDto
        {
            PublicId = relPublicId,
            ParentTablePublicId = parent.PublicId,
            ParentTableName = parent.Name,
            ChildTablePublicId = child.PublicId,
            ChildTableName = child.Name,
            ReferenceFid = refField.Fid!.Value,
            ReferenceFieldName = refField.Name,
            ProxyFid = firstLookup?.Fid,
            Fields = createdFields,
        };
    }

    private async Task<AppField> CreateFieldAsync(
        AppTable table, FieldType fieldType, string name, string? label, bool isRequired, object settingsObj, CancellationToken ct)
    {
        if (await _fieldRepo.NameExistsInTableAsync(table.Id, name, ct))
            throw new DuplicateException("Field", "name", name);

        var fid = await _fieldRepo.GetNextFidAsync(table.Id, ct);
        var field = new AppField
        {
            AppTableId = table.Id,
            FieldTypeId = fieldType.Id,
            TypeCode = fieldType.Code,
            Name = name,
            Label = label,
            IsRequired = isRequired,
            Fid = fid,
            Settings = Serialize(settingsObj),
            CreatedBy = _queryContext.UserId,
            IsSearchable = true,
            IsSortable = true,
            IsFilterable = true,
            IsReportable = true,
        };

        var (id, publicId) = await _fieldRepo.CreateAsync(field, ct);
        field.Id = id;
        field.PublicId = publicId;

        if (!PhysicalNaming.IsComputedTypeCode(field.TypeCode))
        {
            var col = PhysicalNaming.ColumnName(fid);
            await _fieldRepo.UpdatePhysicalColumnNameAsync(id, col, ct);
            field.PhysicalColumnName = col;
            await _schemaEngine.AddColumnAsync(table, field, ct);
        }
        return field;
    }

    private async Task AppendToAutoAddFormsAsync(Guid tablePublicId, IReadOnlyList<int> fids, CancellationToken ct)
    {
        if (fids.Count == 0) return;
        var forms = await _formRepo.ListByTableAsync(tablePublicId, ct);
        foreach (var form in forms.Where(f => f.AutoAddNewFields))
            foreach (var fid in fids)
                await _formRepo.AppendFieldToLastSectionAsync(form.Id, fid, ct);
    }

    private static string Serialize(object settings) => JsonSerializer.Serialize(settings, JsonOpts);

    // Compile-time guard that the field-type code strings match the enum.
    private static class FieldTypeCodeNames
    {
        public const string Reference = nameof(Domain.Enums.FieldTypeCode.Reference);
        public const string Lookup = nameof(Domain.Enums.FieldTypeCode.Lookup);
        public const string Summary = nameof(Domain.Enums.FieldTypeCode.Summary);
        public const string ReportLink = nameof(Domain.Enums.FieldTypeCode.ReportLink);
    }
}
