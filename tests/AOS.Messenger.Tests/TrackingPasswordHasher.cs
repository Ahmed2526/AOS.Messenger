using AOS.Messenger.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace AOS.Messenger.Tests;

public sealed class TrackingPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _inner = new();

    public int VerifyCount { get; private set; }

    public string HashPassword(ApplicationUser user, string password)
    {
        return _inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        VerifyCount++;
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
