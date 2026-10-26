using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Pbl;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Import.Services;

/// <summary>After elements have been created (or already exist), records which PBL logical ref
/// each one corresponds to. Elements are located by the same names the importer created them
/// with, so it is safe to call repeatedly — rows are upserted, never duplicated — and it covers
/// whatever subset of the document actually exists in the app.</summary>
public class ImportSourceRefRecorder
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IFormRepository _formRepo;
    private readonly IAppRoleRepository _roleRepo;
    private readonly IAppVariableRepository _variableRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IImportSourceRefRepository _sourceRefRepo;

    public ImportSourceRefRecorder(
        IAppRepository appRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        IReportRepository reportRepo, IFormRepository formRepo, IAppRoleRepository roleRepo,
        IAppVariableRepository variableRepo, IPageRepository pageRepo, IImportSourceRefRepository sourceRefRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _formRepo = formRepo;
        _roleRepo = roleRepo;
        _variableRepo = variableRepo;
        _pageRepo = pageRepo;
        _sourceRefRepo = sourceRefRepo;
    }

    /// <summary>Returns the "type|logicalRef" key of every element that was found and recorded.</summary>
    public async Task<HashSet<string>> RecordAsync(PblDocument document, Guid appPublicId, CancellationToken ct)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var refs = new List<(string, string, long)>();

        var tables = await _tableRepo.ListByAppAsync(appId, ct);
        var tableByName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var tableRefByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in document.Tables ?? [])
        {
            if (!tableByName.TryGetValue(table.Name, out var created))
                continue;

            tableRefByName[table.Name] = table.LogicalRef;
            refs.Add((ImportElementTypes.Table, table.LogicalRef, created.Id));

            var createdFields = await _fieldRepo.ListByTableAsync(created.Id, ct);
            // Relationship-created fields get regenerated Names, so a relationship is located by its
            // Reference field's Label (what the importer passed in), not by name.
            var referenceFieldIdByLabel = createdFields
                .Where(f => string.Equals(f.TypeCode, "Reference", StringComparison.OrdinalIgnoreCase) && f.Label is not null)
                .GroupBy(f => f.Label!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var fieldIdByName = createdFields
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            foreach (var field in table.Fields ?? [])
                if (fieldIdByName.TryGetValue(field.Name, out var fieldId))
                    refs.Add((ImportElementTypes.Field, field.LogicalRef, fieldId));

            foreach (var rel in (document.Relationships ?? []).Where(r => r.ChildTableRef == table.LogicalRef))
                if (referenceFieldIdByLabel.TryGetValue(rel.ReferenceFieldLabel ?? rel.ReferenceFieldName, out var refFieldId))
                    refs.Add((ImportElementTypes.Relationship, rel.LogicalRef, refFieldId));

            if ((table.Reports ?? []).Count > 0)
            {
                var reportIdByName = (await _reportRepo.ListByTableAsync(created.PublicId, ct))
                    .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
                foreach (var report in table.Reports!)
                    if (reportIdByName.TryGetValue(report.Name, out var reportId))
                        refs.Add((ImportElementTypes.Report, report.LogicalRef, reportId));
            }

            var tableForms = (document.Forms ?? []).Where(f => f.TableRef == table.LogicalRef).ToList();
            if (tableForms.Count > 0)
            {
                var formIdByName = (await _formRepo.ListByTableAsync(created.PublicId, ct))
                    .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
                foreach (var form in tableForms)
                    if (formIdByName.TryGetValue(form.Name, out var formId))
                        refs.Add((ImportElementTypes.Form, form.LogicalRef, formId));
            }
        }

        if ((document.Roles ?? []).Count > 0)
        {
            var roleIdByName = (await _roleRepo.ListDetailsByAppIdAsync(appId, ct))
                .ToDictionary(r => r.Name, r => r.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var role in document.Roles!)
                if (roleIdByName.TryGetValue(role.Name, out var roleId))
                    refs.Add((ImportElementTypes.Role, role.LogicalRef, roleId));
        }

        if ((document.Variables ?? []).Count > 0)
        {
            var variableIdByName = (await _variableRepo.ListAsync(appId, ct))
                .ToDictionary(v => v.Name, v => v.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var variable in document.Variables!)
                if (variableIdByName.TryGetValue(variable.Name, out var variableId))
                    refs.Add((ImportElementTypes.Variable, variable.LogicalRef, variableId));
        }

        if ((document.Pages ?? []).Count > 0)
        {
            var pageIdByName = (await _pageRepo.ListAllByAppAsync(appId, null, ct))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            foreach (var page in document.Pages!)
                if (pageIdByName.TryGetValue(page.Name, out var pageId))
                    refs.Add((ImportElementTypes.Page, page.LogicalRef, pageId));
        }

        await _sourceRefRepo.UpsertManyAsync(appId, refs, ct);
        return refs.Select(r => Key(r.Item1, r.Item2)).ToHashSet(StringComparer.Ordinal);
    }

    public static string Key(string elementType, string logicalRef) => $"{elementType}|{logicalRef}";
}
