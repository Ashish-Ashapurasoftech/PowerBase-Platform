namespace PowerBase.Domain.Exceptions;

public class NotFoundException : DomainException
{
    public NotFoundException(string resource, object id)
        : base($"{resource.ToUpper()}_NOT_FOUND", $"{resource} '{id}' was not found.") { }
}
