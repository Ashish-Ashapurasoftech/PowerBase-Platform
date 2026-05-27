namespace PowerBase.Domain.Exceptions;

public class InternalServerException : DomainException
{
    public InternalServerException(string message) 
        : base("INTERNAL_SERVER_ERROR", message)
    {
    }

    public InternalServerException(string errorCode, string message) 
        : base(errorCode, message)
    {
    }
}
