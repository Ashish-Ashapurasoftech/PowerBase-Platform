namespace PowerBase.Application.Pipelines;

/// <summary>A user mapping error that Handle Errors can catch but a queue retry cannot repair.</summary>
public sealed class PipelineMappingException(string message, Exception innerException)
    : FormatException(message, innerException);
