namespace AOS.Messenger.Domain.Common;

/// <summary>
/// Base class for all domain entities.
/// Provides a typed primary key and structural equality by ID.
/// </summary>
public abstract class Entity<TId>
{
    public TId Id { get; protected set; } = default!;
}
