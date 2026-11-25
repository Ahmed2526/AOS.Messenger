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
[EnableRateLimiting("receipts")]
[RequestSizeLimit(16 * 1024)]
public sealed class ReceiptsController : ControllerBase
{
    private readonly IReceiptService _receiptService;
    private readonly IRealtimeNotifier _realtimeNotifier;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReceiptsController> _logger;

    public ReceiptsController(
        IReceiptService receiptService,
        IRealtimeNotifier realtimeNotifier,
        ICurrentUser currentUser,
        ILogger<ReceiptsController> logger,
        TimeProvider? timeProvider = null)
    {
        _receiptService = receiptService;
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

        var outcome = await _receiptService.MarkDeliveredWithRecipientAsync(conversationId, dto.UpToMessageId, ct);
        if (outcome is not null)
        {
            try
            {
                await _realtimeNotifier.NotifyReceiptUpdatedAsync(outcome.RecipientUserId, outcome.Receipt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast delivery receipt for conversation {ConversationId}", conversationId);
            }
        }

        return Ok(outcome?.Receipt ?? new ReceiptUpdateDto(conversationId, _currentUser.Id.Value, dto.UpToMessageId, _timeProvider.GetUtcNow(), "delivered"));
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

        var outcome = await _receiptService.MarkReadWithRecipientAsync(conversationId, dto.UpToMessageId, ct);
        if (outcome is not null)
        {
            try
            {
                await _realtimeNotifier.NotifyReceiptUpdatedAsync(outcome.RecipientUserId, outcome.Receipt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast read receipt for conversation {ConversationId}", conversationId);
            }
        }

        return Ok(outcome?.Receipt ?? new ReceiptUpdateDto(conversationId, _currentUser.Id.Value, dto.UpToMessageId, _timeProvider.GetUtcNow(), "read"));
    }
}
