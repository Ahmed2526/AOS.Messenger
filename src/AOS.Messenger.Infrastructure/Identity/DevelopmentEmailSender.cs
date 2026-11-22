using System.Net;
using AOS.Messenger.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Identity;

public sealed class DevelopmentEmailSender : IEmailSender
{
    private readonly ILogger<DevelopmentEmailSender> _logger;
    private readonly EmailOptions _options;
    private readonly TimeProvider _timeProvider;

    public DevelopmentEmailSender(
        ILogger<DevelopmentEmailSender> logger,
        IOptions<EmailOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _options = options?.Value ?? new EmailOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default)
    {
        return SendEmailConfirmationAsync(email, null, confirmationLink, ct);
    }

    public Task SendEmailConfirmationAsync(string email, string? displayName, string confirmationLink, CancellationToken ct = default)
    {
        var finalLink = NormalizeLink(confirmationLink);
        var safeRecipient = !string.IsNullOrWhiteSpace(displayName)
            ? WebUtility.HtmlEncode(displayName)
            : WebUtility.HtmlEncode(email);
        var safeLink = WebUtility.HtmlEncode(finalLink);

        var htmlBody = $"<div><p>Hello {safeRecipient},</p><p>Please confirm your email address by clicking <a href=\"{safeLink}\">here</a>.</p></div>";

        RecordEmail("EmailConfirmation", email, finalLink, htmlBody);

        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
    {
        return SendPasswordResetAsync(email, null, resetLink, ct);
    }

    public Task SendPasswordResetAsync(string email, string? displayName, string resetLink, CancellationToken ct = default)
    {
        var finalLink = NormalizeLink(resetLink);
        var safeRecipient = !string.IsNullOrWhiteSpace(displayName)
            ? WebUtility.HtmlEncode(displayName)
            : WebUtility.HtmlEncode(email);
        var safeLink = WebUtility.HtmlEncode(finalLink);

        var htmlBody = $"<div><p>Hello {safeRecipient},</p><p>You can reset your password by clicking <a href=\"{safeLink}\">here</a>.</p></div>";

        RecordEmail("PasswordReset", email, finalLink, htmlBody);

        return Task.CompletedTask;
    }

    public Task SendAccountRecoveryAsync(string email, string recoveryLink, CancellationToken ct = default)
    {
        return SendAccountRecoveryAsync(email, null, recoveryLink, ct);
    }

    public Task SendAccountRecoveryAsync(string email, string? displayName, string recoveryLink, CancellationToken ct = default)
    {
        var finalLink = NormalizeLink(recoveryLink);
        var safeRecipient = !string.IsNullOrWhiteSpace(displayName)
            ? WebUtility.HtmlEncode(displayName)
            : WebUtility.HtmlEncode(email);
        var safeLink = WebUtility.HtmlEncode(finalLink);

        var htmlBody = $"<div><p>Hello {safeRecipient},</p><p>You can recover your account by clicking <a href=\"{safeLink}\">here</a>.</p></div>";

        RecordEmail("AccountRecovery", email, finalLink, htmlBody);

        return Task.CompletedTask;
    }

    public string BuildConfirmationLink(Guid userId, string token)
    {
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return $"{baseUrl}/Account/ConfirmEmail?userId={Uri.EscapeDataString(userId.ToString())}&token={Uri.EscapeDataString(token)}";
    }

    public string BuildPasswordResetLink(string email, string token)
    {
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return $"{baseUrl}/Account/ResetPassword?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    public string BuildAccountRecoveryLink(Guid userId, string token)
    {
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return $"{baseUrl}/Account/ConfirmRecovery?userId={Uri.EscapeDataString(userId.ToString())}&token={Uri.EscapeDataString(token)}";
    }

    private void RecordEmail(string actionType, string email, string link, string htmlBody)
    {
        var maskedEmail = MaskEmail(email);
        var safePath = GetSafePath(link);

        _logger.LogInformation(
            "[DEV EMAIL] Action: {ActionType}, Recipient: {MaskedEmail}, Path: {Path}",
            actionType,
            maskedEmail,
            safePath);

        try
        {
            var devEmailDir = Path.Combine(Directory.GetCurrentDirectory(), ".dev-emails");
            Directory.CreateDirectory(devEmailDir);
            var filename = $"{_timeProvider.GetUtcNow():yyyyMMdd_HHmmss}_{actionType}_{Guid.NewGuid():N}.html";
            File.WriteAllText(Path.Combine(devEmailDir, filename), htmlBody);
        }
        catch
        {
            // Suppress file write exceptions in restricted test environments
        }
    }

    public static string MaskEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "***";
        var parts = email.Split('@', 2);
        if (parts.Length != 2) return "***";

        var user = parts[0];
        var domain = parts[1];

        var maskedUser = user.Length <= 1 ? "*" : $"{user[0]}***";
        var domainParts = domain.Split('.', 2);
        var maskedDomain = domainParts.Length == 2
            ? (domainParts[0].Length <= 1 ? $"*.{domainParts[1]}" : $"{domainParts[0][0]}***.{domainParts[1]}")
            : (domain.Length <= 1 ? "*" : $"{domain[0]}***");

        return $"{maskedUser}@{maskedDomain}";
    }

    public static string GetSafePath(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }
        var queryIndex = link.IndexOf('?');
        return queryIndex >= 0 ? link[..queryIndex] : link;
    }

    private string NormalizeLink(string link)
    {
        var baseUrl = _options.PublicBaseUrl?.TrimEnd('/') ?? "https://localhost:7001";
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return $"{baseUrl}{uri.PathAndQuery}";
        }

        var relativePath = link.StartsWith('/') ? link : $"/{link}";
        return $"{baseUrl}{relativePath}";
    }
}
