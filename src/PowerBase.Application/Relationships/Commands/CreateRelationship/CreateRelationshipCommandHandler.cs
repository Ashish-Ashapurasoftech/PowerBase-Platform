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
    private readonly RelationshipFieldFactory _fieldFactory;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRepository _appRepo;

    public CreateRelationshipCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        IRelationshipRepository relRepo,
        RelationshipFieldFactory fieldFactory,
        IAuditRepository auditRepo,
        IAppRepository appRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _relRepo = relRepo;
        _fieldFactory = fieldFactory;
        _auditRepo = auditRepo;
        _appRepo = appRepo;
    }

    public async Task<RelationshipDto> HandleAsync(CreateRelationshipCommand command, CancellationToken ct = default)
    {
        // A label is only required when we're creating a brand-new reference field. When reusing an existing
        // child field (ReferenceFieldFid set) the field already has one.
        if (command.ReferenceFieldFid is null && string.IsNullOrWhiteSpace(command.ReferenceFieldLabel))
            throw new ValidationException(new Dictionary<string, string[]> { ["referenceFieldLabel"] = ["Reference field label is required."] });

        var parent = await _tableRepo.GetByPublicIdAsync(command.ParentTablePublicId, ct);
        var child = await _tableRepo.GetByPublicIdAsync(command.ChildTablePublicId, ct);
        if (parent.AppId != child.AppId)
            throw new ValidationException(new Dictionary<string, string[]> { ["tables"] = ["Both tables must belong to the same app."] });

        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);

        // 1. Reference field on the child (physical FK column). Settings get the relationship id after creation.
        //    Two paths: create a brand-new Reference field, or convert an existing child Number field in place.
        AppField refField;
        var referenceIsExistingField = command.ReferenceFieldFid is not null;
        if (!referenceIsExistingField)
        {
            refField = await _fieldFactory.CreateAsync(
                child, FieldTypeCodeNames.Reference, command.ReferenceFieldLabel!.Trim(),
                command.IsReferenceRequired,
                new ReferenceSettings { ParentTableId = parent.Id }, ct);
        }
        else
        {
            var existing = childFields.FirstOrDefault(f => f.Fid == command.ReferenceFieldFid!.Value)
                ?? throw new NotFoundException("Field", command.ReferenceFieldFid!.Value);

            // Only a plain, non-system Number field may be repurposed as the reference (it holds parent Record IDs).
            // Number and Reference are both numeric physical columns, so the conversion is metadata-only (no DDL).
            if (existing.IsSystem || !existing.Fid.HasValue || existing.TypeCode != nameof(Domain.Enums.FieldTypeCode.Number))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["referenceFieldFid"] = ["The reference field must be an existing, non-system Number field on the child table."],
                });

            var refType = await _fieldTypeRepo.GetByCodeAsync(FieldTypeCodeNames.Reference, ct)
                ?? throw new NotFoundException("FieldType", "Reference");

            await _fieldRepo.UpdateFieldTypeAsync(
                existing.Id, refType.Id,
                Serialize(new ReferenceSettings { ParentTableId = parent.Id }),
                command.IsReferenceRequired, ct);
            existing.FieldTypeId = refType.Id;
            existing.TypeCode = refType.Code;
            existing.IsRequired = command.IsReferenceRequired;
            refField = existing;
        }

        // 2. Relationship row.
        var (relId, relPublicId) = await _relRepo.CreateAsync(new Relationship
        {
            AppId = child.AppId,
            ParentTableId = parent.Id,
            ChildTableId = child.Id,
            ReferenceFieldId = refField.Id,
            ReferenceFid = refField.Fid!.Value,
            ProxyFieldId = null,
            ReferenceFieldIsExisting = referenceIsExistingField,
        }, ct);

        await _fieldRepo.UpdateSettingsAsync(refField.Id,
            Serialize(new ReferenceSettings { RelationshipId = relId, ParentTableId = parent.Id }), ct);

        // 3. Lookup fields on the child (first one becomes the proxy).
        var createdFields = new List<RelationshipFieldDto> { new(refField.PublicId, refField.Fid!.Value, refField.Name, "reference") };
        // Only auto-append the reference to forms when it's newly created; an existing field is likely already placed.
        var childAddFids = referenceIsExistingField ? new List<int>() : new List<int> { refField.Fid!.Value };
        AppField? firstLookup = null;
        foreach (var spec in command.Lookups)
        {
            var src = parentFields.FirstOrDefault(f => f.Fid == spec.SourceFid)
                ?? throw new NotFoundException("Field", spec.SourceFid);
            ValidateSubField(spec.SourceSubField, src.TypeCode, "sourceSubField");
            var lookup = await _fieldFactory.CreateAsync(child, FieldTypeCodeNames.Lookup, spec.Label.Trim(), false,
                new LookupSettings
                {
                    RelationshipId = relId,
                    ReferenceFid = refField.Fid!.Value,
                    SourceTableId = parent.Id,
                    SourceFid = spec.SourceFid,
                    SourceTypeCode = src.TypeCode,
                    SourceSubField = spec.SourceSubField,
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
            ValidateSubField(spec.TargetSubField, target?.TypeCode, "targetSubField");
            if (spec.TargetSubField is not null && spec.Function is SummaryFunctions.Sum or SummaryFunctions.Avg)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["targetSubField"] = [$"'{spec.Function}' can't aggregate an address sub-field (always text); use Count, Exists, Min, or Max."],
                });
            var summary = await _fieldFactory.CreateAsync(parent, FieldTypeCodeNames.Summary, spec.Label.Trim(), false,
                new SummarySettings
                {
                    RelationshipId = relId,
                    ChildTableId = child.Id,
                    ReferenceFid = refField.Fid!.Value,
                    Function = spec.Function,
                    TargetFid = spec.TargetFid,
                    TargetTypeCode = target?.TypeCode,
                    TargetSubField = spec.TargetSubField,
                }, ct);
            createdFields.Add(new(summary.PublicId, summary.Fid!.Value, summary.Name, "summary"));
            parentAddFids.Add(summary.Fid!.Value);
        }

        // 5. Auto-create a Report Link on the parent: "See {child.Name}" — navigates to filtered child records.
        var appPublicId = await _appRepo.GetPublicIdByIdAsync(parent.AppId, ct);
        var reportLinkLabel = $"{child.Name} records";
        if (!await _fieldRepo.LabelExistsInTableAsync(parent.Id, reportLinkLabel, ct: ct))
        {
            var reportLink = await _fieldFactory.CreateAsync(parent, FieldTypeCodeNames.ReportLink, reportLinkLabel, false,
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
        await _fieldFactory.AppendToAutoAddFormsAsync(child.PublicId, childAddFids, ct);
        await _fieldFactory.AppendToAutoAddFormsAsync(parent.PublicId, parentAddFids, ct);

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

    /// <summary>A sub-field only makes sense against a composite Address field, and only for one
    /// of its real JSON keys — reject anything else eagerly with a clear message rather than
    /// silently creating a Lookup/Summary that will always resolve to null.</summary>
    private static void ValidateSubField(string? subField, string? fieldTypeCode, string paramName)
    {
        if (subField is null)
            return;

        if (fieldTypeCode != nameof(Domain.Enums.FieldTypeCode.Address))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [paramName] = ["A sub-field can only target a composite Address field."],
            });

        if (!AddressSubFields.All.Contains(subField))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [paramName] = [$"Must be one of: {string.Join(", ", AddressSubFields.All)}."],
            });
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
