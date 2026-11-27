namespace AOS.Messenger.Application.Common;

/// <summary>
/// Thrown when a requested resource does not exist.
/// </summary>
public sealed class NotFoundException : Exception
{
    public bool HasInternalEntityDetail { get; }

    public NotFoundException(string message) : base(message) { }

    public NotFoundException(string entityName, object key)
        : base($"Entity '{entityName}' with key '{key}' was not found.")
    {
        HasInternalEntityDetail = true;
    }
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

/// <summary>
/// Thrown when an external object-storage operation fails transiently or unexpectedly.
/// Wraps SDK/HTTP errors without leaking raw infrastructure types into Application or Web.
/// </summary>
public sealed class MediaStorageException : Exception
{
    public MediaStorageException(string message) : base(message) { }

    public MediaStorageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when server-side image processing capacity is temporarily saturated or timed out
/// while the staged upload itself remains valid and retryable.
/// </summary>
public sealed class MediaProcessingBusyException : Exception
{
    public MediaProcessingBusyException(string message) : base(message) { }

    public MediaProcessingBusyException(string message, Exception innerException) : base(message, innerException) { }
}
