using System.Reflection;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class SettingsControllerTests
{
    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeBlockService : IBlockService
    {
        public List<BlockedUserDto> BlockedUsersToReturn { get; } = new();
        public bool BlockResult { get; set; } = true;
        public bool UnblockResult { get; set; } = true;
        public UserId? LastBlockerId { get; private set; }
        public UserId? LastTargetUserId { get; private set; }

        public Task<bool> BlockUserAsync(UserId blockerId, UserId targetUserId, CancellationToken ct = default)
        {
            LastBlockerId = blockerId;
            LastTargetUserId = targetUserId;
            return Task.FromResult(BlockResult);
        }

        public Task<bool> UnblockUserAsync(UserId blockerId, UserId targetUserId, CancellationToken ct = default)
        {
            LastBlockerId = blockerId;
            LastTargetUserId = targetUserId;
            return Task.FromResult(UnblockResult);
        }

        public Task<IReadOnlyList<BlockedUserDto>> GetBlockedUsersAsync(UserId blockerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BlockedUserDto>>(BlockedUsersToReturn);

        public Task<bool> IsBlockedBidirectionalAsync(UserId userA, UserId userB, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated { get; set; }
    }

    private static (SettingsController Controller, FakeBlockService BlockService, FakeCurrentUser CurrentUser) CreateController(
        UserId? currentUserId = null)
    {
        var blockService = new FakeBlockService();
        var currentUser = new FakeCurrentUser
        {
            Id = currentUserId,
            IsAuthenticated = currentUserId is not null
        };

        var httpContext = new DefaultHttpContext();
        var controller = new SettingsController(blockService, currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider())
        };

        return (controller, blockService, currentUser);
    }

    [Fact]
    public void Controller_HasAuthorizeAttribute_AndIsSealed()
    {
        var type = typeof(SettingsController);
        Assert.True(type.IsSealed);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void Index_ReturnsView()
    {
        var (controller, _, _) = CreateController(UserId.New());

        var result = controller.Index();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task BlockedUsers_ReturnsViewWithBlockedUsers_WhenAuthenticated()
    {
        var currentUserId = UserId.New();
        var (controller, blockService, _) = CreateController(currentUserId);
        blockService.BlockedUsersToReturn.Add(new BlockedUserDto(
            UserId.New(),
            "blocked.user",
            "Blocked Person",
            DateTimeOffset.UtcNow));

        var result = await controller.BlockedUsers();

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IReadOnlyList<BlockedUserDto>>(viewResult.Model);
        Assert.Single(model);
        Assert.Equal("blocked.user", model[0].UserName);
    }

    [Fact]
    public async Task BlockedUsers_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.BlockedUsers();

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task BlockUser_CallsService_AndRedirectsToBlockedUsers()
    {
        var currentUserId = UserId.New();
        var targetGuid = Guid.NewGuid();
        var (controller, blockService, _) = CreateController(currentUserId);

        var result = await controller.BlockUser(targetGuid);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("BlockedUsers", redirect.ActionName);
        Assert.Equal("User has been blocked.", controller.TempData["StatusMessage"]);
        Assert.Equal(currentUserId, blockService.LastBlockerId);
        Assert.Equal(new UserId(targetGuid), blockService.LastTargetUserId);
    }

    [Fact]
    public async Task BlockUser_SetsFailureMessage_WhenServiceReturnsFalse()
    {
        var currentUserId = UserId.New();
        var (controller, blockService, _) = CreateController(currentUserId);
        blockService.BlockResult = false;

        var result = await controller.BlockUser(Guid.NewGuid());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("BlockedUsers", redirect.ActionName);
        Assert.Equal("Failed to block user.", controller.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task BlockUser_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.BlockUser(Guid.NewGuid());

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task UnblockUser_CallsService_AndRedirectsToBlockedUsers()
    {
        var currentUserId = UserId.New();
        var targetGuid = Guid.NewGuid();
        var (controller, blockService, _) = CreateController(currentUserId);

        var result = await controller.UnblockUser(targetGuid);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("BlockedUsers", redirect.ActionName);
        Assert.Equal("User has been unblocked.", controller.TempData["StatusMessage"]);
        Assert.Equal(currentUserId, blockService.LastBlockerId);
        Assert.Equal(new UserId(targetGuid), blockService.LastTargetUserId);
    }

    [Fact]
    public async Task UnblockUser_SetsFailureMessage_WhenServiceReturnsFalse()
    {
        var currentUserId = UserId.New();
        var (controller, blockService, _) = CreateController(currentUserId);
        blockService.UnblockResult = false;

        var result = await controller.UnblockUser(Guid.NewGuid());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("BlockedUsers", redirect.ActionName);
        Assert.Equal("Failed to unblock user.", controller.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task UnblockUser_ReturnsChallenge_WhenUnauthenticated()
    {
        var (controller, _, _) = CreateController(currentUserId: null);

        var result = await controller.UnblockUser(Guid.NewGuid());

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public void BlockActions_HaveValidateAntiForgeryTokenAndHttpPostAttributes()
    {
        var blockMethod = typeof(SettingsController).GetMethod(
            nameof(SettingsController.BlockUser),
            new[] { typeof(Guid), typeof(CancellationToken) });

        Assert.NotNull(blockMethod);
        Assert.NotNull(blockMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(blockMethod.GetCustomAttribute<HttpPostAttribute>());

        var unblockMethod = typeof(SettingsController).GetMethod(
            nameof(SettingsController.UnblockUser),
            new[] { typeof(Guid), typeof(CancellationToken) });

        Assert.NotNull(unblockMethod);
        Assert.NotNull(unblockMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(unblockMethod.GetCustomAttribute<HttpPostAttribute>());
    }
}
