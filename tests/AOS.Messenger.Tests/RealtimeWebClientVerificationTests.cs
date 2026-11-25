using System.Text.RegularExpressions;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Verifies Phase 6 client-side SignalR streaming, connection lifecycle,
/// state reconciliation on reconnect, deduplication by ID, and XSS prevention.
/// Complies with AGENTS.md architectural rules.
/// </summary>
public sealed class RealtimeWebClientVerificationTests
{
    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
               !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.sln")) &&
               !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx")) &&
               !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        if (current == null)
        {
            throw new InvalidOperationException("Could not find repository root containing AOS.Messenger.slnx or src/AOS.Messenger.Web.");
        }

        return current.FullName;
    }

    [Fact]
    public void SignalRMinJs_Exists_InWwwRootLib_AndIsNonEmpty()
    {
        var root = FindSolutionRoot();
        var signalRPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "lib", "signalr", "signalr.min.js");

        Assert.True(File.Exists(signalRPath), $"signalr.min.js not found at {signalRPath}");
        var fileInfo = new FileInfo(signalRPath);
        Assert.True(fileInfo.Length > 20_000, "signalr.min.js must be a valid bundle (> 20KB)");
    }

    [Fact]
    public void ChatRealtimeJs_Exists_AndConfiguresHubConnectionWithAutomaticReconnect()
    {
        var root = FindSolutionRoot();
        var legacyScriptPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-realtime.js");
        var scriptPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");

        Assert.False(File.Exists(legacyScriptPath), "Legacy chat-realtime.js must be retired in favor of signalr-client.js.");
        Assert.True(File.Exists(scriptPath), $"signalr-client.js not found at {scriptPath}");
        var content = File.ReadAllText(scriptPath);

        // Assert HubConnectionBuilder setup
        Assert.Contains("HubConnectionBuilder()", content);
        Assert.Contains(".withUrl('/hubs/chat')", content);
        Assert.Contains(".withAutomaticReconnect([0, 2000, 5000, 10000, 30000])", content);
        Assert.Contains(".build()", content);
    }

    [Fact]
    public void ChatRealtimeJs_ImplementsConnectionLifecycleEvents()
    {
        var root = FindSolutionRoot();
        var scriptPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var content = File.ReadAllText(scriptPath);

        // onreconnecting
        Assert.Contains("connection.onreconnecting", content);

        // onreconnected
        Assert.Contains("connection.onreconnected", content);
        Assert.Contains("JoinConversation", content);

        // onclose
        Assert.Contains("connection.onclose", content);

        // page unload / switch: LeaveConversation
        Assert.Contains("connection.invoke('LeaveConversation'", content);
        Assert.Contains("beforeunload", content);
        Assert.Contains("pagehide", content);
    }

    [Fact]
    public void ChatRealtimeJs_ImplementsStateReconciliation_QueryingAuthoritativeRestApi()
    {
        var root = FindSolutionRoot();
        var signalrClientPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var apiClientPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "api-client.js");
        var content = File.ReadAllText(signalrClientPath) + "\n" + File.ReadAllText(apiClientPath);

        // Reconcile function exists
        Assert.Contains("reconcileActiveConversation", content);

        // Keyset query against authoritative SQL Server REST endpoint (RT-005)
        Assert.Contains("/api/conversations/", content);
        Assert.Contains("/bundle?pageSize=", content);
        Assert.Contains("DEFAULT_MESSAGE_PAGE_SIZE = 20", content);
        Assert.Contains("pageSize: DEFAULT_MESSAGE_PAGE_SIZE", content);

        // Deduplication during reconciliation
        Assert.Contains("isMessageRendered", content);
    }

    [Fact]
    public void ChatRealtimeJs_RegistersReceiveMessageHandler_WithDeduplicationAndVisualParity()
    {
        var root = FindSolutionRoot();
        var signalrClientPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var rendererPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js");
        var content = File.ReadAllText(signalrClientPath) + "\n" + File.ReadAllText(rendererPath);

        // ReceiveMessage handler
        Assert.Contains("connection.on('ReceiveMessage'", content);

        // Deduplication against DOM
        Assert.Contains("isMessageRendered", content);
        Assert.Contains("msg-", content);

        // Smooth scroll to bottom
        Assert.Contains("scrollToBottom", content);

        // Sidebar preview update
        Assert.Contains("updateSidebarLastMessage", content);
    }

    [Fact]
    public void ChatRealtimeJs_EnforcesXssSanitization_OnLiveIncomingMessages()
    {
        var root = FindSolutionRoot();
        var scriptPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js");
        var content = File.ReadAllText(scriptPath);

        // escapeHtml definition
        Assert.Contains("export function escapeHtml(str)", content);
        Assert.Contains(".replace(/&/g, '&amp;')", content);
        Assert.Contains(".replace(/</g, '&lt;')", content);
        Assert.Contains(".replace(/>/g, '&gt;')", content);
        Assert.Contains(".replace(/\"/g, '&quot;')", content);
        Assert.Contains(".replace(/'/g, '&#39;')", content);

        // Usage in bubble generation
        Assert.Contains("escapeHtml(message.content)", content);
        Assert.Contains("escapeHtml(message.senderName", content);

        // Disallow raw interpolation of unencoded content
        var rawMatch = Regex.Matches(content, @"\$\{message\.content\}");
        Assert.Empty(rawMatch);
    }

    [Fact]
    public void ConversationCshtml_WiresSignalRScript_AndRealtimeInitializer()
    {
        var root = FindSolutionRoot();
        var conversationPath = Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var signalrClientPath = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var content = File.ReadAllText(conversationPath);
        var signalrClientContent = File.ReadAllText(signalrClientPath);

        // Unified shell scripts referenced without duplicating chat-realtime.js
        Assert.Contains("signalr.min.js", content);
        Assert.Contains("chat-shell.js", content);
        Assert.DoesNotContain("chat-realtime.js", content);

        // Bootstrap metadata passed safely via application/json
        Assert.Contains("id=\"chat-bootstrap\"", content);
        Assert.Contains("initialConversationId", content);
        Assert.Contains("currentUserId", content);

        // Unified SignalR client lifecycle & events
        Assert.Contains("HubConnectionBuilder", signalrClientContent);
        Assert.Contains(".withUrl('/hubs/chat')", signalrClientContent);
        Assert.Contains(".withAutomaticReconnect([0, 2000, 5000, 10000, 30000])", signalrClientContent);
        Assert.Contains("JoinConversation", signalrClientContent);
        Assert.Contains("LeaveConversation", signalrClientContent);
        Assert.Contains("reconcileActiveConversation", signalrClientContent);
        Assert.Contains("'ReceiveMessage'", signalrClientContent);

        // Reconnection banner present in view
        Assert.Contains("id=\"connection-banner\"", content);

        // SSR messages carry msg-{id} for seamless client deduplication
        Assert.Contains("id=\"msg-@message.Id.Value\"", content);
    }

    [Fact]
    public void ConversationSidebar_HasDataConversationId_AndLastMessageClass()
    {
        var root = FindSolutionRoot();
        var componentPath = Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Shared", "Components", "ConversationSidebar", "Default.cshtml");
        var sidebarPath = Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Shared", "Partials", "_ConversationSidebar.cshtml");
        var content = File.Exists(componentPath) ? File.ReadAllText(componentPath) : File.ReadAllText(sidebarPath);

        Assert.Contains("data-conversation-id=\"@conv.Id.Value\"", content);
        Assert.Contains("conversation-last-message", content);
    }

    [Fact]
    public void LayoutCshtml_ContainsConnectionStatusIndicators()
    {
        var root = FindSolutionRoot();
        var layoutPath = Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Shared", "_Layout.cshtml");
        var content = File.ReadAllText(layoutPath);

        Assert.Contains("id=\"connectionStatusDot\"", content);
        Assert.Contains("id=\"connectionStatusText\"", content);
    }
}
