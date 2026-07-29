namespace PowerBase.Domain.Exceptions;

/// <summary>Thrown when an Action Button's server-side Link Expiration window has passed.
/// Enforced at the moment of click regardless of what the client UI still displays
/// (Field-Type spec: "An expired click returns an error").</summary>
public class LinkExpiredException : DomainException
{
    public LinkExpiredException(string message = "This button has expired.")
        : base("LINK_EXPIRED", message) { }
}
