using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Tests;

/// <summary>
/// Unit tests for Phase 1 Domain entities, aggregate rules, and invariants.
/// </summary>
public sealed class DomainEntityTests
{
    [Fact]
    public void Conversation_Create_CanonicalParticipantOrdering_AssignsSmallerUserIdFirst()
    {
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var userA = UserId.From(id1);
        var userB = UserId.From(id2);
        var now = DateTimeOffset.UtcNow;

        Assert.True(userA < userB);

        // When created with (userA, userB)
        var conv1 = Conversation.Create(userA, userB, now);
        Assert.Equal(userA, conv1.Participant1Id);
        Assert.Equal(userB, conv1.Participant2Id);
        Assert.True(conv1.Participant1Id < conv1.Participant2Id);

        // When created with (userB, userA)
        var conv2 = Conversation.Create(userB, userA, now);
        Assert.Equal(userA, conv2.Participant1Id);
        Assert.Equal(userB, conv2.Participant2Id);
        Assert.True(conv2.Participant1Id < conv2.Participant2Id);
    }

    [Fact]
    public void Conversation_Create_SelfConversation_ThrowsDomainException()
    {
        var userId = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var ex = Assert.Throws<DomainException>(() => Conversation.Create(userId, userId, now));
        Assert.Contains("Cannot create a conversation with oneself", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Conversation_ParticipantMethods_WorkAsExpected()
    {
        var userA = UserId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var userB = UserId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var outsider = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv = Conversation.Create(userA, userB, now);

        Assert.True(conv.HasParticipant(userA));
        Assert.True(conv.HasParticipant(userB));
        Assert.False(conv.HasParticipant(outsider));

        Assert.Equal(userB, conv.GetOtherParticipant(userA));
        Assert.Equal(userA, conv.GetOtherParticipant(userB));

        Assert.Throws<DomainException>(() => conv.GetOtherParticipant(outsider));
    }

    [Fact]
    public void Message_DeleteForEveryone_Within3Hours_Succeeds()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var msg = Message.Create(convId, senderId, "idempotency-1", "Original message text", createdAtUtc);

        // Within 3-hour window: 2 hours and 59 minutes
        var deleteTime = createdAtUtc.AddHours(2).AddMinutes(59);
        msg.DeleteForEveryone(senderId, deleteTime);

        Assert.True(msg.IsDeletedForEveryone);
        Assert.Equal(deleteTime, msg.DeletedForEveryoneAtUtc);
        Assert.Null(msg.Content);
    }

    [Fact]
    public void Message_DeleteForEveryone_Exactly3Hours_Succeeds()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var msg = Message.Create(convId, senderId, "idempotency-2", "Original message text", createdAtUtc);

        // Exactly 3 hours
        var deleteTime = createdAtUtc.AddHours(3);
        msg.DeleteForEveryone(senderId, deleteTime);

        Assert.True(msg.IsDeletedForEveryone);
        Assert.Equal(deleteTime, msg.DeletedForEveryoneAtUtc);
        Assert.Null(msg.Content);
    }

    [Fact]
    public void Message_DeleteForEveryone_Beyond3Hours_ThrowsDomainException()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var msg = Message.Create(convId, senderId, "idempotency-3", "Original message text", createdAtUtc);

        // Beyond 3-hour window: 3 hours and 1 minute
        var deleteTime = createdAtUtc.AddHours(3).AddMinutes(1);

        var ex = Assert.Throws<DomainException>(() => msg.DeleteForEveryone(senderId, deleteTime));
        Assert.Contains("3 hours", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(msg.IsDeletedForEveryone);
        Assert.Equal("Original message text", msg.Content);
    }

    [Fact]
    public void Message_DeleteForEveryone_NonSender_ThrowsDomainException()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var nonSenderId = UserId.New();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var msg = Message.Create(convId, senderId, "idempotency-4", "Original message text", createdAtUtc);

        var deleteTime = createdAtUtc.AddMinutes(15);

        var ex = Assert.Throws<DomainException>(() => msg.DeleteForEveryone(nonSenderId, deleteTime));
        Assert.Contains("Only the sender", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(msg.IsDeletedForEveryone);
    }

