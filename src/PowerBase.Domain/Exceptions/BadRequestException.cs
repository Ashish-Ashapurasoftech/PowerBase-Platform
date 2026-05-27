namespace PowerBase.Domain.Exceptions;

public class BadRequestException : DomainException
{
    public BadRequestException(string message) 
        : base("BAD_REQUEST", message)
    {
    }

    public BadRequestException(string errorCode, string message) 
        : base(errorCode, message)
    {
    }
}
