using PowerBase.Application.Common.Formatting;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>Everything loaded once for one Change Type request (preview or execute).</summary>
public sealed class FieldTypeChangePlan
{
    public required App App { get; init; }
    public required AppTable Table { get; init; }
    public required AppField Field { get; init; }
    public required IReadOnlyList<AppField> TableFields { get; init; }
    public required string TargetTypeCode { get; init; }
    /// <summary>Null when this tenant's catalog has no such type.</summary>
    public FieldType? TargetType { get; init; }
    public required ConversionCheck Check { get; init; }
    public required FieldValueConversionContext ConversionContext { get; init; }
    /// <summary>Formula source only: each live record's evaluated value (row Id → value).</summary>
    public IReadOnlyDictionary<long, object?>? FormulaValues { get; init; }

    public string SourceValueType => FieldTypeConversionMatrix.ValueTypeOf(Field.TypeCode);
    public string TargetValueType => FieldTypeConversionMatrix.ValueTypeOf(TargetTypeCode);

    public ConvertedValue Convert(object? raw) => FieldValueConverter.Convert(raw, Field.TypeCode, TargetTypeCode, ConversionContext);
}

/// <summary>
/// Loads and checks a Change Type request: the field (which must belong to the table in the route), its app and table,
/// the target type, the matrix verdict, and the conversion context — app date formatting, the users needed for
/// User ↔ Text/Email, and (for a formula source) every record's evaluated formula value.
/// </summary>
public sealed class FieldTypeChangePlanner
{
    /// <summary>Most distinct stored values read up front to resolve users.</summary>
    private const int MaxDistinctUserValues = 20_000;

    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IFormulaProjector _formulaProjector;
    private readonly IRelationalProjector _relationalProjector;
    private readonly IFieldColumnConversionService _columns;
    private readonly FieldTypeChangeAnalyzer _analyzer;

