using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for modifying user profile state without exposing Identity or EF Core types.
/// </summary>
public interface IUserWriter
{
    Task<bool> UpdateProfileAsync(UserId userId, string displayName, string? bio, CancellationToken ct = default);
}
