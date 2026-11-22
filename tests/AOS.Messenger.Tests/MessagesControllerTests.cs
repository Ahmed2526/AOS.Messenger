using System.Reflection;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class MessagesControllerTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated { get; set; }
    }

    private sealed class FakeMessageService : IMessageService
    {
        public List<MessageDto> MessagesToReturn { get; } = new();
        public string? NextCursorToReturn { get; set; }
        public bool HasMoreToReturn { get; set; }
        public MessageDto? MessageToReturn { get; set; }

        public UserId? LastCallerId { get; private set; }
        public UserId? LastSenderId { get; private set; }
        public ConversationId? LastConversationId { get; private set; }
        public string? LastCursor { get; private set; }
        public int? LastPageSize { get; private set; }
        public SendMessageRequestDto? LastSendDto { get; private set; }

        public Task<MessagesCursorPageDto> GetMessagesAsync(
            UserId callerId,
            ConversationId conversationId,
            string? cursor,
            int pageSize = 50,
            CancellationToken ct = default)
        {
            LastCallerId = callerId;
            LastConversationId = conversationId;
            LastCursor = cursor;
            LastPageSize = pageSize;

            return Task.FromResult(new MessagesCursorPageDto(
                MessagesToReturn,
                NextCursorToReturn,
                HasMoreToReturn));
        }

        public Task<MessageDto> SendMessageAsync(
            UserId senderId,
            ConversationId conversationId,
            SendMessageRequestDto dto,
            CancellationToken ct = default)
        {
            LastSenderId = senderId;
            LastConversationId = conversationId;
            LastSendDto = dto;

            return Task.FromResult(MessageToReturn ?? new MessageDto(
                MessageId.New(),
                conversationId,
                senderId,
                "Test Sender",
                "testsender",
                dto.Content,
                DateTimeOffset.UtcNow,
                null,
                false,
                null,
                true));
        }

        public Task<MessageEditedDto> EditMessageAsync(UserId callerId, ConversationId conversationId, MessageId messageId, EditMessageRequestDto dto, CancellationToken ct = default)
            => Task.FromResult(new MessageEditedDto(conversationId.Value, messageId.Value, dto.Content, DateTimeOffset.UtcNow));

        public Task DeleteForMeAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<MessageDeletedForEveryoneDto> DeleteForEveryoneAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default)
            => Task.FromResult(new MessageDeletedForEveryoneDto(conversationId.Value, messageId.Value, DateTimeOffset.UtcNow));

        public Task<ReactionUpdatedDto> ToggleReactionAsync(UserId callerId, ConversationId conversationId, MessageId messageId, ToggleReactionRequestDto dto, CancellationToken ct = default)
            => Task.FromResult(new ReactionUpdatedDto(conversationId.Value, messageId.Value, callerId.Value, dto.Emoji));

        public Task<ReactionUpdatedDto> RemoveReactionAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default)
            => Task.FromResult(new ReactionUpdatedDto(conversationId.Value, messageId.Value, callerId.Value, null));
    }

    private static (MessagesController Controller, FakeMessageService MessageService, FakeCurrentUser CurrentUser) CreateController(
        UserId? currentUserId = null)
    {
        var messageService = new FakeMessageService();
        var currentUser = new FakeCurrentUser
        {
            Id = currentUserId,
            IsAuthenticated = currentUserId is not null
        };

        var httpContext = new DefaultHttpContext();
        var controller = new MessagesController(messageService, currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return (controller, messageService, currentUser);
    }

    [Fact]
    public void Controller_IsSealed_AndHasExpectedAttributes()
    {
        var type = typeof(MessagesController);
        Assert.True(type.IsSealed, "MessagesController must be sealed.");

        var authorizeAttr = type.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorizeAttr);

        var apiControllerAttr = type.GetCustomAttribute<ApiControllerAttribute>();
        Assert.NotNull(apiControllerAttr);

        var routeAttr = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(routeAttr);
        Assert.Equal("api/conversations/{conversationId:guid}/messages", routeAttr.Template);
    }

    [Theory]
    [InlineData(nameof(MessagesController.GetMessages), typeof(HttpGetAttribute))]
    [InlineData(nameof(MessagesController.SendMessage), typeof(HttpPostAttribute))]
    public void Endpoints_HaveRateLimiting_AndCorrectHttpVerb(string actionName, Type httpVerbAttributeType)
    {
        var method = typeof(MessagesController).GetMethod(actionName);
        Assert.NotNull(method);

        var verbAttr = method.GetCustomAttribute(httpVerbAttributeType);
        Assert.NotNull(verbAttr);

        var rateLimitAttr = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimitAttr);
        Assert.Equal("messages", rateLimitAttr.PolicyName);
    }

    [Fact]
    public async Task GetMessages_ReturnsUnauthorized_WhenCurrentUserIsNull()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.GetMessages(Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetMessages_ReturnsOk_WithMessagesPage()
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        var msg = new MessageDto(
            MessageId.New(),
            conversationId,
            currentUserId,
            "Alice",
            "alice",
            "Hey there!",
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            true);
        service.MessagesToReturn.Add(msg);
        service.NextCursorToReturn = "cursor-abc";
        service.HasMoreToReturn = true;

        var result = await controller.GetMessages(conversationId.Value, cursor: "cursor-123", pageSize: 25);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<MessagesCursorPageDto>(okResult.Value);

        Assert.Single(page.Messages);
        Assert.Equal("cursor-abc", page.NextCursor);
        Assert.True(page.HasMore);

        Assert.Equal(currentUserId, service.LastCallerId);
        Assert.Equal(conversationId, service.LastConversationId);
        Assert.Equal("cursor-123", service.LastCursor);
        Assert.Equal(25, service.LastPageSize);
    }

    [Fact]
    public async Task SendMessage_ReturnsUnauthorized_WhenCurrentUserIsNull()
    {
        var (controller, _, _) = CreateController(currentUserId: null);
        var dto = new SendMessageRequestDto("Hello", "idemp-1");

        var result = await controller.SendMessage(Guid.NewGuid(), dto);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task SendMessage_CallsService_AndReturnsOkWithSentMessage()
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        var replyId = MessageId.New();
        var dto = new SendMessageRequestDto("Test message", "idemp-key-xyz", replyId);

        var result = await controller.SendMessage(conversationId.Value, dto);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var sentMessage = Assert.IsType<MessageDto>(okResult.Value);

        Assert.Equal("Test message", sentMessage.Content);
        Assert.Equal(currentUserId, service.LastSenderId);
        Assert.Equal(conversationId, service.LastConversationId);
        Assert.NotNull(service.LastSendDto);
        Assert.Equal("Test message", service.LastSendDto.Content);
        Assert.Equal("idemp-key-xyz", service.LastSendDto.IdempotencyKey);
        Assert.Equal(replyId, service.LastSendDto.ReplyToMessageId);
    }
}
