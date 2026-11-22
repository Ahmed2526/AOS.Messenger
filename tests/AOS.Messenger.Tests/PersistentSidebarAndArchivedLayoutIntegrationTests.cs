using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Web.ViewComponents;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class PersistentSidebarAndArchivedLayoutIntegrationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public PersistentSidebarAndArchivedLayoutIntegrationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthenticatedUser_Get_AllEightEndpoints_ReturnsOk_WithSingleSidebar_PeerName_And_NoEmptyState()
    {
        // 1. Seed two verified users (UserA and UserB) using ApplicationDbContext
        var userA = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "user_a",
            DisplayName = "Alice In Chains",
            Email = "usera@example.com",
            EmailConfirmed = true,
            NormalizedUserName = "USER_A",
            NormalizedEmail = "USERA@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString()
        };

        var userB = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "user_b",
            DisplayName = "Bob Dylan",
            Email = "userb@example.com",
            EmailConfirmed = true,
            NormalizedUserName = "USER_B",
            NormalizedEmail = "USERB@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString()
        };

        var conv = Conversation.CreateDirect(new UserId(userA.Id), new UserId(userB.Id), DateTimeOffset.UtcNow);

        await using (var db = _fixture.CreateDbContext())
        {
            db.Users.AddRange(userA, userB);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();

            var msg = Message.Create(conv.Id, new UserId(userA.Id), "msg-idemp-1", "Hey Bob!", DateTimeOffset.UtcNow);
            db.Messages.Add(msg);

            var sessionId = Guid.NewGuid();
            var session = UserSession.Create(sessionId, userA.Id, "Test Device", "127.0.0.1", "TestAgent", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
            db.UserSessions.Add(session);

            await db.SaveChangesAsync();

            TestAuthHandler.SetUser(userA.Id, userA.UserName!, userA.DisplayName!, sessionId, userA.Email!);
        }

        // 2. Configure WebApplicationFactory<Program>
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.UseSetting("PublicBaseUrl", "https://localhost:7001");

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
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<ApplicationDbContext>));
                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(_fixture.ConnectionString));

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

        // 3. Issue HTTP GET to ALL 8 authenticated endpoints
        var endpoints = new[]
        {
            "/Chat",
            $"/Chat/Conversation/{conv.Id.Value}",
            "/Chat/NewConversation",
            "/Chat/Archived",
            "/Settings",
            "/Settings/BlockedUsers",
            "/Profile",
            "/Account/Sessions"
        };

        foreach (var endpoint in endpoints)
        {
            var response = await client.GetAsync(endpoint);
            var content = await response.Content.ReadAsStringAsync();

            Assert.True(HttpStatusCode.OK == response.StatusCode,
                $"Expected 200 OK for '{endpoint}', but received {response.StatusCode}. Response content: {content}");
            Assert.True(Regex.Matches(content, "id=\"conversationSearchInput\"").Count == 1);
            Assert.Single(Regex.Matches(content, "id=\"conversationSearchInput\""));
            Assert.Contains("Bob Dylan", content);
            Assert.DoesNotContain("No conversations yet", content);
        }

        // 4. Issue HTTP GET for unauthenticated request
        var unauthenticatedClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        unauthenticatedClient.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        var loginResponse = await unauthenticatedClient.GetAsync("/Account/Login");
        var loginContent = await loginResponse.Content.ReadAsStringAsync();

        Assert.True(HttpStatusCode.OK == loginResponse.StatusCode,
            $"Expected 200 OK for '/Account/Login', but received {loginResponse.StatusCode}. Response content: {loginContent}");
        Assert.True(Regex.Matches(loginContent, "id=\"conversationSearchInput\"").Count == 0);
        Assert.Empty(Regex.Matches(loginContent, "id=\"conversationSearchInput\""));
    }

    [Fact]
    public async Task UnauthenticatedUser_Get_Login_ReturnsOk_WithZeroSidebar()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.UseSetting("PublicBaseUrl", "https://localhost:7001");

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
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<ApplicationDbContext>));
                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(_fixture.ConnectionString));
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Account/Login");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Regex.Matches(content, "id=\"conversationSearchInput\"").Count == 0);
        Assert.Empty(Regex.Matches(content, "id=\"conversationSearchInput\""));
    }

    [Fact]
    public void ConversationSidebarViewComponent_HasZeroReferencesTo_InfrastructureOrDbContext()
    {
        var componentType = typeof(ConversationSidebarViewComponent);
        Assert.Equal("AOS.Messenger.Web", componentType.Assembly.GetName().Name);

        var ctors = componentType.GetConstructors();
        Assert.NotEmpty(ctors);
        foreach (var ctor in ctors)
        {
            foreach (var parameter in ctor.GetParameters())
            {
                var paramType = parameter.ParameterType;
                var paramNamespace = paramType.Namespace ?? string.Empty;
                Assert.DoesNotContain("Infrastructure", paramNamespace, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("EntityFrameworkCore", paramNamespace, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual("ApplicationDbContext", paramType.Name);
            }
        }

        var fields = componentType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var field in fields)
        {
            var fieldType = field.FieldType;
            var fieldNamespace = fieldType.Namespace ?? string.Empty;
            Assert.DoesNotContain("Infrastructure", fieldNamespace, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EntityFrameworkCore", fieldNamespace, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("ApplicationDbContext", fieldType.Name);
        }
    }

    [Fact]
    public void Views_ContainNoInlineStyles_And_RespectCspGuardrails()
    {
        var slnRoot = FindSolutionRoot();
        var viewsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views");
        Assert.True(Directory.Exists(viewsDir));

        var cshtmlFiles = Directory.GetFiles(viewsDir, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(cshtmlFiles);

        var violations = new List<string>();
        var styleAttrRegex = new Regex(@"\bstyle\s*=", RegexOptions.IgnoreCase);

        foreach (var file in cshtmlFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("@*") || trimmed.StartsWith("*"))
                {
                    continue;
                }

                if (styleAttrRegex.IsMatch(line))
                {
                    var relPath = Path.GetRelativePath(slnRoot, file);
                    violations.Add($"File '{relPath}:{i + 1}' contains forbidden inline style attribute: {trimmed}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} inline style attributes in views:\n" + string.Join("\n", violations));
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx"))
                               && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.sln"))
                               && !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        if (current == null)
        {
            throw new InvalidOperationException("Could not find solution root directory containing AOS.Messenger.slnx");
        }

        return current.FullName;
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private static Guid _userId;
        private static string _userName = string.Empty;
        private static string _displayName = string.Empty;
        private static Guid _sessionId;
        private static string _email = string.Empty;

        public static void SetUser(Guid userId, string userName, string displayName, Guid sessionId, string email)
        {
            _userId = userId;
            _userName = userName;
            _displayName = displayName;
            _sessionId = sessionId;
            _email = email;
        }

        public static void ClearUser()
        {
            _userId = Guid.Empty;
            _userName = string.Empty;
            _displayName = string.Empty;
            _sessionId = Guid.Empty;
            _email = string.Empty;
        }

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.ContainsKey("X-Test-Anonymous") || _userId == Guid.Empty)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim(ClaimTypes.Name, _userName),
                new Claim("DisplayName", _displayName),
                new Claim("SessionId", _sessionId.ToString()),
                new Claim(ClaimTypes.Email, _email)
            };
            var identity = new ClaimsIdentity(claims, "TestScheme");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "TestScheme");

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
