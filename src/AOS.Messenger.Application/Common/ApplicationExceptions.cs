namespace AOS.Messenger.Application.Common;

/// <summary>
/// Thrown when a requested resource does not exist.
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }

    public NotFoundException(string entityName, object key)
        : base($"Entity '{entityName}' with key '{key}' was not found.") { }
}

/// <summary>
/// Thrown when the current user is not permitted to perform an action.
/// </summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message = "Access denied.") : base(message) { }
}

/// <summary>
/// Thrown when the operation conflicts with existing state
/// (e.g., duplicate idempotency key with different content, uniqueness violation, optimistic concurrency failure).
/// </summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }

    public ConflictException(string message, Exception innerException) : base(message, innerException) { }
}
