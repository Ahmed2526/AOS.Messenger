using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class AccountControllerTests
{
    private sealed class FakeIdentityService : IIdentityService
    {
        public bool LoginResult { get; set; } = true;
        public bool LoginIsLockedOut { get; set; }
        public string? LoginError { get; set; } = "Invalid credentials.";
        public bool RegisterResult { get; set; } = true;
        public bool ConfirmEmailCalled { get; private set; }

        public Task<(bool Succeeded, UserId? UserId, IEnumerable<string> Errors)> RegisterAsync(
            RegisterUserDto dto, CancellationToken ct = default) =>
            Task.FromResult(RegisterResult
                ? (true, (UserId?)UserId.New(), Enumerable.Empty<string>())
                : (false, (UserId?)null, (IEnumerable<string>)new[] { "Registration failed." }));

        public Task<(bool Succeeded, bool RequiresTwoFactor, bool IsLockedOut, bool IsDeleted, string? Error)> LoginAsync(
            LoginUserDto dto, string? ipAddress, string? userAgent, CancellationToken ct = default) =>
            Task.FromResult(LoginIsLockedOut
                ? (false, false, true, false, (string?)null)
                : LoginResult
                    ? (true, false, false, false, (string?)null)
                    : (false, false, false, false, LoginError));

        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SoftDeleteAccountAsync(UserId userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RecoverAccountAsync(string usernameOrEmail, string password, CancellationToken ct = default) => Task.FromResult(true);

        public bool InitiateAccountRecoveryResultValue { get; set; } = true;
        public bool ConfirmAccountRecoveryResultValue { get; set; } = true;
        public bool InitiateAccountRecoveryCalled { get; private set; }
        public bool ConfirmAccountRecoveryCalled { get; private set; }

        public Task<InitiateAccountRecoveryResult?> InitiateAccountRecoveryAsync(string usernameOrEmail, string password, CancellationToken ct = default)
        {
            InitiateAccountRecoveryCalled = true;
            return Task.FromResult(InitiateAccountRecoveryResultValue
                ? new InitiateAccountRecoveryResult(UserId.New(), "user@example.com", "Test User", "recovery-token")
                : null);
        }

        public Task<bool> ConfirmAccountRecoveryAsync(UserId userId, string token, CancellationToken ct = default)
        {
            ConfirmAccountRecoveryCalled = true;
            return Task.FromResult(ConfirmAccountRecoveryResultValue);
        }
        public Task<bool> ConfirmEmailAsync(UserId userId, string token, CancellationToken ct = default)
        {
            ConfirmEmailCalled = true;
            return Task.FromResult(true);
        }
        public Task<string?> GenerateEmailConfirmationTokenAsync(UserId userId, CancellationToken ct = default) => Task.FromResult<string?>("token");
        public Task<string?> GeneratePasswordResetTokenAsync(string email, CancellationToken ct = default) => Task.FromResult<string?>("reset-token");
        public Task<(bool Succeeded, IEnumerable<string> Errors)> ResetPasswordAsync(ResetPasswordDto dto, CancellationToken ct = default) =>
            Task.FromResult((true, Enumerable.Empty<string>()));

        public Task<(bool Succeeded, IEnumerable<string> Errors)> ChangePasswordAsync(UserId userId, string currentPassword, string newPassword, CancellationToken ct = default) =>
            Task.FromResult((true, Enumerable.Empty<string>()));
    }

    private sealed class FakeSessionService : ISessionService
    {
        public List<UserSessionDto> Sessions { get; } = new();
        public List<(UserId UserId, Guid CurrentSessionId)> RevokeOtherSessionsCalls { get; } = new();

        public Task<IReadOnlyList<UserSessionDto>> GetActiveSessionsAsync(UserId userId, Guid? currentSessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserSessionDto>>(Sessions);

        public Task<Guid> CreateSessionAsync(UserId userId, string deviceName, string? ipAddress, string? userAgent, CancellationToken ct = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<bool> RevokeSessionAsync(UserId userId, Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task RevokeOtherSessionsAsync(UserId userId, Guid currentSessionId, CancellationToken ct = default)
        {
            RevokeOtherSessionsCalls.Add((userId, currentSessionId));
            return Task.CompletedTask;
        }

        public Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task UpdateSessionActivityAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RevokeAllSessionsAsync(UserId userId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated { get; set; }
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<string> SentLinks { get; } = new();

        public Task SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default)
        {
            SentLinks.Add(confirmationLink);
            return Task.CompletedTask;
        }

        public Task SendEmailConfirmationAsync(string email, string? displayName, string confirmationLink, CancellationToken ct = default)
        {
            SentLinks.Add(confirmationLink);
            return Task.CompletedTask;
        }

        public Task SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
        {
            SentLinks.Add(resetLink);
            return Task.CompletedTask;
        }

        public Task SendPasswordResetAsync(string email, string? displayName, string resetLink, CancellationToken ct = default)
        {
            SentLinks.Add(resetLink);
            return Task.CompletedTask;
        }

        public Task SendAccountRecoveryAsync(string email, string recoveryLink, CancellationToken ct = default)
        {
            SentLinks.Add(recoveryLink);
            return Task.CompletedTask;
        }

        public Task SendAccountRecoveryAsync(string email, string? displayName, string recoveryLink, CancellationToken ct = default)
        {
            SentLinks.Add(recoveryLink);
            return Task.CompletedTask;
        }

        public string BuildConfirmationLink(Guid userId, string token) => $"https://localhost:7001/Account/ConfirmEmail?userId={userId}&token={token}";
        public string BuildPasswordResetLink(string email, string token) => $"https://localhost:7001/Account/ResetPassword?email={Uri.EscapeDataString(email)}&token={token}";
        public string BuildAccountRecoveryLink(Guid userId, string token) => $"https://localhost:7001/Account/ConfirmRecovery?userId={userId}&token={token}";
    }

    private sealed class TestUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url[0] == '/')
            {
                if (url.Length == 1) return true;
                if (url[1] != '/' && url[1] != '\\') return true;
                return false;
            }
            if (url[0] == '~' && url.Length > 1 && url[1] == '/')
            {
                if (url.Length == 2) return true;
                if (url[2] != '/' && url[2] != '\\') return true;
                return false;
            }
            return false;
        }
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext) => null;
    }

    private static (AccountController Controller, FakeIdentityService Identity, FakeSessionService Sessions, FakeCurrentUser User) CreateController(
        bool isAuthenticated = false, UserId? userId = null)
    {
        var (c, i, s, u, _) = CreateControllerWithEmailSender(isAuthenticated, userId);
        return (c, i, s, u);
    }

    private sealed class FakeTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static (AccountController Controller, FakeIdentityService Identity, FakeSessionService Sessions, FakeCurrentUser User, FakeEmailSender EmailSender) CreateControllerWithEmailSender(
        bool isAuthenticated = false, UserId? userId = null)
    {
        var identityService = new FakeIdentityService();
        var sessionService = new FakeSessionService();
        var currentUser = new FakeCurrentUser { IsAuthenticated = isAuthenticated, Id = userId };
        var emailSender = new FakeEmailSender();

        var httpContext = new DefaultHttpContext();
        var tempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(httpContext, new FakeTempDataProvider());

        var controller = new AccountController(identityService, sessionService, currentUser, emailSender)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            Url = new TestUrlHelper(),
            TempData = tempData
        };

        return (controller, identityService, sessionService, currentUser, emailSender);
    }

    [Fact]
    public void Login_Get_RedirectsIfAlreadyAuthenticated()
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: true);

        var result = controller.Login() as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Index", result.ActionName);
        Assert.Equal("Chat", result.ControllerName);
    }

    [Fact]
    public void Login_Get_ReturnsViewWhenNotAuthenticated()
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false);

        var result = controller.Login() as ViewResult;

        Assert.NotNull(result);
        Assert.IsType<LoginViewModel>(result.Model);
    }

    [Fact]
    public async Task Login_Post_RedirectsToChatOnSuccess()
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false);

        var model = new LoginViewModel { UserNameOrEmail = "alice", Password = "Password123!" };
        var result = await controller.Login(model) as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Index", result.ActionName);
        Assert.Equal("Chat", result.ControllerName);
    }

    [Fact]
    public async Task Sessions_ReturnsUnauthorizedIfNotAuthenticated()
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false, userId: null);

        var result = await controller.Sessions();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Sessions_ReturnsViewWithSessionsWhenAuthenticated()
    {
        var userId = UserId.New();
        var (controller, _, sessionService, _) = CreateController(isAuthenticated: true, userId: userId);

        sessionService.Sessions.Add(new UserSessionDto(Guid.NewGuid(), "Browser", "127.0.0.1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true));

        var result = await controller.Sessions() as ViewResult;

        Assert.NotNull(result);
        var model = Assert.IsAssignableFrom<IReadOnlyList<UserSessionDto>>(result.Model);
        Assert.Single(model);
    }

    [Fact]
    public void ConfirmEmail_Get_DoesNotMutateEmailConfirmationStatus()
    {
        var (controller, identity, _, _) = CreateController(isAuthenticated: false);
        var validUserId = Guid.NewGuid().ToString();

        var result = controller.ConfirmEmail(validUserId, "sample-token") as ViewResult;

        Assert.NotNull(result);
        var model = Assert.IsType<ConfirmEmailViewModel>(result.Model);
        Assert.Equal(validUserId, model.UserId);
        Assert.Equal("sample-token", model.Token);

        // Verification MUST NOT happen on GET
        Assert.False(identity.ConfirmEmailCalled, "ConfirmEmail [HttpGet] must not call ConfirmEmailAsync on the identity service.");
    }

    [Fact]
    public void ConfirmEmail_Get_WithMissingParams_RedirectsToLogin()
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false);

        var res1 = controller.ConfirmEmail(null, "token") as RedirectToActionResult;
        Assert.NotNull(res1);
        Assert.Equal("Login", res1.ActionName);

        var res2 = controller.ConfirmEmail("uid", null) as RedirectToActionResult;
        Assert.NotNull(res2);
        Assert.Equal("Login", res2.ActionName);
    }

    [Fact]
    public async Task ConfirmEmail_Post_ConfirmsEmailWithoutAutoSigningIn()
    {
        var (controller, identity, _, currentUser) = CreateController(isAuthenticated: false);
        var validUserId = Guid.NewGuid().ToString();

        var model = new ConfirmEmailViewModel
        {
            UserId = validUserId,
            Token = "valid-token"
        };

        var result = await controller.ConfirmEmail(model) as ViewResult;

        Assert.NotNull(result);
        Assert.True(identity.ConfirmEmailCalled, "ConfirmEmail [HttpPost] must call ConfirmEmailAsync.");
        Assert.Equal(true, controller.ViewData["Confirmed"]);

        // Must NOT automatically sign in the user
        Assert.False(currentUser.IsAuthenticated);
    }

    [Theory]
    [InlineData("/Chat")]
    [InlineData("/Chat/Conversation/123")]
    [InlineData("/Settings")]
    public async Task Login_Post_WithLocalReturnUrl_RedirectsToLocalUrl(string localUrl)
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false);

        var model = new LoginViewModel { UserNameOrEmail = "alice", Password = "Password123!" };
        var result = await controller.Login(model, returnUrl: localUrl) as LocalRedirectResult;

        Assert.NotNull(result);
        Assert.Equal(localUrl, result.Url);
    }

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://evil.com/login")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("javascript:alert(1)")]
    public async Task Login_Post_WithNonLocalReturnUrl_RejectsRedirectAndFallsBackToChat(string externalUrl)
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false);

        var model = new LoginViewModel { UserNameOrEmail = "alice", Password = "Password123!" };
        var result = await controller.Login(model, returnUrl: externalUrl) as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Index", result.ActionName);
        Assert.Equal("Chat", result.ControllerName);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("this_username_is_way_too_long_for_registration_33_chars")]
    [InlineData("user@name")]
    [InlineData("user name")]
    [InlineData("user!name")]
    [InlineData("юзернейм")]
    public void RegisterViewModel_Validation_Fails_ForInvalidUsername(string invalidUsername)
    {
        var model = new RegisterViewModel
        {
            UserName = invalidUsername,
            Email = "test@example.com",
            Password = "Password123!",
            ConfirmPassword = "Password123!"
        };

        var context = new System.ComponentModel.DataAnnotations.ValidationContext(model);
        var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, context, validationResults, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(RegisterViewModel.UserName)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("valid_user-123.test")]
    [InlineData("username-exactly-32-chars-long12")]
    public void RegisterViewModel_Validation_Succeeds_ForValidUsername(string validUsername)
    {
        var model = new RegisterViewModel
        {
            UserName = validUsername,
            Email = "test@example.com",
            Password = "Password123!",
            ConfirmPassword = "Password123!"
        };

        var context = new System.ComponentModel.DataAnnotations.ValidationContext(model);
        var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, context, validationResults, validateAllProperties: true);

        Assert.True(isValid);
    }

    [Fact]
    public void RecoverAccount_Get_ReturnsViewResultWithViewModel()
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false);
        var result = controller.RecoverAccount() as ViewResult;
        Assert.NotNull(result);
        Assert.IsType<RecoverAccountViewModel>(result.Model);
    }

    [Fact]
    public async Task RecoverAccount_Post_ReturnsGenericMessage_AndRedirectsToLogin_RegardlessOfExistence()
    {
        var (controller, identity, _, _) = CreateController(isAuthenticated: false);
        identity.InitiateAccountRecoveryResultValue = false; // Nonexistent or active

        var result = await controller.RecoverAccount("any_user", "any_password") as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Login", result.ActionName);
        Assert.Equal(
            "If an eligible deleted account exists for those credentials, an account recovery link has been sent to your registered email address.",
            controller.TempData["SuccessMessage"]);
        Assert.True(identity.InitiateAccountRecoveryCalled);
    }

    [Fact]
    public void ConfirmRecovery_Get_DoesNotMutateState_AndRendersViewWithoutPII()
    {
        var (controller, identity, _, _) = CreateController(isAuthenticated: false);
        var uid = Guid.NewGuid().ToString();

        var result = controller.ConfirmRecovery(uid, "sample-token") as ViewResult;

        Assert.NotNull(result);
        var model = Assert.IsType<ConfirmRecoveryViewModel>(result.Model);
        Assert.Equal(uid, model.UserId);
        Assert.Equal("sample-token", model.Token);
        Assert.False(identity.ConfirmAccountRecoveryCalled, "GET /Account/ConfirmRecovery must NEVER mutate recovery state.");
    }

    [Fact]
    public void ConfirmRecovery_Get_WithMissingParams_RedirectsToLogin()
    {
        var (controller, _, _, _) = CreateController(isAuthenticated: false);

        var res1 = controller.ConfirmRecovery(null, "token") as RedirectToActionResult;
        Assert.NotNull(res1);
        Assert.Equal("Login", res1.ActionName);

        var res2 = controller.ConfirmRecovery("uid", null) as RedirectToActionResult;
        Assert.NotNull(res2);
        Assert.Equal("Login", res2.ActionName);
    }

    [Fact]
    public async Task ConfirmRecovery_Post_Succeeds_RedirectsToLogin_AndNeverAutoSignsIn()
    {
        var (controller, identity, _, currentUser) = CreateController(isAuthenticated: false);
        identity.ConfirmAccountRecoveryResultValue = true;

        var uid = Guid.NewGuid().ToString();
        var model = new ConfirmRecoveryViewModel { UserId = uid, Token = "valid-token" };

        var result = await controller.ConfirmRecovery(model) as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Login", result.ActionName);
        Assert.Equal("Your account has been successfully recovered! You can now log in.", controller.TempData["SuccessMessage"]);
        Assert.True(identity.ConfirmAccountRecoveryCalled);
        Assert.False(currentUser.IsAuthenticated, "User must NEVER be automatically signed in after recovery.");
    }

    [Fact]
    public async Task ConfirmRecovery_Post_OnFailure_RedirectsToLogin_WithErrorMessage()
    {
        var (controller, identity, _, _) = CreateController(isAuthenticated: false);
        identity.ConfirmAccountRecoveryResultValue = false;

        var uid = Guid.NewGuid().ToString();
        var model = new ConfirmRecoveryViewModel { UserId = uid, Token = "bad-token" };

        var result = await controller.ConfirmRecovery(model) as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Login", result.ActionName);
        Assert.Equal("Unable to recover account. The recovery link may be invalid, expired, or already used.", controller.TempData["ErrorMessage"]);
    }

    [Fact]
    public async Task EmailedLinks_UsePublicBaseUrl_EvenWithSpoofedHostHeader()
    {
        var (controller, _, _, _, emailSender) = CreateControllerWithEmailSender(isAuthenticated: false);

        // Attacker injects a malicious Host header
        controller.ControllerContext.HttpContext.Request.Host = new HostString("attacker.evil.com");
        controller.ControllerContext.HttpContext.Request.Scheme = "http";

        // 1. Register link
        var regModel = new RegisterViewModel
        {
            UserName = "valid_user",
            Email = "test@example.com",
            Password = "Password123!",
            ConfirmPassword = "Password123!"
        };
        await controller.Register(regModel);
        Assert.NotEmpty(emailSender.SentLinks);
        var regLink = emailSender.SentLinks.Last();
        Assert.StartsWith("https://localhost:7001/Account/ConfirmEmail", regLink);
        Assert.DoesNotContain("attacker.evil.com", regLink);

        // 2. Forgot password link
        var forgotModel = new ForgotPasswordViewModel { Email = "test@example.com" };
        await controller.ForgotPassword(forgotModel);
        var forgotLink = emailSender.SentLinks.Last();
        Assert.StartsWith("https://localhost:7001/Account/ResetPassword", forgotLink);
        Assert.DoesNotContain("attacker.evil.com", forgotLink);

        // 3. Recover account link
        await controller.RecoverAccount("valid_user", "Password123!");
        var recLink = emailSender.SentLinks.Last();
        Assert.StartsWith("https://localhost:7001/Account/ConfirmRecovery", recLink);
        Assert.DoesNotContain("attacker.evil.com", recLink);
    }

    [Fact]
    public async Task RevokeOtherSessions_UsesAuthoritativeSessionIdClaim_WithoutBindableCurrentIdParameter()
    {
        var method = typeof(AccountController).GetMethod(nameof(AccountController.RevokeOtherSessions));
        Assert.NotNull(method);
        Assert.Empty(method.GetParameters());

        var userId = UserId.New();
        var claimSessionId = Guid.NewGuid();
        var (controller, _, sessionService, _) = CreateController(isAuthenticated: true, userId: userId);
        controller.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("SessionId", claimSessionId.ToString()) },
                "TestAuth"));

        var result = await controller.RevokeOtherSessions() as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Sessions", result.ActionName);
        var call = Assert.Single(sessionService.RevokeOtherSessionsCalls);
        Assert.Equal(userId, call.UserId);
        Assert.Equal(claimSessionId, call.CurrentSessionId);
    }

    [Fact]
    public async Task RevokeOtherSessions_WhenSessionIdClaimMissing_ReturnsUnauthorized()
    {
        var userId = UserId.New();
        var (controller, _, sessionService, _) = CreateController(isAuthenticated: true, userId: userId);

        var result = await controller.RevokeOtherSessions();

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(sessionService.RevokeOtherSessionsCalls);
    }

    [Fact]
    public async Task DeleteAccount_WhenRealtimeNotifierThrows_LogsWarningWithUserIdOnly_AndRedirects()
    {
        var userId = UserId.New();
        var httpContext = new DefaultHttpContext();
        var logger = new RecordingAccountLogger();
        var controller = new AccountController(
            new FakeIdentityService(),
            new FakeSessionService(),
            new FakeCurrentUser { IsAuthenticated = true, Id = userId },
            new FakeEmailSender(),
            new ThrowingRealtimeNotifier(),
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            Url = new TestUrlHelper(),
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(httpContext, new FakeTempDataProvider())
        };

        var result = await controller.DeleteAccount() as RedirectToActionResult;

        Assert.NotNull(result);
        Assert.Equal("Login", result.ActionName);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains(userId.Value.ToString(), entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WhenLockedOut_ReturnsIdenticalGenericErrorAsWrongPassword_AndPostMutationsRequireFromForm()
    {
        var (lockoutController, _, _, _) = CreateController();
        var (wrongPwController, _, _, _) = CreateController();

        // Swap FakeIdentityService via dedicated controller instances built like CreateController
        var lockoutSvc = new FakeIdentityService { LoginIsLockedOut = true };
        var wrongPwSvc = new FakeIdentityService { LoginResult = false, LoginError = "Invalid username or password." };
        var lockoutHttp = new DefaultHttpContext();
        lockoutController = new AccountController(
            lockoutSvc,
            new FakeSessionService(),
            new FakeCurrentUser(),
            new FakeEmailSender(),
            logger: new RecordingAccountLogger())
        {
            ControllerContext = new ControllerContext { HttpContext = lockoutHttp },
            Url = new TestUrlHelper(),
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(lockoutHttp, new FakeTempDataProvider())
        };

        var wrongPwHttp = new DefaultHttpContext();
        wrongPwController = new AccountController(
            wrongPwSvc,
            new FakeSessionService(),
            new FakeCurrentUser(),
            new FakeEmailSender(),
            logger: new RecordingAccountLogger())
        {
            ControllerContext = new ControllerContext { HttpContext = wrongPwHttp },
            Url = new TestUrlHelper(),
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(wrongPwHttp, new FakeTempDataProvider())
        };

        await lockoutController.Login(new LoginViewModel { UserNameOrEmail = "locked", Password = "Wrong1!" });
        await wrongPwController.Login(new LoginViewModel { UserNameOrEmail = "normal", Password = "Wrong1!" });

        var lockoutErr = Assert.Single(lockoutController.ModelState[string.Empty]!.Errors).ErrorMessage;
        var wrongPwErr = Assert.Single(wrongPwController.ModelState[string.Empty]!.Errors).ErrorMessage;
        Assert.Equal("Invalid username or password.", lockoutErr);
        Assert.Equal(wrongPwErr, lockoutErr);

        // Verify email-triggering/sensitive POST actions strictly bind from [FromForm]
        foreach (var methodName in new[] { "Register", "ConfirmEmail", "ForgotPassword", "ResetPassword", "RecoverAccount", "ConfirmRecovery" })
        {
            var postMethod = typeof(AccountController).GetMethods()
                .Single(m => m.Name == methodName && m.IsDefined(typeof(HttpPostAttribute), false));
            var parameters = postMethod.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
            Assert.NotEmpty(parameters);
            Assert.All(parameters, p => Assert.True(p.IsDefined(typeof(FromFormAttribute), false),
                $"{methodName} parameter '{p.Name}' must carry [FromForm]."));
        }
    }

    private sealed class ThrowingRealtimeNotifier : IRealtimeNotifier
    {
        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyRecordingAsync(UserId recipientUserId, RecordingIndicatorDto recording, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyVoicePlayedAsync(ConversationId conversationId, UserId callerUserId, UserId uploaderUserId, VoicePlayedDto played, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated SignalR failure");
    }

    private sealed class RecordingAccountLogger : Microsoft.Extensions.Logging.ILogger<AccountController>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
