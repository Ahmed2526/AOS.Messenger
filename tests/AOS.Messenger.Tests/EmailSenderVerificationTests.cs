using System.Net;
using AOS.Messenger.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class EmailSenderVerificationTests
{
    private sealed class TestLogger : ILogger<DevelopmentEmailSender>
    {
        public List<string> LoggedMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void BuildConfirmationLink_UsesConfiguredPublicBaseUrl()
    {
        var options = Options.Create(new EmailOptions { PublicBaseUrl = "https://messenger.aos.internal" });
        var sender = new DevelopmentEmailSender(new TestLogger(), options);

        var userId = Guid.NewGuid();
        var link = sender.BuildConfirmationLink(userId, "token123/+= special");

        Assert.StartsWith("https://messenger.aos.internal/Account/ConfirmEmail", link);
        Assert.Contains($"userId={userId}", link);
        Assert.Contains("token123", link);
    }

    [Fact]
    public void BuildPasswordResetLink_UsesConfiguredPublicBaseUrl()
    {
        var options = Options.Create(new EmailOptions { PublicBaseUrl = "https://messenger.aos.internal" });
        var sender = new DevelopmentEmailSender(new TestLogger(), options);

        var link = sender.BuildPasswordResetLink("user+test@example.com", "resetToken456");

        Assert.StartsWith("https://messenger.aos.internal/Account/ResetPassword", link);
        Assert.Contains("email=user%2Btest%40example.com", link);
        Assert.Contains("resetToken456", link);
    }

    [Fact]
    public void BuildAccountRecoveryLink_UsesConfiguredPublicBaseUrl()
    {
        var options = Options.Create(new EmailOptions { PublicBaseUrl = "https://messenger.aos.internal" });
        var sender = new DevelopmentEmailSender(new TestLogger(), options);

        var userId = Guid.NewGuid();
        var link = sender.BuildAccountRecoveryLink(userId, "recToken123/+= special");

        Assert.StartsWith("https://messenger.aos.internal/Account/ConfirmRecovery", link);
        Assert.Contains($"userId={userId}", link);
        Assert.Contains("recToken123", link);
    }

    [Fact]
    public async Task SendEmailConfirmationAsync_NormalizesLinksToPublicBaseUrl_AndRedactsSensitiveData()
    {
        var logger = new TestLogger();
        var options = Options.Create(new EmailOptions { PublicBaseUrl = "https://messenger.production.com" });
        var sender = new DevelopmentEmailSender(logger, options);

        // Pass a relative link with sensitive token
        await sender.SendEmailConfirmationAsync("user@example.com", "/Account/ConfirmEmail?userId=123&token=secret-token-abc");

        Assert.Single(logger.LoggedMessages);
        var message = logger.LoggedMessages[0];

        // Must normalize base URL and keep only path
        Assert.Contains("https://messenger.production.com/Account/ConfirmEmail", message);
        Assert.Contains("u***@e***.com", message);

        // Must NEVER log raw email, query string, or token
        Assert.DoesNotContain("user@example.com", message);
        Assert.DoesNotContain("secret-token-abc", message);
        Assert.DoesNotContain("userId=123", message);
        Assert.DoesNotContain("<a href", message);
    }

    [Fact]
    public async Task SendEmailConfirmationAsync_NeverLogsRawEmailOrToken()
    {
        var logger = new TestLogger();
        var options = Options.Create(new EmailOptions { PublicBaseUrl = "https://messenger.production.com" });
        var sender = new DevelopmentEmailSender(logger, options);

        var rawEmail = "alice@example.com";
        var rawToken = "super-secret-confirmation-token-xyz";
        await sender.SendEmailConfirmationAsync(
            rawEmail,
            "Alice Display",
            $"https://localhost:7001/Account/ConfirmEmail?userId=1&token={rawToken}");

        Assert.Single(logger.LoggedMessages);
        var message = logger.LoggedMessages[0];

        Assert.DoesNotContain(rawEmail, message);
        Assert.DoesNotContain(rawToken, message);
        Assert.DoesNotContain("<script>", message);
        Assert.DoesNotContain("<div", message);
        Assert.Contains("a***@e***.com", message);
        Assert.Contains("Action: EmailConfirmation", message);
        Assert.Contains("Path: https://messenger.production.com/Account/ConfirmEmail", message);
    }

    [Fact]
    public async Task SendPasswordResetAsync_NeverLogsRawEmailOrToken()
    {
        var logger = new TestLogger();
        var options = Options.Create(new EmailOptions { PublicBaseUrl = "https://messenger.production.com" });
        var sender = new DevelopmentEmailSender(logger, options);

        var rawEmail = "bob@example.com";
        var rawToken = "secret-reset-token-999";
        await sender.SendPasswordResetAsync(
            rawEmail,
            "Bob Display",
            $"https://localhost:7001/Account/ResetPassword?email={rawEmail}&token={rawToken}");

        Assert.Single(logger.LoggedMessages);
        var message = logger.LoggedMessages[0];

        Assert.DoesNotContain(rawEmail, message);
        Assert.DoesNotContain(rawToken, message);
        Assert.DoesNotContain("<img", message);
        Assert.Contains("b***@e***.com", message);
        Assert.Contains("Action: PasswordReset", message);
        Assert.Contains("Path: https://messenger.production.com/Account/ResetPassword", message);
    }

    [Fact]
    public async Task SendAccountRecoveryAsync_NeverLogsRawEmailOrToken()
    {
        var logger = new TestLogger();
        var options = Options.Create(new EmailOptions { PublicBaseUrl = "https://messenger.production.com" });
        var sender = new DevelopmentEmailSender(logger, options);

        var rawEmail = "carol@example.com";
        var rawToken = "secret-recovery-token-111";
        await sender.SendAccountRecoveryAsync(
            rawEmail,
            "Carol Display",
            $"https://localhost:7001/Account/ConfirmRecovery?userId=2&token={rawToken}");

        Assert.Single(logger.LoggedMessages);
        var message = logger.LoggedMessages[0];

        Assert.DoesNotContain(rawEmail, message);
        Assert.DoesNotContain(rawToken, message);
        Assert.DoesNotContain("<a href", message);
        Assert.Contains("c***@e***.com", message);
        Assert.Contains("Action: AccountRecovery", message);
        Assert.Contains("Path: https://messenger.production.com/Account/ConfirmRecovery", message);
    }

    [Theory]
    [InlineData("alice@example.com", "a***@e***.com")]
    [InlineData("bob.builder@company.org", "b***@c***.org")]
    [InlineData("a@b.com", "*@*.com")]
    [InlineData("user@domain", "u***@d***")]
    public void MaskEmail_MasksCorrectly(string input, string expected)
    {
        Assert.Equal(expected, DevelopmentEmailSender.MaskEmail(input));
    }

    [Theory]
    [InlineData("https://localhost:7001/Account/ConfirmEmail?userId=1&token=abc", "https://localhost:7001/Account/ConfirmEmail")]
    [InlineData("/Account/ResetPassword?email=test&token=123", "/Account/ResetPassword")]
    [InlineData("https://example.com/some/path", "https://example.com/some/path")]
    public void GetSafePath_StripsQueryParameters(string input, string expected)
    {
        Assert.Equal(expected, DevelopmentEmailSender.GetSafePath(input));
    }
}
