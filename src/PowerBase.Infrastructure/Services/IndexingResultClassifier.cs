using System.Collections.Generic;
using System.Linq;
using Azure.Search.Documents.Models;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Reads the per-document results of an Azure AI Search indexing call. The service answers a partly failed batch with
/// HTTP 207 and one result per document instead of throwing, so the status of each key has to be looked at.
/// </summary>
public static class IndexingResultClassifier
{
    /// <summary>Statuses worth trying again: version conflict, index temporarily unavailable, throttled, service busy.</summary>
    public static bool IsRetryable(int status) => status is 409 or 422 or 429 or 503;

    /// <summary>Statuses that mean "try again shortly in this same call" (throttling), as opposed to a later redelivery.</summary>
    public static bool IsThrottle(int status) => status is 429 or 503;

    public static IReadOnlyList<SearchIndexFailure> Classify(IEnumerable<IndexingResult> results) =>
        results
            .Where(r => !r.Succeeded)
            .Select(r => new SearchIndexFailure(r.Key, r.Status, r.ErrorMessage, IsRetryable(r.Status)))
            .ToList();
}
