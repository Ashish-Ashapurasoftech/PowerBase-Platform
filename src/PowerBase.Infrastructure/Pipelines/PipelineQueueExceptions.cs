using System;

namespace PowerBase.Infrastructure.Pipelines;

public class DuplicateMessageException : Exception
{
    public Guid MessageId { get; }
    public DuplicateMessageException(Guid messageId) : base($"MessageId '{messageId}' already exists.")
    {
        MessageId = messageId;
    }
}

public class MessageCollisionException : Exception
{
    public Guid MessageId { get; }
    public MessageCollisionException(Guid messageId) : base($"MessageId '{messageId}' has a payload collision.")
    {
        MessageId = messageId;
    }
}

public class MessageDeduplicatedException : Exception
{
    public Guid MessageId { get; }
    public MessageDeduplicatedException(Guid messageId) : base($"MessageId '{messageId}' was deduplicated successfully.")
    {
        MessageId = messageId;
    }
}
