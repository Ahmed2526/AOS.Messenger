using System.Security.Claims;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AOS.Messenger.Tests;

public class InfrastructureTests
{
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    [Fact]
    public void CurrentUser_WhenHttpContextIsNull_ReturnsNotAuthenticatedAndNullId()
    {
        var accessor = new FakeHttpContextAccessor { HttpContext = null };
        var currentUser = new CurrentUser(accessor);

        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.Id);
    }

    [Fact]
    public void CurrentUser_WhenUserNotAuthenticated_ReturnsFalseAndNullId()
    {
        var context = new DefaultHttpContext();
        var accessor = new FakeHttpContextAccessor { HttpContext = context };
        var currentUser = new CurrentUser(accessor);

        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.Id);
    }

    [Fact]
    public void CurrentUser_WhenUserAuthenticatedWithValidGuid_ReturnsTrueAndParsedUserId()
    {
        var userIdGuid = Guid.NewGuid();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userIdGuid.ToString())],
            "TestAuthType");
        var principal = new ClaimsPrincipal(identity);

        var context = new DefaultHttpContext { User = principal };
        var accessor = new FakeHttpContextAccessor { HttpContext = context };
        var currentUser = new CurrentUser(accessor);

        Assert.True(currentUser.IsAuthenticated);
        Assert.NotNull(currentUser.Id);
        Assert.Equal(userIdGuid, currentUser.Id.Value);
    }

    [Fact]
    public void CurrentUser_WhenUserAuthenticatedWithInvalidGuid_ReturnsTrueAndNullUserId()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "invalid-guid")],
            "TestAuthType");
        var principal = new ClaimsPrincipal(identity);

        var context = new DefaultHttpContext { User = principal };
        var accessor = new FakeHttpContextAccessor { HttpContext = context };
        var currentUser = new CurrentUser(accessor);

        Assert.True(currentUser.IsAuthenticated);
        Assert.Null(currentUser.Id);
    }

    [Fact]
    public void ApplicationDbContext_Model_ConfiguresEntitiesCorrectly()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=fake;Database=fake;")
            .Options;

        using var context = new ApplicationDbContext(options);
        var designModel = context.GetService<IDesignTimeModel>().Model;

        // Identity
        var userType = designModel.FindEntityType(typeof(ApplicationUser));
        Assert.NotNull(userType);

        // Conversation
        var conversationType = designModel.FindEntityType(typeof(Conversation));
        Assert.NotNull(conversationType);
        Assert.Equal("Conversations", conversationType.GetTableName());
        Assert.NotNull(conversationType.FindPrimaryKey());
        Assert.True(conversationType.FindProperty("RowVersion")?.IsConcurrencyToken);
        Assert.Contains(conversationType.GetCheckConstraints(), c => c.Name == "CK_Conversations_ParticipantOrdering");
        var convIndex = conversationType.GetIndexes().FirstOrDefault(i => i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == "Participant1Id") &&
            i.Properties.Any(p => p.Name == "Participant2Id"));
        Assert.NotNull(convIndex);
        Assert.True(convIndex.IsUnique);

        // UserConversationState
        var userStateType = designModel.FindEntityType(typeof(UserConversationState));
        Assert.NotNull(userStateType);
        Assert.Equal("UserConversationStates", userStateType.GetTableName());
        Assert.Equal(2, userStateType.FindPrimaryKey()?.Properties.Count);
        var stateIndex = userStateType.GetIndexes().FirstOrDefault(i => i.Properties.Count == 3 &&
            i.Properties.Any(p => p.Name == "UserId") &&
            i.Properties.Any(p => p.Name == "IsArchived") &&
            i.Properties.Any(p => p.Name == "IsPinned"));
        Assert.NotNull(stateIndex);
        var stateFk = userStateType.GetForeignKeys().FirstOrDefault(fk => fk.PrincipalEntityType == conversationType);
        Assert.NotNull(stateFk);
        Assert.Equal(DeleteBehavior.Cascade, stateFk.DeleteBehavior);

        // Message
        var messageType = designModel.FindEntityType(typeof(Message));
        Assert.NotNull(messageType);
        Assert.Equal("Messages", messageType.GetTableName());
        Assert.True(messageType.FindProperty("RowVersion")?.IsConcurrencyToken);
        var msgIdempotencyIndex = messageType.GetIndexes().FirstOrDefault(i => i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == "SenderId") &&
            i.Properties.Any(p => p.Name == "IdempotencyKey"));
        Assert.NotNull(msgIdempotencyIndex);
        Assert.True(msgIdempotencyIndex.IsUnique);
        var msgKeysetIndex = messageType.GetIndexes().FirstOrDefault(i => i.Properties.Count == 3 &&
            i.Properties.Any(p => p.Name == "ConversationId") &&
            i.Properties.Any(p => p.Name == "CreatedAtUtc") &&
            i.Properties.Any(p => p.Name == "Id"));
        Assert.NotNull(msgKeysetIndex);
        var msgConvFk = messageType.GetForeignKeys().FirstOrDefault(fk => fk.PrincipalEntityType == conversationType);
        Assert.NotNull(msgConvFk);
        Assert.Equal(DeleteBehavior.Cascade, msgConvFk.DeleteBehavior);
        var msgSelfFk = messageType.GetForeignKeys().FirstOrDefault(fk => fk.PrincipalEntityType == messageType);
        Assert.NotNull(msgSelfFk);
        Assert.Equal(DeleteBehavior.Restrict, msgSelfFk.DeleteBehavior);

        // UserBlock
        var blockType = designModel.FindEntityType(typeof(UserBlock));
        Assert.NotNull(blockType);
        Assert.Equal("UserBlocks", blockType.GetTableName());
        Assert.Equal(2, blockType.FindPrimaryKey()?.Properties.Count);
        Assert.Contains(blockType.GetCheckConstraints(), c => c.Name == "CK_UserBlocks_CannotBlockSelf");
    }
}
