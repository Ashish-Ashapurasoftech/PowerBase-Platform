using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppImportJobRepository
{
    Task<AppImportJob> CreateAsync(AppImportJob job, CancellationToken ct = default);

    /// <summary>The job without its uploaded content. Null when it does not exist.</summary>
    Task<AppImportJob?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);

    /// <summary>The job including its uploaded content — only the runner needs this.</summary>
    Task<AppImportJob?> GetWithContentAsync(Guid publicId, CancellationToken ct = default);

    /// <summary>The caller's jobs that are not dismissed, newest first, without content.</summary>
    Task<IReadOnlyList<AppImportJob>> ListForUserAsync(long userId, CancellationToken ct = default);

    Task MarkRunningAsync(long id, CancellationToken ct = default);
    Task UpdateProgressAsync(long id, string stage, int percent, CancellationToken ct = default);
    Task CompleteAsync(long id, Guid appPublicId, string appName, string resultJson, CancellationToken ct = default);
    Task FailAsync(long id, string error, CancellationToken ct = default);
    Task SetEmailResultAsync(long id, bool sent, string? error, CancellationToken ct = default);
    Task DismissAsync(Guid publicId, long userId, CancellationToken ct = default);

    /// <summary>Queued/Running jobs created before <paramref name="startedBefore"/> belong to a server process that no longer
    /// exists; marks them failed so the UI does not show a progress bar that will never move.</summary>
    Task FailInterruptedAsync(long userId, DateTime startedBefore, CancellationToken ct = default);
}

public interface IEmailTemplateRepository
{
    Task<EmailTemplate?> GetAsync(string templateKey, CancellationToken ct = default);
    Task UpsertAsync(string templateKey, string subject, string body, bool isEnabled, long? modifiedBy, CancellationToken ct = default);
    Task DeleteAsync(string templateKey, CancellationToken ct = default);
}
