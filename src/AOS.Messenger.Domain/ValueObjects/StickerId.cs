using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.ValueObjects;

/// <summary>
/// Domain-neutral strongly-typed identifier for a sticker asset, wrapping a Guid.
/// </summary>
public sealed class StickerId : ValueObject, IEquatable<StickerId>, IComparable<StickerId>, IComparable
{
    public Guid Value { get; }

    public StickerId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("StickerId cannot be an empty Guid.");
        }

        Value = value;
    }

    /// <summary>
    /// Creates a random <see cref="StickerId"/> for unit tests and in-memory scenarios only.
    /// SQL-persisted <c>Sticker</c> entities must omit <c>id</c> (leave <see langword="null"/>) so SQL Server assigns <c>NEWSEQUENTIALID()</c>.
    /// </summary>
    public static StickerId New() => new(Guid.NewGuid());

    public static StickerId From(Guid value) => new(value);

    public static StickerId Parse(string value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return new StickerId(guid);
        }

        throw new DomainException($"Invalid StickerId format: '{value}'.");
    }

    public static bool TryParse(string? value, out StickerId? stickerId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            stickerId = new StickerId(guid);
            return true;
        }

        stickerId = null;
        return false;
    }

    public bool Equals(StickerId? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is StickerId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(StickerId? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is StickerId other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(StickerId)}.", nameof(obj));
    }

    public static bool operator <(StickerId? left, StickerId? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(StickerId? left, StickerId? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(StickerId? left, StickerId? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(StickerId? left, StickerId? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
