using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AOS.Messenger.Web.Controllers;

[RequestSizeLimit(16 * 1024)]
public sealed class AccountController : Controller
{
    private readonly IIdentityService _identityService;
    private readonly ISessionService _sessionService;
    private readonly ICurrentUser _currentUser;
    private readonly IEmailSender _emailSender;
    private readonly IRealtimeNotifier? _realtimeNotifier;
    private readonly ILogger<AccountController>? _logger;

    public AccountController(
        IIdentityService identityService,
        ISessionService sessionService,
        ICurrentUser currentUser,
        IEmailSender emailSender,
        IRealtimeNotifier? realtimeNotifier = null,
        ILogger<AccountController>? logger = null)
    {
        _identityService = identityService;
        _sessionService = sessionService;
        _currentUser = currentUser;
        _emailSender = emailSender;
        _realtimeNotifier = realtimeNotifier;
        _logger = logger;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (_currentUser.IsAuthenticated)
        {
            return RedirectToLocal(returnUrl);
        }

        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login-limiter")]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        var (succeeded, _, isLockedOut, _, error) = await _identityService.LoginAsync(
            new LoginUserDto(model.UserNameOrEmail, model.Password, model.RememberMe),
            ip,
            userAgent,
            HttpContext.RequestAborted);

        if (succeeded)
        {
            return RedirectToLocal(returnUrl);
        }

