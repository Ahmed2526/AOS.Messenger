using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Real SQL Server LocalDB integration tests for Ticket 2 remediation:
/// 1. 100 random UserId pairs ordered by SQL Server collation comparator satisfy [Participant1Id] < [Participant2Id]
///    cleanly without check constraint violations in SQL Server.
/// 2. Duplicate conversation insert and duplicate idempotency key trigger real SQL unique constraint violations.
/// 3. Cross-conversation ParentMessageId and LastReadMessageId are rejected by SQL Server composite FKs.
/// </summary>
public sealed class SqlServerConstraintsIntegrationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public SqlServerConstraintsIntegrationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // =========================================================================
    // 1. Participant Ordering Check Constraint & Byte Comparator
    // =========================================================================

    [Fact]
    public async Task HundredRandomUserIdPairs_OrderedByComparator_SatisfySqlCheckConstraintCleanly()
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 100; i++)
        {
            var userA = UserId.New();
            var userB = UserId.New();
            while (userA == userB)
            {
                userB = UserId.New();
            }

            // Create direct conversation using domain constructor (which applies SQL Server collation ordering)
            var conversation = Conversation.CreateDirect(userA, userB, now.AddSeconds(i));
            db.Conversations.Add(conversation);
        }

        // Must succeed without throwing DbUpdateException (which would indicate CK_Conversations_ParticipantOrdering violation)
        var savedCount = await db.SaveChangesAsync();
        Assert.True(savedCount >= 100);
    }

    [Fact]
    public async Task ParticipantOrdering_ViolatingCheckConstraint_ThrowsRealSqlException()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        while (userA == userB)
        {
            userB = UserId.New();
        }

        var validConv = Conversation.CreateDirect(userA, userB, DateTimeOffset.UtcNow);

        // Attempt to insert deliberately reversed participants (Participant1Id > Participant2Id in SQL byte order)
        // using raw SQL to bypass domain guard and verify that the database check constraint CK_Conversations_ParticipantOrdering fires.
        var invalidId = Guid.NewGuid();
        var p1 = validConv.Participant2Id.Value;
        var p2 = validConv.Participant1Id.Value;
        var now = DateTimeOffset.UtcNow;

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Conversations (Id, Participant1Id, Participant2Id, CreatedAtUtc) VALUES ({invalidId}, {p1}, {p2}, {now})");
        });

        Assert.Contains("CK_Conversations_ParticipantOrdering", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 2. Real SQL Unique Constraint Violations
    // =========================================================================

    [Fact]
    public async Task DuplicateConversation_DirectInsert_TriggersRealSqlUniqueIndexViolation()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv1 = Conversation.CreateDirect(userA, userB, now);
        db.Conversations.Add(conv1);
        await db.SaveChangesAsync();

        // Attempting to directly insert another conversation with the exact same participants
        await using var db2 = _fixture.CreateDbContext();
        var conv2 = Conversation.CreateDirect(userA, userB, now.AddMinutes(1));
        db2.Conversations.Add(conv2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.IsType<SqlException>(ex.InnerException);
    }

    [Fact]
    public async Task DuplicateConversation_ViaPersistence_HandlesUniqueViolationIdempotently()
    {
        var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IConversationPersistence>();

        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv1 = Conversation.CreateDirect(userA, userB, now);
        var added1 = await repo.AddAsync(conv1);

        var conv2 = Conversation.CreateDirect(userB, userA, now.AddSeconds(10));
        var added2 = await repo.AddAsync(conv2);

        Assert.Equal(added1.Id, added2.Id);
    }

    [Fact]
    public async Task DuplicateIdempotencyKey_DirectInsert_TriggersRealSqlUniqueIndexViolation()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv = Conversation.CreateDirect(userA, userB, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var msg1 = Message.Create(conv.Id, userA, "idem-sql-key", "Message 1", now);
        db.Messages.Add(msg1);
        await db.SaveChangesAsync();

        await using var db2 = _fixture.CreateDbContext();
        var msg2 = Message.Create(conv.Id, userA, "idem-sql-key", "Message 2", now.AddSeconds(5));
        db2.Messages.Add(msg2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.IsType<SqlException>(ex.InnerException);
    }

    [Fact]
    public async Task DuplicateIdempotencyKey_ViaPersistence_ReturnsExistingOnSameContent_ThrowsConflictOnDifferent()
    {
        var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessagePersistence>();
        var convRepo = scope.ServiceProvider.GetRequiredService<IConversationPersistence>();

        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv = await convRepo.AddAsync(Conversation.CreateDirect(userA, userB, now));

        var msg1 = Message.Create(conv.Id, userA, "idem-spec-key", "Identical Text", now);
        var added1 = await repo.AddAsync(msg1);

        // Same content -> idempotently returns existing
        var msg2 = Message.Create(conv.Id, userA, "idem-spec-key", "Identical Text", now.AddSeconds(1));
        var added2 = await repo.AddAsync(msg2);
        Assert.Equal(added1.Id, added2.Id);

        // Different content -> throws ConflictException
        var msg3 = Message.Create(conv.Id, userA, "idem-spec-key", "Changed Text", now.AddSeconds(2));
        var conflictEx = await Assert.ThrowsAsync<ConflictException>(() => repo.AddAsync(msg3));
        Assert.Contains("idempotency key", conflictEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 3. Composite Foreign Keys Cross-Conversation Rejection
    // =========================================================================

    [Fact]
    public async Task ReplyCompositeFk_RejectsCrossConversationParentMessage()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var userC = UserId.New();
        var now = DateTimeOffset.UtcNow;

        // Conv 1 and Conv 2
        var conv1 = Conversation.CreateDirect(userA, userB, now);
        var conv2 = Conversation.CreateDirect(userA, userC, now);
        db.Conversations.AddRange(conv1, conv2);
        await db.SaveChangesAsync();

        // Message in Conv 2
        var msgInConv2 = Message.Create(conv2.Id, userA, "key-in-conv2", "Content in conv 2", now);
        db.Messages.Add(msgInConv2);
        await db.SaveChangesAsync();

        // Attempt to create a message in Conv 1 whose ParentMessageId points to msgInConv2 (which is in Conv 2!)
        var crossReply = Message.Create(conv1.Id, userA, "key-cross-reply", "Cross Reply", now, parentMessageId: msgInConv2.Id);
        db.Messages.Add(crossReply);

        // SQL Server composite FK (ConversationId, ParentMessageId) -> Messages(ConversationId, Id) must reject this!
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.Contains("FK_Messages_Messages_ConversationId_ParentMessageId", ex.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplyCompositeFk_AcceptsSameConversationParentMessage()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv = Conversation.CreateDirect(userA, userB, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var parentMsg = Message.Create(conv.Id, userA, "parent-key", "Parent Message", now);
        db.Messages.Add(parentMsg);
        await db.SaveChangesAsync();

        var validReply = Message.Create(conv.Id, userB, "reply-key", "Reply Message", now.AddSeconds(1), parentMessageId: parentMsg.Id);
        db.Messages.Add(validReply);

        var saved = await db.SaveChangesAsync();
        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task ReadMarkerCompositeFk_RejectsCrossConversationLastReadMessage()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var userC = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv1 = Conversation.CreateDirect(userA, userB, now);
        var conv2 = Conversation.CreateDirect(userA, userC, now);
        db.Conversations.AddRange(conv1, conv2);
        await db.SaveChangesAsync();

        var msgInConv2 = Message.Create(conv2.Id, userA, "key-conv2-read", "Conv 2 message", now);
        db.Messages.Add(msgInConv2);
        await db.SaveChangesAsync();

        // Attempt to set LastReadMessageId on UserConversationState for Conv 1 to msgInConv2 (which is in Conv 2)
        var state = UserConversationState.Create(conv1.Id, userA);
        state.MarkRead(msgInConv2.Id, now);
        db.UserConversationStates.Add(state);

        // SQL Server composite FK (ConversationId, LastReadMessageId) -> Messages(ConversationId, Id) must reject this!
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.Contains("FK_UserConversationStates_Messages_ConversationId_LastReadMessageId", ex.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadMarkerCompositeFk_AcceptsSameConversationLastReadMessage()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv = Conversation.CreateDirect(userA, userB, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var msg = Message.Create(conv.Id, userA, "read-valid-key", "Valid message", now);
        db.Messages.Add(msg);
        await db.SaveChangesAsync();

        var state = UserConversationState.Create(conv.Id, userA);
        state.MarkRead(msg.Id, now);
        db.UserConversationStates.Add(state);

        var saved = await db.SaveChangesAsync();
        Assert.Equal(1, saved);
        Assert.NotNull(state.LastReadAtUtc);
    }

    // =========================================================================
    // 5. Phase 8 Composite FKs & Tombstone Content Check Constraint
    // =========================================================================

    [Fact]
    public async Task MessageReactions_CrossConversationCompositeFk_RejectedBySqlServer()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var userC = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv1 = Conversation.CreateDirect(userA, userB, now);
        var conv2 = Conversation.CreateDirect(userA, userC, now);
        db.Conversations.AddRange(conv1, conv2);
        await db.SaveChangesAsync();

        var msgInConv2 = Message.Create(conv2.Id, userA, "rx-cross-key", "Message in Conv 2", now);
        db.Messages.Add(msgInConv2);
        await db.SaveChangesAsync();

        var invalidReaction = MessageReaction.Create(conv1.Id, msgInConv2.Id, userA, "👍", now);
        db.MessageReactions.Add(invalidReaction);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.Contains("FK_MessageReactions_Messages_ConversationId_MessageId", ex.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserMessageDeletions_CrossConversationCompositeFk_RejectedBySqlServer()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var userC = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv1 = Conversation.CreateDirect(userA, userB, now);
        var conv2 = Conversation.CreateDirect(userA, userC, now);
        db.Conversations.AddRange(conv1, conv2);
        await db.SaveChangesAsync();

        var msgInConv2 = Message.Create(conv2.Id, userA, "del-cross-key", "Message in Conv 2", now);
        db.Messages.Add(msgInConv2);
        await db.SaveChangesAsync();

        var invalidDeletion = UserMessageDeletion.Create(conv1.Id, msgInConv2.Id, userA, now);
        db.UserMessageDeletions.Add(invalidDeletion);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.Contains("FK_UserMessageDeletions_Messages_ConversationId_MessageId", ex.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Messages_TombstoneCheckConstraint_EnforcesNullContentWhenDeleted()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv = Conversation.CreateDirect(userA, userB, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var msg = Message.Create(conv.Id, userA, "ck-tomb-key", "Visible text", now);
        db.Messages.Add(msg);
        await db.SaveChangesAsync();

        // Attempting to set IsDeletedForEveryone = 1 while Content is non-null violates CK_Messages_Content_Tombstone
        var ex1 = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Messages SET IsDeletedForEveryone = 1 WHERE Id = {msg.Id.Value}"));
        Assert.Contains("CK_Messages_Content_Tombstone", ex1.Message, StringComparison.OrdinalIgnoreCase);

        // Attempting to set Content = NULL while IsDeletedForEveryone = 0 violates CK_Messages_Content_Tombstone
        var ex2 = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Messages SET Content = NULL WHERE Id = {msg.Id.Value}"));
        Assert.Contains("CK_Messages_Content_Tombstone", ex2.Message, StringComparison.OrdinalIgnoreCase);

        // Setting both IsDeletedForEveryone = 1 and Content = NULL succeeds
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Messages SET IsDeletedForEveryone = 1, Content = NULL, DeletedForEveryoneAtUtc = {now} WHERE Id = {msg.Id.Value}");
        Assert.Equal(1, updated);
    }
}
