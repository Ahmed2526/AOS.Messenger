using AOS.Messenger.Application.Common.Interfaces;
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
    private readonly ICurrentUser _currentUser;

    public ConversationsController(
        IConversationService conversationService,
        ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(conversationService);
        ArgumentNullException.ThrowIfNull(currentUser);

        _conversationService = conversationService;
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
}
