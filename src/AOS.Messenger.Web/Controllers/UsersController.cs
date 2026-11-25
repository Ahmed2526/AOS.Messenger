using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/users")]
[RequestSizeLimit(16 * 1024)]
public sealed class UsersController : ControllerBase
{
    private readonly IUserDiscoveryService _userDiscoveryService;
    private readonly IProfileService _profileService;
    private readonly IAvatarService? _avatarService;
    private readonly ICurrentUser _currentUser;

    [ActivatorUtilitiesConstructor]
    public UsersController(
        IUserDiscoveryService userDiscoveryService,
        IProfileService profileService,
        IAvatarService avatarService,
        ICurrentUser currentUser)
    {
        _userDiscoveryService = userDiscoveryService;
        _profileService = profileService;
        _avatarService = avatarService;
        _currentUser = currentUser;
    }

    public UsersController(
        IUserDiscoveryService userDiscoveryService,
        IProfileService profileService,
        ICurrentUser currentUser)
    {
        _userDiscoveryService = userDiscoveryService;
        _profileService = profileService;
        _avatarService = null;
        _currentUser = currentUser;
    }

    [HttpGet("search")]
    [EnableRateLimiting("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? query,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(Array.Empty<UserSearchResultDto>());
        }

        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var results = await _userDiscoveryService.SearchUsersAsync(
            _currentUser.Id,
            query,
            limit,
            ct);

        return Ok(results);
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting("search")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var profile = await _profileService.GetProfileAsync(_currentUser.Id, new UserId(id), ct);
        if (profile is null)
        {
            return NotFound();
        }

        return Ok(profile);
    }

    [HttpGet("{id:guid}/avatar")]
    [EnableRateLimiting("avatar-download")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetAvatarRedirect(
        Guid id,
        [FromQuery] string? size = null,
        CancellationToken ct = default)
    {
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        if (_avatarService is null)
        {
            throw new DomainException("Avatar service is not configured.");
        }

        var variant = ParseAvatarSizeVariant(size);
        var dto = await _avatarService.GetAvatarUrlAsync(_currentUser.Id, new UserId(id), variant, ct);
        return Redirect(dto.Url);
    }

    [HttpGet("{id:guid}/avatar/url")]
    [EnableRateLimiting("avatar-download")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetAvatarUrl(
        Guid id,
        [FromQuery] string? size = null,
        CancellationToken ct = default)
    {
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        if (_avatarService is null)
        {
            throw new DomainException("Avatar service is not configured.");
        }

        var variant = ParseAvatarSizeVariant(size);
        var dto = await _avatarService.GetAvatarUrlAsync(_currentUser.Id, new UserId(id), variant, ct);
        return Ok(dto);
    }

    private static AvatarSizeVariant ParseAvatarSizeVariant(string? size)
    {
        if (string.IsNullOrWhiteSpace(size) || string.Equals(size.Trim(), "thumb", StringComparison.OrdinalIgnoreCase))
        {
            return AvatarSizeVariant.Thumbnail;
        }

        if (string.Equals(size.Trim(), "full", StringComparison.OrdinalIgnoreCase))
        {
            return AvatarSizeVariant.Full;
        }

        throw new DomainException("Invalid avatar size. Allowed values are 'thumb' and 'full'.");
    }
}
