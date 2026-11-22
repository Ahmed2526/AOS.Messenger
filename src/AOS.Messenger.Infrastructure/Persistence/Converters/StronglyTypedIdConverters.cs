using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AOS.Messenger.Infrastructure.Persistence.Converters;

/// <summary>
/// EF Core ValueConverter for domain-neutral strongly-typed UserId to Guid.
/// </summary>
public sealed class UserIdConverter : ValueConverter<UserId, Guid>
{
    public UserIdConverter()
        : base(
            id => id.Value,
            value => UserId.From(value))
    {
    }
}

/// <summary>
/// EF Core ValueConverter for nullable UserId to nullable Guid.
/// </summary>
public sealed class NullableUserIdConverter : ValueConverter<UserId?, Guid?>
{
    public NullableUserIdConverter()
        : base(
            id => id == null ? null : id.Value,
            value => value.HasValue ? UserId.From(value.Value) : null)
    {
    }
}

/// <summary>
/// EF Core ValueConverter for domain-neutral strongly-typed ConversationId to Guid.
/// </summary>
public sealed class ConversationIdConverter : ValueConverter<ConversationId, Guid>
{
    public ConversationIdConverter()
        : base(
            id => id == null ? Guid.Empty : id.Value,
            value => ConversationId.From(value))
    {
    }
}

/// <summary>
/// EF Core ValueConverter for nullable ConversationId to nullable Guid.
/// </summary>
public sealed class NullableConversationIdConverter : ValueConverter<ConversationId?, Guid?>
{
    public NullableConversationIdConverter()
        : base(
            id => id == null ? null : id.Value,
            value => value.HasValue ? ConversationId.From(value.Value) : null)
    {
    }
}

/// <summary>
/// EF Core ValueConverter for domain-neutral strongly-typed MessageId to long (bigint IDENTITY).
/// </summary>
public sealed class MessageIdConverter : ValueConverter<MessageId, long>
{
    public MessageIdConverter()
        : base(
            id => id == null ? 0L : id.Value,
            value => MessageId.From(value))
    {
    }
}

/// <summary>
/// EF Core ValueConverter for nullable MessageId to nullable long.
/// </summary>
public sealed class NullableMessageIdConverter : ValueConverter<MessageId?, long?>
{
    public NullableMessageIdConverter()
        : base(
            id => id == null ? null : id.Value,
            value => value.HasValue ? MessageId.From(value.Value) : null)
    {
    }
}
