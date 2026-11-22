using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Application abstraction for accessing the currently authenticated user.
/// Returns domain-neutral UserId without exposing ASP.NET Core Identity types.
/// Implemented by Infrastructure.
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    /// Gets the strongly-typed identifier of the authenticated user, or null if unauthenticated.
    /// </summary>
    UserId? Id { get; }

    /// <summary>
    /// Indicates whether a user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }
}
