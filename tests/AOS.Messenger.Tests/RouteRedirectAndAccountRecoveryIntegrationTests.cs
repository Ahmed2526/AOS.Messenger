using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class RouteRedirectAndAccountRecoveryIntegrationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public RouteRedirectAndAccountRecoveryIntegrationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_RecoverAccount_ReturnsOk_WithAntiforgeryFormMarkup()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001"
                    });
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Account/RecoverAccount");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/Account/RecoverAccount\"", content);
        Assert.Contains("__RequestVerificationToken", content);
        Assert.Contains("UsernameOrEmail", content);
        Assert.Contains("Password", content);
    }

    [Fact]
    public async Task Get_ChatNew_WhenUnauthenticated_RedirectsToLogin()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001"
                    });
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Chat/New");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/Login", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Get_ChatNew_WhenAuthenticated_RedirectsToNewConversation()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001"
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = "TestScheme";
                        options.DefaultChallengeScheme = "TestScheme";
                        options.DefaultScheme = "TestScheme";
                    });
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Chat/New");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/Chat/NewConversation", response.Headers.Location.OriginalString);
    }

    [Fact]
    public async Task AccountAuthFormEndpoints_EnforceRequestSizeLimitBeforeRateLimitFormParsing()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001"
                    });
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        // 1. Oversized Content-Length form POST to ForgotPassword -> 413 Payload Too Large before form parsing / email extraction
        var oversizedBody = "Email=" + new string('a', 20 * 1024);
        using (var content = new StringContent(oversizedBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"))
        {
            var resp = await client.PostAsync("/Account/ForgotPassword", content);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        }

        // 2. Oversized chunked (no Content-Length) form POST to ForgotPassword -> 413 Payload Too Large
        using (var streamContent = new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(oversizedBody))))
        {
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/Account/ForgotPassword")
            {
                Content = streamContent
            };
            req.Headers.TransferEncodingChunked = true;

            var chunkedResp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResp.StatusCode);
        }

        // 3. Malformed multipart/form-data (missing boundary) -> 400 Bad Request from pre-rate-limit validation, never 500
        using (var badMultipart = new StringContent("--invalid-body", System.Text.Encoding.UTF8, "multipart/form-data"))
        {
            var badResp = await client.PostAsync("/Account/ForgotPassword", badMultipart);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
            var body = await badResp.Content.ReadAsStringAsync();
            Assert.Contains("malformed or invalid", body);
        }

        // 4. Non-dual-email POST (e.g. /Account/Login) still enforces [RequestSizeLimit(16 * 1024)] -> 413
        using (var loginContent = new StringContent(oversizedBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"))
        {
            var loginResp = await client.PostAsync("/Account/Login", loginContent);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, loginResp.StatusCode);
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "testuser"),
                new Claim("SessionId", Guid.NewGuid().ToString())
            };
            var identity = new ClaimsIdentity(claims, "TestScheme");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "TestScheme");

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