        if (isLockedOut)
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return View(model);
        }

        ModelState.AddModelError(string.Empty, error ?? "Invalid username or password.");
        return View(model);
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register()
    {
        if (_currentUser.IsAuthenticated)
        {
            return RedirectToAction("Index", "Chat");
        }

        return View(new RegisterViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("email-endpoint-limiter")]
    public async Task<IActionResult> Register([FromForm] RegisterViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var (succeeded, userId, errors) = await _identityService.RegisterAsync(
            new RegisterUserDto(model.UserName, model.Email, model.DisplayName, model.Password),
            HttpContext.RequestAborted);

        if (!succeeded || userId is null)
        {
            foreach (var error in errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
            return View(model);
        }

        var token = await _identityService.GenerateEmailConfirmationTokenAsync(userId, HttpContext.RequestAborted);
        if (token is not null)
        {
            var confirmLink = _emailSender.BuildConfirmationLink(userId.Value, token);
            await _emailSender.SendEmailConfirmationAsync(model.Email, confirmLink, HttpContext.RequestAborted);
        }

        // Redirect to login after registration; email confirmation is required before sign-in
        return RedirectToAction("Login", new { registered = true });
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> Logout()
    {
        await _identityService.LogoutAsync(HttpContext.RequestAborted);
        return RedirectToAction("Login", "Account");
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ConfirmEmail(string? userId, string? token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
        {
            return RedirectToAction("Login");
        }

        if (!UserId.TryParse(userId, out var parsedUserId) || parsedUserId is null)
        {
            return BadRequest("Invalid User ID.");
        }

        var model = new ConfirmEmailViewModel
        {
            UserId = userId,
            Token = token
        };

        return View(model);
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("email-endpoint-limiter")]
    public async Task<IActionResult> ConfirmEmail([FromForm] ConfirmEmailViewModel model)
    {
        if (!ModelState.IsValid)
        {
            ViewData["Confirmed"] = false;
            return View(model);
        }

        if (!UserId.TryParse(model.UserId, out var parsedUserId) || parsedUserId is null)
        {
            return BadRequest("Invalid User ID.");
        }

        var result = await _identityService.ConfirmEmailAsync(parsedUserId, model.Token, HttpContext.RequestAborted);
        ViewData["Confirmed"] = result;
        return View(model);
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("email-endpoint-limiter")]
    public async Task<IActionResult> ForgotPassword([FromForm] ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var token = await _identityService.GeneratePasswordResetTokenAsync(model.Email, HttpContext.RequestAborted);
        if (token is not null)
        {
            var resetLink = _emailSender.BuildPasswordResetLink(model.Email, token);
            await _emailSender.SendPasswordResetAsync(model.Email, resetLink, HttpContext.RequestAborted);
        }

        ViewData["Sent"] = true;
        return View(model);
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ResetPassword(string email, string token)
    {
        return View(new ResetPasswordViewModel { Email = email, Token = token });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("email-endpoint-limiter")]
    public async Task<IActionResult> ResetPassword([FromForm] ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var (succeeded, errors) = await _identityService.ResetPasswordAsync(
            new ResetPasswordDto(model.Email, model.Token, model.NewPassword),
            HttpContext.RequestAborted);

        if (!succeeded)
        {
            foreach (var error in errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
            return View(model);
        }

        return RedirectToAction("Login", new { passwordReset = true });
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult RecoverAccount()
    {
        return View(new RecoverAccountViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("recover-account-limiter")]
    public async Task<IActionResult> RecoverAccount([FromForm] RecoverAccountViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var recoveryInfo = await _identityService.InitiateAccountRecoveryAsync(model.UsernameOrEmail, model.Password, HttpContext.RequestAborted);
        if (recoveryInfo is not null)
        {
            var recoveryLink = _emailSender.BuildAccountRecoveryLink(recoveryInfo.UserId.Value, recoveryInfo.Token);
            await _emailSender.SendAccountRecoveryAsync(recoveryInfo.Email, recoveryInfo.DisplayName, recoveryLink, HttpContext.RequestAborted);
        }

        TempData["SuccessMessage"] = "If an eligible deleted account exists for those credentials, an account recovery link has been sent to your registered email address.";
        return RedirectToAction("Login");
    }

    [NonAction]
    public Task<IActionResult> RecoverAccount(string usernameOrEmail, string password)
    {
        return RecoverAccount(new RecoverAccountViewModel
        {
            UsernameOrEmail = usernameOrEmail,
            Password = password
        });
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ConfirmRecovery(string? userId, string? token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
        {
            TempData["ErrorMessage"] = "Invalid account recovery link.";
            return RedirectToAction("Login");
        }

        return View(new ConfirmRecoveryViewModel
        {
            UserId = userId,
            Token = token
        });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("recover-account-limiter")]
    public async Task<IActionResult> ConfirmRecovery([FromForm] ConfirmRecoveryViewModel model)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(model.UserId) || string.IsNullOrWhiteSpace(model.Token))
        {
            TempData["ErrorMessage"] = "Invalid account recovery confirmation request.";
            return RedirectToAction("Login");
        }

        if (!Guid.TryParse(model.UserId, out var guid))
        {
            TempData["ErrorMessage"] = "Invalid account recovery confirmation request.";
            return RedirectToAction("Login");
        }

        var succeeded = await _identityService.ConfirmAccountRecoveryAsync(new UserId(guid), model.Token, HttpContext.RequestAborted);
        if (succeeded)
        {
            TempData["SuccessMessage"] = "Your account has been successfully recovered! You can now log in.";
        }
        else
        {
            TempData["ErrorMessage"] = "Unable to recover account. The recovery link may be invalid, expired, or already used.";
        }

        return RedirectToAction("Login");
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Sessions()
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var sessionClaim = User.FindFirstValue("SessionId");
        Guid? currentSessionId = Guid.TryParse(sessionClaim, out var guid) ? guid : null;
        var sessions = await _sessionService.GetActiveSessionsAsync(_currentUser.Id, currentSessionId, HttpContext.RequestAborted);
        return View(sessions);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> RevokeSession(Guid id)
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        await _sessionService.RevokeSessionAsync(_currentUser.Id, id, HttpContext.RequestAborted);
        return RedirectToAction("Sessions");
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> RevokeOtherSessions()
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var sessionClaim = User?.FindFirstValue("SessionId");
        if (!Guid.TryParse(sessionClaim, out var currentSessionId) || currentSessionId == Guid.Empty)
        {
            return Unauthorized();
        }

        await _sessionService.RevokeOtherSessionsAsync(_currentUser.Id, currentSessionId, HttpContext.RequestAborted);
        return RedirectToAction("Sessions");
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("messages")]
    public async Task<IActionResult> DeleteAccount()
    {
        if (_currentUser.Id is null)
        {
            return Unauthorized();
        }

        var userId = _currentUser.Id;
        var deleted = await _identityService.SoftDeleteAccountAsync(userId, HttpContext.RequestAborted);
        if (deleted && _realtimeNotifier is not null)
        {
            try
            {
                await _realtimeNotifier.NotifyUserProfileUpdatedAsync(
                    userId,
                    new UserProfileUpdatedDto(userId, "Deleted Account", null),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to broadcast account deletion profile update for user {UserId}.", userId.Value);
            }
        }

        return RedirectToAction("Login");
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && (Url?.IsLocalUrl(returnUrl) ?? false))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction("Index", "Chat");
    }
}

public sealed class ConfirmEmailViewModel
{
    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;
}

public sealed class ConfirmRecoveryViewModel
{
    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;
}

public sealed class RecoverAccountViewModel
{
    [Required(ErrorMessage = "Username or email is required.")]
    [Display(Name = "Username or Email")]
    public string UsernameOrEmail { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginViewModel
{
    [Required]
    [Display(Name = "Username or Email")]
    public string UserNameOrEmail { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }
}

public sealed class RegisterViewModel
{
    [Required]
    [RegularExpression(@"^[a-zA-Z0-9._-]{3,32}$", ErrorMessage = "Username must be between 3 and 32 characters and contain only letters, numbers, periods, hyphens, and underscores.")]
    [StringLength(32, MinimumLength = 3)]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [StringLength(50)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class ForgotPasswordViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

public sealed class ResetPasswordViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Compare("NewPassword", ErrorMessage = "The password and confirmation password do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
