using System.Globalization;
using System.Text;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Application service implementing text messaging, participant authorization,
/// bidirectional block enforcement, idempotency, deterministic keyset pagination,
/// message editing, soft/hard deletion, replies, and reactions.
/// </summary>
public sealed class MessageService : IMessageService
{
    private readonly IMessagePersistence _messagePersistence;
    private readonly IConversationPersistence _conversationPersistence;
    private readonly IBlockPersistence _blockPersistence;
    private readonly IUserReader _userReader;
    private readonly IRealtimeNotifier _realtimeNotifier;
    private readonly IReactionPersistence _reactionPersistence;
    private readonly IMediaPersistence _mediaPersistence;
    private readonly IStickerPersistence? _stickerPersistence;
    private readonly ILogger<MessageService> _logger;
    private readonly TimeProvider _timeProvider;

    [ActivatorUtilitiesConstructor]
    public MessageService(
        IMessagePersistence messagePersistence,
        IConversationPersistence conversationPersistence,
        IBlockPersistence blockPersistence,
        IUserReader userReader,
        IRealtimeNotifier realtimeNotifier,
        IReactionPersistence reactionPersistence,
        ILogger<MessageService> logger,
        TimeProvider? timeProvider = null,
        IMediaPersistence? mediaPersistence = null,
        IStickerPersistence? stickerPersistence = null)
    {
        ArgumentNullException.ThrowIfNull(messagePersistence);
        ArgumentNullException.ThrowIfNull(conversationPersistence);
        ArgumentNullException.ThrowIfNull(blockPersistence);
        ArgumentNullException.ThrowIfNull(userReader);
        ArgumentNullException.ThrowIfNull(realtimeNotifier);
        ArgumentNullException.ThrowIfNull(reactionPersistence);
        ArgumentNullException.ThrowIfNull(logger);

        _messagePersistence = messagePersistence;
        _conversationPersistence = conversationPersistence;
        _blockPersistence = blockPersistence;
        _userReader = userReader;
        _realtimeNotifier = realtimeNotifier;
        _reactionPersistence = reactionPersistence;
        _mediaPersistence = mediaPersistence ?? NullMediaPersistence.Instance;
        _stickerPersistence = stickerPersistence;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MessageService(
        IMessagePersistence messagePersistence,
        IConversationPersistence conversationPersistence,
        IBlockPersistence blockPersistence,
        IUserReader userReader,
        IRealtimeNotifier realtimeNotifier,
        ILogger<MessageService> logger,
        TimeProvider? timeProvider = null)
        : this(
            messagePersistence,
            conversationPersistence,
            blockPersistence,
            userReader,
            realtimeNotifier,
            NullReactionPersistence.Instance,
            logger,
            timeProvider)
    {
    }

    public MessageService(
        IMessagePersistence messagePersistence,
        IConversationPersistence conversationPersistence,
        IBlockPersistence blockPersistence,
        IUserReader userReader)
        : this(
            messagePersistence,
            conversationPersistence,
            blockPersistence,
            userReader,
            NullRealtimeNotifier.Instance,
            NullReactionPersistence.Instance,
            NullLogger<MessageService>.Instance,
            TimeProvider.System)
    {
    }

    public async Task<MessageDto> SendMessageAsync(
        UserId senderId,
        ConversationId conversationId,
        SendMessageRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(senderId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(dto);

        var mediaIds = dto.MediaIds;
        var hasAttachments = mediaIds is { Count: > 0 };
        var stickerId = dto.StickerId;
        var hasSticker = stickerId is not null;

        if (hasSticker && hasAttachments)
        {
            throw new DomainException("Sticker messages cannot include media attachments.");
        }

        if (hasAttachments)
        {
            if (mediaIds!.Count > MediaConstraints.MaxAttachmentsPerMessage)
            {
                throw new DomainException(
                    $"A message cannot have more than {MediaConstraints.MaxAttachmentsPerMessage} attachments.");
            }

            var uniqueIds = new HashSet<MediaId>();
            foreach (var mediaId in mediaIds)
            {
                if (mediaId is null)
                {
                    throw new DomainException("Media attachment identifiers cannot be null.");
                }

                if (!uniqueIds.Add(mediaId))
                {
                    throw new DomainException("Duplicate media attachments in the same message are not allowed.");
                }
            }
        }

        string? normalizedContent = null;
        if (Message.HasVisibleContent(dto.Content))
        {
            if (hasSticker)
            {
                throw new DomainException("Sticker messages cannot include text content.");
            }

            var trimmedContent = dto.Content!.Trim();
            if (trimmedContent.Length > 4000)
            {
                throw new DomainException("Message content cannot exceed 4000 characters.");
            }

            normalizedContent = trimmedContent;
        }
        else if (!hasAttachments && !hasSticker)
        {
            throw new DomainException("Message content cannot be empty or whitespace.");
        }

        if (string.IsNullOrWhiteSpace(dto.IdempotencyKey))
        {
            throw new DomainException("Idempotency key cannot be empty or whitespace.");
        }

        var trimmedIdempotencyKey = dto.IdempotencyKey.Trim();
        if (trimmedIdempotencyKey.Length > 128)
        {
            throw new DomainException("Idempotency key cannot exceed 128 characters.");
        }

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(senderId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var otherParticipantId = conversation.GetOtherParticipant(senderId);
        var isBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(senderId, otherParticipantId, ct);
        if (isBlocked)
        {
            throw new ForbiddenException("Cannot send message in a blocked conversation.");
        }

        Message? parentMessage = null;
        if (dto.ReplyToMessageId is not null)
        {
            parentMessage = await _messagePersistence.GetByIdAsync(conversationId, dto.ReplyToMessageId, ct);
            if (parentMessage is null)
            {
                throw new NotFoundException("Parent message", dto.ReplyToMessageId.Value);
            }

            // Verify parent is not prior to caller's cutoff
            var cutoff = await GetEffectiveCutoffUtcAsync(conversationId, senderId, ct);
            if (cutoff.HasValue && parentMessage.CreatedAtUtc < cutoff.Value)
            {
                throw new NotFoundException("Parent message", dto.ReplyToMessageId.Value);
            }

            // Verify parent is not hidden via DeleteForMe for caller
            var isHiddenForCaller = await _messagePersistence.IsMessageDeletedForUserAsync(conversationId, dto.ReplyToMessageId, senderId, ct);
            if (isHiddenForCaller)
            {
                throw new NotFoundException("Parent message", dto.ReplyToMessageId.Value);
            }
        }

        if (hasSticker)
        {
            if (_stickerPersistence is null
                || !await _stickerPersistence.CanUserAccessStickerAsync(senderId, stickerId!, forSending: true, ct))
            {
                throw new NotFoundException("Sticker", stickerId!.Value);
            }
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var message = Message.Create(
            conversationId,
            senderId,
            trimmedIdempotencyKey,
            normalizedContent,
            nowUtc,
            dto.ReplyToMessageId,
            hasAttachments: hasAttachments,
            stickerId: stickerId);

        var persistResult = hasAttachments
            ? await _messagePersistence.AddWithMediaAsync(message, mediaIds!, nowUtc, ct)
            : await _messagePersistence.AddAsync(message, ct);

        var (persisted, isNewlyCreated, attachedMedia) = (
            persistResult.Message,
            persistResult.IsNewlyCreated,
            persistResult.AttachedMedia);

        if (isNewlyCreated && persisted.StickerId is not null && _stickerPersistence is not null)
        {
            try
            {
                await _stickerPersistence.UpsertRecentStickerAsync(
                    senderId,
                    persisted.StickerId,
                    nowUtc,
                    StickerConstraints.MaxRecentStickersPerUser,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to record recent sticker {StickerId} for user {UserId} after message commit.",
                    persisted.StickerId.Value,
                    senderId.Value);
            }
        }

        MessageReplyDto? replyDto = null;
        if (persisted.ParentMessageId is not null)
        {
            var parent = parentMessage != null && parentMessage.Id == persisted.ParentMessageId
                ? parentMessage
                : await _messagePersistence.GetByIdAsync(conversationId, persisted.ParentMessageId, ct);

            if (parent is not null)
            {
                var parentSenderProfile = await _userReader.GetByIdAsync(parent.SenderId, ct);
                var parentSenderName = FormatDisplayName(parentSenderProfile);
                IReadOnlyList<Media>? parentMedia = null;
                if (!parent.IsDeletedForEveryone && parent.HasAttachments)
                {
                    var parentMediaMap = await _mediaPersistence.GetByMessageIdsAsync(conversationId, [parent.Id], ct);
                    parentMediaMap.TryGetValue(parent.Id, out parentMedia);
                }

                var preview = parent.IsDeletedForEveryone
                    ? "This message was deleted."
                    : parent.StickerId is not null
                        ? MediaPreviewFormatter.FormatStickerPreview()
                        : parentMedia is { Count: > 0 }
                            ? MediaPreviewFormatter.FormatPreview(parent.Content, parentMedia)
                            : TruncatePreview(parent.Content ?? string.Empty);

                replyDto = new MessageReplyDto(
                    parent.Id,
                    parent.SenderId,
                    parentSenderName,
                    preview,
                    parent.IsDeletedForEveryone);
            }
        }

        IReadOnlyList<MediaAttachmentDto> attachmentDtos = [];
        if (!persisted.IsDeletedForEveryone)
        {
            IReadOnlyList<Media>? resolvedMedia = attachedMedia;
            if ((resolvedMedia is null || resolvedMedia.Count == 0) && persisted.HasAttachments)
            {
                var msgMediaMap = await _mediaPersistence.GetByMessageIdsAsync(conversationId, [persisted.Id], ct);
                msgMediaMap.TryGetValue(persisted.Id, out resolvedMedia);
            }

            if (resolvedMedia is { Count: > 0 })
            {
                attachmentDtos = resolvedMedia
                    .OrderBy(m => m.DisplayOrder)
                    .ThenBy(m => m.Id.Value)
                    .Select(ToMediaAttachmentDto)
                    .ToList();
            }
        }

        StickerMessageDto? stickerDto = null;
        StickerMessageDto? broadcastStickerDto = null;
        if (!persisted.IsDeletedForEveryone && persisted.StickerId is not null && _stickerPersistence is not null)
        {
            var stickerRecord = await _stickerPersistence.GetStickerWithPackAsync(persisted.StickerId, ct);
            if (stickerRecord is not null)
            {
                var isFav = await _stickerPersistence.IsStickerFavoritedByUserAsync(senderId, persisted.StickerId, ct);
                stickerDto = StickerService.ToStickerMessageDto(stickerRecord.Sticker, stickerRecord.Pack, senderId, isFav);
                broadcastStickerDto = stickerDto with
                {
                    PackAuthor = stickerRecord.Pack.IsSystemPack
                        ? stickerRecord.Pack.AuthorName
                        : StickerConstraints.NeutralCommunityAuthor,
                    IsFavoritedByCurrentUser = false
                };
            }
        }

        var senderProfile = await _userReader.GetByIdAsync(persisted.SenderId, ct);
        var senderName = FormatDisplayName(senderProfile);
        var senderHandle = FormatUserName(senderProfile);
        var senderAvatarVersion = senderProfile is { IsDeleted: false } ? senderProfile.AvatarVersion : null;

        var messageDto = new MessageDto(
            persisted.Id,
            persisted.ConversationId,
            persisted.SenderId,
            senderName,
            senderHandle,
            persisted.Content ?? string.Empty,
            persisted.CreatedAtUtc,
            persisted.EditedAtUtc,
            persisted.IsDeletedForEveryone,
            replyDto,
            IsOutgoing: true,
            Reactions: [],
            Attachments: attachmentDtos,
            SenderAvatarVersion: senderAvatarVersion,
            Sticker: stickerDto);

        if (isNewlyCreated)
        {
            try
            {
                var broadcastDto = broadcastStickerDto is not null
                    ? messageDto with { Sticker = broadcastStickerDto }
                    : messageDto;
                await _realtimeNotifier.NotifyNewMessageAsync(conversationId, broadcastDto, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast realtime message notification for message {MessageId}", persisted.Id);
            }
        }

        return messageDto;
    }

    public async Task<MessagesCursorPageDto> GetMessagesAsync(
        UserId callerId,
        ConversationId conversationId,
        string? cursor,
        int pageSize = PaginationDefaults.DefaultMessagePageSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);

        if (pageSize <= 0)
        {
            pageSize = PaginationDefaults.DefaultMessagePageSize;
        }
        else if (pageSize > PaginationDefaults.MaxMessagePageSize)
        {
            pageSize = PaginationDefaults.MaxMessagePageSize;
        }

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var effectiveCutoffUtc = await GetEffectiveCutoffUtcAsync(conversationId, callerId, ct);

        DateTimeOffset? cursorCreatedAtUtc = null;
        MessageId? cursorId = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, out var decodedTime, out var decodedId))
            {
                throw new DomainException("Invalid message cursor.");
            }

            cursorCreatedAtUtc = decodedTime;
            cursorId = decodedId;
        }

        var rawMessages = await _messagePersistence.GetKeysetMessagesAsync(
            conversationId,
            callerId,
            cursorCreatedAtUtc,
            cursorId,
            effectiveCutoffUtc,
            pageSize,
            ct);

        var hasMore = rawMessages.Count > pageSize;
        var pageItems = hasMore ? rawMessages.Take(pageSize).ToList() : rawMessages;

        string? nextCursor = null;
        if (hasMore && pageItems.Count > 0)
        {
            var lastItem = pageItems[^1];
            nextCursor = EncodeCursor(lastItem.CreatedAtUtc, lastItem.Id);
        }

        var senderIds = pageItems.Select(m => m.SenderId).Distinct().ToList();
        var parentIds = pageItems.Where(m => m.ParentMessageId != null)
            .Select(m => m.ParentMessageId!)
            .Distinct()
            .ToList();

        var parentMessages = parentIds.Count > 0
            ? await _messagePersistence.GetByIdsAsync(conversationId, parentIds, ct)
            : new Dictionary<MessageId, Message>();

        // Check if any parents are hidden via DeleteForMe for caller
        HashSet<MessageId> hiddenParentIds = parentIds.Count > 0
            ? await _messagePersistence.GetDeletedMessageIdsForUserAsync(conversationId, callerId, parentIds, ct)
            : [];

        var allUserIds = senderIds
            .Union(parentMessages.Values.Select(p => p.SenderId))
            .Distinct()
            .ToList();

        var profiles = (await _userReader.GetByIdsAsync(allUserIds, ct))
            .ToDictionary(p => p.Id);

        // Batch load reactions for the page messages
        var messageIds = pageItems.Select(m => m.Id).ToList();
        var reactionsByMessage = messageIds.Count > 0
            ? await _reactionPersistence.GetReactionsForMessagesAsync(conversationId, messageIds, callerId, ct)
            : new Dictionary<MessageId, IReadOnlyList<ReactionSummaryDto>>();

        // Batch load attachments for non-tombstoned page messages AND visible non-tombstoned reply parents
        var mediaMessageIds = new HashSet<MessageId>();
        foreach (var m in pageItems)
        {
            if (!m.IsDeletedForEveryone && m.HasAttachments)
            {
                mediaMessageIds.Add(m.Id);
            }
        }

        foreach (var parent in parentMessages.Values)
        {
            if (!hiddenParentIds.Contains(parent.Id)
                && (!effectiveCutoffUtc.HasValue || parent.CreatedAtUtc >= effectiveCutoffUtc.Value)
                && !parent.IsDeletedForEveryone
                && parent.HasAttachments)
            {
                mediaMessageIds.Add(parent.Id);
            }
        }

        var isConversationBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(
            callerId,
            conversation.GetOtherParticipant(callerId),
            ct);

        var mediaByMessage = mediaMessageIds.Count > 0
            ? await _mediaPersistence.GetByMessageIdsAsync(conversationId, mediaMessageIds, ct)
            : new Dictionary<MessageId, IReadOnlyList<Media>>();

        var stickerIds = pageItems
            .Where(m => !m.IsDeletedForEveryone && m.StickerId is not null)
            .Select(m => m.StickerId!)
            .Distinct()
            .ToList();

        var stickersById = stickerIds.Count > 0 && _stickerPersistence is not null
            ? await _stickerPersistence.GetStickersWithPacksByIdsAsync(stickerIds, ct)
            : new Dictionary<StickerId, StickerWithPackRecord>();

        var favoriteStickerIds = stickerIds.Count > 0 && _stickerPersistence is not null
            ? await _stickerPersistence.GetUserFavoriteStickerIdsAsync(callerId, stickerIds, ct)
            : [];

        var dtos = new List<MessageDto>(pageItems.Count);
        foreach (var m in pageItems)
        {
            profiles.TryGetValue(m.SenderId, out var senderProfile);
            var senderName = FormatDisplayName(senderProfile);
            var senderHandle = FormatUserName(senderProfile);
            var senderAvatarVersion = (!isConversationBlocked && senderProfile is { IsDeleted: false })
                ? senderProfile.AvatarVersion
                : null;

            MessageReplyDto? replyDto = null;
            if (m.ParentMessageId != null &&
                !hiddenParentIds.Contains(m.ParentMessageId) &&
                parentMessages.TryGetValue(m.ParentMessageId, out var parent) &&
                (!effectiveCutoffUtc.HasValue || parent.CreatedAtUtc >= effectiveCutoffUtc.Value))
            {
                profiles.TryGetValue(parent.SenderId, out var parentProfile);
                var parentName = FormatDisplayName(parentProfile);
                IReadOnlyList<Media>? parentMedia = null;
                if (!parent.IsDeletedForEveryone && parent.HasAttachments)
                {
                    mediaByMessage.TryGetValue(parent.Id, out parentMedia);
                }

                var preview = parent.IsDeletedForEveryone
                    ? "This message was deleted."
                    : parent.StickerId is not null
                        ? MediaPreviewFormatter.FormatStickerPreview()
                        : parentMedia is { Count: > 0 }
                            ? MediaPreviewFormatter.FormatPreview(parent.Content, parentMedia)
                            : TruncatePreview(parent.Content ?? string.Empty);

                replyDto = new MessageReplyDto(
                    parent.Id,
                    parent.SenderId,
                    parentName,
                    preview,
                    parent.IsDeletedForEveryone);
            }

            var isOutgoing = m.SenderId == callerId;
            reactionsByMessage.TryGetValue(m.Id, out var reactions);
            IReadOnlyList<MediaAttachmentDto> attachments = !m.IsDeletedForEveryone
                && mediaByMessage.TryGetValue(m.Id, out var msgMedia)
                ? msgMedia
                    .OrderBy(a => a.DisplayOrder)
                    .ThenBy(a => a.Id.Value)
                    .Select(ToMediaAttachmentDto)
                    .ToList()
                : [];

            StickerMessageDto? stickerDto = null;
            if (!m.IsDeletedForEveryone
                && m.StickerId is not null
                && stickersById.TryGetValue(m.StickerId, out var stickerRecord))
            {
                stickerDto = StickerService.ToStickerMessageDto(
                    stickerRecord.Sticker,
                    stickerRecord.Pack,
                    callerId,
                    favoriteStickerIds.Contains(m.StickerId));
            }

            dtos.Add(new MessageDto(
                m.Id,
                m.ConversationId,
                m.SenderId,
                senderName,
                senderHandle,
                m.Content ?? string.Empty,
                m.CreatedAtUtc,
                m.EditedAtUtc,
                m.IsDeletedForEveryone,
                replyDto,
                isOutgoing,
                reactions ?? [],
                attachments,
                senderAvatarVersion,
                stickerDto));
        }

        return new MessagesCursorPageDto(dtos, nextCursor, hasMore);
    }

    public async Task<MessageEditedDto> EditMessageAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        EditMessageRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(dto);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var otherParticipantId = conversation.GetOtherParticipant(callerId);
        var isBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(callerId, otherParticipantId, ct);
        if (isBlocked)
        {
            throw new ForbiddenException("Cannot edit message in a blocked conversation.");
        }

        var message = await _messagePersistence.GetByIdAsync(conversationId, messageId, ct);
        if (message is null)
        {
            throw new NotFoundException("Message", messageId.Value);
        }

        if (message.SenderId != callerId)
        {
            throw new ForbiddenException("Only the author can edit this message.");
        }

        if (message.IsDeletedForEveryone)
        {
            throw new ForbiddenException("Cannot edit a message that has been deleted for everyone.");
        }

        if (message.StickerId is not null)
        {
            throw new DomainException("Sticker messages cannot be edited.");
        }

        await EnsureMessageVisibleToUserAsync(conversationId, message, callerId, ct);

        string? normalizedNewContent = null;
        if (Message.HasVisibleContent(dto.Content))
        {
            var trimmedContent = dto.Content!.Trim();
            if (trimmedContent.Length > 4000)
            {
                throw new DomainException("Message content cannot exceed 4000 characters.");
            }

            normalizedNewContent = trimmedContent;
        }
        else if (!message.HasAttachments)
        {
            throw new DomainException("Message content cannot be empty.");
        }

        IReadOnlyList<Media>? attachedMedia = null;
        if (message.HasAttachments)
        {
            var mediaMap = await _mediaPersistence.GetByMessageIdsAsync(conversationId, [message.Id], ct);
            mediaMap.TryGetValue(message.Id, out attachedMedia);
        }

        if (string.Equals(normalizedNewContent, message.Content, StringComparison.Ordinal))
        {
            var unchangedPreview = MediaPreviewFormatter.FormatPreview(message.Content, attachedMedia);
            return new MessageEditedDto(
                conversationId.Value,
                message.Id.Value,
                message.Content ?? string.Empty,
                message.EditedAtUtc,
                unchangedPreview);
        }

        var nowUtc = _timeProvider.GetUtcNow();
        message.Edit(normalizedNewContent, nowUtc);
        await _messagePersistence.UpdateAsync(message, ct);

        var previewText = MediaPreviewFormatter.FormatPreview(message.Content, attachedMedia);
        var editedDto = new MessageEditedDto(
            conversationId.Value,
            message.Id.Value,
            message.Content ?? string.Empty,
            message.EditedAtUtc,
            previewText);

        try
        {
            await _realtimeNotifier.NotifyMessageEditedAsync(conversationId, editedDto, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast message edited notification for message {MessageId}", message.Id);
        }

        return editedDto;
    }

    public async Task DeleteForMeAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var message = await _messagePersistence.GetByIdAsync(conversationId, messageId, ct);
        if (message is null)
        {
            throw new NotFoundException("Message", messageId.Value);
        }

        var cutoff = await GetEffectiveCutoffUtcAsync(conversationId, callerId, ct);
        if (cutoff.HasValue && message.CreatedAtUtc < cutoff.Value)
        {
            throw new NotFoundException("Message", messageId.Value);
        }

        var nowUtc = _timeProvider.GetUtcNow();
        await _messagePersistence.DeleteForMeAsync(conversationId, messageId, callerId, nowUtc, ct);

        long? newLastMessageId = null;
        string? newLastMessagePreview = null;

        var lastMessages = await _conversationPersistence.GetLastMessagesForConversationsAsync([conversationId], callerId, ct);
        if (lastMessages.TryGetValue(conversationId, out var lastMessage) &&
            (!cutoff.HasValue || lastMessage.CreatedAtUtc >= cutoff.Value))
        {
            newLastMessageId = lastMessage.Id.Value;
            if (lastMessage.IsDeletedForEveryone)
            {
                newLastMessagePreview = "This message was deleted";
            }
            else if (lastMessage.StickerId is not null)
            {
                newLastMessagePreview = MediaPreviewFormatter.FormatStickerPreview();
            }
            else if (lastMessage.HasAttachments)
            {
                var mediaMap = await _mediaPersistence.GetByMessageIdsAsync(conversationId, [lastMessage.Id], ct);
                mediaMap.TryGetValue(lastMessage.Id, out var lastMsgMedia);
                newLastMessagePreview = MediaPreviewFormatter.FormatPreview(lastMessage.Content, lastMsgMedia);
            }
            else
            {
                newLastMessagePreview = lastMessage.Content;
            }
        }

        var deletedForMeDto = new MessageDeletedForMeDto(
            conversationId.Value,
            messageId.Value,
            newLastMessageId,
            newLastMessagePreview);

        try
        {
            await _realtimeNotifier.NotifyMessageDeletedForMeAsync(callerId, deletedForMeDto, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast delete-for-me notification for message {MessageId}", messageId);
        }
    }

    public async Task<MessageDeletedForEveryoneDto> DeleteForEveryoneAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var otherParticipantId = conversation.GetOtherParticipant(callerId);
        var isBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(callerId, otherParticipantId, ct);
        if (isBlocked)
        {
            throw new ForbiddenException("Cannot delete message in a blocked conversation.");
        }

        var message = await _messagePersistence.GetByIdAsync(conversationId, messageId, ct);
        if (message is null)
        {
            throw new NotFoundException("Message", messageId.Value);
        }

        if (message.SenderId != callerId)
        {
            throw new ForbiddenException("Only the author can delete this message for everyone.");
        }

        await EnsureMessageVisibleToUserAsync(conversationId, message, callerId, ct);

        var nowUtc = _timeProvider.GetUtcNow();
        if (nowUtc - message.CreatedAtUtc > TimeSpan.FromHours(3))
        {
            throw new ForbiddenException("A message can only be deleted for everyone within 3 hours of creation.");
        }

        await _messagePersistence.DeleteForEveryoneTransactionalAsync(conversationId, messageId, nowUtc, ct);

        var deletedDto = new MessageDeletedForEveryoneDto(
            conversationId.Value,
            messageId.Value,
            nowUtc);

        try
        {
            await _realtimeNotifier.NotifyMessageDeletedForEveryoneAsync(conversationId, deletedDto, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast delete-for-everyone notification for message {MessageId}", messageId);
        }

        return deletedDto;
    }

    public async Task<ReactionUpdatedDto> ToggleReactionAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        ToggleReactionRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(dto);

        if (!ReactionAllowlist.IsAllowed(dto.Emoji))
        {
            throw new DomainException($"Emoji '{dto.Emoji}' is not an allowed reaction.");
        }

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var otherParticipantId = conversation.GetOtherParticipant(callerId);
        var isBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(callerId, otherParticipantId, ct);
        if (isBlocked)
        {
            throw new ForbiddenException("Cannot react to a message in a blocked conversation.");
        }

        var message = await _messagePersistence.GetByIdAsync(conversationId, messageId, ct);
        if (message is null)
        {
            throw new NotFoundException("Message", messageId.Value);
        }

        if (message.IsDeletedForEveryone)
        {
            throw new ForbiddenException("Cannot react to a deleted message.");
        }

        await EnsureMessageVisibleToUserAsync(conversationId, message, callerId, ct);

        var nowUtc = _timeProvider.GetUtcNow();
        var resultingEmoji = await _reactionPersistence.ToggleReactionAsync(
            conversationId,
            messageId,
            callerId,
            dto.Emoji,
            nowUtc,
            ct);

        var reactionGroups = await _reactionPersistence.GetReactionGroupsForMessageAsync(conversationId, messageId, ct);
        var updatedDto = new ReactionUpdatedDto(
            conversationId.Value,
            messageId.Value,
            callerId.Value,
            resultingEmoji,
            reactionGroups);

        try
        {
            await _realtimeNotifier.NotifyReactionUpdatedAsync(conversationId, updatedDto, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast reaction updated notification for message {MessageId}", messageId);
        }

        return updatedDto;
    }

    public async Task<ReactionUpdatedDto> RemoveReactionAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var otherParticipantId = conversation.GetOtherParticipant(callerId);
        var isBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(callerId, otherParticipantId, ct);
        if (isBlocked)
        {
            throw new ForbiddenException("Cannot modify reaction in a blocked conversation.");
        }

        var message = await _messagePersistence.GetByIdAsync(conversationId, messageId, ct);
        if (message is null)
        {
            throw new NotFoundException("Message", messageId.Value);
        }

        if (message.IsDeletedForEveryone)
        {
            throw new ForbiddenException("Cannot remove reaction from a deleted message.");
        }

        await EnsureMessageVisibleToUserAsync(conversationId, message, callerId, ct);

        await _reactionPersistence.RemoveReactionAsync(conversationId, messageId, callerId, ct);

        var reactionGroups = await _reactionPersistence.GetReactionGroupsForMessageAsync(conversationId, messageId, ct);
        var updatedDto = new ReactionUpdatedDto(
            conversationId.Value,
            messageId.Value,
            callerId.Value,
            null,
            reactionGroups);

        try
        {
            await _realtimeNotifier.NotifyReactionUpdatedAsync(conversationId, updatedDto, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast reaction removed notification for message {MessageId}", messageId);
        }

        return updatedDto;
    }

    private async Task<DateTimeOffset?> GetEffectiveCutoffUtcAsync(
        ConversationId conversationId,
        UserId userId,
        CancellationToken ct)
    {
        var userState = await _conversationPersistence.GetUserStateAsync(conversationId, userId, ct);
        var cutoff = userState?.ClearedAtUtc;
        if (userState?.DeletedAtUtc is not null &&
            (!cutoff.HasValue || userState.DeletedAtUtc.Value > cutoff.Value))
        {
            cutoff = userState.DeletedAtUtc.Value;
        }

        return cutoff;
    }

    private async Task EnsureMessageVisibleToUserAsync(
        ConversationId conversationId,
        Message message,
        UserId callerId,
        CancellationToken ct)
    {
        var cutoff = await GetEffectiveCutoffUtcAsync(conversationId, callerId, ct);
        if (cutoff.HasValue && message.CreatedAtUtc < cutoff.Value)
        {
            throw new NotFoundException("Message", message.Id.Value);
        }

        var isHidden = await _messagePersistence.IsMessageDeletedForUserAsync(conversationId, message.Id, callerId, ct);
        if (isHidden)
        {
            throw new NotFoundException("Message", message.Id.Value);
        }
    }

    private static string FormatDisplayName(UserProfileDto? profile)
    {
        if (profile is null || profile.IsDeleted)
        {
            return "Deleted Account";
        }

        return string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.UserName
            : profile.DisplayName;
    }

    private static string FormatUserName(UserProfileDto? profile)
    {
        if (profile is null || profile.IsDeleted)
        {
            return "deleted_user";
        }

        return profile.UserName;
    }

    private static string TruncatePreview(string content, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLength)
        {
            return content;
        }

        var endIndex = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(content);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (enumerator.ElementIndex + element.Length > maxLength)
            {
                break;
            }

            endIndex = enumerator.ElementIndex + element.Length;
        }

