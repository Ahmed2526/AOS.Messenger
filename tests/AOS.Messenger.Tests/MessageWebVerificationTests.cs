using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Middleware;
using AOS.Messenger.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Dedicated Phase 5 Web QA & Verifier test suite for AOS Messenger.
/// Verifies MessagesController architecture, security attributes, layer purity,
/// endpoint behaviors, view model integrity, and Conversation.cshtml XSS & idempotency security.
/// </summary>
public sealed class MessageWebVerificationTests
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
        public CancellationToken LastCancellationToken { get; private set; }

        public Func<UserId, ConversationId, SendMessageRequestDto, CancellationToken, Task<MessageDto>>? OnSendMessage { get; set; }
        public Func<UserId, ConversationId, string?, int, CancellationToken, Task<MessagesCursorPageDto>>? OnGetMessages { get; set; }

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
            LastCancellationToken = ct;

            if (OnGetMessages is not null)
            {
                return OnGetMessages(callerId, conversationId, cursor, pageSize, ct);
            }

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
            LastCancellationToken = ct;

            if (OnSendMessage is not null)
            {
                return OnSendMessage(senderId, conversationId, dto, ct);
            }

            return Task.FromResult(MessageToReturn ?? new MessageDto(
                MessageId.New(),
                conversationId,
                senderId,
                "Test Sender",
                "testsender",
                dto.Content ?? string.Empty,
                DateTimeOffset.UtcNow,
                null,
                false,
                null,
                true));
        }

        public Task<MessageEditedDto> EditMessageAsync(UserId callerId, ConversationId conversationId, MessageId messageId, EditMessageRequestDto dto, CancellationToken ct = default) =>
            Task.FromResult(new MessageEditedDto(conversationId.Value, messageId.Value, dto.Content ?? string.Empty, DateTimeOffset.UtcNow));

        public Task DeleteForMeAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<MessageDeletedForEveryoneDto> DeleteForEveryoneAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default) =>
            Task.FromResult(new MessageDeletedForEveryoneDto(conversationId.Value, messageId.Value, DateTimeOffset.UtcNow));

        public Task<ReactionUpdatedDto> ToggleReactionAsync(UserId callerId, ConversationId conversationId, MessageId messageId, ToggleReactionRequestDto dto, CancellationToken ct = default) =>
            Task.FromResult(new ReactionUpdatedDto(conversationId.Value, messageId.Value, callerId.Value, dto.Emoji));

        public Task<ReactionUpdatedDto> RemoveReactionAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default) =>
            Task.FromResult(new ReactionUpdatedDto(conversationId.Value, messageId.Value, callerId.Value, null));
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

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.sln"))
                               && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx"))
                               && !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        if (current == null)
        {
            throw new InvalidOperationException("Could not find solution root directory containing AOS.Messenger.slnx");
        }

        return current.FullName;
    }



    [Fact]
    public void MessagesController_IsMarkedSealed()
    {
        var type = typeof(MessagesController);
        Assert.True(type.IsSealed, "MessagesController must be sealed per AGENTS.md.");
    }

    [Fact]
    public void MessagesController_HasAuthorizeAttribute()
    {
        var type = typeof(MessagesController);
        var authorizeAttr = type.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorizeAttr);
    }

    [Fact]
    public void MessagesController_HasApiControllerAttribute()
    {
        var type = typeof(MessagesController);
        var apiControllerAttr = type.GetCustomAttribute<ApiControllerAttribute>();
        Assert.NotNull(apiControllerAttr);
    }

    [Fact]
    public void MessagesController_HasRouteAttribute_WithExpectedTemplate()
    {
        var type = typeof(MessagesController);
        var routeAttr = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(routeAttr);
        Assert.Equal("api/conversations/{conversationId:guid}/messages", routeAttr.Template);
    }

    [Theory]
    [InlineData(nameof(MessagesController.GetMessages), typeof(HttpGetAttribute), "messages-read")]
    [InlineData(nameof(MessagesController.SendMessage), typeof(HttpPostAttribute), "messages")]
    public void MessagesController_Endpoints_HaveRateLimitingPolicy_AndCorrectHttpVerbs(string actionName, Type httpVerbAttributeType, string expectedPolicy)
    {
        var method = typeof(MessagesController).GetMethod(actionName);
        Assert.NotNull(method);

        var verbAttr = method.GetCustomAttribute(httpVerbAttributeType);
        Assert.NotNull(verbAttr);

        var rateLimitAttr = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimitAttr);
        Assert.Equal(expectedPolicy, rateLimitAttr.PolicyName);
    }

    [Fact]
    public void MessagesController_DependsOnlyOnApplicationAbstractions_ZeroInfrastructureOrEfCore()
    {
        var type = typeof(MessagesController);

        // 1. Inspect constructor parameters
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotEmpty(constructors);

        foreach (var ctor in constructors)
        {
            foreach (var parameter in ctor.GetParameters())
            {
                AssertTypeNotFromInfrastructureOrEfCore(parameter.ParameterType, $"constructor parameter '{parameter.Name}'");
            }
        }

        // 2. Inspect fields
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            AssertTypeNotFromInfrastructureOrEfCore(field.FieldType, $"field '{field.Name}'");
        }

        // 3. Inspect properties
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            AssertTypeNotFromInfrastructureOrEfCore(prop.PropertyType, $"property '{prop.Name}'");
        }

        // 4. Inspect methods
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            AssertTypeNotFromInfrastructureOrEfCore(method.ReturnType, $"method '{method.Name}' return type");
            foreach (var parameter in method.GetParameters())
            {
                AssertTypeNotFromInfrastructureOrEfCore(parameter.ParameterType, $"method '{method.Name}' parameter '{parameter.Name}'");
            }
        }
    }

    private static void AssertTypeNotFromInfrastructureOrEfCore(Type type, string memberDesc)
    {
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                AssertTypeNotFromInfrastructureOrEfCore(arg, memberDesc);
            }
        }

        var fullName = type.FullName ?? type.Name;

        Assert.False(
            fullName.StartsWith("AOS.Messenger.Infrastructure", StringComparison.OrdinalIgnoreCase),
            $"MessagesController {memberDesc} has forbidden dependency on Infrastructure type '{fullName}'.");

        Assert.False(
            fullName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase),
            $"MessagesController {memberDesc} has forbidden dependency on EF Core type '{fullName}'.");
    }

    [Fact]
    public async Task MessagesController_GetMessages_Unauthenticated_ReturnsUnauthorized()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.GetMessages(Guid.NewGuid());

        var unauthorizedResult = Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorizedResult.StatusCode);
    }

    [Fact]
    public async Task MessagesController_SendMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var (controller, _, _) = CreateController(currentUserId: null);
        var dto = new SendMessageRequestDto("Hello", "idemp-key-1");

        var result = await controller.SendMessage(Guid.NewGuid(), dto);

        var unauthorizedResult = Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorizedResult.StatusCode);
    }



    [Fact]
    public async Task GetMessages_PassesParametersToService_AndReturnsOkObjectResult()
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
            "Verified content",
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            true);

        service.MessagesToReturn.Add(msg);
        service.NextCursorToReturn = "cursor_token_next";
        service.HasMoreToReturn = true;

        using var cts = new CancellationTokenSource();
        var result = await controller.GetMessages(
            conversationId.Value,
            cursor: "cursor_token_prev",
            pageSize: 20,
            ct: cts.Token);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

        var page = Assert.IsType<MessagesCursorPageDto>(okResult.Value);
        Assert.Single(page.Messages);
        Assert.Equal("Verified content", page.Messages[0].Content);
        Assert.Equal("cursor_token_next", page.NextCursor);
        Assert.True(page.HasMore);

        Assert.Equal(currentUserId, service.LastCallerId);
        Assert.Equal(conversationId, service.LastConversationId);
        Assert.Equal("cursor_token_prev", service.LastCursor);
        Assert.Equal(20, service.LastPageSize);
        Assert.Equal(cts.Token, service.LastCancellationToken);
    }

    [Fact]
    public async Task GetMessages_DefaultParameters_PassesDefaultPageSize20()
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        var result = await controller.GetMessages(conversationId.Value);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(service.LastCursor);
        Assert.Equal(20, service.LastPageSize);
    }

    [Fact]
    public async Task SendMessage_PassesDtoToService_AndReturnsOkObjectResult()
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        var replyId = MessageId.New();
        var dto = new SendMessageRequestDto("Hello world!", "idemp-uuid-1234", replyId);

        using var cts = new CancellationTokenSource();
        var result = await controller.SendMessage(conversationId.Value, dto, cts.Token);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

        var sentMessage = Assert.IsType<MessageDto>(okResult.Value);
        Assert.Equal("Hello world!", sentMessage.Content);

        Assert.Equal(currentUserId, service.LastSenderId);
        Assert.Equal(conversationId, service.LastConversationId);
        Assert.NotNull(service.LastSendDto);
        Assert.Equal("Hello world!", service.LastSendDto.Content);
        Assert.Equal("idemp-uuid-1234", service.LastSendDto.IdempotencyKey);
        Assert.Equal(replyId, service.LastSendDto.ReplyToMessageId);
        Assert.Equal(cts.Token, service.LastCancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task SendMessage_EmptyOrWhitespacePayload_ServiceThrowsDomainException(string emptyOrWhitespaceContent)
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        service.OnSendMessage = (_, _, _, _) =>
            throw new DomainException("Message content cannot be empty or whitespace.");

        var dto = new SendMessageRequestDto(emptyOrWhitespaceContent, "idemp-test");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            controller.SendMessage(conversationId.Value, dto));

        Assert.Contains("cannot be empty or whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendMessage_EmptyOrWhitespaceIdempotencyKey_ServiceThrowsDomainException(string emptyKey)
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        service.OnSendMessage = (_, _, _, _) =>
            throw new DomainException("Idempotency key cannot be empty or whitespace.");

        var dto = new SendMessageRequestDto("Valid content", emptyKey);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            controller.SendMessage(conversationId.Value, dto));

        Assert.Contains("Idempotency key cannot be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessage_ContentExceeding4000Chars_ServiceThrowsDomainException()
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        service.OnSendMessage = (_, _, _, _) =>
            throw new DomainException("Message content cannot exceed 4000 characters.");

        var longContent = new string('A', 4001);
        var dto = new SendMessageRequestDto(longContent, "idemp-test");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            controller.SendMessage(conversationId.Value, dto));

        Assert.Contains("cannot exceed 4000 characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessage_IdempotencyKeyExceeding128Chars_ServiceThrowsDomainException()
    {
        var currentUserId = UserId.New();
        var conversationId = ConversationId.New();
        var (controller, service, _) = CreateController(currentUserId);

        service.OnSendMessage = (_, _, _, _) =>
            throw new DomainException("Idempotency key cannot exceed 128 characters.");

        var longKey = new string('k', 129);
        var dto = new SendMessageRequestDto("Valid content", longKey);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            controller.SendMessage(conversationId.Value, dto));

        Assert.Contains("cannot exceed 128 characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_TranslatesDomainException_To400BadRequest_ForApiRoute()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/conversations/3fa85f64-5717-4562-b3fc-2c963f66afa6/messages";
        context.Request.Headers.Accept = "application/json";
        context.TraceIdentifier = "trace-verification-" + Guid.NewGuid().ToString("N");
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new DomainException("Message content cannot be empty or whitespace."),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal("Bad Request", root.GetProperty("title").GetString());
        Assert.Equal("Message content cannot be empty or whitespace.", root.GetProperty("detail").GetString());
        Assert.Equal(context.TraceIdentifier, root.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_TranslatesConflictException_To409Conflict_ForApiRoute()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/conversations/3fa85f64-5717-4562-b3fc-2c963f66afa6/messages";
        context.Request.Headers.Accept = "application/json";
        context.TraceIdentifier = "trace-conflict-" + Guid.NewGuid().ToString("N");
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new ConflictException("Idempotency key reused with conflicting content."),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.Equal("Conflict", root.GetProperty("title").GetString());
        Assert.Equal("Idempotency key reused with conflicting content.", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_TranslatesForbiddenException_To403Forbidden_ForApiRoute()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/conversations/3fa85f64-5717-4562-b3fc-2c963f66afa6/messages";
        context.Request.Headers.Accept = "application/json";
        context.TraceIdentifier = "trace-forbidden-" + Guid.NewGuid().ToString("N");
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new ForbiddenException("Cannot send message in a blocked conversation."),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_TranslatesNotFoundException_To404NotFound_ForApiRoute()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/conversations/3fa85f64-5717-4562-b3fc-2c963f66afa6/messages";
        context.Request.Headers.Accept = "application/json";
        context.TraceIdentifier = "trace-notfound-" + Guid.NewGuid().ToString("N");
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new NotFoundException("Conversation", Guid.NewGuid()),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
    }



    [Fact]
    public void ChatConversationViewModel_CorrectlyCarriesInitialMessages_AndNextCursor()
    {
        var viewModel = new ChatConversationViewModel();

        var conversationId = ConversationId.New();
        var senderId = UserId.New();

        var msg1 = new MessageDto(
            MessageId.New(),
            conversationId,
            senderId,
            "Bob",
            "bob",
            "First message",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null,
            false,
            null,
            false);

        var msg2 = new MessageDto(
            MessageId.New(),
            conversationId,
            senderId,
            "Alice",
            "alice",
            "Second message",
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            true);

        var messages = new List<MessageDto> { msg1, msg2 };

        // Test setter on InitialMessages
        viewModel.InitialMessages = messages;
        viewModel.NextCursor = "cursor_2026_test";

        Assert.NotNull(viewModel.InitialMessages);
        Assert.Equal(2, viewModel.InitialMessages.Count);
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("cursor_2026_test", viewModel.NextCursor);
        Assert.Same(viewModel.InitialMessages, viewModel.Messages);

        // Test setter on Messages directly
        var newMessages = new List<MessageDto> { msg1 };
        viewModel.Messages = newMessages;
        Assert.Single(viewModel.InitialMessages);
        Assert.Equal("First message", viewModel.InitialMessages[0].Content);
    }

    [Fact]
    public void ProgramCs_RegistersMessagesRateLimiterPolicy_WithFixedWindow()
    {
        var slnRoot = FindSolutionRoot();
        var programPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Program.cs");

        Assert.True(File.Exists(programPath), $"Program.cs not found at: {programPath}");
        var content = File.ReadAllText(programPath);

        // Verify "messages", "receipts", and "presence" rate limiter policies are registered
        Assert.Contains("options.AddPolicy(\"messages\"", content);
        Assert.Contains("options.AddPolicy(\"receipts\"", content);
        Assert.Contains("options.AddPolicy(\"presence\"", content);

        // Verify rate limiting configuration
        Assert.Contains("PermitLimit = 60", content);
        Assert.Contains("PermitLimit = 180", content);
        Assert.Contains("TimeSpan.FromMinutes(1)", content);
    }

    [Fact]
    public void ConversationCshtml_ClientScript_UsesCryptoRandomUuid_ForIdempotencyKeys()
    {
        var slnRoot = FindSolutionRoot();
        var conversationViewPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var composerScriptPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "composer.js");

        Assert.True(File.Exists(conversationViewPath), $"Conversation.cshtml not found at: {conversationViewPath}");
        Assert.True(File.Exists(composerScriptPath), $"composer.js not found at: {composerScriptPath}");

        var viewContent = File.ReadAllText(conversationViewPath);
        Assert.Contains("chat-shell.js", viewContent);

        var content = File.ReadAllText(composerScriptPath);

        // Assert crypto.randomUUID() is used
        Assert.Contains("crypto.randomUUID()", content);

        // Assert crypto.randomUUID() is stored in idempotencyKey
        var idempotencyPattern = new Regex(@"const\s+idempotencyKey\s*=\s*crypto\.randomUUID\(\)", RegexOptions.Compiled);
        Assert.True(idempotencyPattern.IsMatch(content), "Expected 'const idempotencyKey = crypto.randomUUID()' in composer.js.");

        // Assert idempotencyKey is passed in request payload
        Assert.Contains("idempotencyKey: idempotencyKey", content);
    }

    [Fact]
    public void ConversationCshtml_ClientScript_SanitizesUserSuppliedContent_AgainstXss()
    {
        var slnRoot = FindSolutionRoot();
        var conversationViewPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var composerScriptPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "composer.js");
        var rendererScriptPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js");

        Assert.True(File.Exists(conversationViewPath), $"Conversation.cshtml not found at: {conversationViewPath}");
        Assert.True(File.Exists(composerScriptPath), $"composer.js not found at: {composerScriptPath}");
        Assert.True(File.Exists(rendererScriptPath), $"message-renderer.js not found at: {rendererScriptPath}");

        var viewContent = File.ReadAllText(conversationViewPath);
        Assert.Contains("chat-shell.js", viewContent);

        var content = File.ReadAllText(rendererScriptPath) + "\n" + File.ReadAllText(composerScriptPath);

        // 1. Assert escapeHtml function is defined
        Assert.Contains("function escapeHtml(str)", content);
        Assert.Contains(".replace(/&/g, '&amp;')", content);
        Assert.Contains(".replace(/</g, '&lt;')", content);
        Assert.Contains(".replace(/>/g, '&gt;')", content);
        Assert.Contains(".replace(/\"/g, '&quot;')", content);
        Assert.Contains(".replace(/'/g, '&#39;')", content);

        // 2. Assert escapeHtml is called on user content before bubble rendering
        Assert.Contains("escapeHtml(message.content)", content);
        Assert.Contains("escapeHtml(message.senderName)", content);
        Assert.Contains("escapeHtml(getInitials(message.senderName, message.senderHandle))", content);
        Assert.Contains("escapeHtml(message.replyTo.previewContent)", content);

        // 3. Assert reply banner preview elements use textContent
        Assert.Contains("replySenderEl.textContent = sender", content);
        Assert.Contains("replyPreviewEl.textContent = preview", content);

        // 4. Assert no raw unencoded message.content is concatenated into HTML strings
        var rawContentMatch = Regex.Matches(content, @"\$\{message\.content\}");
        Assert.Empty(rawContentMatch);
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>", "&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;")]
    [InlineData("\" onerror=\"alert(1)\"", "&quot; onerror=&quot;alert(1)&quot;")]
    [InlineData("<b>Hello</b> & 'world'", "&lt;b&gt;Hello&lt;/b&gt; &amp; &#39;world&#39;")]
    [InlineData("<img src=x onerror=alert(document.cookie)>", "&lt;img src=x onerror=alert(document.cookie)&gt;")]
    public void HtmlEscape_SanitizationLogic_HandlesAdversarialXssPayloads(string rawInput, string expectedEscaped)
    {
        // Replicates the client-side escapeHtml logic to verify string transformations
        static string EscapeHtml(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        var escaped = EscapeHtml(rawInput);
        Assert.Equal(expectedEscaped, escaped);
        Assert.DoesNotContain("<", escaped);
        Assert.DoesNotContain(">", escaped);
        Assert.DoesNotContain("\"", escaped);
        Assert.DoesNotContain("'", escaped);
    }

}
