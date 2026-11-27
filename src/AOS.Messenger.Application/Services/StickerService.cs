using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Application service orchestrating built-in and custom sticker catalogs, custom pack CRUD,
/// presigned R2 sticker uploads with Magick.NET WebP normalization, favorites, and strict IDOR checks.
/// </summary>
public sealed class StickerService : IStickerService
{
    private readonly IStickerPersistence _stickerPersistence;
    private readonly IMediaStorage _mediaStorage;
    private readonly IStickerProcessor _stickerProcessor;
    private readonly IUserReader _userReader;
    private readonly ILogger<StickerService> _logger;
    private readonly MediaOptions _options;
    private readonly TimeProvider _timeProvider;

    public StickerService(
        IStickerPersistence stickerPersistence,
        IMediaStorage mediaStorage,
        IStickerProcessor stickerProcessor,
        IUserReader userReader,
        ILogger<StickerService> logger,
        IOptions<MediaOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stickerPersistence);
        ArgumentNullException.ThrowIfNull(mediaStorage);
        ArgumentNullException.ThrowIfNull(stickerProcessor);
        ArgumentNullException.ThrowIfNull(userReader);
        ArgumentNullException.ThrowIfNull(logger);

        _stickerPersistence = stickerPersistence;
        _mediaStorage = mediaStorage;
        _stickerProcessor = stickerProcessor;
        _userReader = userReader;
        _logger = logger;
        _options = options?.Value ?? new MediaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StickerCatalogDto> GetCatalogAsync(UserId callerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);

        var systemPacks = await _stickerPersistence.GetSystemPacksAsync(ct);
        var customPacks = await _stickerPersistence.GetUserCustomPacksAsync(callerId, ct);

        var allPackIds = systemPacks.Select(p => p.Id)
            .Concat(customPacks.Select(p => p.Id))
            .ToList();

        var packStickers = await _stickerPersistence.GetReadyStickersByPackIdsAsync(allPackIds, ct);
        var favorites = await _stickerPersistence.GetUserFavoriteStickersAsync(callerId, ct);
        var recents = await _stickerPersistence.GetUserRecentStickersAsync(callerId, StickerConstraints.MaxRecentStickersPerUser, ct);

        var activeFavorites = favorites
            .Where(record => record.Sticker.State == StickerState.Ready
                             && record.Sticker.PurgedAtUtc is null
                             && (!record.Pack.IsSystemPack || !record.Pack.IsDeleted))
            .ToList();

        var favoriteIds = activeFavorites.Select(f => f.Sticker.Id).ToHashSet();

        var stickersByPack = packStickers
            .GroupBy(s => s.PackId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var systemPackDtos = systemPacks
            .Select(pack => BuildPackDto(
                pack,
                stickersByPack.TryGetValue(pack.Id, out var list) ? list : [],
                callerId,
                favoriteIds))
            .ToList();

        var customPackDtos = customPacks
            .Select(pack => BuildPackDto(
                pack,
                stickersByPack.TryGetValue(pack.Id, out var list) ? list : [],
                callerId,
                favoriteIds))
            .ToList();

        var favoriteDtos = activeFavorites
            .Select(record => ToStickerDto(record.Sticker, record.Pack, callerId, isFavorited: true))
            .ToList();

        var recentDtos = recents
            .Where(record => record.Sticker.State == StickerState.Ready
                             && record.Sticker.PurgedAtUtc is null
                             && (!record.Pack.IsSystemPack || !record.Pack.IsDeleted))
            .Select(record => ToStickerDto(record.Sticker, record.Pack, callerId, favoriteIds.Contains(record.Sticker.Id)))
            .ToList();

        return new StickerCatalogDto(recentDtos, favoriteDtos, systemPackDtos, customPackDtos);
    }

    public async Task<StickerPackDto> CreateCustomPackAsync(
        UserId callerId,
        CreateStickerPackRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(dto);

        var profile = await _userReader.GetByIdAsync(callerId, ct);
        if (profile is null || profile.IsDeleted)
        {
            throw new NotFoundException("User", callerId.Value);
        }

        var authorName = ResolveCustomPackAuthorName(profile);
        var pack = StickerPack.CreateCustom(callerId, dto.Name, authorName, sortOrder: 0, _timeProvider.GetUtcNow());

        var created = await _stickerPersistence.CreateCustomPackWithLimitCheckAsync(
            pack,
            StickerConstraints.MaxCustomPacksPerUser,
            ct);

        _logger.LogInformation("Created custom sticker pack {PackId} for user {UserId}.", created.Id.Value, callerId.Value);

        return BuildPackDto(created, [], callerId, []);
    }

