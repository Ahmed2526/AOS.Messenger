using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/conversations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AutoValidateAntiforgeryToken]
[EnableRateLimiting("messages")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversationService;
    private readonly IMessageService _messageService;
    private readonly ICurrentUser _currentUser;

    public ConversationsController(
        IConversationService conversationService,
        IMessageService messageService,
        ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(conversationService);
        ArgumentNullException.ThrowIfNull(messageService);
        ArgumentNullException.ThrowIfNull(currentUser);

        _conversationService = conversationService;
        _messageService = messageService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> GetConversations(
        [FromQuery] bool includeArchived = false,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var conversations = await _conversationService.GetConversationListAsync(
            _currentUser.Id,
            includeArchived,
            ct);

        return Ok(conversations);
    }

    [HttpGet("{conversationId:guid}/bundle")]
    public async Task<IActionResult> GetConversationBundle(
        Guid conversationId,
        [FromQuery] int pageSize = 30,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var convId = new ConversationId(conversationId);
        var conversation = await _conversationService.GetConversationAsync(_currentUser.Id, convId, ct);
        var page = await _messageService.GetMessagesAsync(_currentUser.Id, convId, cursor: null, pageSize, ct);
        var orderedMessages = page.Messages
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .ToList();

        return Ok(new ConversationBundleDto(
            _currentUser.Id,
            conversation,
            orderedMessages,
            page.NextCursor,
            page.HasMore));
    }
}
