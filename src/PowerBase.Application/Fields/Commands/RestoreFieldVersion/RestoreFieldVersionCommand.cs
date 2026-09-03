namespace PowerBase.Application.Fields.Commands.RestoreFieldVersion;

public record RestoreFieldVersionCommand(
    Guid TablePublicId,
    Guid FieldPublicId,
    int VersionToRestore,
    string CommitMessage);