    public async Task<StickerPackDto> RenameCustomPackAsync(
        UserId callerId,
        StickerPackId packId,
        RenameStickerPackRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(dto);

        var pack = await _stickerPersistence.GetPackByIdAsync(packId, ct);
        if (pack is null || pack.IsDeleted)
        {
            throw new NotFoundException("StickerPack", packId.Value);
        }

        if (pack.IsSystemPack || pack.OwnerUserId != callerId)
        {
            throw new ForbiddenException("Only the owner can rename this custom sticker pack.");
        }

        pack.Rename(dto.Name, _timeProvider.GetUtcNow());
        await _stickerPersistence.UpdatePackAsync(pack, ct);

        var stickers = await _stickerPersistence.GetReadyStickersByPackIdsAsync([pack.Id], ct);
        var favIds = await _stickerPersistence.GetUserFavoriteStickerIdsAsync(
            callerId,
            stickers.Select(s => s.Id),
            ct);

        return BuildPackDto(pack, stickers, callerId, favIds);
    }

    public async Task DeleteCustomPackAsync(
        UserId callerId,
        StickerPackId packId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(packId);

        await _stickerPersistence.SoftDeletePackAndArchiveStickersAsync(
            packId,
            callerId,
            _timeProvider.GetUtcNow(),
            ct);

        _logger.LogInformation("Soft-deleted custom sticker pack {PackId} for user {UserId}.", packId.Value, callerId.Value);
    }

    public async Task<InitiateStickerUploadResponseDto> InitiateUploadAsync(
        UserId callerId,
        InitiateStickerUploadRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(dto);

        var profile = await _userReader.GetByIdAsync(callerId, ct);
        if (profile is null || profile.IsDeleted)
        {
            throw new NotFoundException("User", callerId.Value);
        }

        var canonicalMime = StickerConstraints.ResolveAllowedStickerUpload(dto.FileName, dto.ContentType);
        if (dto.SizeBytes <= 0 || dto.SizeBytes > StickerConstraints.MaxRawStickerUploadBytes)
        {
            throw new DomainException(
                $"Sticker upload size must be between 1 byte and {StickerConstraints.MaxRawStickerUploadBytes} bytes.");
        }

        var normalizedTags = StickerConstraints.NormalizeEmojiTags(dto.EmojiTags);
        var nowUtc = _timeProvider.GetUtcNow();

        StickerPack targetPack;
        if (dto.PackId is not null)
        {
            var pack = await _stickerPersistence.GetPackByIdAsync(dto.PackId, ct);
            if (pack is null || pack.IsDeleted)
            {
                throw new NotFoundException("StickerPack", dto.PackId.Value);
            }

            if (pack.IsSystemPack || pack.OwnerUserId != callerId)
            {
                throw new ForbiddenException("You can only upload stickers to your own custom packs.");
            }

            targetPack = pack;
        }
        else
        {
            var authorName = ResolveCustomPackAuthorName(profile);
            targetPack = await _stickerPersistence.GetOrCreateDefaultCustomPackAsync(callerId, authorName, nowUtc, ct);
        }

        var pending = Sticker.CreatePendingCustom(
            targetPack.Id,
            callerId,
            canonicalMime,
            dto.SizeBytes,
            normalizedTags,
            displayOrder: 0,
            nowUtc);

        var created = await _stickerPersistence.TryCreatePendingStickerWithQuotaCheckAsync(
            pending,
            StickerConstraints.MaxStickersPerCustomPack,
            StickerConstraints.MaxTotalCustomStickersPerUser,
            _options.MaxConcurrentPendingPerUser,
            _options.DailyUploadQuotaBytes,
            nowUtc.AddHours(-24),
            ct);

        var expiresIn = TimeSpan.FromMinutes(Math.Max(1, _options.PresignedPutTtlMinutes));
        var uploadUrl = _mediaStorage.GeneratePresignedPutUrl(
            created.ObjectKey!,
            canonicalMime,
            created.DeclaredSizeBytes,
            expiresIn,
            nowUtc);

        _logger.LogInformation(
            "Initiated custom sticker upload {StickerId} in pack {PackId} for user {UserId} ({SizeBytes} bytes).",
            created.Id.Value,
            targetPack.Id.Value,
            callerId.Value,
            created.DeclaredSizeBytes);

        return new InitiateStickerUploadResponseDto(
            created.Id,
            targetPack.Id,
            uploadUrl,
            nowUtc + expiresIn,
            canonicalMime,
            created.DeclaredSizeBytes);
    }

