using System.Globalization;
using System.Text;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
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
        TimeProvider? timeProvider = null)
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

        if (!Message.HasVisibleContent(dto.Content))
        {
            throw new DomainException("Message content cannot be empty or whitespace.");
        }

        var trimmedContent = dto.Content.Trim();
        if (trimmedContent.Length > 4000)
        {
            throw new DomainException("Message content cannot exceed 4000 characters.");
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

        var message = Message.Create(
            conversationId,
            senderId,
            trimmedIdempotencyKey,
            trimmedContent,
            _timeProvider.GetUtcNow(),
            dto.ReplyToMessageId);

        var (persisted, isNewlyCreated) = await _messagePersistence.AddAsync(message, ct);

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
                var parentContent = parent.Content ?? string.Empty;
                var preview = parent.IsDeletedForEveryone
                    ? "This message was deleted."
                    : TruncatePreview(parentContent);

                replyDto = new MessageReplyDto(
                    parent.Id,
                    parent.SenderId,
                    parentSenderName,
                    preview,
                    parent.IsDeletedForEveryone);
            }
        }

        var senderProfile = await _userReader.GetByIdAsync(persisted.SenderId, ct);
        var senderName = FormatDisplayName(senderProfile);
        var senderHandle = FormatUserName(senderProfile);

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
            Reactions: []);

        if (isNewlyCreated)
        {
            try
            {
                await _realtimeNotifier.NotifyNewMessageAsync(conversationId, messageDto, ct);
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
        int pageSize = 30,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);

        if (pageSize <= 0)
        {
            pageSize = 30;
        }
        else if (pageSize > 100)
        {
            pageSize = 100;
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

        var dtos = new List<MessageDto>(pageItems.Count);
        foreach (var m in pageItems)
        {
            profiles.TryGetValue(m.SenderId, out var senderProfile);
            var senderName = FormatDisplayName(senderProfile);
            var senderHandle = FormatUserName(senderProfile);

            MessageReplyDto? replyDto = null;
            if (m.ParentMessageId != null &&
                !hiddenParentIds.Contains(m.ParentMessageId) &&
                parentMessages.TryGetValue(m.ParentMessageId, out var parent) &&
                (!effectiveCutoffUtc.HasValue || parent.CreatedAtUtc >= effectiveCutoffUtc.Value))
            {
                profiles.TryGetValue(parent.SenderId, out var parentProfile);
                var parentName = FormatDisplayName(parentProfile);
                var parentContent = parent.Content ?? string.Empty;
                var preview = parent.IsDeletedForEveryone
                    ? "This message was deleted."
                    : TruncatePreview(parentContent);

                replyDto = new MessageReplyDto(
                    parent.Id,
                    parent.SenderId,
                    parentName,
                    preview,
                    parent.IsDeletedForEveryone);
            }

            var isOutgoing = m.SenderId == callerId;
            reactionsByMessage.TryGetValue(m.Id, out var reactions);

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
                reactions ?? []));
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

        await EnsureMessageVisibleToUserAsync(conversationId, message, callerId, ct);

        if (!Message.HasVisibleContent(dto.Content))
        {
            throw new DomainException("Message content cannot be empty.");
        }

        var trimmedContent = dto.Content.Trim();
        if (trimmedContent.Length > 4000)
        {
            throw new DomainException("Message content cannot exceed 4000 characters.");
        }

        if (string.Equals(trimmedContent, message.Content, StringComparison.Ordinal))
        {
            return new MessageEditedDto(
                conversationId.Value,
                message.Id.Value,
                message.Content!,
                message.EditedAtUtc);
        }

        var nowUtc = _timeProvider.GetUtcNow();
        message.Edit(trimmedContent, nowUtc);
        await _messagePersistence.UpdateAsync(message, ct);

        var editedDto = new MessageEditedDto(
            conversationId.Value,
            message.Id.Value,
            message.Content!,
            message.EditedAtUtc);

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
            newLastMessagePreview = lastMessage.IsDeletedForEveryone
                ? "This message was deleted"
                : lastMessage.Content;
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
