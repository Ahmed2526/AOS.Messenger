using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.ValueObjects;

/// <summary>
/// Domain-neutral strongly-typed identifier for a media object, wrapping a Guid.
/// </summary>
public sealed class MediaId : ValueObject, IEquatable<MediaId>, IComparable<MediaId>, IComparable
{
    public Guid Value { get; }

    public MediaId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("MediaId cannot be an empty Guid.");
        }

        Value = value;
    }

    /// <summary>
    /// Creates a random <see cref="MediaId"/> for unit tests and in-memory scenarios only.
    /// SQL-persisted <c>Media</c> entities must omit <c>id</c> (leave <see langword="null"/>) so SQL Server assigns <c>NEWSEQUENTIALID()</c>.
    /// </summary>
    public static MediaId New() => new(Guid.NewGuid());

    public static MediaId From(Guid value) => new(value);

    public static MediaId Parse(string value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return new MediaId(guid);
        }

        throw new DomainException($"Invalid MediaId format: '{value}'.");
    }

    public static bool TryParse(string? value, out MediaId? mediaId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            mediaId = new MediaId(guid);
            return true;
        }

        mediaId = null;
        return false;
    }

    public bool Equals(MediaId? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is MediaId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(MediaId? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is MediaId other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(MediaId)}.", nameof(obj));
    }

    public static bool operator <(MediaId? left, MediaId? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(MediaId? left, MediaId? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(MediaId? left, MediaId? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(MediaId? left, MediaId? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
