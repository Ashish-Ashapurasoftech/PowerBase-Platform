namespace PowerBase.Domain.Exceptions;

public class ConcurrencyException : DomainException
{
    public ConcurrencyException(string resource)
        : base($"{resource.ToUpper()}_CONFLICT", $"{resource} was modified by another request. Please reload and try again.")
    {
    }
}
