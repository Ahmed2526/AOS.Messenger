using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents a direct (one-to-one) conversation between two distinct users.
/// Participant identifiers are canonically ordered using SQL Server collation order such that Participant1Id is always less than Participant2Id.
/// </summary>
public sealed class Conversation : Entity<ConversationId>
{
    public UserId Participant1Id { get; private set; } = null!;
    public UserId Participant2Id { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private Conversation()
    {
    }

    public static Conversation Create(
        UserId userA,
        UserId userB,
        DateTimeOffset nowUtc,
        ConversationId? id = null)
    {
        return CreateDirect(userA, userB, nowUtc, id);
    }

    public static Conversation CreateDirect(
        UserId userA,
        UserId userB,
        DateTimeOffset nowUtc,
        ConversationId? id = null)
    {
        ArgumentNullException.ThrowIfNull(userA);
        ArgumentNullException.ThrowIfNull(userB);

        if (userA == userB)
        {
            throw new DomainException("Cannot create a conversation with oneself.");
        }

        var conversation = new Conversation
        {
            Id = id!,
            CreatedAtUtc = nowUtc,
            RowVersion = []
        };

        if (UserId.CompareSqlServer(userA, userB) < 0)
        {
            conversation.Participant1Id = userA;
            conversation.Participant2Id = userB;
        }
        else
        {
            conversation.Participant1Id = userB;
            conversation.Participant2Id = userA;
        }

        return conversation;
    }

    public bool HasParticipant(UserId userId)
    {
        ArgumentNullException.ThrowIfNull(userId);
        return Participant1Id == userId || Participant2Id == userId;
    }

    public UserId GetOtherParticipant(UserId userId)
    {
        ArgumentNullException.ThrowIfNull(userId);

        if (Participant1Id == userId)
        {
            return Participant2Id;
        }

        if (Participant2Id == userId)
        {
            return Participant1Id;
        }

        throw new DomainException($"User '{userId}' is not a participant in conversation '{Id}'.");
    }
}
