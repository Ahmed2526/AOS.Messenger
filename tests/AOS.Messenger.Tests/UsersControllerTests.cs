using System.Reflection;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class UsersControllerTests
{
    private sealed class FakeUserDiscoveryService : IUserDiscoveryService
    {
        public List<UserSearchResultDto> ResultsToReturn { get; } = new();
        public UserId? LastCurrentUserId { get; private set; }
        public string? LastQuery { get; private set; }
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
            UserId currentUserId,
            string query,
            int limit = 20,
            CancellationToken ct = default)
        {
            LastCurrentUserId = currentUserId;
            LastQuery = query;
            LastLimit = limit;
            return Task.FromResult<IReadOnlyList<UserSearchResultDto>>(ResultsToReturn);
        }
    }

    private sealed class FakeProfileService : IProfileService
    {
        public UserProfileDto? ProfileToReturn { get; set; }

        public Task<UserProfileDto?> GetProfileAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default) =>
            Task.FromResult(ProfileToReturn);

        public Task<UserProfileDto?> GetProfileAsync(UserId userId, CancellationToken ct = default) =>
            Task.FromResult(ProfileToReturn);

        public Task<bool> UpdateProfileAsync(UserId userId, UpdateProfileDto dto, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated { get; set; }
    }

    private static (UsersController Controller, FakeUserDiscoveryService Discovery, FakeProfileService Profile, FakeCurrentUser CurrentUser) CreateController(
        UserId? currentUserId = null)
    {
        var discovery = new FakeUserDiscoveryService();
        var profile = new FakeProfileService();
        var currentUser = new FakeCurrentUser
        {
            Id = currentUserId,
            IsAuthenticated = currentUserId is not null
        };

        var controller = new UsersController(discovery, profile, currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return (controller, discovery, profile, currentUser);
    }

    [Fact]
    public void Controller_HasExpectedAttributes_AndIsSealed()
    {
        var type = typeof(UsersController);
        Assert.True(type.IsSealed);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());

        var routeAttr = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(routeAttr);
        Assert.Equal("api/users", routeAttr.Template);
    }

    [Fact]
    public void SearchAction_HasEnableRateLimitingAttribute_ForSearchPolicy()
    {
        var method = typeof(UsersController).GetMethod(nameof(UsersController.Search));
        Assert.NotNull(method);

        var rateLimitAttr = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimitAttr);
        Assert.Equal("search", rateLimitAttr.PolicyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_ReturnsEmptyArray_WhenQueryIsNullOrWhiteSpace(string? query)
    {
        var (controller, discovery, _, _) = CreateController(UserId.New());

        var result = await controller.Search(query);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var results = Assert.IsAssignableFrom<IEnumerable<UserSearchResultDto>>(okResult.Value);
        Assert.Empty(results);
        Assert.Null(discovery.LastQuery);
    }

    [Fact]
    public async Task Search_ReturnsMatchingResults_WhenQueryProvided()
    {
        var currentUserId = UserId.New();
        var (controller, discovery, _, _) = CreateController(currentUserId);
        discovery.ResultsToReturn.Add(new UserSearchResultDto(
            UserId.New(),
            "elena.r",
            "Elena Rostova",
            "Designer"));

        var result = await controller.Search("elena", limit: 10);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var results = Assert.IsAssignableFrom<IReadOnlyList<UserSearchResultDto>>(okResult.Value);
        Assert.Single(results);
        Assert.Equal("elena.r", results[0].UserName);
        Assert.Equal(currentUserId, discovery.LastCurrentUserId);
        Assert.Equal("elena", discovery.LastQuery);
        Assert.Equal(10, discovery.LastLimit);
    }

    [Fact]
    public async Task Search_ReturnsUnauthorized_WhenUnauthenticated()
    {
        var (controller, _, _, _) = CreateController(currentUserId: null);

        var result = await controller.Search("query");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetById_ReturnsOk_WhenProfileFound()
    {
        var targetUserId = UserId.New();
        var (controller, _, profile, _) = CreateController(UserId.New());
        profile.ProfileToReturn = new UserProfileDto(
            targetUserId,
            "alex",
            "Alexander Chen",
            "Bio",
            IsDeleted: false);

        var result = await controller.GetById(targetUserId.Value);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var model = Assert.IsType<UserProfileDto>(okResult.Value);
        Assert.Equal(targetUserId, model.Id);
        Assert.Equal("alex", model.UserName);
    }

    [Fact]
    public async Task GetById_ReturnsNotFound_WhenProfileDoesNotExist()
    {
        var (controller, _, profile, _) = CreateController(UserId.New());
        profile.ProfileToReturn = null;

        var result = await controller.GetById(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }
}
