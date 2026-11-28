using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

/// <summary>
/// Thin authenticated HTTP endpoints for presigned media uploads, verification, cancellation, and download URLs.
/// Persistent media state commits in SQL Server; exceptions map centrally via ExceptionHandlingMiddleware.
/// </summary>
[Authorize]
[ApiController]
[Route("api/conversations/{conversationId:guid}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AutoValidateAntiforgeryToken]
[RequestSizeLimit(16 * 1024)]
public sealed class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly ICurrentUser _currentUser;

    public MediaController(
        IMediaService mediaService,
        ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(currentUser);

        _mediaService = mediaService;
        _currentUser = currentUser;
    }

    [HttpPost("media/initiate")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> InitiateUpload(
        Guid conversationId,
        [FromBody] InitiateMediaUploadRequestDto dto,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _mediaService.InitiateUploadAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            dto,
            ct);

        return Ok(result);
    }

    [HttpPost("media/{mediaId:guid}/complete")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> CompleteUpload(
        Guid conversationId,
        Guid mediaId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _mediaService.CompleteUploadAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            new MediaId(mediaId),
            ct);

        return Ok(result);
    }

    [HttpDelete("media/{mediaId:guid}")]
    [EnableRateLimiting("media-cancel")]
    public async Task<IActionResult> CancelUpload(
        Guid conversationId,
        Guid mediaId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        await _mediaService.CancelUploadAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            new MediaId(mediaId),
            ct);

        return NoContent();
    }

    [HttpGet("messages/{messageId:long}/media/{mediaId:guid}")]
    [EnableRateLimiting("media-download")]
    public async Task<IActionResult> DownloadRedirect(
        Guid conversationId,
        long messageId,
        Guid mediaId,
        [FromQuery] bool download = false,
        [FromQuery] string? variant = null,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var thumbnail = ParseThumbnailVariant(variant);
        var result = await _mediaService.GetDownloadUrlAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            new MessageId(messageId),
            new MediaId(mediaId),
            download,
            thumbnail,
            ct);

        return Redirect(result.Url);
    }

    [HttpGet("messages/{messageId:long}/media/{mediaId:guid}/url")]
    [EnableRateLimiting("media-download")]
    public async Task<IActionResult> GetDownloadUrl(
        Guid conversationId,
        long messageId,
        Guid mediaId,
        [FromQuery] bool download = false,
        [FromQuery] string? variant = null,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var thumbnail = ParseThumbnailVariant(variant);
        var result = await _mediaService.GetDownloadUrlAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            new MessageId(messageId),
            new MediaId(mediaId),
            download,
            thumbnail,
            ct);

        return Ok(result);
    }

    [HttpPost("messages/{messageId:long}/media/{mediaId:guid}/played")]
    [EnableRateLimiting("receipts")]
    public async Task<IActionResult> MarkVoicePlayed(
        Guid conversationId,
        long messageId,
        Guid mediaId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _mediaService.MarkVoicePlayedAsync(
            _currentUser.Id,
            new ConversationId(conversationId),
            new MessageId(messageId),
            new MediaId(mediaId),
            ct);

        return Ok(result);
    }

    private static bool ParseThumbnailVariant(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant) || string.Equals(variant.Trim(), "original", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(variant.Trim(), "thumb", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new DomainException("Invalid media variant. Allowed values are 'original' and 'thumb'.");
    }

    private void SetResourcePolicy()
    {
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }
}
