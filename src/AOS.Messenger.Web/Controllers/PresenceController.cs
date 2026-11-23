using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/presence")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[AutoValidateAntiforgeryToken]
[EnableRateLimiting("messages")]
public sealed class PresenceController : ControllerBase
{
    private readonly IPresenceService _presenceService;
    private readonly ICurrentUser _currentUser;

    public PresenceController(
        IPresenceService presenceService,
        ICurrentUser currentUser)
    {
        _presenceService = presenceService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> GetPresences(CancellationToken ct)
    {
        var callerId = _currentUser.Id;
        if (callerId is null)
        {
            return Unauthorized();
        }

        var presences = await _presenceService.GetPresenceForConversationsAsync(callerId, ct);
        return Ok(presences);
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> GetPeerPresence(Guid userId, CancellationToken ct)
    {
        var callerId = _currentUser.Id;
        if (callerId is null)
        {
            return Unauthorized();
        }

        var presence = await _presenceService.GetPeerPresenceAsync(callerId, new UserId(userId), ct);
        if (presence is null)
        {
            return NotFound();
        }

        return Ok(presence);
    }
}