    public async Task<StickerDto> CompleteUploadAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(stickerId);

        var sticker = await _stickerPersistence.GetStickerByIdAsync(stickerId, ct);
        if (sticker is null)
        {
            throw new NotFoundException("Sticker", stickerId.Value);
        }

        if (sticker.UploaderId != callerId)
        {
            throw new ForbiddenException("Only the uploader can complete this sticker upload.");
        }

        if (sticker.State == StickerState.Ready)
        {
            var existingRecord = await _stickerPersistence.GetStickerWithPackAsync(stickerId, ct)
                ?? throw new NotFoundException("Sticker", stickerId.Value);
            var isFav = await _stickerPersistence.IsStickerFavoritedByUserAsync(callerId, stickerId, ct);
            return ToStickerDto(existingRecord.Sticker, existingRecord.Pack, callerId, isFav);
        }

        if (sticker.State == StickerState.Failed)
        {
            throw new DomainException(sticker.FailureReason ?? "Sticker verification failed.");
        }

        if (sticker.State == StickerState.Archived)
        {
            throw new ConflictException("Sticker was already deleted.");
        }

        var stagingKey = sticker.ObjectKey!;
        var stagingMeta = await _mediaStorage.GetObjectMetadataAsync(stagingKey, ct);
        if (stagingMeta is null)
        {
            var nowUtc = _timeProvider.GetUtcNow();
            sticker.MarkFailed("Uploaded sticker object was not found in staging storage.", nowUtc);
            sticker.MarkPurged(nowUtc);
            try
            {
                await _stickerPersistence.UpdateStickerAsync(sticker, ct);
            }
            catch (ConflictException ex)
            {
                return await HandleConcurrentConflictAsync(stickerId, callerId, ex.Message, ct);
            }

            throw new DomainException("Uploaded sticker file was not found in staging storage. Please upload again.");
        }

        if (string.IsNullOrWhiteSpace(stagingMeta.ETag))
        {
            throw new MediaStorageException("Uploaded sticker metadata is missing a valid ETag.");
        }

        if (stagingMeta.ContentLength <= 0
            || stagingMeta.ContentLength > StickerConstraints.MaxRawStickerUploadBytes
            || stagingMeta.ContentLength != sticker.DeclaredSizeBytes)
        {
            var failureMessage = stagingMeta.ContentLength <= 0
                ? "Uploaded sticker file cannot be empty."
                : stagingMeta.ContentLength != sticker.DeclaredSizeBytes
                    ? "Uploaded sticker size does not match the initiated upload size."
                    : $"Sticker upload size exceeds maximum allowed {StickerConstraints.MaxRawStickerUploadBytes} bytes.";

            var (recovered, _) = await FailAndCleanStagingAsync(
                sticker,
                callerId,
                stagingKey,
                failureMessage,
                stagingMeta.ContentLength > 0 ? stagingMeta.ContentLength : null,
                ct);
            if (recovered is not null)
            {
                return recovered;
            }

            throw new DomainException(failureMessage);
        }

        ProcessedStickerResult processed;
        try
        {
            processed = await _stickerProcessor.ProcessStickerAsync(sticker, stagingMeta, ct);
        }
        catch (DomainException ex)
        {
            var (recovered, _) = await FailAndCleanStagingAsync(
                sticker,
                callerId,
                stagingKey,
                ex.Message,
                stagingMeta.ContentLength,
                ct);
            if (recovered is not null)
            {
                return recovered;
            }

            throw;
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException)
        {
            return await HandleConcurrentConflictAsync(stickerId, callerId, ex.Message, ct);
        }

        var permanentObjectId = MediaConstraints.GenerateStorageObjectId();
        var permanentObjectKey = $"{MediaConstraints.StickerKeyPrefix}{permanentObjectId}";
        const string disposition = "inline; filename=\"sticker.webp\"";

