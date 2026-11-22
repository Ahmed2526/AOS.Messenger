using System.Security.Claims;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;

namespace AOS.Messenger.Infrastructure.Identity;

/// <summary>
/// Infrastructure implementation of ICurrentUser.
/// Extracts the authenticated user's UserId from HttpContext claims without leaking Identity types.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public UserId? Id
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var idClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(idClaim))
            {
                return null;
            }

            return UserId.TryParse(idClaim, out var userId) ? userId : null;
        }
    }

    public bool IsAuthenticated =>
        _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true;
}
