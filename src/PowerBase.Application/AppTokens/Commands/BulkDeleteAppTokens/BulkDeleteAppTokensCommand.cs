namespace PowerBase.Application.AppTokens.Commands.BulkDeleteAppTokens;

public record BulkDeleteAppTokensCommand(Guid AppPublicId, IReadOnlyList<Guid> PublicIds);
