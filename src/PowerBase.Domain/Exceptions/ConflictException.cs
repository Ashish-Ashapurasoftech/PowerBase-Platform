namespace PowerBase.Domain.Exceptions;

public class ConflictException : DomainException
{
    public ConflictException(string message)
        : base("CONFLICT", message) { }
}
