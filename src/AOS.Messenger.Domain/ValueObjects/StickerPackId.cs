using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.ValueObjects;

/// <summary>
/// Domain-neutral strongly-typed identifier for a sticker pack, wrapping a Guid.
/// </summary>
public sealed class StickerPackId : ValueObject, IEquatable<StickerPackId>, IComparable<StickerPackId>, IComparable
{
    public Guid Value { get; }

    public StickerPackId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("StickerPackId cannot be an empty Guid.");
        }

        Value = value;
    }

    /// <summary>
    /// Creates a random <see cref="StickerPackId"/> for unit tests and in-memory scenarios only.
    /// SQL-persisted <c>StickerPack</c> entities must omit <c>id</c> (leave <see langword="null"/>) so SQL Server assigns <c>NEWSEQUENTIALID()</c>.
    /// </summary>
    public static StickerPackId New() => new(Guid.NewGuid());

    public static StickerPackId From(Guid value) => new(value);

    public static StickerPackId Parse(string value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return new StickerPackId(guid);
        }

        throw new DomainException($"Invalid StickerPackId format: '{value}'.");
    }

    public static bool TryParse(string? value, out StickerPackId? packId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            packId = new StickerPackId(guid);
            return true;
        }

        packId = null;
        return false;
    }

    public bool Equals(StickerPackId? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is StickerPackId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(StickerPackId? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is StickerPackId other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(StickerPackId)}.", nameof(obj));
    }

    public static bool operator <(StickerPackId? left, StickerPackId? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(StickerPackId? left, StickerPackId? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(StickerPackId? left, StickerPackId? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(StickerPackId? left, StickerPackId? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
