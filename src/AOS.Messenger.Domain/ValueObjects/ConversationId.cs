using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.ValueObjects;

/// <summary>
/// Domain-neutral strongly-typed identifier for a conversation, wrapping a Guid.
/// </summary>
public sealed class ConversationId : ValueObject, IEquatable<ConversationId>, IComparable<ConversationId>, IComparable
{
    public Guid Value { get; }

    public ConversationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("ConversationId cannot be an empty Guid.");
        }

        Value = value;
    }

    public static ConversationId New() => new(Guid.NewGuid());

    public static ConversationId From(Guid value) => new(value);

    public static ConversationId Parse(string value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return new ConversationId(guid);
        }

        throw new DomainException($"Invalid ConversationId format: '{value}'.");
    }

    public static bool TryParse(string? value, out ConversationId? conversationId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            conversationId = new ConversationId(guid);
            return true;
        }

        conversationId = null;
        return false;
    }

    public bool Equals(ConversationId? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is ConversationId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(ConversationId? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is ConversationId other) return CompareTo(other);
        throw new ArgumentException($"Object must be of type {nameof(ConversationId)}.", nameof(obj));
    }

    public static bool operator <(ConversationId? left, ConversationId? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(ConversationId? left, ConversationId? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(ConversationId? left, ConversationId? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(ConversationId? left, ConversationId? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
