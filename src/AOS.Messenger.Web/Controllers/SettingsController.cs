using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
[RequestSizeLimit(16 * 1024)]
public sealed class SettingsController : Controller
{
    private readonly IBlockService _blockService;
    private readonly ICurrentUser _currentUser;

    public SettingsController(IBlockService blockService, ICurrentUser currentUser)
    {
        _blockService = blockService;
        _currentUser = currentUser;
    }

    // Screen 11: A♡S Messenger - Settings
    [HttpGet]
    public IActionResult Index()
    {
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> BlockedUsers(CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        var blockedUsers = await _blockService.GetBlockedUsersAsync(_currentUser.Id, ct);
        return View(blockedUsers);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> BlockUser(Guid targetUserId, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        var success = await _blockService.BlockUserAsync(_currentUser.Id, new UserId(targetUserId), ct);
        TempData["StatusMessage"] = success
            ? "User has been blocked."
            : "Failed to block user.";

        return RedirectToAction(nameof(BlockedUsers));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> UnblockUser(Guid targetUserId, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        var success = await _blockService.UnblockUserAsync(_currentUser.Id, new UserId(targetUserId), ct);
        TempData["StatusMessage"] = success
            ? "User has been unblocked."
            : "Failed to unblock user.";

        return RedirectToAction(nameof(BlockedUsers));
    }
}
