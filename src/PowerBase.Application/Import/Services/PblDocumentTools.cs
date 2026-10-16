using System.Text.Json;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Diff;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using YamlDotNet.Core;

namespace PowerBase.Application.Import.Services;

/// <summary>Document-level helpers shared by the diff and update handlers: loading/validating an
/// uploaded file, dropping tables the user excluded, and cutting a document down to just the
/// elements about to be added.</summary>
public static class PblDocumentTools
{
    /// <summary>Parses (QBL or PBL), blocks on conversion/validation errors, and returns the
    /// document with its conversion warnings as skipped items.</summary>
    public static (PblDocument Document, List<ImportSkippedItem> Warnings) Load(string content, PblValidator validator)
    {
        PblDocument document;
        List<PblIssue> conversionIssues;
        try
        {
            (document, conversionIssues) = ImportDocumentParser.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Pbl"] = [$"Could not parse PBL document: {ex.Message}"] });
        }
        catch (YamlException ex)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Qbl"] = [$"Could not parse QBL document: {ex.Message}"] });
        }

        var conversionErrors = conversionIssues.Where(i => i.Severity == PblIssueSeverity.Error).ToList();
        if (conversionErrors.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["Qbl"] = conversionErrors.Select(e => e.Message).ToArray() });

        var validation = validator.Validate(document);
        if (!validation.IsValid)
            throw new ValidationException(new Dictionary<string, string[]> { ["Pbl"] = validation.Errors.Select(e => e.Message).ToArray() });

        var warnings = conversionIssues
            .Where(i => i.Severity == PblIssueSeverity.Warning)
            .Select(i => new ImportSkippedItem { LogicalRef = i.ElementRef ?? "", Name = i.ElementRef ?? "", Reason = i.Message })
            .ToList();

        return (document, warnings);
    }

    /// <summary>Removes the given tables and everything that hangs off them: their forms, any
    /// relationship touching them, and role permissions on them. Mutates <paramref name="document"/>.</summary>
    public static void ExcludeTables(PblDocument document, IReadOnlyCollection<string>? excludedTableRefs)
    {
        if (excludedTableRefs is null || excludedTableRefs.Count == 0)
            return;

        var excluded = new HashSet<string>(excludedTableRefs, StringComparer.Ordinal);
        document.Tables = (document.Tables ?? []).Where(t => !excluded.Contains(t.LogicalRef)).ToList();
        document.Relationships = (document.Relationships ?? [])
            .Where(r => !excluded.Contains(r.ParentTableRef) && !excluded.Contains(r.ChildTableRef)).ToList();
        document.Forms = (document.Forms ?? []).Where(f => !excluded.Contains(f.TableRef)).ToList();
        foreach (var role in document.Roles ?? [])
            role.TablePermissions = role.TablePermissions.Where(p => !excluded.Contains(p.TableRef)).ToList();
    }

    /// <summary>Copy of <paramref name="document"/> holding only the elements whose key is in
    /// <paramref name="approved"/>. Every table is kept (empty if nothing on it is approved) so
    /// the importer can still resolve table refs to names; tables themselves are expected to
    /// exist by the time the subset is applied.</summary>
    public static PblDocument Subset(PblDocument document, ISet<string> approved)
    {
        static string K(string type, string logicalRef) => ImportSourceRefRecorder.Key(type, logicalRef);

        return new PblDocument
        {
            Version = document.Version,
            App = document.App,
            Tables = (document.Tables ?? []).Select(t => new PblTable
            {
                LogicalRef = t.LogicalRef,
                Name = t.Name,
                SingularLabel = t.SingularLabel,
                PluralLabel = t.PluralLabel,
                Description = t.Description,
                Icon = t.Icon,
                AliasHint = t.AliasHint,
                Fields = (t.Fields ?? []).Where(f => approved.Contains(K(ImportElementTypes.Field, f.LogicalRef))).ToList(),
                Reports = (t.Reports ?? []).Where(r => approved.Contains(K(ImportElementTypes.Report, r.LogicalRef))).ToList(),
            }).ToList(),
            Relationships = (document.Relationships ?? []).Where(r => approved.Contains(K(ImportElementTypes.Relationship, r.LogicalRef))).ToList(),
            Forms = (document.Forms ?? []).Where(f => approved.Contains(K(ImportElementTypes.Form, f.LogicalRef))).ToList(),
            Roles = (document.Roles ?? []).Where(r => approved.Contains(K(ImportElementTypes.Role, r.LogicalRef))).ToList(),
            Variables = (document.Variables ?? []).Where(v => approved.Contains(K(ImportElementTypes.Variable, v.LogicalRef))).ToList(),
            Pages = (document.Pages ?? []).Where(p => approved.Contains(K(ImportElementTypes.Page, p.LogicalRef))).ToList(),
        };
    }
}
