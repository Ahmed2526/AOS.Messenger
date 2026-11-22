using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Models;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class FrontendSanitizationAndSecurityTests
{
    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.sln"))
                               && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx"))
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

    [Fact]
    public void Views_ContainNoHtmlRaw()
    {
        var slnRoot = FindSolutionRoot();
        var viewsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views");
        Assert.True(Directory.Exists(viewsDir), $"Views directory does not exist: {viewsDir}");

        var cshtmlFiles = Directory.GetFiles(viewsDir, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(cshtmlFiles);

        var violations = new List<string>();
        foreach (var file in cshtmlFiles)
        {
            var content = File.ReadAllText(file);
            var stripped = Regex.Replace(content, @"@\*[\s\S]*?\*@", string.Empty);
            if (stripped.Contains("@Html.Raw", StringComparison.OrdinalIgnoreCase) ||
                stripped.Contains("Html.Raw(", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(slnRoot, file);
                violations.Add($"File '{relativePath}' contains Html.Raw");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} files violating '@Html.Raw' policy:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void JavaScript_ContainsNoInnerHTML_OuterHTML_Or_InsertAdjacentHTML()
    {
        var slnRoot = FindSolutionRoot();
        var jsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js");
        Assert.True(Directory.Exists(jsDir), $"JavaScript directory does not exist: {jsDir}");

        var jsFiles = Directory.GetFiles(jsDir, "*.js", SearchOption.AllDirectories);
        Assert.NotEmpty(jsFiles);

        var forbiddenPatterns = new[]
        {
            @"\binnerHTML\b",
            @"\bouterHTML\b",
            @"\binsertAdjacentHTML\b"
        };

        var violations = new List<string>();
        foreach (var file in jsFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                {
                    continue;
                }

                foreach (var pattern in forbiddenPatterns)
                {
                    if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                    {
                        var relativePath = Path.GetRelativePath(slnRoot, file);
                        violations.Add($"File '{relativePath}:{i + 1}' matches forbidden DOM mutation pattern '{pattern}': {trimmed}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} JavaScript lines violating safe DOM manipulation policy:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Views_ContainNoInlineScriptBlocks_ExceptApplicationJson()
    {
        var slnRoot = FindSolutionRoot();
        var viewsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views");
        Assert.True(Directory.Exists(viewsDir), $"Views directory does not exist: {viewsDir}");

        var cshtmlFiles = Directory.GetFiles(viewsDir, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(cshtmlFiles);

        var violations = new List<string>();
        var scriptBlockRegex = new Regex(@"<script(?<attrs>[^>]*)>(?<body>[\s\S]*?)<\/script>", RegexOptions.IgnoreCase);

        foreach (var file in cshtmlFiles)
        {
            var content = File.ReadAllText(file);
            var stripped = Regex.Replace(content, @"@\*[\s\S]*?\*@", string.Empty);

            var matches = scriptBlockRegex.Matches(stripped);
            foreach (Match match in matches)
            {
                var attrs = match.Groups["attrs"].Value;
                var body = match.Groups["body"].Value.Trim();

                var isJson = Regex.IsMatch(attrs, @"\btype\s*=\s*[""']application/json[""']", RegexOptions.IgnoreCase);
                var hasSrc = Regex.IsMatch(attrs, @"\bsrc\s*=", RegexOptions.IgnoreCase);

                if (isJson)
                {
                    continue;
                }

                if (hasSrc)
                {
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var relativePath = Path.GetRelativePath(slnRoot, file);
                        violations.Add($"File '{relativePath}' contains <script src=...> with non-empty inline body:\n{match.Value}");
                    }
                    continue;
                }

                var relPath = Path.GetRelativePath(slnRoot, file);
                violations.Add($"File '{relPath}' contains forbidden inline <script> block:\n{match.Value}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} forbidden inline script blocks:\n" + string.Join("\n\n", violations));
    }

    [Fact]
    public void Views_ContainNoInlineOnClickAttributes()
    {
        var slnRoot = FindSolutionRoot();
        var viewsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views");
        Assert.True(Directory.Exists(viewsDir), $"Views directory does not exist: {viewsDir}");

        var cshtmlFiles = Directory.GetFiles(viewsDir, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(cshtmlFiles);

        var violations = new List<string>();
        var onclickRegex = new Regex(@"\bonclick\s*=", RegexOptions.IgnoreCase);

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

                if (onclickRegex.IsMatch(line))
                {
                    var relativePath = Path.GetRelativePath(slnRoot, file);
                    violations.Add($"File '{relativePath}:{i + 1}' contains forbidden inline onclick handler: {trimmed}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} inline onclick handlers:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Views_ContainNoTailwindCdnScripts()
    {
        var slnRoot = FindSolutionRoot();
        var viewsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views");
        Assert.True(Directory.Exists(viewsDir), $"Views directory does not exist: {viewsDir}");

        var cshtmlFiles = Directory.GetFiles(viewsDir, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(cshtmlFiles);

        var violations = new List<string>();
        var cdnRegex = new Regex(@"cdn\.tailwindcss\.com", RegexOptions.IgnoreCase);

        foreach (var file in cshtmlFiles)
        {
            var content = File.ReadAllText(file);
            var stripped = Regex.Replace(content, @"@\*[\s\S]*?\*@", string.Empty);

            if (cdnRegex.IsMatch(stripped))
            {
                var relativePath = Path.GetRelativePath(slnRoot, file);
                violations.Add($"File '{relativePath}' references Tailwind CDN (cdn.tailwindcss.com)");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} Tailwind CDN references:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void IMessagePersistence_GetByIdAsync_RequiresConversationId()
    {
        // 1. Check IMessagePersistence interface contract
        var interfaceMethod = typeof(IMessagePersistence).GetMethod(nameof(IMessagePersistence.GetByIdAsync));
        Assert.NotNull(interfaceMethod);

        var interfaceParams = interfaceMethod.GetParameters();
        Assert.Contains(interfaceParams, p => p.ParameterType == typeof(ConversationId));
        Assert.Contains(interfaceParams, p => p.ParameterType == typeof(MessageId));
        Assert.Equal(typeof(ConversationId), interfaceParams[0].ParameterType);
        Assert.Equal(typeof(MessageId), interfaceParams[1].ParameterType);

        // 2. Check MessagePersistence concrete repository implementation
        var repoMethod = typeof(MessagePersistence).GetMethod(nameof(MessagePersistence.GetByIdAsync));
        Assert.NotNull(repoMethod);

        var repoParams = repoMethod.GetParameters();
        Assert.Contains(repoParams, p => p.ParameterType == typeof(ConversationId));
        Assert.Contains(repoParams, p => p.ParameterType == typeof(MessageId));
        Assert.Equal(typeof(ConversationId), repoParams[0].ParameterType);
        Assert.Equal(typeof(MessageId), repoParams[1].ParameterType);
    }

    [Fact]
    public void DisplayName_ViewModel_CappedAt64Characters()
    {
        var prop = typeof(UpdateProfileViewModel).GetProperty(nameof(UpdateProfileViewModel.DisplayName));
        Assert.NotNull(prop);
        var stringLengthAttr = prop.GetCustomAttribute<StringLengthAttribute>();
        Assert.NotNull(stringLengthAttr);
        Assert.Equal(64, stringLengthAttr.MaximumLength);
    }

    [Fact]
    public void RegisterViewModel_UserName_EnforcesAsciiRegex()
    {
        var prop = typeof(RegisterViewModel).GetProperty(nameof(RegisterViewModel.UserName));
        Assert.NotNull(prop);
        var regexAttr = prop.GetCustomAttribute<RegularExpressionAttribute>();
        Assert.NotNull(regexAttr);
        Assert.Equal(@"^[a-zA-Z0-9._-]{3,32}$", regexAttr.Pattern);
    }

    [Fact]
    public void UserSearchResultDto_DoesNotExposeEmail()
    {
        var properties = typeof(UserSearchResultDto).GetProperties();
        Assert.DoesNotContain(properties, p => p.Name.Contains("Email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChatController_SendMessage_HasMessagesRateLimitingAttribute()
    {
        var method = typeof(ChatController).GetMethod(nameof(ChatController.SendMessage));
        Assert.NotNull(method);
        var rateLimitAttr = method.GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name == "EnableRateLimitingAttribute");
        Assert.NotNull(rateLimitAttr);
        var policyProp = rateLimitAttr.GetType().GetProperty("PolicyName");
        Assert.Equal("messages", policyProp?.GetValue(rateLimitAttr));
    }

    [Fact]
    public void Layout_LoadsThemeInitInHead_AndThemeModuleNearBody()
    {
        var slnRoot = FindSolutionRoot();
        var layoutPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Shared", "_Layout.cshtml");
        Assert.True(File.Exists(layoutPath));

        var content = File.ReadAllText(layoutPath);

        // Pre-paint theme-init must be loaded in <head> without defer or module
        var headMatch = Regex.Match(content, @"<head[\s\S]*?<\/head>", RegexOptions.IgnoreCase);
        Assert.True(headMatch.Success);
        Assert.Contains("theme-init.js", headMatch.Value);
        Assert.DoesNotMatch(@"theme-init\.js[^>]*\b(defer|type=""module"")", headMatch.Value);

        // Modular theme.js loaded before </body>
        Assert.Contains("theme.js", content);
        Assert.Matches(@"<script[^>]*type=""module""[^>]*theme\.js", content);

        // Theme toggle button uses data-theme-toggle
        Assert.Matches(@"<button[^>]*id=""themeToggleBtn""[^>]*data-theme-toggle", content);
    }

    [Fact]
    public void Tailwind_AppCss_EnablesCustomVariantDark()
    {
        var slnRoot = FindSolutionRoot();
        var appCssPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Styles", "app.css");
        Assert.True(File.Exists(appCssPath));

        var content = File.ReadAllText(appCssPath);
        Assert.Contains("@custom-variant dark (&:where(.dark, .dark *));", content);
    }

    [Fact]
    public void Theme_ToggleButtons_UseDataThemeToggleAttributes()
    {
        var slnRoot = FindSolutionRoot();
        var settingsPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Settings", "Index.cshtml");
        Assert.True(File.Exists(settingsPath));
        var settingsContent = File.ReadAllText(settingsPath);
        Assert.Contains("data-theme-toggle=\"light\"", settingsContent);
        Assert.Contains("data-theme-toggle=\"dark\"", settingsContent);
        Assert.Contains("data-theme-toggle=\"system\"", settingsContent);

        var profilePath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Profile", "Index.cshtml");
        Assert.True(File.Exists(profilePath));
        var profileContent = File.ReadAllText(profilePath);
        Assert.Contains("data-theme-toggle=\"light\"", profileContent);
        Assert.Contains("data-theme-toggle=\"dark\"", profileContent);
        Assert.Contains("data-theme-toggle=\"system\"", profileContent);
    }

    [Fact]
    public void Layout_BrandLabel_HasWhitespaceNowrapAndNoHorizontalOverflow()
    {
        var slnRoot = FindSolutionRoot();
        var layoutPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Shared", "_Layout.cshtml");
        Assert.True(File.Exists(layoutPath));

        var content = File.ReadAllText(layoutPath);
        Assert.Matches(@"<a[^>]*asp-controller=""Chat""[^>]*asp-action=""Index""[^>]*class=""[^""]*whitespace-nowrap[^""]*""[\s\S]*?>[\s\S]*?A♡S Messenger[\s\S]*?<\/a>", content);

        // Header has overflow-x-clip and max-w-full
        Assert.Contains("overflow-x-clip", content);
        Assert.Contains("max-w-full", content);

        // Window controls hidden on mobile
        Assert.Contains("hidden sm:flex items-center gap-1.5 shrink-0", content);

        // Main element is overflow-hidden on conversation route
        Assert.Contains("isConversation", content);
        Assert.Contains("overflow-hidden", content);

        // Div tags balanced
        var openDivs = Regex.Matches(content, "<div[\\s>]").Count;
        var closeDivs = Regex.Matches(content, "</div>").Count;
        Assert.Equal(openDivs, closeDivs);
    }

    [Fact]
    public void Conversation_Header_HasPeerIdentityBlockAndResponsiveActions()
    {
        var slnRoot = FindSolutionRoot();
        var conversationPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        Assert.True(File.Exists(conversationPath));

        var content = File.ReadAllText(conversationPath);

        // Peer identity container is flex-1 min-w-0 overflow-hidden with truncate
        Assert.Contains("id=\"peer-identity-block\"", content);
        Assert.Contains("flex-1 min-w-0 overflow-hidden", content);

        // Chat header is shrink-0
        Assert.Contains("id=\"active-conversation-header\"", content);
        Assert.Contains("shrink-0", content);

        // Message stream is flex-1 min-h-0 overflow-y-auto
        Assert.Contains("id=\"message-stream\"", content);
        Assert.Contains("flex-1 min-h-0 overflow-y-auto", content);

        // Composer is shrink-0
        Assert.Contains("id=\"active-conversation-composer\"", content);

        // Desktop action toolbar
        Assert.Contains("hidden md:flex items-center gap-1 shrink-0", content);

        // Mobile overflow menu toggle and menu container
        Assert.Contains("data-chat-actions-toggle", content);
        Assert.Contains("data-chat-actions-menu", content);
        Assert.Contains("md:hidden shrink-0", content);

        // All 5 actions present in mobile menu
        Assert.Contains("asp-action=\"Pin\"", content);
        Assert.Contains("asp-action=\"Mute\"", content);
        Assert.Contains("asp-action=\"Archive\"", content);
        Assert.Contains("asp-action=\"Clear\"", content);
        Assert.Contains("asp-action=\"Delete\"", content);

        // chat-actions.js script module loaded
        Assert.Contains("chat-actions.js", content);

        // Div tags balanced (no premature closing of conversation-page-root)
        var openDivs = Regex.Matches(content, "<div[\\s>]").Count;
        var closeDivs = Regex.Matches(content, "</div>").Count;
        Assert.Equal(openDivs, closeDivs);
    }

    [Fact]
    public void Profile_HeroBannerCard_HasUnclippedAvatarAndRounded2xl()
    {
        var slnRoot = FindSolutionRoot();
        var profilePath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Profile", "Index.cshtml");
        Assert.True(File.Exists(profilePath));

        var content = File.ReadAllText(profilePath);

        // Hero card container
        Assert.Contains("relative overflow-hidden rounded-2xl p-6", content);
        Assert.Contains("id=\"profile-hero-card\"", content);

        // Decorative background layer
        Assert.Contains("pointer-events-none absolute inset-0 z-0", content);

        // Foreground content row with avatar
        Assert.Contains("relative z-10 flex items-center gap-4 sm:gap-5", content);
        Assert.Contains("id=\"profile-hero-avatar\"", content);
        Assert.Contains("w-16 h-16 sm:w-20 sm:h-20 rounded-full shrink-0 flex items-center justify-center text-xl font-semibold", content);

        // No negative vertical offsets on avatar foreground container
        var foregroundIdx = content.IndexOf("relative z-10 flex items-center");
        var nameIdx = content.IndexOf("@(Model?.DisplayName ?? \"My Profile\")");
        var avatarRegion = content.Substring(foregroundIdx, nameIdx - foregroundIdx);
        Assert.DoesNotContain("-bottom-", avatarRegion);
        Assert.DoesNotContain("translate-y-", avatarRegion);
    }
}
