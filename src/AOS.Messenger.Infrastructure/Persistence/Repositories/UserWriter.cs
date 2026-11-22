using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

public sealed class UserWriter : IUserWriter
{
    private readonly UserManager<ApplicationUser> _userManager;

    public UserWriter(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<bool> UpdateProfileAsync(UserId userId, string displayName, string? bio, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null || user.IsDeleted)
        {
            return false;
        }

        // Only modify DisplayName and Bio. Username and Email must NOT be touched.
        user.DisplayName = displayName;
        user.Bio = bio;

        var result = await _userManager.UpdateAsync(user);
        return result.Succeeded;
    }
}
