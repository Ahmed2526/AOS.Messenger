using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Tests;

public class DomainTests
{
    private sealed class TestEntity : Entity<Guid>
    {
        public TestEntity() { }
        public TestEntity(Guid id) : base(id) { }
    }

    private sealed class AnotherEntity : Entity<Guid>
    {
        public AnotherEntity() { }
        public AnotherEntity(Guid id) : base(id) { }
    }

    private sealed class TestValueObject : ValueObject
    {
        public string Text { get; }
        public int Number { get; }

        public TestValueObject(string text, int number)
        {
            Text = text;
            Number = number;
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Text;
            yield return Number;
        }
    }

    private sealed class LargeHashValueObject : ValueObject
    {
        public int Value1 { get; }
        public int Value2 { get; }

        public LargeHashValueObject(int val1, int val2)
        {
            Value1 = val1;
            Value2 = val2;
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value1;
            yield return Value2;
        }
    }

    [Fact]
    public void Entity_Equality_SameIdAndType_AreEqual()
    {
        var id = Guid.NewGuid();
        var entity1 = new TestEntity(id);
        var entity2 = new TestEntity(id);

        Assert.True(entity1.Equals(entity2));
        Assert.True(entity1 == entity2);
        Assert.False(entity1 != entity2);
        Assert.Equal(entity1.GetHashCode(), entity2.GetHashCode());
    }

    [Fact]
    public void Entity_Equality_DifferentId_AreNotEqual()
    {
        var entity1 = new TestEntity(Guid.NewGuid());
        var entity2 = new TestEntity(Guid.NewGuid());

        Assert.False(entity1.Equals(entity2));
        Assert.False(entity1 == entity2);
        Assert.True(entity1 != entity2);
    }

    [Fact]
    public void Entity_Equality_DifferentType_AreNotEqual()
    {
        var id = Guid.NewGuid();
        var entity1 = new TestEntity(id);
        var entity2 = new AnotherEntity(id);

        Assert.False(entity1.Equals(entity2));
        Assert.False(entity1 == (object)entity2);
    }

    [Fact]
    public void Entity_IsTransient_ReturnsTrueForDefaultId_AndFalseForAssignedId()
    {
        var transient = new TestEntity();
        Assert.True(transient.IsTransient());
        Assert.Equal(Guid.Empty, transient.Id);

        var persisted = new TestEntity(Guid.NewGuid());
        Assert.False(persisted.IsTransient());
    }

    [Fact]
    public void Entity_TransientEquality_DifferentInstancesAreNeverEqual()
    {
        var transient1 = new TestEntity();
        var transient2 = new TestEntity();

        Assert.False(transient1.Equals(transient2));
        Assert.False(transient2.Equals(transient1));
        Assert.False(transient1 == transient2);
        Assert.True(transient1 != transient2);
        Assert.False(transient1.Equals((object)transient2));
    }

    [Fact]
    public void Entity_TransientEquality_SameInstanceIsEqualToItself()
    {
        var transient = new TestEntity();
        var sameRef = transient;

        Assert.True(transient.Equals(sameRef));
        Assert.True(transient == sameRef);
        Assert.False(transient != sameRef);
        Assert.True(transient.Equals((object)sameRef));
    }

    [Fact]
    public void Entity_TransientComparedWithPersisted_AreNotEqual()
    {
        var transient = new TestEntity();
        var persisted = new TestEntity(Guid.NewGuid());

        Assert.False(transient.Equals(persisted));
        Assert.False(persisted.Equals(transient));
        Assert.False(transient == persisted);
        Assert.True(transient != persisted);
    }

    [Fact]
    public void Entity_EqualsNull_ReturnsFalse()
    {
        var entity = new TestEntity(Guid.NewGuid());
        Assert.False(entity.Equals(null));
        Assert.False(entity.Equals((object?)null));
        Assert.False(entity == null);
        Assert.False(null == entity);
        Assert.True(entity != null);
        Assert.True(null != entity);
    }

    [Fact]
    public void ValueObject_Equality_MatchingComponents_AreEqual()
    {
        var vo1 = new TestValueObject("test", 100);
        var vo2 = new TestValueObject("test", 100);

        Assert.True(vo1.Equals(vo2));
        Assert.True(vo1 == vo2);
        Assert.Equal(vo1.GetHashCode(), vo2.GetHashCode());
    }

    [Fact]
    public void ValueObject_Equality_DifferingComponents_AreNotEqual()
    {
        var vo1 = new TestValueObject("test", 100);
        var vo2 = new TestValueObject("test", 200);

        Assert.False(vo1.Equals(vo2));
        Assert.True(vo1 != vo2);
    }

