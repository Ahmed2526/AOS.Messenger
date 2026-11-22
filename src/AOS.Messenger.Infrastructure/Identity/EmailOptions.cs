namespace AOS.Messenger.Infrastructure.Identity;

/// <summary>
/// Strongly-typed email configuration options.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string PublicBaseUrl { get; set; } = "https://localhost:7001";
}
