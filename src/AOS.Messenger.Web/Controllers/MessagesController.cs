using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/conversations/{conversationId:guid}/messages")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AutoValidateAntiforgeryToken]
[EnableRateLimiting("messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly ICurrentUser _currentUser;

    public MessagesController(
        IMessageService messageService,
        ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(messageService);
        ArgumentNullException.ThrowIfNull(currentUser);

        _messageService = messageService;
        _currentUser = currentUser;
    }

    [HttpGet]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> GetMessages(
        Guid conversationId,
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 30,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var page = await _messageService.GetMessagesAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            cursor,
            pageSize,
            ct);

        return Ok(page);
    }

    [HttpPost]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> SendMessage(
        Guid conversationId,
        [FromBody] SendMessageRequestDto dto,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var message = await _messageService.SendMessageAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            dto,
            ct);

        return Ok(message);
    }

    [HttpPut("{messageId:long}")]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> EditMessage(
        Guid conversationId,
        long messageId,
        [FromBody] EditMessageRequestDto dto,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var convId = new ConversationId(conversationId);
        var msgId = new MessageId(messageId);

        var result = await _messageService.EditMessageAsync(_currentUser.Id, convId, msgId, dto, ct);

        return Ok(result);
    }

    [HttpPost("{messageId:long}/delete-for-me")]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> DeleteForMe(
        Guid conversationId,
        long messageId,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var convId = new ConversationId(conversationId);
        var msgId = new MessageId(messageId);

        await _messageService.DeleteForMeAsync(_currentUser.Id, convId, msgId, ct);

        return NoContent();
    }

    [HttpPost("{messageId:long}/delete-for-everyone")]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> DeleteForEveryone(
        Guid conversationId,
        long messageId,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var convId = new ConversationId(conversationId);
        var msgId = new MessageId(messageId);

        var result = await _messageService.DeleteForEveryoneAsync(_currentUser.Id, convId, msgId, ct);

        return Ok(result);
    }

    [HttpPost("{messageId:long}/reactions")]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> ToggleReaction(
        Guid conversationId,
        long messageId,
        [FromBody] ToggleReactionRequestDto dto,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var convId = new ConversationId(conversationId);
        var msgId = new MessageId(messageId);

        var result = await _messageService.ToggleReactionAsync(_currentUser.Id, convId, msgId, dto, ct);

        return Ok(result);
    }

    [HttpDelete("{messageId:long}/reactions")]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> RemoveReaction(
        Guid conversationId,
        long messageId,
        CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var convId = new ConversationId(conversationId);
        var msgId = new MessageId(messageId);

        var result = await _messageService.RemoveReactionAsync(_currentUser.Id, convId, msgId, ct);

        return Ok(result);
    }
}
