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

    [Fact]
    public void MaterialSymbolsCss_IsLayeredUnderComponents_SoTailwindHiddenAndSizeUtilitiesWin()
    {
        var slnRoot = FindSolutionRoot();
        var cssFiles = new[]
        {
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Styles", "app.css"),
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "css", "stitch-theme.css"),
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "css", "app.css")
        };

        foreach (var path in cssFiles)
        {
            Assert.True(File.Exists(path), $"Expected CSS file to exist: {path}");
            var css = File.ReadAllText(path);

            var componentBlocks = ExtractCssLayerBlocks(css, "components");
            Assert.NotEmpty(componentBlocks);

            var allMatches = Regex.Matches(css, @"\.material-symbols-outlined\b");
            Assert.True(allMatches.Count > 0, $"Expected .material-symbols-outlined rule in {path}");

            foreach (Match match in allMatches)
            {
                var insideComponentsLayer = componentBlocks.Any(
                    block => match.Index >= block.Start && match.Index < block.End);
                Assert.True(
                    insideComponentsLayer,
                    $"Unlayered .material-symbols-outlined found at index {match.Index} in {path}; must reside inside @layer components so Tailwind @layer utilities (.hidden, text-[...]) win.");
            }
        }

        var compiledCss = File.ReadAllText(cssFiles[2]);
        var utilityBlocks = ExtractCssLayerBlocks(compiledCss, "utilities");
        Assert.NotEmpty(utilityBlocks);
        Assert.True(
            utilityBlocks.Any(block => Regex.IsMatch(block.Content, @"\.hidden\s*\{\s*display:\s*none\b")),
            "Expected compiled Tailwind utilities layer in wwwroot/css/app.css to define .hidden { display: none }.");
    }

    [Fact]
    public void CustomCssFiles_ContainZeroUnlayeredStyleRules_SoTailwindUtilitiesAlwaysWin()
    {
        var slnRoot = FindSolutionRoot();
        var customCssFiles = new[]
        {
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Styles", "app.css"),
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "css", "stitch-theme.css"),
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "css", "site.css")
        };

        foreach (var path in customCssFiles)
        {
            Assert.True(File.Exists(path), $"Expected CSS file to exist: {path}");
            var css = File.ReadAllText(path);
            var unlayeredRemainder = StripCommentsAndSafeTopLevelAtRules(css).Trim();
            Assert.True(
                string.IsNullOrWhiteSpace(unlayeredRemainder),
                $"Unlayered CSS rules found in {path}; wrap rules in @layer/@utility/@theme so Tailwind utilities always win. Offending CSS: {unlayeredRemainder}");
        }

        var compiledPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "css", "app.css");
        Assert.True(File.Exists(compiledPath));
        var compiledCss = File.ReadAllText(compiledPath);

        var baseCss = string.Concat(ExtractCssLayerBlocks(compiledCss, "base").Select(b => b.Content));
        Assert.Matches(@"\.dark\s*\{[^}]*--color-surface:", baseCss);
        Assert.Matches(@"\.dark\s*\{[^}]*--text-color-primary:", baseCss);
        Assert.Matches(@"\.dark\s*\{[^}]*--text-color-error:", baseCss);

        var utilitiesCss = string.Concat(ExtractCssLayerBlocks(compiledCss, "utilities").Select(b => b.Content));
        Assert.False(string.IsNullOrWhiteSpace(utilitiesCss));
        Assert.Contains(".text-primary{color:var(--text-color-primary)}", utilitiesCss);
        Assert.Contains(".text-error{color:var(--text-color-error)}", utilitiesCss);

        var requiredUtilities = new[]
        {
            ".bg-surface-bright",
            ".bg-surface-lowest",
            ".bg-surface-low",
            ".bg-surface-container-lowest",
            @".peer-checked\:bg-primary-container",
            @".after\:bg-surface-bright",
            @".hover\:bg-primary\/90",
            ".text-primary",
            ".text-error",
            @".hover\:text-primary",
            @".dark\:text-red-400",
            ".pb-safe",
            ".pt-safe",
            ".icon-filled",
            ".p-gutter"
        };

        foreach (var utilitySelector in requiredUtilities)
        {
            Assert.Contains(utilitySelector, utilitiesCss);
        }

        Assert.Matches(@"\.font-headline\s*\{", utilitiesCss);

        var textPrimaryIdx = utilitiesCss.IndexOf(".text-primary{", StringComparison.Ordinal);
        var hoverTextPrimaryIdx = utilitiesCss.IndexOf(@".hover\:text-primary", StringComparison.Ordinal);
        Assert.True(textPrimaryIdx >= 0 && hoverTextPrimaryIdx > textPrimaryIdx);

        var textErrorIdx = utilitiesCss.IndexOf(".text-error{", StringComparison.Ordinal);
        var darkTextRedIdx = utilitiesCss.IndexOf(@".dark\:text-red-400", StringComparison.Ordinal);
        Assert.True(textErrorIdx >= 0 && darkTextRedIdx > textErrorIdx);

        var bgSurfaceVariantIdx = utilitiesCss.IndexOf(".bg-surface-variant{", StringComparison.Ordinal);
        var bgSurfaceContainerHighestIdx = utilitiesCss.IndexOf(".bg-surface-container-highest{", StringComparison.Ordinal);
        var peerCheckedIdx = utilitiesCss.IndexOf(@".peer-checked\:bg-primary-container", StringComparison.Ordinal);
        Assert.True(bgSurfaceVariantIdx >= 0 && peerCheckedIdx > bgSurfaceVariantIdx);
        Assert.True(bgSurfaceContainerHighestIdx >= 0 && peerCheckedIdx > bgSurfaceContainerHighestIdx);
    }

    private static List<(int Start, int End, string Content)> ExtractCssLayerBlocks(string css, string layerName)
    {
        var blocks = new List<(int Start, int End, string Content)>();
        var matches = Regex.Matches(css, $@"@layer\s+{Regex.Escape(layerName)}\s*\{{");

        foreach (Match match in matches)
        {
            var openBraceIdx = match.Index + match.Length - 1;
            var closeBraceIdx = FindMatchingBraceEnd(css, openBraceIdx);
            if (closeBraceIdx >= 0)
            {
                blocks.Add((openBraceIdx, closeBraceIdx + 1, css.Substring(openBraceIdx, closeBraceIdx + 1 - openBraceIdx)));
            }
        }

        return blocks;
    }

    private static string StripCommentsAndSafeTopLevelAtRules(string css)
    {
        var safeAtRulePattern = new Regex(
            @"^@(import|custom-variant|font-face|theme|layer|utility|keyframes|property)(?=[\s;{(])",
            RegexOptions.CultureInvariant);
        var remainder = new List<char>(css.Length);

        for (var i = 0; i < css.Length; i++)
        {
            var ch = css[i];
            if (ch == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var endComment = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (endComment < 0)
                {
                    break;
                }

                i = endComment + 1;
                continue;
            }

            if (ch == '@' && safeAtRulePattern.IsMatch(css.AsSpan(i)))
            {
                var parenDepth = 0;
                char? quote = null;
                var escaped = false;
                var skipped = false;

                for (var j = i; j < css.Length; j++)
                {
                    var inner = css[j];
                    if (TrySkipNonCodeChar(css, ref j, ref quote, ref escaped, ref inner))
                    {
                        continue;
                    }

                    if (inner == '(')
                    {
                        parenDepth++;
                    }
                    else if (inner == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    else if (parenDepth == 0 && inner == ';')
                    {
                        i = j;
                        skipped = true;
                        break;
                    }
                    else if (parenDepth == 0 && inner == '{')
                    {
                        var closeBraceIdx = FindMatchingBraceEnd(css, j);
                        i = closeBraceIdx >= 0 ? closeBraceIdx : css.Length - 1;
                        skipped = true;
                        break;
                    }
                }

                if (skipped)
                {
                    continue;
                }
            }

            remainder.Add(ch);
        }

        return new string(remainder.ToArray());
    }

    private static int FindMatchingBraceEnd(string css, int openBraceIdx)
    {
        var depth = 0;
        char? quote = null;
        var escaped = false;

        for (var i = openBraceIdx; i < css.Length; i++)
        {
            var ch = css[i];
            if (TrySkipNonCodeChar(css, ref i, ref quote, ref escaped, ref ch))
            {
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool TrySkipNonCodeChar(string css, ref int index, ref char? quote, ref bool escaped, ref char ch)
    {
        if (escaped)
        {
            escaped = false;
            return true;
        }

        if (ch == '\\')
        {
            escaped = true;
            return true;
        }

        if (quote.HasValue)
        {
            if (ch == quote.Value)
            {
                quote = null;
            }

            return true;
        }

        if (ch == '/' && index + 1 < css.Length && css[index + 1] == '*')
        {
            var endComment = css.IndexOf("*/", index + 2, StringComparison.Ordinal);
            index = endComment >= 0 ? endComment + 1 : css.Length - 1;
            return true;
        }

        if (ch is '\'' or '"')
        {
            quote = ch;
            return true;
        }

        return false;
    }

    [Fact]
    public void OutgoingMessageBubble_RendersActionsBarBeforeBubble_ForFlushRightAlignment()
    {
        var slnRoot = FindSolutionRoot();
        var conversationPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var rendererPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js");
        Assert.True(File.Exists(conversationPath));
        Assert.True(File.Exists(rendererPath));

        var conversationHtml = File.ReadAllText(conversationPath);
        var outgoingStart = conversationHtml.IndexOf("data-is-outgoing=\"true\"", StringComparison.Ordinal);
        var outgoingEnd = conversationHtml.IndexOf("data-is-outgoing=\"false\"", StringComparison.Ordinal);
        Assert.True(outgoingStart >= 0 && outgoingEnd > outgoingStart);

        var rowStart = conversationHtml.LastIndexOf("<div", outgoingStart, StringComparison.Ordinal);
        Assert.True(rowStart >= 0);
        var outgoingBlock = conversationHtml.Substring(rowStart, outgoingEnd - rowStart);

        Assert.Contains("flex-wrap-reverse", outgoingBlock);
        var actionsIdx = outgoingBlock.IndexOf("message-actions-bar", StringComparison.Ordinal);
        var bubbleIdx = outgoingBlock.IndexOf("message-bubble max-w-[75%]", StringComparison.Ordinal);
        Assert.True(actionsIdx >= 0 && bubbleIdx > actionsIdx);

        var rendererJs = File.ReadAllText(rendererPath);
        Assert.Contains("flex-wrap-reverse justify-end items-center gap-y-1 message-bubble-row", rendererJs);
        Assert.Contains("row.insertBefore(actionsBar, bubble);", rendererJs);
    }

    [Fact]
    public void ConversationSidebar_HighlightsArchivedTab_OnlyOnArchivedRoute()
    {
        var slnRoot = FindSolutionRoot();
        var sidebarPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Shared", "Components", "ConversationSidebar", "Default.cshtml");
        Assert.True(File.Exists(sidebarPath));

        var content = File.ReadAllText(sidebarPath);
        Assert.Contains("isArchivedView", content);
        Assert.Contains("ViewContext.RouteData.Values[\"action\"]", content);
        Assert.Contains("aria-current=\"@(!isArchivedView ? \"page\" : null)\"", content);
        Assert.Contains("aria-current=\"@(isArchivedView ? \"page\" : null)\"", content);
        Assert.Contains("!isArchivedView ? \"bg-[#B3929D] text-white\"", content);
        Assert.Contains("isArchivedView ? \"px-2.5 py-1 bg-[#B3929D] text-white\"", content);
    }

    [Fact]
    public void Layout_RendersConnectionStatusIndicator_OnlyWhenAuthenticated()
    {
        var slnRoot = FindSolutionRoot();
        var layoutPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Shared", "_Layout.cshtml");
        Assert.True(File.Exists(layoutPath));

        var content = File.ReadAllText(layoutPath);
        var toggleIdx = content.IndexOf("id=\"themeToggleBtn\"", StringComparison.Ordinal);
        var indicatorIdx = content.IndexOf("id=\"connectionStatusIndicator\"", StringComparison.Ordinal);
        Assert.True(toggleIdx >= 0 && indicatorIdx > toggleIdx);

        var between = content.Substring(toggleIdx, indicatorIdx - toggleIdx);
        Assert.Contains("@if (User.Identity?.IsAuthenticated == true)", between);
    }

    [Fact]
    public void SettingsSearchInput_HasVisibleDarkModeSurfaceAndBorder()
    {
        var slnRoot = FindSolutionRoot();
        var settingsPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Settings", "Index.cshtml");
        Assert.True(File.Exists(settingsPath));

        var content = File.ReadAllText(settingsPath);
        var match = Regex.Match(content, @"<input[^>]*id=""settingsSearch""[^>]*>");
        Assert.True(match.Success, "Expected #settingsSearch input in Settings/Index.cshtml");
        Assert.Contains("dark:bg-[#45433F]", match.Value);
        Assert.Contains("dark:focus:bg-[#514D49]", match.Value);
        Assert.Contains("dark:border-[#5B5752]", match.Value);
    }

    [Fact]
    public void Layout_HidesAuthenticatedNavLinks_WhenUnauthenticated()
    {
        var slnRoot = FindSolutionRoot();
        var layoutPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Shared", "_Layout.cshtml");
        Assert.True(File.Exists(layoutPath));

        var content = File.ReadAllText(layoutPath);

        var navMatch = Regex.Match(content, @"<nav[^>]*>[\s\S]*?</nav>");
        Assert.True(navMatch.Success, "Expected <nav> element in _Layout.cshtml");
        var navHtml = navMatch.Value;

        var navIfIdx = navHtml.IndexOf("@if (User.Identity?.IsAuthenticated == true)", StringComparison.Ordinal);
        var navElseIdx = navHtml.IndexOf("else", navIfIdx, StringComparison.Ordinal);
        Assert.True(navIfIdx >= 0 && navElseIdx > navIfIdx);

        var navAuthSection = navHtml.Substring(navIfIdx, navElseIdx - navIfIdx);
        var navElseSection = navHtml.Substring(navElseIdx);
        foreach (var label in new[] { "Chat", "Settings", "Profile", "Sessions" })
        {
            Assert.Contains($">{label}</a>", navAuthSection);
            Assert.DoesNotContain($">{label}</a>", navElseSection);
        }

        Assert.Contains("asp-action=\"Login\"", navElseSection);
        Assert.DoesNotContain(">/<", navElseSection);

        var mobileStart = content.IndexOf("data-mobile-nav-menu", StringComparison.Ordinal);
        var mobileEnd = content.IndexOf("</div>", mobileStart, StringComparison.Ordinal);
        Assert.True(mobileStart >= 0 && mobileEnd > mobileStart);
        var mobileHtml = content.Substring(mobileStart, mobileEnd - mobileStart);

        var mobileIfIdx = mobileHtml.IndexOf("@if (User.Identity?.IsAuthenticated == true)", StringComparison.Ordinal);
        var mobileElseIdx = mobileHtml.IndexOf("else", mobileIfIdx, StringComparison.Ordinal);
        Assert.True(mobileIfIdx >= 0 && mobileElseIdx > mobileIfIdx);

        var mobileAuthSection = mobileHtml.Substring(mobileIfIdx, mobileElseIdx - mobileIfIdx);
        var mobileElseSection = mobileHtml.Substring(mobileElseIdx);
        foreach (var label in new[] { "Chat", "Settings", "Profile", "Sessions" })
        {
            Assert.Contains($">{label}</a>", mobileAuthSection);
            Assert.DoesNotContain($">{label}</a>", mobileElseSection);
        }

        Assert.Contains("asp-action=\"Login\"", mobileElseSection);
        Assert.DoesNotContain("border-t", mobileElseSection);
    }

    [Fact]
    public void Phase9MediaViewsAndScripts_HaveZeroInlineStyleAttributes_AndBalancedMediaMarkup()
    {
        var slnRoot = FindSolutionRoot();
        var viewsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat");
        var mediaViews = new[]
        {
            Path.Combine(viewsDir, "Conversation.cshtml"),
            Path.Combine(viewsDir, "_ChatActivePaneEmpty.cshtml"),
            Path.Combine(viewsDir, "_ChatModals.cshtml")
        };

        foreach (var viewPath in mediaViews)
        {
            Assert.True(File.Exists(viewPath));
            var content = File.ReadAllText(viewPath);

            // CSP style-src 'self' compliance: no inline style="..." HTML attributes
            Assert.DoesNotMatch(@"\bstyle\s*=", content);

            var openDivs = Regex.Matches(content, "<div[\\s>]").Count;
            var closeDivs = Regex.Matches(content, "</div>").Count;
            Assert.Equal(openDivs, closeDivs);
        }

        var convHtml = File.ReadAllText(mediaViews[0]);
        Assert.Contains("id=\"composer-attach-btn\"", convHtml);
        Assert.Contains("id=\"composer-attach-menu\"", convHtml);
        Assert.Contains("id=\"composer-attachments-tray\"", convHtml);
        Assert.Contains("id=\"chat-dropzone-overlay\"", convHtml);
        Assert.Contains("id=\"pending-uploads-container\"", convHtml);
        Assert.Contains("data-has-attachments=", convHtml);
        Assert.Contains("data-message-preview=", convHtml);
        Assert.DoesNotContain("asp-action=\"Attachments\"", convHtml);

        // #pending-uploads-container must be a sibling AFTER #messages-container (not nested inside it)
        var msgContainerIdx = convHtml.IndexOf("id=\"messages-container\"", StringComparison.Ordinal);
        var pendingIdx = convHtml.IndexOf("id=\"pending-uploads-container\"", StringComparison.Ordinal);
        Assert.True(msgContainerIdx >= 0 && pendingIdx > msgContainerIdx);

        var modalsHtml = File.ReadAllText(mediaViews[2]);
        Assert.Contains("id=\"media-lightbox-modal\"", modalsHtml);
        Assert.Contains("id=\"media-lightbox-img\"", modalsHtml);

        var jsDir = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js");
        foreach (var jsPath in Directory.GetFiles(jsDir, "*.js", SearchOption.AllDirectories))
        {
            var jsContent = File.ReadAllText(jsPath);
            Assert.DoesNotMatch(@"setAttribute\(\s*['""]style['""]", jsContent);
        }
    }
}
