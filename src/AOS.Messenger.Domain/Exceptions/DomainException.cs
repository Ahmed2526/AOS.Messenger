namespace AOS.Messenger.Domain.Exceptions;

/// <summary>
/// Thrown when a domain invariant or business rule is violated.
/// Defined in Domain layer as per architecture specification.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }

    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}