    [Fact]
    public void ValueObject_GetHashCode_DoesNotOverflowWithLargeIntegers()
    {
        var vo = new LargeHashValueObject(int.MaxValue, int.MaxValue);
        var hash = Record.Exception(() => vo.GetHashCode());
        Assert.Null(hash);
    }

    [Fact]
    public void UserId_New_CreatesValidNonEmptyId()
    {
        var userId = UserId.New();

        Assert.NotEqual(Guid.Empty, userId.Value);
        Assert.False(string.IsNullOrWhiteSpace(userId.ToString()));
    }

    [Fact]
    public void UserId_From_EmptyGuid_ThrowsDomainException()
    {
        var ex = Assert.Throws<DomainException>(() => UserId.From(Guid.Empty));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserId_Parse_ValidString_ReturnsUserId()
    {
        var guid = Guid.NewGuid();
        var userId = UserId.Parse(guid.ToString());

        Assert.Equal(guid, userId.Value);
    }

    [Fact]
    public void UserId_Parse_InvalidString_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => UserId.Parse("not-a-guid"));
    }

    [Fact]
    public void UserId_TryParse_ValidAndInvalid()
    {
        var guid = Guid.NewGuid();
        Assert.True(UserId.TryParse(guid.ToString(), out var parsedValid));
        Assert.NotNull(parsedValid);
        Assert.Equal(guid, parsedValid.Value);

        Assert.False(UserId.TryParse("invalid", out var parsedInvalid));
        Assert.Null(parsedInvalid);

        Assert.False(UserId.TryParse(Guid.Empty.ToString(), out var parsedEmpty));
        Assert.Null(parsedEmpty);
    }

    [Fact]
    public void UserId_Equality_SameGuid_AreEqual()
    {
        var guid = Guid.NewGuid();
        var user1 = UserId.From(guid);
        var user2 = UserId.From(guid);

        Assert.True(user1.Equals(user2));
        Assert.True(user1.Equals((object)user2));
        Assert.True(user1 == user2);
        Assert.False(user1 != user2);
        Assert.Equal(user1.GetHashCode(), user2.GetHashCode());
        Assert.Equal(guid.GetHashCode(), user1.GetHashCode());
    }

    [Fact]
    public void UserId_Equality_DifferentGuid_AreNotEqual()
    {
        var user1 = UserId.New();
        var user2 = UserId.New();

        Assert.False(user1.Equals(user2));
        Assert.False(user1.Equals((object)user2));
        Assert.False(user1 == user2);
        Assert.True(user1 != user2);
    }

    [Fact]
    public void UserId_Equality_NullAndDifferentType_ReturnsFalse()
    {
        var user = UserId.New();

        Assert.False(user.Equals(null));
        Assert.False(user.Equals((object?)null));
        Assert.False(user.Equals("not-a-user-id"));
        Assert.False(user == null);
        Assert.False(null == user);
        Assert.True(user != null);
        Assert.True(null != user);

        UserId? nullUser1 = null;
        UserId? nullUser2 = null;
        Assert.True(nullUser1 == nullUser2);
        Assert.False(nullUser1 != nullUser2);
    }

    [Fact]
    public void UserId_Comparison_SupportsOrderingForCanonicalPairing()
    {
        var guid1 = new Guid("11111111-1111-1111-1111-111111111111");
        var guid2 = new Guid("22222222-2222-2222-2222-222222222222");

        var user1 = UserId.From(guid1);
        var user2 = UserId.From(guid2);

        Assert.True(user1.CompareTo(user2) < 0);
        Assert.True(user2.CompareTo(user1) > 0);
        Assert.Equal(0, user1.CompareTo(UserId.From(guid1)));
    }

    [Fact]
    public void UserId_CompareTo_Null_ReturnsPositive()
    {
        var user = UserId.New();

        Assert.True(user.CompareTo(null) > 0);
        Assert.True(((IComparable)user).CompareTo(null) > 0);
    }

    [Fact]
    public void UserId_CompareTo_Object_ValidAndInvalidTypes()
    {
        var guid1 = new Guid("11111111-1111-1111-1111-111111111111");
        var guid2 = new Guid("22222222-2222-2222-2222-222222222222");
        var user1 = UserId.From(guid1);
        var user2 = UserId.From(guid2);

        IComparable comparable = user1;
        Assert.True(comparable.CompareTo((object)user2) < 0);
        Assert.Equal(0, comparable.CompareTo((object)UserId.From(guid1)));

        var ex = Assert.Throws<ArgumentException>(() => comparable.CompareTo("not-a-userid"));
        Assert.Contains(nameof(UserId), ex.Message);
    }

    [Fact]
    public void UserId_ComparisonOperators_BehaveConsistently()
    {
        var guid1 = new Guid("11111111-1111-1111-1111-111111111111");
        var guid2 = new Guid("22222222-2222-2222-2222-222222222222");

        var user1 = UserId.From(guid1);
        var user2 = UserId.From(guid2);
        var user1Copy = UserId.From(guid1);

        // < and <=
        Assert.True(user1 < user2);
        Assert.True(user1 <= user2);
        Assert.False(user2 < user1);
        Assert.False(user2 <= user1);
        Assert.True(user1 <= user1Copy);
        Assert.False(user1 < user1Copy);

        // > and >=
        Assert.True(user2 > user1);
        Assert.True(user2 >= user1);
        Assert.False(user1 > user2);
        Assert.False(user1 >= user2);
        Assert.True(user1 >= user1Copy);
        Assert.False(user1 > user1Copy);

        // Null comparison operator behavior
        UserId? nullUser = null;
        Assert.True(nullUser < user1);
        Assert.False(user1 < nullUser);
        Assert.True(nullUser <= user1);
        Assert.False(user1 <= nullUser);
        Assert.True(nullUser <= (UserId?)null);

        Assert.True(user1 > nullUser);
        Assert.False(nullUser > user1);
        Assert.True(user1 >= nullUser);
        Assert.False(nullUser >= user1);
        Assert.True(nullUser >= (UserId?)null);
    }

    [Fact]
    public void ConversationId_Tests()
    {
        var id1 = ConversationId.New();
        var id2 = ConversationId.From(id1.Value);
        var id3 = ConversationId.New();

        Assert.Equal(id1, id2);
        Assert.True(id1 == id2);
        Assert.False(id1 == id3);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());

        Assert.Throws<DomainException>(() => new ConversationId(Guid.Empty));
        Assert.Throws<DomainException>(() => ConversationId.Parse("invalid-guid"));

        var parsed = ConversationId.Parse(id1.Value.ToString());
        Assert.Equal(id1, parsed);

        Assert.True(ConversationId.TryParse(id1.Value.ToString(), out var tryParsed));
        Assert.Equal(id1, tryParsed);

        Assert.False(ConversationId.TryParse("not-a-guid", out _));
        Assert.False(ConversationId.TryParse(Guid.Empty.ToString(), out _));
    }

    [Fact]
    public void MessageId_Tests()
    {
        var id1 = MessageId.New();
        var id2 = MessageId.From(id1.Value);
        var id3 = MessageId.New();

        Assert.Equal(id1, id2);
        Assert.True(id1 == id2);
        Assert.False(id1 == id3);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());

        Assert.Throws<DomainException>(() => MessageId.Parse("invalid-number"));

        var parsed = MessageId.Parse(id1.Value.ToString());
        Assert.Equal(id1, parsed);

        Assert.True(MessageId.TryParse(id1.Value.ToString(), out var tryParsed));
        Assert.Equal(id1, tryParsed);

        Assert.False(MessageId.TryParse("not-a-number", out _));
    }

    [Fact]
    public void Conversation_Creation_EnforcesCanonicalOrdering_AndRejectsSelf()
    {
        var user1 = UserId.From(new Guid("11111111-1111-1111-1111-111111111111"));
        var user2 = UserId.From(new Guid("22222222-2222-2222-2222-222222222222"));
        var now = DateTimeOffset.UtcNow;

        // When user1 < user2 passed in order
        var conv1 = Conversation.Create(user1, user2, now);
        Assert.Equal(user1, conv1.Participant1Id);
        Assert.Equal(user2, conv1.Participant2Id);

        // When user2 > user1 passed in reverse order
        var conv2 = Conversation.Create(user2, user1, now);
        Assert.Equal(user1, conv2.Participant1Id);
        Assert.Equal(user2, conv2.Participant2Id);

        // Self conversation throws
        Assert.Throws<DomainException>(() => Conversation.Create(user1, user1, now));
    }

    [Fact]
    public void Conversation_ParticipantMethods()
    {
        var user1 = UserId.From(new Guid("11111111-1111-1111-1111-111111111111"));
        var user2 = UserId.From(new Guid("22222222-2222-2222-2222-222222222222"));
        var otherUser = UserId.New();
        var now = DateTimeOffset.UtcNow;

        var conv = Conversation.Create(user1, user2, now);

        Assert.True(conv.HasParticipant(user1));
        Assert.True(conv.HasParticipant(user2));
        Assert.False(conv.HasParticipant(otherUser));

        Assert.Equal(user2, conv.GetOtherParticipant(user1));
        Assert.Equal(user1, conv.GetOtherParticipant(user2));
        Assert.Throws<DomainException>(() => conv.GetOtherParticipant(otherUser));
    }

    [Fact]
    public void UserConversationState_Mutations()
    {
        var convId = ConversationId.New();
        var userId = UserId.New();
        var state = UserConversationState.Create(convId, userId);

        Assert.Equal(convId, state.ConversationId);
        Assert.Equal(userId, state.UserId);
        Assert.Null(state.ClearedAtUtc);
        Assert.Null(state.DeletedAtUtc);
        Assert.Null(state.MutedAtUtc);
        Assert.False(state.IsPinned);
        Assert.Null(state.PinnedAtUtc);
        Assert.False(state.IsArchived);
        Assert.Null(state.ArchivedAtUtc);
        Assert.Null(state.LastReadMessageId);

        var t1 = DateTimeOffset.UtcNow;
        state.ClearChat(t1);
        Assert.Equal(t1, state.ClearedAtUtc);

        var t2 = t1.AddMinutes(1);
        state.DeleteConversation(t2);
        Assert.Equal(t2, state.DeletedAtUtc);

        var t3 = t2.AddMinutes(1);
        state.Pin(t3);
        Assert.True(state.IsPinned);
        Assert.Equal(t3, state.PinnedAtUtc);
        state.Unpin();
        Assert.False(state.IsPinned);
        Assert.Null(state.PinnedAtUtc);

        var t4 = t3.AddMinutes(1);
        state.Archive(t4);
        Assert.True(state.IsArchived);
        Assert.Equal(t4, state.ArchivedAtUtc);
        state.Unarchive();
        Assert.False(state.IsArchived);
        Assert.Null(state.ArchivedAtUtc);

        var msgId = MessageId.New();
        state.MarkRead(msgId, t4.AddSeconds(30));
        Assert.Equal(msgId, state.LastReadMessageId);

        var t5 = t4.AddMinutes(1);
        state.Mute(t5);
        Assert.Equal(t5, state.MutedAtUtc);
        state.Unmute();
        Assert.Null(state.MutedAtUtc);
    }

    [Fact]
    public void Message_Create_And_Validations()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<DomainException>(() => Message.Create(convId, senderId, "", "Hello", now));
        Assert.Throws<DomainException>(() => Message.Create(convId, senderId, "key", "   ", now));

        var msg = Message.Create(convId, senderId, "key-123", "Hello World", now);
        Assert.Equal(convId, msg.ConversationId);
        Assert.Equal(senderId, msg.SenderId);
        Assert.Equal("key-123", msg.IdempotencyKey);
        Assert.Equal("Hello World", msg.Content);
        Assert.Equal(now, msg.CreatedAtUtc);
        Assert.False(msg.IsDeletedForEveryone);
        Assert.Null(msg.EditedAtUtc);
    }

    [Fact]
    public void Message_Edit_Rules()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var now = DateTimeOffset.UtcNow;
        var msg = Message.Create(convId, senderId, "key-1", "Original", now);

        var editTime = now.AddMinutes(10);
        msg.Edit("Updated Content", editTime);
        Assert.Equal("Updated Content", msg.Content);
        Assert.Equal(editTime, msg.EditedAtUtc);

        Assert.Throws<DomainException>(() => msg.Edit("", editTime.AddMinutes(1)));

        msg.DeleteForEveryone(senderId, editTime.AddMinutes(5));
        Assert.Throws<DomainException>(() => msg.Edit("Cannot edit after delete", editTime.AddMinutes(10)));
    }

    [Fact]
    public void Message_DeleteForEveryone_Rules()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var otherUser = UserId.New();
        var now = DateTimeOffset.UtcNow;
        var msg = Message.Create(convId, senderId, "key-1", "Original", now);

        // Only sender can delete for everyone
        Assert.Throws<DomainException>(() => msg.DeleteForEveryone(otherUser, now.AddMinutes(10)));

        // Exceeding 3-hour window throws
        Assert.Throws<DomainException>(() => msg.DeleteForEveryone(senderId, now.AddHours(3).AddSeconds(1)));

        // Within 3-hour window succeeds
        var deleteTime = now.AddHours(2).AddMinutes(59);
        msg.DeleteForEveryone(senderId, deleteTime);

        Assert.True(msg.IsDeletedForEveryone);
        Assert.Equal(deleteTime, msg.DeletedForEveryoneAtUtc);
        Assert.Null(msg.Content);
    }

    [Fact]
    public void UserBlock_Create_And_Validations()
    {
        var user1 = UserId.New();
        var user2 = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<DomainException>(() => UserBlock.Create(user1, user1, now));

        var block = UserBlock.Create(user1, user2, now);
        Assert.Equal(user1, block.BlockerId);
        Assert.Equal(user2, block.BlockedUserId);
        Assert.Equal(now, block.CreatedAtUtc);
    }
}
