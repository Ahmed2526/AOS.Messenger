namespace AOS.Messenger.Infrastructure.Identity;

/// <summary>
/// Persistent user session and device record stored in SQL Server.
/// Tracks active logins, devices, and handles session revocation.
/// </summary>
public sealed class UserSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastActiveAtUtc { get; set; }

    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }

    public bool IsRevoked { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public static UserSession Create(
        Guid id,
        Guid userId,
        string deviceName,
        string? ipAddress,
        string? userAgent,
        DateTimeOffset createdAtUtc,
        DateTimeOffset absoluteExpiresAtUtc)
    {
        return new UserSession
        {
            Id = id,
            UserId = userId,
            DeviceName = deviceName,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAtUtc = createdAtUtc,
            LastActiveAtUtc = createdAtUtc,
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc,
            IsRevoked = false
        };
    }
}
