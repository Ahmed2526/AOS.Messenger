using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Application use-case contract for sticker catalog browsing, custom pack management,
/// R2 custom sticker uploads, favorites, and authorized sticker image URL resolution.
/// </summary>
public interface IStickerService
{
    Task<StickerCatalogDto> GetCatalogAsync(UserId callerId, CancellationToken ct = default);

    Task<StickerPackDto> CreateCustomPackAsync(
        UserId callerId,
        CreateStickerPackRequestDto dto,
        CancellationToken ct = default);

    Task<StickerPackDto> RenameCustomPackAsync(
        UserId callerId,
        StickerPackId packId,
        RenameStickerPackRequestDto dto,
        CancellationToken ct = default);

    Task DeleteCustomPackAsync(
        UserId callerId,
        StickerPackId packId,
        CancellationToken ct = default);

    Task<InitiateStickerUploadResponseDto> InitiateUploadAsync(
        UserId callerId,
        InitiateStickerUploadRequestDto dto,
        CancellationToken ct = default);

    Task<StickerDto> CompleteUploadAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default);

    Task CancelUploadAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default);

    Task ArchiveCustomStickerAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default);

    Task<StickerDto> AddFavoriteAsync(
        UserId callerId,
        AddFavoriteStickerRequestDto dto,
        CancellationToken ct = default);

    Task RemoveFavoriteAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default);

    Task<StickerImageUrlDto> GetStickerImageUrlAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default);
}
