using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Provisions a relationship field (Reference/Lookup/Summary) on a table — the shared
/// field-creation path used when growing a relationship (Add Lookup / Add Summary).
/// Mirrors the inline logic in <c>CreateRelationshipCommandHandler</c>: resolves the
/// field type, allocates a Fid, creates the meta row, and adds a physical column only
/// for non-computed (Reference) fields. Also appends new fields to auto-add forms.
/// </summary>
public sealed class RelationshipFieldFactory
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IFormRepository _formRepo;
    private readonly IQueryContext _queryContext;
    private readonly IFieldNameResolver _fieldNameResolver;

    public RelationshipFieldFactory(
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        ISchemaEngineService schemaEngine,
        IFormRepository formRepo,
        IQueryContext queryContext,
        IFieldNameResolver fieldNameResolver)
    {
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _schemaEngine = schemaEngine;
        _formRepo = formRepo;
        _queryContext = queryContext;
        _fieldNameResolver = fieldNameResolver;
    }

    /// <summary>Creates a relationship field (Reference/Lookup/Summary/ReportLink). Name is generated
    /// from <paramref name="label"/> — callers never supply Name directly (see IFieldNameResolver).</summary>
    public async Task<AppField> CreateAsync(
        AppTable table, string typeCode, string label, bool isRequired, object settingsObj, CancellationToken ct)
    {
        var fieldType = await _fieldTypeRepo.GetByCodeAsync(typeCode, ct) ?? throw new NotFoundException("FieldType", typeCode);

        if (await _fieldRepo.LabelExistsInTableAsync(table.Id, label, ct: ct))
            throw new DuplicateException("Field", "label", label);

        var name = await _fieldNameResolver.GenerateUniqueNameAsync(table.Id, label, isSystem: false, ct);

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

    public async Task AppendToAutoAddFormsAsync(Guid tablePublicId, IReadOnlyList<int> fids, CancellationToken ct)
    {
        if (fids.Count == 0) return;
        var forms = await _formRepo.ListByTableAsync(tablePublicId, ct);
        foreach (var form in forms.Where(f => f.AutoAddNewFields))
            foreach (var fid in fids)
                await _formRepo.AppendFieldToLastSectionAsync(form.Id, fid, ct);
    }

    public static string Serialize(object settings) => JsonSerializer.Serialize(settings, JsonOpts);
}
