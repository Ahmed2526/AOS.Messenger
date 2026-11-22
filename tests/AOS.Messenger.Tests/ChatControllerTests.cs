using System.Reflection;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ChatControllerTests
{
    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated { get; set; }
    }

    private sealed class FakeConversationService : IConversationService
    {
        public List<ConversationSummaryDto> SummariesToReturn { get; } = new();
        public ConversationDetailDto? DetailToReturn { get; set; }
        public Exception? ExceptionToThrowOnGetConversation { get; set; }
        public Exception? ExceptionToThrowOnGetOrCreate { get; set; }

        public UserId? LastCurrentUserId { get; private set; }
        public UserId? LastTargetUserId { get; private set; }
        public ConversationId? LastConversationId { get; private set; }
        public bool? LastIsPinned { get; private set; }
        public bool? LastIsArchived { get; private set; }
        public bool? LastIsMuted { get; private set; }
        public bool? LastIncludeArchived { get; private set; }

        public Task<ConversationDetailDto> GetOrCreateConversationAsync(
            UserId currentUserId,
            UserId targetUserId,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastTargetUserId = targetUserId;
            if (ExceptionToThrowOnGetOrCreate is not null)
            {
                throw ExceptionToThrowOnGetOrCreate;
            }

            return Task.FromResult(DetailToReturn ?? new ConversationDetailDto(
                ConversationId.New(),
                targetUserId,
                "Target User",
                "target",
                null,
                false,
                false,
                false,
                false,
                DateTimeOffset.UtcNow));
        }

        public Task<ConversationDetailDto> GetConversationAsync(
            UserId currentUserId,
            ConversationId conversationId,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastConversationId = conversationId;
            if (ExceptionToThrowOnGetConversation is not null)
            {
                throw ExceptionToThrowOnGetConversation;
            }

            return Task.FromResult(DetailToReturn ?? new ConversationDetailDto(
                conversationId,
                UserId.New(),
                "Target User",
                "target",
                null,
                false,
                false,
                false,
                false,
                DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(
            UserId currentUserId,
            bool includeArchived = false,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastIncludeArchived = includeArchived;
            return Task.FromResult<IReadOnlyList<ConversationSummaryDto>>(SummariesToReturn);
        }

        public Task<bool> PinConversationAsync(
            UserId currentUserId,
            ConversationId conversationId,
            bool isPinned,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastConversationId = conversationId;
            LastIsPinned = isPinned;
            return Task.FromResult(true);
        }

        public Task<bool> ArchiveConversationAsync(
            UserId currentUserId,
            ConversationId conversationId,
            bool isArchived,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastConversationId = conversationId;
            LastIsArchived = isArchived;
            return Task.FromResult(true);
        }

        public Task<bool> MuteConversationAsync(
            UserId currentUserId,
            ConversationId conversationId,
            bool isMuted,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastConversationId = conversationId;
            LastIsMuted = isMuted;
            return Task.FromResult(true);
        }

        public Task<bool> ClearChatAsync(
            UserId currentUserId,
            ConversationId conversationId,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastConversationId = conversationId;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteConversationAsync(
            UserId currentUserId,
            ConversationId conversationId,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastConversationId = conversationId;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeMessageService : IMessageService
    {
        public List<MessageDto> MessagesToReturn { get; } = new();
        public string? NextCursorToReturn { get; set; }
        public MessageDto? SentMessageToReturn { get; set; }

        public UserId? LastSenderId { get; private set; }
        public ConversationId? LastConversationId { get; private set; }
        public SendMessageRequestDto? LastSendDto { get; private set; }
        public string? LastCursor { get; private set; }
        public int? LastPageSize { get; private set; }

        public Task<MessageDto> SendMessageAsync(
            UserId senderId,
            ConversationId conversationId,
            SendMessageRequestDto dto,
            CancellationToken ct = default)
        {
            LastSenderId = senderId;
            LastConversationId = conversationId;
            LastSendDto = dto;

            return Task.FromResult(SentMessageToReturn ?? new MessageDto(
                MessageId.New(),
                conversationId,
                senderId,
                "Sender",
                "sender",
                dto.Content,
                DateTimeOffset.UtcNow,
                null,
                false,
                null,
                true));
        }

        public Task<MessagesCursorPageDto> GetMessagesAsync(
            UserId callerId,
            ConversationId conversationId,
            string? cursor,
            int pageSize = 50,
            CancellationToken ct = default)
        {
            LastSenderId = callerId;
            LastConversationId = conversationId;
            LastCursor = cursor;
            LastPageSize = pageSize;

            return Task.FromResult(new MessagesCursorPageDto(
                MessagesToReturn,
                NextCursorToReturn,
                NextCursorToReturn != null));
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

    private static (ChatController Controller, FakeConversationService Service, FakeCurrentUser CurrentUser) CreateController(
        UserId? currentUserId = null)
    {
        var service = new FakeConversationService();
        var messageService = new FakeMessageService();
        var currentUser = new FakeCurrentUser
        {
            Id = currentUserId,
            IsAuthenticated = currentUserId is not null
        };

        var httpContext = new DefaultHttpContext();
        var controller = new ChatController(service, messageService, currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider())
        };

        return (controller, service, currentUser);
    }

    private static (ChatController Controller, FakeConversationService Service, FakeMessageService MessageService, FakeCurrentUser CurrentUser) CreateControllerWithMessages(
        UserId? currentUserId = null)
    {
        var service = new FakeConversationService();
        var messageService = new FakeMessageService();
        var currentUser = new FakeCurrentUser
        {
            Id = currentUserId,
            IsAuthenticated = currentUserId is not null
        };

        var httpContext = new DefaultHttpContext();
        var controller = new ChatController(service, messageService, currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider())
        };

        return (controller, service, messageService, currentUser);
    }

    [Fact]
    public void Controller_IsSealed_AndHasAuthorizeAttribute()
    {
        var type = typeof(ChatController);
        Assert.True(type.IsSealed);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(nameof(ChatController.Start))]
    [InlineData(nameof(ChatController.Pin))]
    [InlineData(nameof(ChatController.Archive))]
    [InlineData(nameof(ChatController.Mute))]
    [InlineData(nameof(ChatController.Clear))]
    [InlineData(nameof(ChatController.Delete))]
    [InlineData(nameof(ChatController.SendMessage))]
    public void PostActions_EnforceValidateAntiForgeryToken_AndHttpPost(string actionName)
    {
        var method = typeof(ChatController).GetMethod(actionName);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task Index_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Index();

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Index_ReturnsViewWithConversations_WhenAuthenticated()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var convId = ConversationId.New();
        service.SummariesToReturn.Add(new ConversationSummaryDto(
            convId,
            UserId.New(),
            "Alice Smith",
            "alice",
            "Hello there!",
            DateTimeOffset.UtcNow,
            0,
            false,
            false,
            false));

        var result = await controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatIndexViewModel>(viewResult.Model);
        Assert.Single(model.Conversations);
        Assert.Equal(convId, model.Conversations[0].Id);
        Assert.Null(model.SelectedConversationId);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.False(service.LastIncludeArchived);
    }

    [Fact]
    public async Task Conversation_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Conversation(Guid.NewGuid());

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Conversation_RedirectsToIndex_WhenIdIsEmpty()
    {
        var currentUserId = UserId.New();
        var (controller, _, _) = CreateController(currentUserId);

        var result = await controller.Conversation(Guid.Empty);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Index), redirect.ActionName);
    }

    [Fact]
    public async Task Conversation_ReturnsViewWithConversationDetailAndList_WhenAuthenticated()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var conversationId = ConversationId.New();
        var otherUserId = UserId.New();

        var detail = new ConversationDetailDto(
            conversationId,
            otherUserId,
            "Bob Jones",
            "bobjones",
            "Developer bio",
            false,
            true,
            false,
            false,
            DateTimeOffset.UtcNow);
        service.DetailToReturn = detail;

        var result = await controller.Conversation(conversationId.Value);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatConversationViewModel>(viewResult.Model);
        Assert.Equal(conversationId, model.Conversation.Id);
        Assert.Equal("Bob Jones", model.Conversation.OtherParticipantName);
        Assert.True(model.Conversation.IsPinned);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(conversationId, service.LastConversationId);
    }

    [Fact]
    public async Task Conversation_PropagatesNotFoundException_WhenNotFound()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var missingId = Guid.NewGuid();
        service.ExceptionToThrowOnGetConversation = new NotFoundException("Conversation", missingId);

        await Assert.ThrowsAsync<NotFoundException>(() => controller.Conversation(missingId));
    }

    [Fact]
    public async Task Conversation_PropagatesForbiddenException_WhenNonParticipant()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var forbiddenId = Guid.NewGuid();
        service.ExceptionToThrowOnGetConversation = new ForbiddenException("User is not a participant in this conversation.");

        await Assert.ThrowsAsync<ForbiddenException>(() => controller.Conversation(forbiddenId));
    }

    [Fact]
    public async Task Start_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Start(Guid.NewGuid());

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Start_CreatesConversation_AndRedirectsToConversation()
    {
        var currentUserId = UserId.New();
        var targetUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var createdConvId = ConversationId.New();

        service.DetailToReturn = new ConversationDetailDto(
            createdConvId,
            targetUserId,
            "Target User",
            "target",
            null,
            false,
            false,
            false,
            false,
            DateTimeOffset.UtcNow);

        var result = await controller.Start(targetUserId.Value);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Conversation), redirect.ActionName);
        Assert.Equal(createdConvId.Value, redirect.RouteValues?["id"]);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(targetUserId, service.LastTargetUserId);
    }

    [Fact]
    public async Task Pin_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Pin(Guid.NewGuid(), isPinned: true);

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Pin_CallsService_AndRedirectsToConversation()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var convGuid = Guid.NewGuid();

        var result = await controller.Pin(convGuid, isPinned: true);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Conversation), redirect.ActionName);
        Assert.Equal(convGuid, redirect.RouteValues?["id"]);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(new ConversationId(convGuid), service.LastConversationId);
        Assert.True(service.LastIsPinned);
    }

    [Fact]
    public async Task Archive_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Archive(Guid.NewGuid(), isArchived: true);

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Archive_RedirectsToIndex_WhenArchiving()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var convGuid = Guid.NewGuid();

        var result = await controller.Archive(convGuid, isArchived: true);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Index), redirect.ActionName);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(new ConversationId(convGuid), service.LastConversationId);
        Assert.True(service.LastIsArchived);
    }

    [Fact]
    public async Task Archive_RedirectsToArchived_WhenUnarchiving()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var convGuid = Guid.NewGuid();

        var result = await controller.Archive(convGuid, isArchived: false);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Archived), redirect.ActionName);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(new ConversationId(convGuid), service.LastConversationId);
        Assert.False(service.LastIsArchived);
    }

    [Fact]
    public async Task Mute_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Mute(Guid.NewGuid(), isMuted: true);

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Mute_CallsService_AndRedirectsToConversation()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var convGuid = Guid.NewGuid();

        var result = await controller.Mute(convGuid, isMuted: true);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Conversation), redirect.ActionName);
        Assert.Equal(convGuid, redirect.RouteValues?["id"]);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(new ConversationId(convGuid), service.LastConversationId);
        Assert.True(service.LastIsMuted);
    }

    [Fact]
    public async Task Clear_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Clear(Guid.NewGuid());

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Clear_CallsService_AndRedirectsToConversation()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var convGuid = Guid.NewGuid();

        var result = await controller.Clear(convGuid);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Conversation), redirect.ActionName);
        Assert.Equal(convGuid, redirect.RouteValues?["id"]);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(new ConversationId(convGuid), service.LastConversationId);
    }

    [Fact]
    public async Task Delete_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Delete(Guid.NewGuid());

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Delete_CallsService_AndRedirectsToIndex()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);
        var convGuid = Guid.NewGuid();

        var result = await controller.Delete(convGuid);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Index), redirect.ActionName);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
        Assert.Equal(new ConversationId(convGuid), service.LastConversationId);
    }

    [Fact]
    public async Task Archived_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Archived();

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Archived_ReturnsViewWithArchivedConversations()
    {
        var currentUserId = UserId.New();
        var (controller, service, _) = CreateController(currentUserId);

        var archivedConv = new ConversationSummaryDto(
            ConversationId.New(),
            UserId.New(),
            "Archived User",
            "archived",
            "Old message",
            DateTimeOffset.UtcNow,
            0,
            false,
            true, // IsArchived
            false);

        var activeConv = new ConversationSummaryDto(
            ConversationId.New(),
            UserId.New(),
            "Active User",
            "active",
            "Recent message",
            DateTimeOffset.UtcNow,
            0,
            false,
            false, // Not archived
            false);

        service.SummariesToReturn.Add(archivedConv);
        service.SummariesToReturn.Add(activeConv);

        var result = await controller.Archived();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ArchivedChatsViewModel>(viewResult.Model);
        Assert.Single(model.ArchivedConversations);
        Assert.Equal(archivedConv.Id, model.ArchivedConversations[0].Id);
        Assert.True(service.LastIncludeArchived);
        Assert.Equal(currentUserId, service.LastCurrentUserId);
    }

    [Fact]
    public void UtilityActions_ReturnViews()
    {
        var (controller, _, _) = CreateController(UserId.New());

        Assert.IsType<ViewResult>(controller.DarkTheme());
        Assert.IsType<ViewResult>(controller.Mobile());
        Assert.IsType<ViewResult>(controller.VoiceRecording());
        Assert.IsType<ViewResult>(controller.Attachments());
        Assert.IsType<ViewResult>(controller.NewConversation());
        Assert.IsType<ViewResult>(controller.DeleteModal());
        Assert.IsType<ViewResult>(controller.ContextMenu());
    }

    [Fact]
    public void New_RedirectsToNewConversation()
    {
        var (controller, _, _) = CreateController();
        var result = controller.New();
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.NewConversation), redirect.ActionName);
    }

    [Fact]
    public async Task SendMessage_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.SendMessage(Guid.NewGuid(), "Hello");

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task SendMessage_RedirectsToConversation_WhenContentIsEmpty()
    {
        var currentUserId = UserId.New();
        var (controller, _, msgService, _) = CreateControllerWithMessages(currentUserId);
        var convId = Guid.NewGuid();

        var result = await controller.SendMessage(convId, "   ");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Conversation), redirect.ActionName);
        Assert.Null(msgService.LastSendDto);
    }

    [Fact]
    public async Task SendMessage_SendsMessage_AndRedirectsToConversation()
    {
        var currentUserId = UserId.New();
        var (controller, _, msgService, _) = CreateControllerWithMessages(currentUserId);
        var convId = Guid.NewGuid();
        var replyId = 12345L;

        var result = await controller.SendMessage(convId, "Hello world!", replyId, "custom-key");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChatController.Conversation), redirect.ActionName);
        Assert.Equal(convId, redirect.RouteValues?["id"]);
        Assert.NotNull(msgService.LastSendDto);
        Assert.Equal("Hello world!", msgService.LastSendDto.Content);
        Assert.Equal("custom-key", msgService.LastSendDto.IdempotencyKey);
        Assert.Equal(new MessageId(replyId), msgService.LastSendDto.ReplyToMessageId);
        Assert.Equal(currentUserId, msgService.LastSenderId);
        Assert.Equal(new ConversationId(convId), msgService.LastConversationId);
    }

    [Fact]
    public void SendMessage_HasEnableRateLimitingAttribute_ForMessagesPolicy()
    {
        var method = typeof(ChatController).GetMethod(nameof(ChatController.SendMessage));
        Assert.NotNull(method);

        var rateLimitAttr = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimitAttr);
        Assert.Equal("messages", rateLimitAttr.PolicyName);
    }
}
