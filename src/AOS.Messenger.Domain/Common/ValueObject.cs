namespace AOS.Messenger.Domain.Common;

/// <summary>
/// Base class for domain value objects.
/// Value objects are equal when all their components are equal.
/// </summary>
public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
            return false;

        return ((ValueObject)obj)
            .GetEqualityComponents()
            .SequenceEqual(GetEqualityComponents());
    }

    public override int GetHashCode() =>
        GetEqualityComponents()
            .Aggregate(1, (current, obj) =>
                unchecked(current * 23 + (obj?.GetHashCode() ?? 0)));

    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(ValueObject? left, ValueObject? right) =>
        !(left == right);
}
