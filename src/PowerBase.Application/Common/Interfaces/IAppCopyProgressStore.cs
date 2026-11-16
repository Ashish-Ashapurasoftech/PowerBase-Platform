using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>Progress of running app copies, for the copy dialog's progress bar. Kept in memory by the API instance that
/// runs the copy (a copy is one request of a few seconds to a minute), under an id the dialog chooses, and readable only by
/// the user who started the copy.</summary>
public interface IAppCopyProgressStore
{
    /// <summary>Records a step. The percentage never goes back, and an id already used by another user is left alone.</summary>
    void Report(Guid progressId, long userId, int percent, string stage);

    /// <summary>Marks the copy as ended: 100% when it worked, failed otherwise.</summary>
    void Complete(Guid progressId, long userId, bool failed);

    /// <summary>The progress under this id, or null when there is none for this user.</summary>
    AppCopyProgress? Get(Guid progressId, long userId);
}
