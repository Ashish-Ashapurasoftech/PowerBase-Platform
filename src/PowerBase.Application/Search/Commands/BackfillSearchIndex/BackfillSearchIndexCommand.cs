using System;

namespace PowerBase.Application.Search.Commands.BackfillSearchIndex;

public record BackfillSearchIndexCommand(
    long TenantId,
    Action<int, int, int, int, string?>? OnProgress = null,
    long? TableId = null);