        if (endIndex <= 0)
        {
            endIndex = Math.Min(maxLength, content.Length);
            if (endIndex > 0 && char.IsHighSurrogate(content[endIndex - 1]))
            {
                endIndex--;
            }
        }

        return endIndex < content.Length
            ? string.Concat(content.AsSpan(0, endIndex), "...")
            : content;
    }

    private static string EncodeCursor(DateTimeOffset createdAtUtc, MessageId id)
    {
        var raw = $"{createdAtUtc.UtcTicks}:{id.Value}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset createdAtUtc, out MessageId id)
    {
        createdAtUtc = default;
        id = null!;

        try
        {
            var bytes = Convert.FromBase64String(cursor);
            var decoded = Encoding.UTF8.GetString(bytes);
            var colonIndex = decoded.IndexOf(':');
            if (colonIndex <= 0)
            {
                return false;
            }

            var ticksStr = decoded[..colonIndex];
            var idStr = decoded[(colonIndex + 1)..];

            if (!long.TryParse(ticksStr, out var ticks))
            {
                return false;
            }

            if (!long.TryParse(idStr, out var messageIdVal) || messageIdVal < 0)
            {
                return false;
            }

            createdAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
            id = new MessageId(messageIdVal);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MediaAttachmentDto ToMediaAttachmentDto(Media media)
    {
        return new MediaAttachmentDto(
            media.Id,
            media.Kind.ToApiString(),
            media.SanitizedFileName,
            media.VerifiedContentType ?? media.DeclaredContentType,
            media.VerifiedSizeBytes ?? media.DeclaredSizeBytes,
            media.Width,
            media.Height,
            media.DurationMs,
            media.DisplayOrder,
            HasThumbnail: !string.IsNullOrWhiteSpace(media.ThumbnailObjectKey));
    }
}

internal sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public static readonly NullRealtimeNotifier Instance = new();

    private NullRealtimeNotifier()
    {
    }

    public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NullReactionPersistence : IReactionPersistence
{
    public static readonly NullReactionPersistence Instance = new();

    private NullReactionPersistence()
    {
    }

    public Task<MessageReaction?> GetUserReactionAsync(ConversationId conversationId, MessageId messageId, UserId userId, CancellationToken ct = default)
    {
        return Task.FromResult<MessageReaction?>(null);
    }

    public Task<string?> ToggleReactionAsync(ConversationId conversationId, MessageId messageId, UserId userId, string emoji, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(emoji);
    }

    public Task<bool> RemoveReactionAsync(ConversationId conversationId, MessageId messageId, UserId userId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public Task<IReadOnlyDictionary<MessageId, IReadOnlyList<ReactionSummaryDto>>> GetReactionsForMessagesAsync(ConversationId conversationId, IEnumerable<MessageId> messageIds, UserId currentUserId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<MessageId, IReadOnlyList<ReactionSummaryDto>> empty = new Dictionary<MessageId, IReadOnlyList<ReactionSummaryDto>>();
        return Task.FromResult(empty);
    }

    public Task<IReadOnlyList<ReactionGroupDto>> GetReactionGroupsForMessageAsync(ConversationId conversationId, MessageId messageId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ReactionGroupDto>>([]);
    }

    public Task DeleteReactionsForMessageAsync(ConversationId conversationId, MessageId messageId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
