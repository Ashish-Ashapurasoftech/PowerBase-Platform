namespace PowerBase.Domain.Exceptions;

public class DuplicateException : DomainException
{
    public DuplicateException(string resource, string field, object value)
        : base($"DUPLICATE_{resource.ToUpper()}", $"{resource} with {field} '{value}' already exists.") { }

    public DuplicateException(string resource, string message)
        : base($"DUPLICATE_{resource.ToUpper()}", message) { }
}
