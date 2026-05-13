namespace PowerBase.Domain.Exceptions;

public class UnauthorizedActionException : DomainException
{
    public UnauthorizedActionException(string action)
        : base("UNAUTHORIZED_ACTION", $"You are not authorized to perform the action: {action}.") { }
}
