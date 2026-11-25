using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

/// <summary>
/// Thin authenticated HTTP endpoints for built-in/custom sticker catalogs, pack management,
/// presigned R2 custom sticker uploads, favorites, and authorized sticker image serving.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stickers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AutoValidateAntiforgeryToken]
[RequestSizeLimit(16 * 1024)]
public sealed class StickersController : ControllerBase
{
    private readonly IStickerService _stickerService;
    private readonly ICurrentUser _currentUser;

    public StickersController(
        IStickerService stickerService,
        ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(stickerService);
        ArgumentNullException.ThrowIfNull(currentUser);

        _stickerService = stickerService;
        _currentUser = currentUser;
    }

    [HttpGet("catalog")]
    [EnableRateLimiting("media-download")]
    public async Task<IActionResult> GetCatalog(CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var catalog = await _stickerService.GetCatalogAsync(_currentUser.Id, ct);
        return Ok(catalog);
    }

    [HttpPost("packs")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> CreatePack(
        [FromBody] CreateStickerPackRequestDto dto,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var pack = await _stickerService.CreateCustomPackAsync(_currentUser.Id, dto, ct);
        return Ok(pack);
    }

    [HttpPut("packs/{packId:guid}")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> RenamePack(
        Guid packId,
        [FromBody] RenameStickerPackRequestDto dto,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var pack = await _stickerService.RenameCustomPackAsync(
            _currentUser.Id,
            new StickerPackId(packId),
            dto,
            ct);
        return Ok(pack);
    }

    [HttpDelete("packs/{packId:guid}")]
    [EnableRateLimiting("media-cancel")]
    public async Task<IActionResult> DeletePack(
        Guid packId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        await _stickerService.DeleteCustomPackAsync(_currentUser.Id, new StickerPackId(packId), ct);
        return NoContent();
    }

    [HttpPost("uploads/initiate")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> InitiateUpload(
        [FromBody] InitiateStickerUploadRequestDto dto,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _stickerService.InitiateUploadAsync(_currentUser.Id, dto, ct);
        return Ok(result);
    }

    [HttpPost("uploads/{stickerId:guid}/complete")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> CompleteUpload(
        Guid stickerId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _stickerService.CompleteUploadAsync(_currentUser.Id, new StickerId(stickerId), ct);
        return Ok(result);
    }

    [HttpDelete("uploads/{stickerId:guid}")]
    [EnableRateLimiting("media-cancel")]
    public async Task<IActionResult> CancelUpload(
        Guid stickerId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        await _stickerService.CancelUploadAsync(_currentUser.Id, new StickerId(stickerId), ct);
        return NoContent();
    }

    [HttpDelete("{stickerId:guid}")]
    [EnableRateLimiting("media-cancel")]
    public async Task<IActionResult> ArchiveCustomSticker(
        Guid stickerId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        await _stickerService.ArchiveCustomStickerAsync(_currentUser.Id, new StickerId(stickerId), ct);
        return NoContent();
    }

    [HttpPost("favorites")]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> AddFavorite(
        [FromBody] AddFavoriteStickerRequestDto dto,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var sticker = await _stickerService.AddFavoriteAsync(_currentUser.Id, dto, ct);
        return Ok(sticker);
    }

    [HttpDelete("favorites/{stickerId:guid}")]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> RemoveFavorite(
        Guid stickerId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        await _stickerService.RemoveFavoriteAsync(_currentUser.Id, new StickerId(stickerId), ct);
        return NoContent();
    }

    [HttpGet("{stickerId:guid}/image")]
    [EnableRateLimiting("media-download")]
    public async Task<IActionResult> GetImageRedirect(
        Guid stickerId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _stickerService.GetStickerImageUrlAsync(_currentUser.Id, new StickerId(stickerId), ct);
        return Redirect(result.Url);
    }

    [HttpGet("{stickerId:guid}/url")]
    [EnableRateLimiting("media-download")]
    public async Task<IActionResult> GetImageUrl(
        Guid stickerId,
        CancellationToken ct = default)
    {
        SetResourcePolicy();
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var result = await _stickerService.GetStickerImageUrlAsync(_currentUser.Id, new StickerId(stickerId), ct);
        return Ok(result);
    }

    private void SetResourcePolicy()
    {
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }
}
