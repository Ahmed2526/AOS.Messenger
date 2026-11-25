using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AOS.Messenger.Tests;

/// <summary>
/// Verifies EF Core model configuration, indexes, primary keys, and value converters in ApplicationDbContext.
/// </summary>
public sealed class DbContextConfigurationTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=fake;Database=fake;")
            .Options;

        using var context = new ApplicationDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void ModelBuilding_SucceedsWithoutErrors()
    {
        var model = BuildModel();

        Assert.NotNull(model);
        Assert.NotNull(model.FindEntityType(typeof(ApplicationUser)));
        Assert.NotNull(model.FindEntityType(typeof(Conversation)));
        Assert.NotNull(model.FindEntityType(typeof(Message)));
        Assert.NotNull(model.FindEntityType(typeof(UserConversationState)));
        Assert.NotNull(model.FindEntityType(typeof(UserBlock)));
        Assert.NotNull(model.FindEntityType(typeof(Media)));
    }

    [Fact]
    public void Conversation_HasUniqueIndexOnParticipants()
    {
        var model = BuildModel();
        var entityType = model.FindEntityType(typeof(Conversation));
        Assert.NotNull(entityType);

        var index = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(Conversation.Participant1Id)) &&
            i.Properties.Any(p => p.Name == nameof(Conversation.Participant2Id)));

        Assert.NotNull(index);
        Assert.True(index.IsUnique, "Participant index must be unique to enforce direct conversation uniqueness.");
    }

    [Fact]
    public void Conversation_HasIndexOnParticipant2Id()
    {
        var model = BuildModel();
        var entityType = model.FindEntityType(typeof(Conversation));
        Assert.NotNull(entityType);

        var index = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 &&
            i.Properties[0].Name == nameof(Conversation.Participant2Id));

        Assert.NotNull(index);
    }

    [Fact]
    public void ApplicationUser_HasIndexOnEmailConfirmedAndCreatedAtUtc()
    {
        var model = BuildModel();
        var entityType = model.FindEntityType(typeof(Infrastructure.Identity.ApplicationUser));
        Assert.NotNull(entityType);

        var index = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 2 &&
            i.Properties[0].Name == nameof(Infrastructure.Identity.ApplicationUser.EmailConfirmed) &&
            i.Properties[1].Name == nameof(Infrastructure.Identity.ApplicationUser.CreatedAtUtc));

        Assert.NotNull(index);
    }

    [Fact]
    public void Message_HasUniqueIndexOnSenderIdAndIdempotencyKey()
    {
        var model = BuildModel();
        var entityType = model.FindEntityType(typeof(Message));
        Assert.NotNull(entityType);

        var index = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(Message.SenderId)) &&
            i.Properties.Any(p => p.Name == nameof(Message.IdempotencyKey)));

        Assert.NotNull(index);
        Assert.True(index.IsUnique, "SenderId and IdempotencyKey index must be unique to enforce message idempotency.");
    }

    [Fact]
    public void Message_HasKeysetOrderingIndexOnConversationIdCreatedAtUtcDescIdDesc()
    {
        var model = BuildModel();
        var entityType = model.FindEntityType(typeof(Message));
        Assert.NotNull(entityType);

        var index = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 3 &&
            i.Properties[0].Name == nameof(Message.ConversationId) &&
            i.Properties[1].Name == nameof(Message.CreatedAtUtc) &&
            i.Properties[2].Name == nameof(Message.Id));

        Assert.NotNull(index);

        var isDescending = index.IsDescending;
        Assert.NotNull(isDescending);
        Assert.Equal(3, isDescending.Count);
        Assert.False(isDescending[0], "ConversationId should be ascending");
        Assert.True(isDescending[1], "CreatedAtUtc should be descending for keyset pagination");
        Assert.True(isDescending[2], "Id should be descending for keyset pagination");
    }

    [Fact]
    public void CompositePrimaryKeys_ConfiguredForUserConversationStatesAndUserBlocks()
    {
        var model = BuildModel();

        // UserConversationState composite PK: (ConversationId, UserId)
        var stateType = model.FindEntityType(typeof(UserConversationState));
        Assert.NotNull(stateType);
        var statePk = stateType.FindPrimaryKey();
        Assert.NotNull(statePk);
        Assert.Equal(2, statePk.Properties.Count);
        var statePkNames = statePk.Properties.Select(p => p.Name).ToList();
        Assert.Contains(nameof(UserConversationState.ConversationId), statePkNames);
        Assert.Contains(nameof(UserConversationState.UserId), statePkNames);

        // UserBlock composite PK: (BlockerId, BlockedUserId)
        var blockType = model.FindEntityType(typeof(UserBlock));
        Assert.NotNull(blockType);
        var blockPk = blockType.FindPrimaryKey();
        Assert.NotNull(blockPk);
        Assert.Equal(2, blockPk.Properties.Count);
        var blockPkNames = blockPk.Properties.Select(p => p.Name).ToList();
        Assert.Contains(nameof(UserBlock.BlockerId), blockPkNames);
        Assert.Contains(nameof(UserBlock.BlockedUserId), blockPkNames);
    }

    [Fact]
    public void ValueConverters_ConfiguredForStronglyTypedIds()
    {
        var model = BuildModel();

        // Conversation value converters
        var convType = model.FindEntityType(typeof(Conversation));
        Assert.NotNull(convType);
        AssertConverter(convType, nameof(Conversation.Id), typeof(ConversationId), typeof(Guid));
        AssertConverter(convType, nameof(Conversation.Participant1Id), typeof(UserId), typeof(Guid));
        AssertConverter(convType, nameof(Conversation.Participant2Id), typeof(UserId), typeof(Guid));

        // Message value converters
        var msgType = model.FindEntityType(typeof(Message));
        Assert.NotNull(msgType);
        AssertConverter(msgType, nameof(Message.Id), typeof(MessageId), typeof(long));
        AssertConverter(msgType, nameof(Message.ConversationId), typeof(ConversationId), typeof(Guid));
        AssertConverter(msgType, nameof(Message.SenderId), typeof(UserId), typeof(Guid));
        AssertConverter(msgType, nameof(Message.ParentMessageId), typeof(MessageId), typeof(long));

        // UserConversationState value converters
        var stateType = model.FindEntityType(typeof(UserConversationState));
        Assert.NotNull(stateType);
        AssertConverter(stateType, nameof(UserConversationState.ConversationId), typeof(ConversationId), typeof(Guid));
        AssertConverter(stateType, nameof(UserConversationState.UserId), typeof(UserId), typeof(Guid));
        AssertConverter(stateType, nameof(UserConversationState.LastReadMessageId), typeof(MessageId), typeof(long));

        // UserBlock value converters
        var blockType = model.FindEntityType(typeof(UserBlock));
        Assert.NotNull(blockType);
        AssertConverter(blockType, nameof(UserBlock.BlockerId), typeof(UserId), typeof(Guid));
        AssertConverter(blockType, nameof(UserBlock.BlockedUserId), typeof(UserId), typeof(Guid));

        // Media value converters
        var mediaType = model.FindEntityType(typeof(Media));
        Assert.NotNull(mediaType);
        AssertConverter(mediaType, nameof(Media.Id), typeof(MediaId), typeof(Guid));
        AssertConverter(mediaType, nameof(Media.UploaderId), typeof(UserId), typeof(Guid));
        AssertConverter(mediaType, nameof(Media.ConversationId), typeof(ConversationId), typeof(Guid));
        AssertConverter(mediaType, nameof(Media.MessageId), typeof(MessageId), typeof(long));
    }

    [Fact]
    public void Media_HasNewSequentialIdClusteredPk_UniqueIndexes_AndMessageHasAttachments()
    {
        var model = BuildModel();

        var mediaType = model.FindEntityType(typeof(Media));
        Assert.NotNull(mediaType);

        var pk = mediaType.FindPrimaryKey();
        Assert.NotNull(pk);
        Assert.True(pk.IsClustered());

        var idProp = mediaType.FindProperty(nameof(Media.Id));
        Assert.NotNull(idProp);
        Assert.Equal("NEWSEQUENTIALID()", idProp.GetDefaultValueSql());

        var objectKeyIdx = mediaType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Media.ObjectKey));
        Assert.NotNull(objectKeyIdx);
        Assert.True(objectKeyIdx.IsUnique);

        var storageObjIdx = mediaType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Media.StorageObjectId));
        Assert.NotNull(storageObjIdx);
        Assert.True(storageObjIdx.IsUnique);

        var quotaIdx = mediaType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 2
            && i.Properties[0].Name == nameof(Media.UploaderId)
            && i.Properties[1].Name == nameof(Media.CreatedAtUtc));
        Assert.NotNull(quotaIdx);
        var includedProps = quotaIdx.GetIncludeProperties();
        Assert.NotNull(includedProps);
        Assert.Equal(
            new[] { nameof(Media.State), nameof(Media.DeclaredSizeBytes), nameof(Media.VerifiedSizeBytes) },
            includedProps);

        var msgType = model.FindEntityType(typeof(Message));
        Assert.NotNull(msgType);
        Assert.NotNull(msgType.FindProperty(nameof(Message.HasAttachments)));
    }

    [Fact]
    public void StickerAndStickerPack_HaveNewSequentialIdClusteredPk_AndTransientFactories()
    {
        var model = BuildModel();

        var packType = model.FindEntityType(typeof(StickerPack));
        Assert.NotNull(packType);
        var packPk = packType.FindPrimaryKey();
        Assert.NotNull(packPk);
        Assert.True(packPk.IsClustered());
        var packIdProp = packType.FindProperty(nameof(StickerPack.Id));
        Assert.NotNull(packIdProp);
        Assert.Equal("NEWSEQUENTIALID()", packIdProp.GetDefaultValueSql());
        AssertConverter(packType, nameof(StickerPack.Id), typeof(StickerPackId), typeof(Guid));

        var stickerType = model.FindEntityType(typeof(Sticker));
        Assert.NotNull(stickerType);
        var stickerPk = stickerType.FindPrimaryKey();
        Assert.NotNull(stickerPk);
        Assert.True(stickerPk.IsClustered());
        var stickerIdProp = stickerType.FindProperty(nameof(Sticker.Id));
        Assert.NotNull(stickerIdProp);
        Assert.Equal("NEWSEQUENTIALID()", stickerIdProp.GetDefaultValueSql());
        AssertConverter(stickerType, nameof(Sticker.Id), typeof(StickerId), typeof(Guid));

        var now = DateTimeOffset.UtcNow;
        var ownerId = UserId.New();

        var customPack = StickerPack.CreateCustom(ownerId, "My Pack", "Tester", 1, now);
        Assert.True(customPack.IsTransient());
        var systemPack = StickerPack.CreateSystem("test-pack", "Test Pack", "AOS", 1, now);
        Assert.True(systemPack.IsTransient());

        var explicitPackId = StickerPackId.New();
        var explicitPack = StickerPack.CreateCustom(ownerId, "Explicit Pack", "Tester", 2, now, explicitPackId);
        Assert.False(explicitPack.IsTransient());
        Assert.Equal(explicitPackId, explicitPack.Id);

        var pendingSticker = Sticker.CreatePendingCustom(explicitPackId, ownerId, "image/png", 1024, "👋", 1, now);
        Assert.True(pendingSticker.IsTransient());
        var systemSticker = Sticker.CreateSystem(explicitPackId, "/stickers/packs/test-pack/wave.webp", "👋", 1, 1024, now);
        Assert.True(systemSticker.IsTransient());

        var explicitStickerId = StickerId.New();
        var explicitSticker = Sticker.CreatePendingCustom(explicitPackId, ownerId, "image/png", 1024, "👋", 2, now, explicitStickerId);
        Assert.False(explicitSticker.IsTransient());
        Assert.Equal(explicitStickerId, explicitSticker.Id);
    }

    private static void AssertConverter(
        IEntityType entityType,
        string propertyName,
        Type modelType,
        Type providerType)
    {
        var property = entityType.FindProperty(propertyName);
        Assert.NotNull(property);

        var converter = property.GetValueConverter();
        Assert.NotNull(converter);
        Assert.True(
            modelType.IsAssignableFrom(converter.ModelClrType) || Nullable.GetUnderlyingType(converter.ModelClrType) == modelType,
            $"Property '{propertyName}' ModelClrType was '{converter.ModelClrType.Name}', expected '{modelType.Name}'");
        Assert.True(
            converter.ProviderClrType == providerType || Nullable.GetUnderlyingType(converter.ProviderClrType) == providerType,
            $"Property '{propertyName}' ProviderClrType was '{converter.ProviderClrType.Name}', expected '{providerType.Name}'");
    }

    [Fact]
    public void BatchedAndTieBreakerQueries_TranslateInSqlServer()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=fake;Database=fake;")
            .Options;
        using var context = new ApplicationDbContext(options);

        var convId1 = ConversationId.New();
        var convId2 = ConversationId.New();
        var userId = UserId.New();
        var msgId = MessageId.New();
        var time = DateTimeOffset.UtcNow;

        // 1. Single unread count query with tie breaker
        var unreadQuery = context.Messages
            .Where(m => m.ConversationId == convId1 && m.SenderId != userId &&
                (m.CreatedAtUtc > time || (m.CreatedAtUtc == time && m.Id > msgId)));
        var unreadSql = unreadQuery.ToQueryString();
        Assert.NotNull(unreadSql);
        Assert.Contains(">", unreadSql);

        // 2. Batched last message query
        var convIds = new List<ConversationId> { convId1, convId2 };
        var lastMessagesQuery = context.Messages
            .Where(m => convIds.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => g.OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).FirstOrDefault()!);
        var lastMessagesSql = lastMessagesQuery.ToQueryString();
        Assert.NotNull(lastMessagesSql);

        // 3. Batched messages after query
        var messagesAfterQuery = context.Messages
            .Where(m => convIds.Contains(m.ConversationId) && m.CreatedAtUtc > time)
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, MaxCreatedAtUtc = g.Max(m => m.CreatedAtUtc) });
        var messagesAfterSql = messagesAfterQuery.ToQueryString();
        Assert.NotNull(messagesAfterSql);
    }
}
