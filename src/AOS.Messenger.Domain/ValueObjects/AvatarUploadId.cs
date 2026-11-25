using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.ValueObjects;

/// <summary>
/// Domain-neutral strongly-typed identifier for a staged user avatar upload, wrapping a Guid.
/// </summary>
public sealed class AvatarUploadId : ValueObject, IEquatable<AvatarUploadId>, IComparable<AvatarUploadId>, IComparable
{
    public Guid Value { get; }

    public AvatarUploadId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("AvatarUploadId cannot be an empty Guid.");
        }

        Value = value;
    }

    public static AvatarUploadId New() => new(Guid.NewGuid());

    public static AvatarUploadId From(Guid value) => new(value);

    public static AvatarUploadId Parse(string value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return new AvatarUploadId(guid);
        }

        throw new DomainException($"Invalid AvatarUploadId format: '{value}'.");
    }

    public static bool TryParse(string? value, out AvatarUploadId? uploadId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            uploadId = new AvatarUploadId(guid);
            return true;
        }

        uploadId = null;
        return false;
    }

    public bool Equals(AvatarUploadId? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is AvatarUploadId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(AvatarUploadId? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is AvatarUploadId other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(AvatarUploadId)}.", nameof(obj));
    }

    public static bool operator <(AvatarUploadId? left, AvatarUploadId? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(AvatarUploadId? left, AvatarUploadId? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(AvatarUploadId? left, AvatarUploadId? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(AvatarUploadId? left, AvatarUploadId? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
