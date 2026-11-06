namespace PowerBase.Domain.Exceptions;

public class UnauthorizedActionException : DomainException
{
    /// <param name="action">What was refused, e.g. "delete this app" or a whole sentence; a full stop it ends with is not
    /// doubled.</param>
    public UnauthorizedActionException(string action)
        : base("UNAUTHORIZED_ACTION", $"You are not authorized to perform the action: {action.TrimEnd('.')}.") { }
}
