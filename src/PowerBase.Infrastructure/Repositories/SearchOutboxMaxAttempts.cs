using Microsoft.Extensions.Configuration;
using PowerBase.Application.Search;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>Search:Outbox:MaxAttempts, the limit the relay parks outbox rows and field jobs at (same section it reads).</summary>
internal static class SearchOutboxMaxAttempts
{
    public static int From(IConfiguration? configuration)
    {
        var configured = configuration?.GetSection("Search:Outbox").Get<SearchOutboxOptions>()?.MaxAttempts;
        return configured is > 0 ? configured.Value : new SearchOutboxOptions().MaxAttempts;
    }
}