    public FieldTypeChangePlanner(
        IAppRepository appRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IFieldTypeRepository fieldTypeRepo,
        IAppUserRepository appUserRepo, IUserRepository userRepo, IRecordRepository recordRepo,
        IFormulaProjector formulaProjector, IRelationalProjector relationalProjector,
        IFieldColumnConversionService columns, FieldTypeChangeAnalyzer analyzer)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _recordRepo = recordRepo;
        _formulaProjector = formulaProjector;
        _relationalProjector = relationalProjector;
        _columns = columns;
        _analyzer = analyzer;
    }

    /// <summary>Loads the table and field named in the route; the field must belong to that table.</summary>
    public async Task<(AppTable Table, AppField Field)> LoadFieldAsync(Guid tablePublicId, Guid fieldPublicId, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var field = await _fieldRepo.GetByPublicIdAsync(fieldPublicId, ct) ?? throw new NotFoundException("Field", fieldPublicId);
        if (field.AppTableId != table.Id) throw new NotFoundException("Field", fieldPublicId);
        return (table, field);
    }

    public async Task<FieldTypeChangePlan> BuildAsync(Guid tablePublicId, Guid fieldPublicId, string targetTypeCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetTypeCode))
            throw new ValidationException(new Dictionary<string, string[]> { ["targetTypeCode"] = ["Choose the new field type."] });

        var (table, field) = await LoadFieldAsync(tablePublicId, fieldPublicId, ct);
        var app = await _appRepo.GetByIdAsync(table.AppId, ct);
        var tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var targetType = await _fieldTypeRepo.GetByCodeAsync(targetTypeCode, ct);

        var check = FieldTypeConversionMatrix.Check(field.TypeCode, targetTypeCode, await _analyzer.GetConstraintsAsync(table, field, ct));
        if (check.IsAllowed && targetType is null)
            check = ConversionCheck.Blocked(field.TypeCode, targetTypeCode, $"The {targetTypeCode} field type isn't available in this app.");

        IReadOnlyDictionary<long, object?>? formulaValues = null;
        if (check.IsAllowed && check.Kind == ConversionKind.FormulaToScalar)
            formulaValues = await EvaluateFormulaAsync(table, tableFields, field, ct);

        var context = new FieldValueConversionContext
        {
            AppFormatting = DisplayValueFormatter.ParseAppFormatting(app.Formatting),
            SourceSettingsJson = field.Settings,
            TargetSettingsJson = null, // the new type starts from default settings
        };
        if (check.IsAllowed && check.Kind is ConversionKind.ScalarToScalar or ConversionKind.FormulaToScalar)
            context = await WithUsersAsync(context, app, table, field, targetTypeCode, formulaValues, ct);

        return new FieldTypeChangePlan
        {
            App = app, Table = table, Field = field, TableFields = tableFields, TargetTypeCode = targetTypeCode,
            TargetType = targetType, Check = check, ConversionContext = context, FormulaValues = formulaValues,
        };
    }

    /// <summary>Evaluates the formula for every live record, exactly as a record read does (relational values first).</summary>
    private async Task<IReadOnlyDictionary<long, object?>> EvaluateFormulaAsync(
        AppTable table, IReadOnlyList<AppField> tableFields, AppField field, CancellationToken ct)
    {
        var rows = await _recordRepo.ListAllRowsDecryptedAsync(table, tableFields, ct);
        var relational = await _relationalProjector.ProjectAsync(table, tableFields, rows, ct);
        var computed = _formulaProjector.Project(tableFields, rows, relational, table);

        var values = new Dictionary<long, object?>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].TryGetValue("Id", out var id) || id is null) continue;
            values[System.Convert.ToInt64(id)] = computed[i].GetValueOrDefault(field.Fid!.Value);
        }
        return values;
    }

    /// <summary>User ↔ Text/Email needs users resolved up front (the converter itself is pure): user Ids already
    /// stored → their emails, and emails found in the text → this app's users only.</summary>
    private async Task<FieldValueConversionContext> WithUsersAsync(
        FieldValueConversionContext context, App app, AppTable table, AppField field, string targetTypeCode,
        IReadOnlyDictionary<long, object?>? formulaValues, CancellationToken ct)
    {
        var source = FieldTypeConversionMatrix.ValueTypeOf(field.TypeCode);
        var target = FieldTypeConversionMatrix.ValueTypeOf(targetTypeCode);
        if (source != "User" && target != "User") return context;

        IReadOnlyList<object> distinct = formulaValues is not null
            ? formulaValues.Values.Where(v => v is not null).Distinct().Take(MaxDistinctUserValues).ToList()!
            : await _columns.ReadDistinctValuesAsync(table, field, MaxDistinctUserValues, ct);

        if (source == "User")
        {
            var ids = distinct.Select(v => long.TryParse(System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Split('|')[0], out var id) ? id : (long?)null)
                .OfType<long>().Distinct().ToList();
            var identities = ids.Count == 0 ? new Dictionary<long, UserIdentity>() : await _userRepo.GetIdentitiesByIdsAsync(ids, ct);
            return new FieldValueConversionContext
            {
                AppFormatting = context.AppFormatting, SourceSettingsJson = context.SourceSettingsJson, TargetSettingsJson = context.TargetSettingsJson,
                UserEmailsById = identities.ToDictionary(p => p.Key, p => p.Value.Email),
            };
        }

        var appEmails = (await _appUserRepo.ListByAppIdAsync(app.Id, ct))
            .Select(u => u.UserEmail).Where(e => !string.IsNullOrWhiteSpace(e))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var idsByEmail = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in distinct)
        {
            var text = System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (source == "RichText") text = HtmlText.ToPlainText(text);
            if (!ContactText.TryExtractEmail(text, out var email) || idsByEmail.ContainsKey(email) || !appEmails.Contains(email)) continue;
            if (await _userRepo.GetByEmailAsync(email, ct) is { } user) idsByEmail[email] = user.Id;
        }
        return new FieldValueConversionContext
        {
            AppFormatting = context.AppFormatting, SourceSettingsJson = context.SourceSettingsJson, TargetSettingsJson = context.TargetSettingsJson,
            UserIdsByEmail = idsByEmail,
            // So the preview shows a matched user by email rather than by internal id.
            UserEmailsById = idsByEmail.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key),
        };
    }
}
