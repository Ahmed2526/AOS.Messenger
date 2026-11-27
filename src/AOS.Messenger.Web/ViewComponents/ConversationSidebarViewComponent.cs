using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AOS.Messenger.Web.ViewComponents;

public sealed record ConversationSidebarViewModel(
    IReadOnlyList<ConversationSummaryDto> Conversations,
    ConversationId? ActiveConversationId,
    UserProfileDto? CurrentUserProfile = null);

public sealed class ConversationSidebarViewComponent : ViewComponent
{
    private readonly IConversationService _conversationService;
    private readonly ICurrentUser _currentUser;
    private readonly IProfileService? _profileService;

    [ActivatorUtilitiesConstructor]
    public ConversationSidebarViewComponent(
        IConversationService conversationService,
        ICurrentUser currentUser,
        IProfileService? profileService = null)
    {
        _conversationService = conversationService;
        _currentUser = currentUser;
        _profileService = profileService;
    }

    public async Task<IViewComponentResult> InvokeAsync(ConversationId? activeConversationId = null)
    {
        if (_currentUser.Id is null)
        {
            return View(new ConversationSidebarViewModel(Array.Empty<ConversationSummaryDto>(), null));
        }

        if (activeConversationId is null)
        {
            var routeValues = ViewContext?.RouteData?.Values;
            object? routeValue = null;
            if (routeValues is not null)
            {
                routeValue = routeValues.TryGetValue("id", out var routeId) && routeId is not null
                    ? routeId
                    : (routeValues.TryGetValue("conversationId", out var convRouteId) ? convRouteId : null);
            }

            if (routeValue is not null && ConversationId.TryParse(routeValue.ToString(), out var parsedId))
            {
                activeConversationId = parsedId;
            }
        }

        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;

        IReadOnlyList<ConversationSummaryDto>? conversations = null;
        if (HttpContext?.Items.TryGetValue(
                Controllers.ChatController.SidebarConversationsHttpContextKey,
                out var cached) == true
            && cached is (UserId cachedUserId, IReadOnlyList<ConversationSummaryDto> cachedConversations)
            && cachedUserId == _currentUser.Id)
        {
            conversations = cachedConversations;
        }

        conversations ??= await _conversationService.GetConversationListAsync(
            _currentUser.Id,
            includeArchived: false,
            ct);

        var currentUserProfile = _profileService is not null
            ? await _profileService.GetProfileAsync(_currentUser.Id, ct)
            : null;

        return View(new ConversationSidebarViewModel(conversations, activeConversationId, currentUserProfile));
    }
}
