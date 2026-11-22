using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.ValueObjects;

/// <summary>
/// Domain-neutral strongly-typed identifier for a user, wrapping a Guid.
/// Decouples domain entities and application logic from ASP.NET Core Identity.
/// Implements deterministic SQL Server uniqueidentifier byte-order comparison.
/// </summary>
public sealed class UserId : ValueObject, IEquatable<UserId>, IComparable<UserId>, IComparable
{
    private static readonly int[] SqlGuidByteOrder = [10, 11, 12, 13, 14, 15, 8, 9, 6, 7, 4, 5, 0, 1, 2, 3];

    public Guid Value { get; }

    public UserId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("UserId cannot be an empty Guid.");
        }

        Value = value;
    }

    public static UserId New() => new(Guid.NewGuid());

    public static UserId From(Guid value) => new(value);

    public static UserId Parse(string value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return new UserId(guid);
        }

        throw new DomainException($"Invalid UserId format: '{value}'.");
    }

    public static bool TryParse(string? value, out UserId? userId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            userId = new UserId(guid);
            return true;
        }

        userId = null;
        return false;
    }

    public static int CompareSqlServer(UserId? left, UserId? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        var bytesA = left.Value.ToByteArray();
        var bytesB = right.Value.ToByteArray();

        for (int i = 0; i < 16; i++)
        {
            int index = SqlGuidByteOrder[i];
            int cmp = bytesA[index].CompareTo(bytesB[index]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    public bool Equals(UserId? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is UserId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(UserId? other) => CompareSqlServer(this, other);

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is UserId other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(UserId)}.", nameof(obj));
    }

    public static bool operator <(UserId? left, UserId? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(UserId? left, UserId? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(UserId? left, UserId? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(UserId? left, UserId? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