    [Fact]
    public void Message_DeleteForEveryone_TombstoneSemantics_ClearsContentAndPreventsEditing()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var msg = Message.Create(convId, senderId, "idempotency-5", "Original message text", createdAtUtc);

        var deleteTime = createdAtUtc.AddMinutes(30);
        msg.DeleteForEveryone(senderId, deleteTime);

        // Assert tombstone properties
        Assert.True(msg.IsDeletedForEveryone);
        Assert.Equal(deleteTime, msg.DeletedForEveryoneAtUtc);
        Assert.Null(msg.Content);

        // Attempting to edit a tombstoned message must fail
        var editEx = Assert.Throws<DomainException>(() => msg.Edit("New content", deleteTime.AddMinutes(5)));
        Assert.Contains("deleted for everyone", editEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserConversationState_PerUserIsolation_ChangesToUser1DoNotAffectUser2()
    {
        var convId = ConversationId.New();
        var user1 = UserId.New();
        var user2 = UserId.New();

        var state1 = UserConversationState.Create(convId, user1);
        var state2 = UserConversationState.Create(convId, user2);

        var now = DateTimeOffset.UtcNow;

        // Apply mutations to state1
        state1.ClearChat(now);
        state1.DeleteConversation(now.AddMinutes(1));
        state1.Pin(now.AddMinutes(2));
        state1.Archive(now.AddMinutes(3));
        state1.Mute(now.AddMinutes(4));
        var msgId = MessageId.New();
        state1.MarkRead(msgId, now.AddMinutes(5));

        // Assert state1 reflects mutations
        Assert.Equal(now, state1.ClearedAtUtc);
        Assert.Equal(now.AddMinutes(1), state1.DeletedAtUtc);
        Assert.True(state1.IsPinned);
        Assert.Equal(now.AddMinutes(2), state1.PinnedAtUtc);
        Assert.True(state1.IsArchived);
        Assert.Equal(now.AddMinutes(3), state1.ArchivedAtUtc);
        Assert.Equal(now.AddMinutes(4), state1.MutedAtUtc);
        Assert.Equal(msgId, state1.LastReadMessageId);

        // Assert state2 is completely isolated and unaffected
        Assert.Null(state2.ClearedAtUtc);
        Assert.Null(state2.DeletedAtUtc);
        Assert.False(state2.IsPinned);
        Assert.Null(state2.PinnedAtUtc);
        Assert.False(state2.IsArchived);
        Assert.Null(state2.ArchivedAtUtc);
        Assert.Null(state2.MutedAtUtc);
        Assert.Null(state2.LastReadMessageId);
    }

    [Fact]
    public void UserConversationState_PinAndArchive_ToggleCorrectly()
    {
        var convId = ConversationId.New();
        var user = UserId.New();
        var state = UserConversationState.Create(convId, user);
        var now = DateTimeOffset.UtcNow;

        // Pin then unpin
        state.Pin(now);
        Assert.True(state.IsPinned);
        Assert.Equal(now, state.PinnedAtUtc);
        state.Unpin();
        Assert.False(state.IsPinned);
        Assert.Null(state.PinnedAtUtc);

        // Archive then unarchive
        state.Archive(now.AddMinutes(5));
        Assert.True(state.IsArchived);
        Assert.Equal(now.AddMinutes(5), state.ArchivedAtUtc);
        state.Unarchive();
        Assert.False(state.IsArchived);
        Assert.Null(state.ArchivedAtUtc);

        // Mute then unmute
        state.Mute(now.AddMinutes(10));
        Assert.Equal(now.AddMinutes(10), state.MutedAtUtc);
        state.Unmute();
        Assert.Null(state.MutedAtUtc);
    }

    [Fact]
    public void UserBlock_Create_SelfBlockingRejection_ThrowsDomainException()
    {
        var user = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var ex = Assert.Throws<DomainException>(() => UserBlock.Create(user, user, now));
        Assert.Contains("cannot block themselves", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserBlock_Create_DistinctUsers_Succeeds()
    {
        var blocker = UserId.New();
        var blocked = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var block = UserBlock.Create(blocker, blocked, now);

        Assert.Equal(blocker, block.BlockerId);
        Assert.Equal(blocked, block.BlockedUserId);
        Assert.Equal(now, block.CreatedAtUtc);
    }
}
