using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserDiscoveryService _userDiscoveryService;
    private readonly IProfileService _profileService;
    private readonly ICurrentUser _currentUser;

    public UsersController(
        IUserDiscoveryService userDiscoveryService,
        IProfileService profileService,
        ICurrentUser currentUser)
    {
        _userDiscoveryService = userDiscoveryService;
        _profileService = profileService;
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
}
