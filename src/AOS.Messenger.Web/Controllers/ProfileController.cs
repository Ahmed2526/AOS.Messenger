using System.Security.Claims;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AOS.Messenger.Web.Controllers;

[Authorize]
public sealed class ProfileController : Controller
{
    private readonly IProfileService _profileService;
    private readonly ICurrentUser _currentUser;

    public ProfileController(IProfileService profileService, ICurrentUser currentUser)
    {
        _profileService = profileService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        var profile = await _profileService.GetProfileAsync(_currentUser.Id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        var email = User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value;

        var model = new ProfileViewModel
        {
            Id = profile.Id,
            UserName = profile.UserName,
            DisplayName = profile.DisplayName,
            Bio = profile.Bio,
            Email = email,
            StatusMessage = TempData["StatusMessage"] as string
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(UpdateProfileViewModel model, CancellationToken ct = default)
    {
        if (_currentUser.Id is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Invalid profile data.";
            return RedirectToAction(nameof(Index));
        }

        var success = await _profileService.UpdateProfileAsync(
            _currentUser.Id,
            new UpdateProfileDto(model.DisplayName, model.Bio),
            ct);

        TempData["StatusMessage"] = success
            ? "Your profile has been updated."
            : "Error: Failed to update profile.";

        return RedirectToAction(nameof(Index));
    }
}