        long processedSizeBytes;
        int width;
        int height;
        bool isAnimated;
        try
        {
            await using (processed)
            {
                processedSizeBytes = processed.ProcessedSizeBytes;
                width = processed.Width;
                height = processed.Height;
                isAnimated = processed.IsAnimated;

                await _mediaStorage.PromoteFromStreamAsync(
                    stagingKey,
                    permanentObjectKey,
                    processed.ProcessedStream,
                    processedSizeBytes,
                    processed.ContentType,
                    disposition,
                    ct);
            }
        }
        catch (Exception ex)
        {
            await BestEffortDeleteStorageAsync(permanentObjectKey, CancellationToken.None);
            if (ex is NotFoundException or ConflictException)
            {
                return await HandleConcurrentConflictAsync(stickerId, callerId, ex.Message, ct);
            }

            throw;
        }

        var committedAtUtc = _timeProvider.GetUtcNow();
        sticker.MarkReady(
            permanentObjectId,
            permanentObjectKey,
            processedSizeBytes,
            width,
            height,
            isAnimated,
            committedAtUtc);

        try
        {
            await _stickerPersistence.CommitReadyStickerIfPackActiveAsync(sticker, committedAtUtc, ct);
        }
        catch (Exception ex)
        {
            // Read persisted state rather than the locally modified Ready entity.
            var persisted = await _stickerPersistence.GetStickerWithPackAsync(stickerId, CancellationToken.None);
            var current = persisted?.Sticker;
            if (current?.ObjectKey != permanentObjectKey)
            {
                await BestEffortDeleteStorageAsync(permanentObjectKey, CancellationToken.None);
            }

            if (current?.State != StickerState.Pending)
            {
                await BestEffortDeleteStorageAsync(stagingKey, CancellationToken.None);
            }

            if (persisted is not null && current?.State == StickerState.Ready)
            {
                var isFav = await _stickerPersistence.IsStickerFavoritedByUserAsync(callerId, stickerId, CancellationToken.None);
                return ToStickerDto(current, persisted.Pack, callerId, isFav);
            }

            if (current?.State == StickerState.Failed)
            {
                throw new DomainException(current.FailureReason ?? "Sticker verification failed.");
            }

            if (ex is NotFoundException)
            {
                throw new ConflictException(ex.Message, ex);
            }

            throw;
        }

        await BestEffortDeleteStorageAsync(stagingKey, CancellationToken.None);

        var completedRecord = await _stickerPersistence.GetStickerWithPackAsync(sticker.Id, ct)
            ?? throw new NotFoundException("Sticker", sticker.Id.Value);

        _logger.LogInformation(
            "Completed custom sticker upload {StickerId} for user {UserId} ({ProcessedBytes} bytes, animated={IsAnimated}).",
            sticker.Id.Value,
            callerId.Value,
            processedSizeBytes,
            isAnimated);

