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

/// <summary>
/// EF Core ValueConverter for domain-neutral strongly-typed MediaId to Guid.
/// </summary>
public sealed class MediaIdConverter : ValueConverter<MediaId, Guid>
{
    public MediaIdConverter()
        : base(
            id => id == null ? Guid.Empty : id.Value,
            value => MediaId.From(value))
    {
    }
}

/// <summary>
/// EF Core ValueConverter for nullable MediaId to nullable Guid.
/// </summary>
public sealed class NullableMediaIdConverter : ValueConverter<MediaId?, Guid?>
{
    public NullableMediaIdConverter()
        : base(
            id => id == null ? null : id.Value,
            value => value.HasValue ? MediaId.From(value.Value) : null)
    {
    }
}

/// <summary>
/// EF Core ValueConverter for domain-neutral strongly-typed AvatarUploadId to Guid.
/// </summary>
public sealed class AvatarUploadIdConverter : ValueConverter<AvatarUploadId, Guid>
{
    public AvatarUploadIdConverter()
        : base(
            id => id == null ? Guid.Empty : id.Value,
            value => AvatarUploadId.From(value))
    {
    }
}

/// <summary>
/// EF Core ValueConverter for nullable AvatarUploadId to nullable Guid.
/// </summary>
public sealed class NullableAvatarUploadIdConverter : ValueConverter<AvatarUploadId?, Guid?>
{
    public NullableAvatarUploadIdConverter()
        : base(
            id => id == null ? null : id.Value,
            value => value.HasValue ? AvatarUploadId.From(value.Value) : null)
    {
    }
}

/// <summary>
/// EF Core ValueConverter for domain-neutral strongly-typed StickerPackId to Guid.
/// </summary>
public sealed class StickerPackIdConverter : ValueConverter<StickerPackId, Guid>
{
    public StickerPackIdConverter()
        : base(
            id => id == null ? Guid.Empty : id.Value,
            value => StickerPackId.From(value))
    {
    }
}

/// <summary>
/// EF Core ValueConverter for nullable StickerPackId to nullable Guid.
/// </summary>
public sealed class NullableStickerPackIdConverter : ValueConverter<StickerPackId?, Guid?>
{
    public NullableStickerPackIdConverter()
        : base(
            id => id == null ? null : id.Value,
            value => value.HasValue ? StickerPackId.From(value.Value) : null)
    {
    }
}

/// <summary>
/// EF Core ValueConverter for domain-neutral strongly-typed StickerId to Guid.
/// </summary>
public sealed class StickerIdConverter : ValueConverter<StickerId, Guid>
{
    public StickerIdConverter()
        : base(
            id => id == null ? Guid.Empty : id.Value,
            value => StickerId.From(value))
    {
    }
}

/// <summary>
/// EF Core ValueConverter for nullable StickerId to nullable Guid.
/// </summary>
public sealed class NullableStickerIdConverter : ValueConverter<StickerId?, Guid?>
{
    public NullableStickerIdConverter()
        : base(
            id => id == null ? null : id.Value,
            value => value.HasValue ? StickerId.From(value.Value) : null)
    {
    }
}
