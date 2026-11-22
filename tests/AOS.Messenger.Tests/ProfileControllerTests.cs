using System.Reflection;
using System.Security.Claims;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ProfileControllerTests
{
    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeProfileService : IProfileService
    {
        public UserProfileDto? ProfileToReturn { get; set; }
        public bool UpdateResult { get; set; } = true;
        public UpdateProfileDto? LastUpdateDto { get; private set; }
        public UserId? LastUpdatedUserId { get; private set; }

        public Task<UserProfileDto?> GetProfileAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default) =>
            Task.FromResult(ProfileToReturn);

        public Task<UserProfileDto?> GetProfileAsync(UserId userId, CancellationToken ct = default) =>
            Task.FromResult(ProfileToReturn);

        public Task<bool> UpdateProfileAsync(UserId userId, UpdateProfileDto dto, CancellationToken ct = default)
        {
            LastUpdatedUserId = userId;
            LastUpdateDto = dto;
            return Task.FromResult(UpdateResult);
        }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated { get; set; }
    }

    private static (ProfileController Controller, FakeProfileService ProfileService, FakeCurrentUser CurrentUser) CreateController(
        UserId? currentUserId = null,
        string? email = "user@example.com")
    {
        var profileService = new FakeProfileService();
        var currentUser = new FakeCurrentUser
        {
            Id = currentUserId,
            IsAuthenticated = currentUserId is not null
        };

        var httpContext = new DefaultHttpContext();
        if (currentUserId is not null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, currentUserId.Value.ToString()),
                new(ClaimTypes.Name, "testuser")
            };
            if (email is not null)
            {
                claims.Add(new(ClaimTypes.Email, email));
            }
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        var controller = new ProfileController(profileService, currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider())
        };

        return (controller, profileService, currentUser);
    }

    [Fact]
    public void Controller_HasAuthorizeAttribute_AndIsSealed()
    {
        var type = typeof(ProfileController);
        Assert.True(type.IsSealed);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public async Task Index_Get_ReturnsProfileView_WhenAuthenticatedAndFound()
    {
        var userId = UserId.New();
        var (controller, service, _) = CreateController(userId, "julian@editorial.studio");
        service.ProfileToReturn = new UserProfileDto(
            userId,
            "julian",
            "Julian Croft",
            "Quiet writer",
            IsDeleted: false);

        var result = await controller.Index();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ProfileViewModel>(viewResult.Model);
        Assert.Equal(userId, model.Id);
        Assert.Equal("julian", model.UserName);
        Assert.Equal("Julian Croft", model.DisplayName);
        Assert.Equal("Quiet writer", model.Bio);
        Assert.Equal("julian@editorial.studio", model.Email);
    }

    [Fact]
    public async Task Index_Get_ReturnsNotFound_WhenProfileDoesNotExist()
    {
        var userId = UserId.New();
        var (controller, service, _) = CreateController(userId);
        service.ProfileToReturn = null;

        var result = await controller.Index();

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Index_Get_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.Index();

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task Index_Post_UpdatesProfile_AndRedirects_WhenValid()
    {
        var userId = UserId.New();
        var (controller, service, _) = CreateController(userId);
        var model = new UpdateProfileViewModel
        {
            DisplayName = "Updated Name",
            Bio = "Updated Bio"
        };

        var result = await controller.Index(model);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Your profile has been updated.", controller.TempData["StatusMessage"]);
        Assert.Equal(userId, service.LastUpdatedUserId);
        Assert.NotNull(service.LastUpdateDto);
        Assert.Equal("Updated Name", service.LastUpdateDto.DisplayName);
        Assert.Equal("Updated Bio", service.LastUpdateDto.Bio);
    }

    [Fact]
    public async Task Index_Post_SetsErrorMessage_AndRedirects_WhenModelStateInvalid()
    {
        var userId = UserId.New();
        var (controller, service, _) = CreateController(userId);
        controller.ModelState.AddModelError("DisplayName", "DisplayName is required.");
        var model = new UpdateProfileViewModel { DisplayName = "", Bio = "Bio" };

        var result = await controller.Index(model);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error: Invalid profile data.", controller.TempData["StatusMessage"]);
        Assert.Null(service.LastUpdateDto);
    }

    [Fact]
    public async Task Index_Post_SetsErrorMessage_WhenServiceReturnsFalse()
    {
        var userId = UserId.New();
        var (controller, service, _) = CreateController(userId);
        service.UpdateResult = false;
        var model = new UpdateProfileViewModel
        {
            DisplayName = "Name",
            Bio = "Bio"
        };

        var result = await controller.Index(model);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error: Failed to update profile.", controller.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task Index_Post_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);
        var model = new UpdateProfileViewModel { DisplayName = "Name", Bio = "Bio" };

        var result = await controller.Index(model);

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public void Index_Post_HasValidateAntiForgeryTokenAttribute()
    {
        var method = typeof(ProfileController).GetMethod(
            nameof(ProfileController.Index),
            new[] { typeof(UpdateProfileViewModel), typeof(CancellationToken) });

        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
    }
}