        return ToStickerDto(completedRecord.Sticker, completedRecord.Pack, callerId, isFavorited: false);
    }

    public async Task CancelUploadAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(stickerId);

        var sticker = await _stickerPersistence.GetStickerByIdAsync(stickerId, ct);
        if (sticker is null)
        {
            throw new NotFoundException("Sticker", stickerId.Value);
        }

        if (sticker.UploaderId != callerId)
        {
            throw new ForbiddenException("Only the uploader can cancel this sticker upload.");
        }

        if (sticker.State == StickerState.Failed)
        {
            return;
        }

        if (sticker.State != StickerState.Pending)
        {
            throw new ConflictException($"Sticker upload cannot be cancelled from state '{sticker.State}'.");
        }

        var stagingKey = sticker.ObjectKey!;
        long? measuredBytes = null;
        try
        {
            var meta = await _mediaStorage.GetObjectMetadataAsync(stagingKey, ct);
            if (meta is not null && meta.ContentLength > 0)
            {
                measuredBytes = meta.ContentLength;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect staging object metadata while cancelling sticker {StickerId}; falling back to declared bytes.", stickerId.Value);
            measuredBytes = sticker.DeclaredSizeBytes;
        }

        var (readyConflictDto, unresolvedConflict) = await FailAndCleanStagingAsync(
            sticker,
            callerId,
            stagingKey,
            "Upload cancelled by user.",
            verifiedSizeBytes: measuredBytes,
            ct);
        if (readyConflictDto is not null)
        {
            throw new ConflictException($"Sticker upload cannot be cancelled from state '{StickerState.Ready}'.");
        }

        if (unresolvedConflict is not null)
        {
            throw unresolvedConflict;
        }
    }

    public async Task ArchiveCustomStickerAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(stickerId);

        await _stickerPersistence.ArchiveStickerAndPruneReferencesAsync(
            stickerId,
            callerId,
            _timeProvider.GetUtcNow(),
            ct);

        _logger.LogInformation("Archived custom sticker {StickerId} for user {UserId}.", stickerId.Value, callerId.Value);
    }

    public async Task<StickerDto> AddFavoriteAsync(
        UserId callerId,
        AddFavoriteStickerRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(dto.StickerId);

        var record = await _stickerPersistence.GetStickerWithPackAsync(dto.StickerId, ct);
        if (record is null
            || record.Sticker.PurgedAtUtc is not null
            || record.Sticker.State != StickerState.Ready
            || (record.Pack.IsSystemPack && record.Pack.IsDeleted))
        {
            throw new NotFoundException("Sticker", dto.StickerId.Value);
        }

        var allowed = await _stickerPersistence.CanUserAccessStickerAsync(
            callerId,
            record,
            forSending: false,
            ct);

        if (!allowed && dto.SourceConversationId is not null && dto.SourceMessageId is not null)
        {
            allowed = await _stickerPersistence.IsStickerVisibleInMessageAsync(
                callerId,
                dto.SourceConversationId,
                dto.SourceMessageId,
                dto.StickerId,
                ct);
        }

        if (!allowed)
        {
            throw new NotFoundException("Sticker", dto.StickerId.Value);
        }

        await _stickerPersistence.AddFavoriteWithLimitCheckAsync(
            callerId,
            dto.StickerId,
            StickerConstraints.MaxFavoriteStickersPerUser,
            _timeProvider.GetUtcNow(),
            ct);

        return ToStickerDto(record.Sticker, record.Pack, callerId, isFavorited: true);
    }

    public async Task RemoveFavoriteAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(stickerId);

        await _stickerPersistence.RemoveFavoriteAsync(callerId, stickerId, ct);
    }

    public async Task<StickerImageUrlDto> GetStickerImageUrlAsync(
        UserId callerId,
        StickerId stickerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(stickerId);

        var record = await _stickerPersistence.GetStickerWithPackAsync(stickerId, ct);
        if (record is null || record.Sticker.PurgedAtUtc is not null)
        {
            throw new NotFoundException("Sticker", stickerId.Value);
        }

        var canAccess = await _stickerPersistence.CanUserAccessStickerAsync(
            callerId,
            record,
            forSending: false,
            ct);

        if (!canAccess)
        {
            throw new NotFoundException("Sticker", stickerId.Value);
        }

        var sticker = record.Sticker;
        var nowUtc = _timeProvider.GetUtcNow();

        if (!string.IsNullOrWhiteSpace(sticker.StaticAssetPath))
        {
            return new StickerImageUrlDto(
                sticker.StaticAssetPath,
                nowUtc.AddHours(24),
                IsStatic: true,
                IsAnimated: sticker.IsAnimated);
        }

        if (string.IsNullOrWhiteSpace(sticker.ObjectKey))
        {
            throw new NotFoundException("Sticker", stickerId.Value);
        }

        var expiresIn = TimeSpan.FromMinutes(StickerConstraints.StickerPresignedGetTtlMinutes);
        var url = _mediaStorage.GeneratePresignedGetUrl(
            sticker.ObjectKey,
            StickerConstraints.CanonicalStickerContentType,
            "inline; filename=\"sticker.webp\"",
            expiresIn,
            nowUtc);

        return new StickerImageUrlDto(
            url,
            nowUtc + expiresIn,
            IsStatic: false,
            IsAnimated: sticker.IsAnimated);
    }

    internal static StickerDto ToStickerDto(
        Sticker sticker,
        StickerPack pack,
        UserId viewerId,
        bool isFavorited)
    {
        var isOwned = sticker.UploaderId == viewerId;
        var packAuthor = pack.IsSystemPack
            ? pack.AuthorName
            : (isOwned ? pack.AuthorName : StickerConstraints.NeutralCommunityAuthor);

        return new StickerDto(
            sticker.Id,
            pack.Id,
            pack.Name,
            packAuthor,
            sticker.IsAnimated,
            pack.IsSystemPack,
            isOwned,
            sticker.StaticAssetPath,
            sticker.Width,
            sticker.Height,
            sticker.EmojiTags,
            isFavorited);
    }

    internal static StickerMessageDto ToStickerMessageDto(
        Sticker sticker,
        StickerPack pack,
        UserId viewerId,
        bool isFavoritedByViewer)
    {
        var isOwned = sticker.UploaderId == viewerId;
        var packAuthor = pack.IsSystemPack
            ? pack.AuthorName
            : (isOwned ? pack.AuthorName : StickerConstraints.NeutralCommunityAuthor);

        return new StickerMessageDto(
            sticker.Id,
            pack.Id,
            pack.Name,
            packAuthor,
            sticker.IsAnimated,
            pack.IsSystemPack,
            sticker.StaticAssetPath,
            sticker.Width,
            sticker.Height,
            sticker.EmojiTags,
            isFavoritedByViewer);
    }

    private static StickerPackDto BuildPackDto(
        StickerPack pack,
        IReadOnlyList<Sticker> stickers,
        UserId viewerId,
        HashSet<StickerId> favoriteIds)
    {
        var isOwner = !pack.IsSystemPack && pack.OwnerUserId == viewerId;
        var packAuthor = pack.IsSystemPack || isOwner
            ? pack.AuthorName
            : StickerConstraints.NeutralCommunityAuthor;

        var stickerDtos = stickers
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.CreatedAtUtc)
            .Select(s => ToStickerDto(s, pack, viewerId, favoriteIds.Contains(s.Id)))
            .ToList();

        return new StickerPackDto(
            pack.Id,
            pack.Slug,
            pack.Name,
            packAuthor,
            pack.IsSystemPack,
            pack.SortOrder,
            stickerDtos);
    }

    private static string ResolveCustomPackAuthorName(UserProfileDto profile)
    {
        var name = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.UserName : profile.DisplayName;
        var trimmed = string.IsNullOrWhiteSpace(name) ? StickerConstraints.NeutralCommunityAuthor : name.Trim();
        return trimmed.Length > StickerConstraints.MaxAuthorNameLength
            ? trimmed[..StickerConstraints.MaxAuthorNameLength]
            : trimmed;
    }

    private async Task<(StickerDto? ReadyDto, ConflictException? UnresolvedConflict)> FailAndCleanStagingAsync(
        Sticker sticker,
        UserId callerId,
        string stagingKey,
        string failureReason,
        long? verifiedSizeBytes,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        sticker.MarkFailed(failureReason, nowUtc, verifiedSizeBytes);

        try
        {
            await _stickerPersistence.UpdateStickerAsync(sticker, ct);
        }
        catch (ConflictException ex)
        {
            var reloaded = await _stickerPersistence.GetStickerWithPackAsync(sticker.Id, CancellationToken.None);
            if (reloaded is not null && reloaded.Sticker.State == StickerState.Ready)
            {
                var isFav = await _stickerPersistence.IsStickerFavoritedByUserAsync(callerId, sticker.Id, CancellationToken.None);
                return (ToStickerDto(reloaded.Sticker, reloaded.Pack, callerId, isFav), ex);
            }

            if (reloaded?.Sticker.State == StickerState.Failed)
            {
                return (null, null);
            }

            return (null, ex);
        }

        if (await BestEffortDeleteStorageAsync(stagingKey, CancellationToken.None))
        {
            sticker.MarkPurged(_timeProvider.GetUtcNow());
            try
            {
                await _stickerPersistence.UpdateStickerAsync(sticker, CancellationToken.None);
            }
            catch (ConflictException)
            {
            }
        }

        return (null, null);
    }

    private async Task<StickerDto> HandleConcurrentConflictAsync(
        StickerId stickerId,
        UserId callerId,
        string conflictMessage,
        CancellationToken ct)
    {
        var reloaded = await _stickerPersistence.GetStickerWithPackAsync(stickerId, ct);
        if (reloaded is not null && reloaded.Sticker.State == StickerState.Ready)
        {
            var isFav = await _stickerPersistence.IsStickerFavoritedByUserAsync(callerId, stickerId, ct);
            return ToStickerDto(reloaded.Sticker, reloaded.Pack, callerId, isFav);
        }

        if (reloaded is not null && reloaded.Sticker.State == StickerState.Failed)
        {
            throw new DomainException(reloaded.Sticker.FailureReason ?? "Sticker verification failed.");
        }

        throw new ConflictException(
            string.IsNullOrWhiteSpace(conflictMessage)
                ? "The staged sticker upload was modified concurrently. Please try again."
                : conflictMessage);
    }

    private async Task<bool> BestEffortDeleteStorageAsync(string objectKey, CancellationToken ct)
    {
        try
        {
            await _mediaStorage.DeleteObjectAsync(objectKey, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Best-effort sticker storage cleanup failed for key {ObjectKey}.", objectKey);
            return false;
        }
    }
}
