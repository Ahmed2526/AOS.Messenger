using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.ValueObjects;

/// <summary>
/// Domain-neutral strongly-typed identifier for a message, wrapping a monotonic long (bigint IDENTITY).
/// </summary>
public sealed class MessageId : ValueObject, IEquatable<MessageId>, IComparable<MessageId>, IComparable
{
    private static long _sequenceCounter = 1000L;

    public long Value { get; }

    public MessageId(long value)
    {
        Value = value;
    }

    public static MessageId New() => new(Interlocked.Increment(ref _sequenceCounter));

    public static MessageId From(long value) => new(value);

    public static MessageId Parse(string value)
    {
        if (long.TryParse(value, out var id))
        {
            return new MessageId(id);
        }

        throw new DomainException($"Invalid MessageId format: '{value}'.");
    }

    public static bool TryParse(string? value, out MessageId? messageId)
    {
        if (long.TryParse(value, out var id))
        {
            messageId = new MessageId(id);
            return true;
        }

        messageId = null;
        return false;
    }

    public bool Equals(MessageId? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is MessageId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(MessageId? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is MessageId other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(MessageId)}.", nameof(obj));
    }

    public static bool operator <(MessageId? left, MessageId? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(MessageId? left, MessageId? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(MessageId? left, MessageId? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(MessageId? left, MessageId? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
