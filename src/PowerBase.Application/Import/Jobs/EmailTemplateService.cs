using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Jobs;

public sealed record EmailTemplateDto(
    string Key, string Subject, string Body, bool IsEnabled, bool IsCustomized, IReadOnlyList<string> Placeholders, DateTime? ModifiedOn);

/// <summary>Lets a tenant administrator read, change and reset the emails the platform sends about background imports.</summary>
public sealed class EmailTemplateService(IEmailTemplateRepository templates, IQueryContext context)
{
    private const int MaxSubject = 300;
    private const int MaxBody = 100_000;
    private static readonly Regex Token = new(@"\{\{\s*(\w+)\s*\}\}", RegexOptions.Compiled);

    public async Task<IReadOnlyList<EmailTemplateDto>> ListAsync(CancellationToken ct)
    {
        var result = new List<EmailTemplateDto>();
        foreach (var key in AppImportEmailTemplates.Keys)
            result.Add(await GetAsync(key, ct));
        return result;
    }

    public async Task<EmailTemplateDto> GetAsync(string key, CancellationToken ct)
    {
        RequireKnown(key);
        var custom = await templates.GetAsync(key, ct);
        if (custom is not null)
            return new EmailTemplateDto(key, custom.Subject, custom.Body, custom.IsEnabled, true, AppImportEmailTemplates.Placeholders, custom.ModifiedOn);
        var (subject, body) = AppImportEmailTemplates.Default(key);
        return new EmailTemplateDto(key, subject, body, true, false, AppImportEmailTemplates.Placeholders, null);
    }

    public async Task<EmailTemplateDto> SaveAsync(string key, string? subject, string? body, bool isEnabled, CancellationToken ct)
    {
        RequireKnown(key);
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(subject)) errors["Subject"] = ["Subject is required."];
        else if (subject.Length > MaxSubject) errors["Subject"] = [$"Subject must be at most {MaxSubject} characters."];
        if (string.IsNullOrWhiteSpace(body)) errors["Body"] = ["Body is required."];
        else if (body.Length > MaxBody) errors["Body"] = [$"Body must be at most {MaxBody} characters."];

        // A misspelled placeholder would silently vanish from every email: refuse it instead.
        var unknown = Token.Matches((subject ?? "") + (body ?? "")).Select(m => m.Groups[1].Value)
            .Where(n => !AppImportEmailTemplates.Placeholders.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
        if (unknown.Count > 0)
            errors["Placeholders"] = [$"Unknown placeholder(s): {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}."];
        if (errors.Count > 0) throw new ValidationException(errors);

        await templates.UpsertAsync(key, subject!.Trim(), body!, isEnabled, context.UserId, ct);
        return await GetAsync(key, ct);
    }

    /// <summary>Removes the tenant's override so the built-in default applies again.</summary>
    public async Task<EmailTemplateDto> ResetAsync(string key, CancellationToken ct)
    {
        RequireKnown(key);
        await templates.DeleteAsync(key, ct);
        return await GetAsync(key, ct);
    }

    private static void RequireKnown(string key)
    {
        if (!AppImportEmailTemplates.Keys.Contains(key, StringComparer.Ordinal))
            throw new NotFoundException("EmailTemplate", key);
    }
}
