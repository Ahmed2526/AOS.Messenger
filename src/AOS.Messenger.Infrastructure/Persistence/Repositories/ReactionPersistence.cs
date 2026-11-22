using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence implementation for message emoji reactions.
/// Handles setting, toggling, removing reactions, and chunked batch loading.
/// </summary>
public sealed class ReactionPersistence : IReactionPersistence
{
    private readonly ApplicationDbContext _db;

    public ReactionPersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<MessageReaction?> GetUserReactionAsync(
        ConversationId conversationId,
        MessageId messageId,
        UserId userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(userId);

        return await _db.MessageReactions
            .FirstOrDefaultAsync(r => r.ConversationId == conversationId && r.MessageId == messageId && r.UserId == userId, ct);
    }

    public async Task<string?> ToggleReactionAsync(
        ConversationId conversationId,
        MessageId messageId,
        UserId userId,
        string emoji,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(emoji);

        var normalized = ReactionAllowlist.Normalize(emoji);
        var convGuid = conversationId.Value;
        var msgIdVal = messageId.Value;

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Lock Messages before MessageReactions so delete-for-everyone and reactions never invert lock order or commit orphaned reactions.
            var isDeletedList = await _db.Database
                .SqlQuery<bool>($"SELECT [IsDeletedForEveryone] AS [Value] FROM [Messages] WITH (UPDLOCK, ROWLOCK) WHERE [ConversationId] = {convGuid} AND [Id] = {msgIdVal}")
                .ToListAsync(ct);

            if (isDeletedList.Count == 0)
            {
                throw new NotFoundException("Message", msgIdVal);
            }

            if (isDeletedList[0])
            {
                throw new ForbiddenException("Cannot react to a deleted message.");
            }

            var existing = await _db.MessageReactions
                .FirstOrDefaultAsync(r => r.ConversationId == conversationId && r.MessageId == messageId && r.UserId == userId, ct);

            string? resultEmoji;
            if (existing is not null)
            {
                if (existing.Emoji == normalized)
                {
                    _db.MessageReactions.Remove(existing);
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                        resultEmoji = null;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        _db.Entry(existing).State = EntityState.Detached;
                        resultEmoji = await ReconcileToggleAfterRaceAsync(conversationId, messageId, userId, normalized, nowUtc, ct);
                    }
                }
                else
                {
                    existing.UpdateEmoji(normalized, nowUtc);
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                        resultEmoji = normalized;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        _db.Entry(existing).State = EntityState.Detached;
                        resultEmoji = await ReconcileToggleAfterRaceAsync(conversationId, messageId, userId, normalized, nowUtc, ct);
                    }
                }
            }
            else
            {
                var reaction = MessageReaction.Create(conversationId, messageId, userId, normalized, nowUtc);
                try
                {
                    _db.MessageReactions.Add(reaction);
                    await _db.SaveChangesAsync(ct);
                    resultEmoji = normalized;
                }
                catch (DbUpdateException)
                {
                    _db.Entry(reaction).State = EntityState.Detached;
                    resultEmoji = await ReconcileToggleAfterRaceAsync(conversationId, messageId, userId, normalized, nowUtc, ct);
                }
            }

            await tx.CommitAsync(ct);
            return resultEmoji;
        });
    }

    private async Task<string?> ReconcileToggleAfterRaceAsync(
        ConversationId conversationId,
        MessageId messageId,
        UserId userId,
        string normalized,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var reloaded = await _db.MessageReactions
            .FirstOrDefaultAsync(r => r.ConversationId == conversationId && r.MessageId == messageId && r.UserId == userId, ct);

        if (reloaded is not null)
        {
            if (reloaded.Emoji == normalized)
            {
                _db.MessageReactions.Remove(reloaded);
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _db.Entry(reloaded).State = EntityState.Detached;
                }

                return null;
            }

            reloaded.UpdateEmoji(normalized, nowUtc);
            try
            {
                await _db.SaveChangesAsync(ct);
                return normalized;
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.Entry(reloaded).State = EntityState.Detached;
                return null;
            }
        }

        return null;
    }

    public async Task<bool> RemoveReactionAsync(
        ConversationId conversationId,
        MessageId messageId,
        UserId userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(userId);

        var convGuid = conversationId.Value;
        var msgIdVal = messageId.Value;

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var isDeletedList = await _db.Database
                .SqlQuery<bool>($"SELECT [IsDeletedForEveryone] AS [Value] FROM [Messages] WITH (UPDLOCK, ROWLOCK) WHERE [ConversationId] = {convGuid} AND [Id] = {msgIdVal}")
                .ToListAsync(ct);

            if (isDeletedList.Count == 0)
            {
                throw new NotFoundException("Message", msgIdVal);
            }

            if (isDeletedList[0])
            {
                throw new ForbiddenException("Cannot remove reaction from a deleted message.");
            }

            var deleted = await _db.MessageReactions
                .Where(r => r.ConversationId == conversationId && r.MessageId == messageId && r.UserId == userId)
                .ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
            return deleted > 0;
        });
    }

    public async Task<IReadOnlyDictionary<MessageId, IReadOnlyList<ReactionSummaryDto>>> GetReactionsForMessagesAsync(
        ConversationId conversationId,
        IEnumerable<MessageId> messageIds,
        UserId currentUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageIds);
        ArgumentNullException.ThrowIfNull(currentUserId);

        var idList = messageIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<MessageId, IReadOnlyList<ReactionSummaryDto>>();
        }

        var allReactions = new List<MessageReaction>();
        foreach (var chunk in idList.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var reactions = await _db.MessageReactions
                .AsNoTracking()
                .Where(r => r.ConversationId == conversationId && chunkList.Contains(r.MessageId))
                .ToListAsync(ct);

            allReactions.AddRange(reactions);
        }

        var result = new Dictionary<MessageId, IReadOnlyList<ReactionSummaryDto>>();

        var groupedByMessage = allReactions.GroupBy(r => r.MessageId);
        foreach (var msgGroup in groupedByMessage)
        {
            var summaries = msgGroup
                .GroupBy(r => r.Emoji)
                .Select(emojiGroup => new ReactionSummaryDto(
                    emojiGroup.Key,
                    emojiGroup.Count(),
                    emojiGroup.Any(r => r.UserId == currentUserId)))
                .OrderByDescending(s => s.Count)
                .ThenBy(s => s.Emoji)
                .ToList();

            result[msgGroup.Key] = summaries;
        }

        return result;
    }

    public async Task<IReadOnlyList<ReactionGroupDto>> GetReactionGroupsForMessageAsync(
        ConversationId conversationId,
        MessageId messageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);

        var reactions = await _db.MessageReactions
            .AsNoTracking()
            .Where(r => r.ConversationId == conversationId && r.MessageId == messageId)
            .ToListAsync(ct);

        return reactions
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionGroupDto(
                g.Key,
                g.Select(r => r.UserId.Value).ToList()))
            .OrderByDescending(g => g.UserIds.Count)
            .ThenBy(g => g.Emoji)
            .ToList();
    }

    public async Task DeleteReactionsForMessageAsync(
        ConversationId conversationId,
        MessageId messageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);

        await _db.MessageReactions
            .Where(r => r.ConversationId == conversationId && r.MessageId == messageId)
            .ExecuteDeleteAsync(ct);
    }
}
