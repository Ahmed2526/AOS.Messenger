namespace AOS.Messenger.Application.Common.Interfaces;

public interface IEmailSender
{
    Task SendEmailConfirmationAsync(
        string email, string confirmationLink, CancellationToken ct = default);

    Task SendPasswordResetAsync(
        string email, string resetLink, CancellationToken ct = default);

    Task SendEmailConfirmationAsync(
        string email, string? displayName, string confirmationLink, CancellationToken ct = default);

    Task SendPasswordResetAsync(
        string email, string? displayName, string resetLink, CancellationToken ct = default);

    Task SendAccountRecoveryAsync(
        string email, string recoveryLink, CancellationToken ct = default);

    Task SendAccountRecoveryAsync(
        string email, string? displayName, string recoveryLink, CancellationToken ct = default);

    string BuildConfirmationLink(Guid userId, string token);

    string BuildPasswordResetLink(string email, string token);

    string BuildAccountRecoveryLink(Guid userId, string token);
}
