using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record StickerDto(
    StickerId Id,
    StickerPackId PackId,
    string PackName,
    string PackAuthor,
    bool IsAnimated,
    bool IsSystem,
    bool IsOwnedByCurrentUser,
    string? StaticUrl,
    int Width,
    int Height,
    string EmojiTags,
    bool IsFavorited);

public sealed record StickerPackDto(
    StickerPackId Id,
    string? Slug,
    string Name,
    string AuthorName,
    bool IsSystemPack,
    int SortOrder,
    IReadOnlyList<StickerDto> Stickers);

public sealed record StickerCatalogDto(
    IReadOnlyList<StickerDto> Recents,
    IReadOnlyList<StickerDto> Favorites,
    IReadOnlyList<StickerPackDto> SystemPacks,
    IReadOnlyList<StickerPackDto> CustomPacks);

public sealed record StickerMessageDto(
    StickerId Id,
    StickerPackId PackId,
    string PackName,
    string PackAuthor,
    bool IsAnimated,
    bool IsSystem,
    string? StaticUrl,
    int Width,
    int Height,
    string EmojiTags,
    bool IsFavoritedByCurrentUser);

public sealed record CreateStickerPackRequestDto(
    string Name);

public sealed record RenameStickerPackRequestDto(
    string Name);

public sealed record InitiateStickerUploadRequestDto(
    StickerPackId? PackId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string? EmojiTags = null);

public sealed record InitiateStickerUploadResponseDto(
    StickerId StickerId,
    StickerPackId PackId,
    string UploadUrl,
    DateTimeOffset ExpiresAtUtc,
    string RequiredContentType,
    long RequiredContentLength);

public sealed record AddFavoriteStickerRequestDto(
    StickerId StickerId,
    ConversationId? SourceConversationId = null,
    MessageId? SourceMessageId = null);

public sealed record StickerImageUrlDto(
    string Url,
    DateTimeOffset ExpiresAtUtc,
    bool IsStatic,
    bool IsAnimated);
