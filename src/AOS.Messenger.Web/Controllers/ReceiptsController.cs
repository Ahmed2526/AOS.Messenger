using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/conversations/{conversationId:guid}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AutoValidateAntiforgeryToken]
[EnableRateLimiting("messages")]
public sealed class ReceiptsController : ControllerBase
{
    private readonly IReceiptService _receiptService;
    private readonly IConversationService _conversationService;
    private readonly IRealtimeNotifier _realtimeNotifier;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReceiptsController> _logger;

    public ReceiptsController(
        IReceiptService receiptService,
        IConversationService conversationService,
        IRealtimeNotifier realtimeNotifier,
        ICurrentUser currentUser,
        ILogger<ReceiptsController> logger,
        TimeProvider? timeProvider = null)
    {
        _receiptService = receiptService;
        _conversationService = conversationService;
        _realtimeNotifier = realtimeNotifier;
        _currentUser = currentUser;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [HttpPost("delivered")]
    public async Task<IActionResult> MarkDelivered(
        Guid conversationId,
        [FromBody] MarkReceiptRequestDto dto,
        CancellationToken ct)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _receiptService.MarkDeliveredAsync(conversationId, dto.UpToMessageId, ct);
        if (result is not null)
        {
            try
            {
                var conv = await _conversationService.GetConversationAsync(_currentUser.Id, new ConversationId(conversationId), ct);
                await _realtimeNotifier.NotifyReceiptUpdatedAsync(conv.OtherParticipantId, result, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast delivery receipt for conversation {ConversationId}", conversationId);
            }
        }

        return Ok(result ?? new ReceiptUpdateDto(conversationId, _currentUser.Id.Value, dto.UpToMessageId, _timeProvider.GetUtcNow(), "delivered"));
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkRead(
        Guid conversationId,
        [FromBody] MarkReceiptRequestDto dto,
        CancellationToken ct)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _receiptService.MarkReadAsync(conversationId, dto.UpToMessageId, ct);
        if (result is not null)
        {
            try
            {
                var conv = await _conversationService.GetConversationAsync(_currentUser.Id, new ConversationId(conversationId), ct);
                await _realtimeNotifier.NotifyReceiptUpdatedAsync(conv.OtherParticipantId, result, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast read receipt for conversation {ConversationId}", conversationId);
            }
        }

        return Ok(result ?? new ReceiptUpdateDto(conversationId, _currentUser.Id.Value, dto.UpToMessageId, _timeProvider.GetUtcNow(), "read"));
    }
}
