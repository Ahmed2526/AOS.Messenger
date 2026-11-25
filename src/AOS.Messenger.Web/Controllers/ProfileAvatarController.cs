using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

/// <summary>
/// Thin authenticated HTTP endpoints for uploading, completing, and removing the current user's profile photo.
/// </summary>
[Authorize]
[ApiController]
[Route("api/profile/avatar")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AutoValidateAntiforgeryToken]
[RequestSizeLimit(16 * 1024)]
public sealed class ProfileAvatarController : ControllerBase
{
    private readonly IAvatarService _avatarService;
    private readonly ICurrentUser _currentUser;

    public ProfileAvatarController(
        IAvatarService avatarService,
        ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(avatarService);
        ArgumentNullException.ThrowIfNull(currentUser);

        _avatarService = avatarService;
        _currentUser = currentUser;
    }

    [HttpPost("initiate")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> InitiateUpload(
        [FromBody] InitiateAvatarUploadRequestDto dto,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _avatarService.InitiateUploadAsync(_currentUser.Id, dto, ct);
        return Ok(result);
    }

    [HttpPost("{uploadId:guid}/complete")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> CompleteUpload(
        Guid uploadId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _avatarService.CompleteUploadAsync(
            _currentUser.Id,
            new AvatarUploadId(uploadId),
            ct);

        return Ok(result);
    }

    [HttpGet("{uploadId:guid}/status")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> GetUploadStatus(Guid uploadId, CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _avatarService.GetUploadStatusAsync(_currentUser.Id, new AvatarUploadId(uploadId), ct);
        return result is null ? NoContent() : Ok(result);
    }

    [HttpDelete("")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> RemoveAvatar(CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        await _avatarService.RemoveAvatarAsync(_currentUser.Id, ct);
        return NoContent();
    }

    private void SetResourcePolicy()
    {
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }
}
