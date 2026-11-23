using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace AOS.Messenger.Web.ViewComponents;

public sealed record ConversationSidebarViewModel(
    IReadOnlyList<ConversationSummaryDto> Conversations,
    ConversationId? ActiveConversationId);

public sealed class ConversationSidebarViewComponent : ViewComponent
{
    private readonly IConversationService _conversationService;
    private readonly ICurrentUser _currentUser;

    public ConversationSidebarViewComponent(
        IConversationService conversationService,
        ICurrentUser currentUser)
    {
        _conversationService = conversationService;
        _currentUser = currentUser;
    }

    public async Task<IViewComponentResult> InvokeAsync(ConversationId? activeConversationId = null)
    {
        if (_currentUser.Id is null)
        {
            return View(new ConversationSidebarViewModel(Array.Empty<ConversationSummaryDto>(), null));
        }

        if (activeConversationId is null)
        {
            var routeValue = ViewContext.RouteData.Values.TryGetValue("id", out var routeId) && routeId is not null
                ? routeId
                : (ViewContext.RouteData.Values.TryGetValue("conversationId", out var convRouteId) ? convRouteId : null);

            if (routeValue is not null && Guid.TryParse(routeValue.ToString(), out var parsedGuid))
            {
                activeConversationId = new ConversationId(parsedGuid);
            }
        }

        var conversations = await _conversationService.GetConversationListAsync(
            _currentUser.Id,
            includeArchived: false,
            HttpContext.RequestAborted);

        return View(new ConversationSidebarViewModel(conversations, activeConversationId));
    }
}
