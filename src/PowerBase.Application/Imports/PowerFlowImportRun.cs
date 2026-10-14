using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>What a PowerFlow run of an import needs from the import feature, as three small calls: put a file in, throw one away, start a run. The
/// real one only hands each to the code the import screen and the run API already use (so a run started here is checked, queued and reported exactly
/// like theirs); it is a seam so the rules below can be tested without a database.</summary>
public interface IPowerFlowImportActions
{
    /// <returns>The id of the stored upload.</returns>
    Task<Guid> UploadAsync(Guid tableId, Stream content, string fileName, long length, CancellationToken ct);
    Task DiscardAsync(Guid fileId, CancellationToken ct);
    Task<ImportRunStart> StartAsync(Guid importId, IReadOnlyList<string>? notifyEmails, string? baseUrl, Guid appId, string? clientToken, Guid? fileId, CancellationToken ct);
}

public sealed class PowerFlowImportActions(UploadImportFileHandler upload, DiscardImportFileHandler discard, StartImportRunHandler start) : IPowerFlowImportActions
{
    public async Task<Guid> UploadAsync(Guid tableId, Stream content, string fileName, long length, CancellationToken ct) =>
        (await upload.HandleAsync(tableId, content, fileName, length, ct)).FileId;

    public Task DiscardAsync(Guid fileId, CancellationToken ct) => discard.HandleAsync(fileId, ct);

    public Task<ImportRunStart> StartAsync(Guid importId, IReadOnlyList<string>? notifyEmails, string? baseUrl, Guid appId, string? clientToken, Guid? fileId, CancellationToken ct) =>
        start.StartAsync(importId, ImportTrigger.Api, notifyEmails, baseUrl, appId, clientToken, fileId, ct);
}

/// <summary>Starts a saved import from a PowerFlow, in one call whatever the import reads. A table import needs nothing but its id; a file import also needs
/// the file, sent with the call (the upload and the run are one step, so there is no file id to carry between two calls). The file is the person's own,
/// checked and read exactly as an upload from the import screen, and it is thrown away again if the run is not started (a rejected run, or a repeat of a call
/// that already started one). Everything else - permissions on every table, merge-key checks, one run at a time, the email, the history - is the run API's own.
/// <para>Checked in this order: who may ask, what was sent, then the data, so a stranger learns nothing from a validation message.</para></summary>
public sealed class StartPowerFlowImportHandler(
    IAppRepository apps, IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions, IPowerFlowImportActions actions)
{
    public const int MaxClientTokenLength = 200;
    private const int MaxEmailListLength = 4000;
    private static readonly char[] EmailSeparators = [',', ';', '\n', '\r', '\t', ' '];

    /// <param name="file">The file for a file import (a CSV or an Excel .xlsx); null for a table import.</param>
    /// <param name="notifyEmails">People to tell besides whoever the token belongs to, separated by commas or semicolons; replaces those saved on the import for this run.</param>
    public async Task<ImportRunStart> HandleAsync(
        Guid appId, Guid templateId, Stream? file, string? fileName, long fileLength, string? notifyEmails, string? clientToken, string? baseUrl, CancellationToken ct)
    {
        if (appId == Guid.Empty) throw Invalid("appId", "appId must be a valid id.");
        await access.RequireMembershipByAppPublicIdAsync(appId, ct);

        // What was sent.
        if (templateId == Guid.Empty) throw Invalid("templateId", "templateId must be a valid id.");
        var token = string.IsNullOrWhiteSpace(clientToken) ? null : clientToken.Trim();
        if (token is { Length: > MaxClientTokenLength }) throw Invalid("X-PowerBase-Client-Token", $"The client token can be at most {MaxClientTokenLength} characters.");
        var emails = ParseEmails(notifyEmails);

        // The data: an import of this app, and what kind it reads decides what must (and must not) come with it.
        var definition = await definitions.GetByPublicIdAsync(templateId, ct) ?? throw new NotFoundException("ImportDefinition", templateId);
        var destination = await tables.GetByIdAsync(definition.DestinationTableId, ct);
        if (destination.AppId != await apps.GetIdByPublicIdAsync(appId, ct)) throw new NotFoundException("ImportDefinition", templateId);

        var readsFile = string.Equals(definition.SourceKind, ImportSourceKinds.File, StringComparison.OrdinalIgnoreCase);
        if (readsFile && file is null) throw Invalid("file", "This import reads a file: send it as the form field 'file' (a CSV or an Excel .xlsx file).");
        if (!readsFile && file is not null) throw Invalid("file", "This import reads a table, not a file: send no file.");

        Guid? fileId = null;
        if (readsFile) fileId = await actions.UploadAsync(destination.PublicId, file!, fileName ?? "", fileLength, ct);

        ImportRunStart started;
        try { started = await actions.StartAsync(templateId, emails, baseUrl, appId, token, fileId, ct); }
        catch
        {
            await DiscardQuietlyAsync(fileId);
            throw;
        }
        // A repeat of a call that already started a run returns that run, which reads the file it first had: this one is not needed.
        if (started.Replayed) await DiscardQuietlyAsync(fileId);
        return started;
    }

    /// <summary>A file that is no longer needed is removed; failing to remove it must never hide the real outcome (the nightly clean-up removes it anyway).</summary>
    private async Task DiscardQuietlyAsync(Guid? fileId)
    {
        if (fileId is not { } id) return;
        try { await actions.DiscardAsync(id, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* removed by the 24-hour clean-up of uploads nobody ran */ }
    }

    /// <summary>The people to tell, from one form field: split on commas, semicolons and spaces, then checked as every list of people is (plain addresses, at most
    /// the allowed number, none repeated). Nothing means the people saved on the import are kept.</summary>
    private static List<string>? ParseEmails(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Length > MaxEmailListLength) throw Invalid("notifyEmails", "The list of people to tell is too long.");
        var list = ImportNotify.Normalize(raw.Split(EmailSeparators, StringSplitOptions.RemoveEmptyEntries));
        return list.Count == 0 ? null : list;
    }

    private static ValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
