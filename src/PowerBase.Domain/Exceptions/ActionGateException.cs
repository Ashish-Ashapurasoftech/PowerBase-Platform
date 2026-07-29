namespace PowerBase.Domain.Exceptions;

/// <summary>Thrown when an Action Button's Bool-Field Gate or Password Gate fails at click
/// time (the action does not proceed).</summary>
public class ActionGateException : DomainException
{
    public ActionGateException(string message)
        : base("ACTION_GATE_FAILED", message) { }
}
