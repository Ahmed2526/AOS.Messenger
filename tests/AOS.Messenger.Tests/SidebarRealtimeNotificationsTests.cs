using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class SidebarRealtimeNotificationsTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public SidebarRealtimeNotificationsTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
               !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx")) &&
               !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.sln")) &&
               !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not locate solution root.");
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

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start node process for JavaScript verification.");

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        if (!process.WaitForExit(15000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill errors on already-exited process
            }
            throw new TimeoutException("Node script timed out after 15 seconds.");
        }

        process.WaitForExit();
        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
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

                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(_fixture.ConnectionString));

                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = "SidebarTestScheme";
                        options.DefaultChallengeScheme = "SidebarTestScheme";
                        options.DefaultScheme = "SidebarTestScheme";
                    });
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, SidebarTestAuthHandler>("SidebarTestScheme", _ => { });
                });
            });
    }

    private static ApplicationUser CreateUser(string prefix, string displayName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"{prefix}_{suffix}";
        var email = $"{username}@example.com";
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = username,
            DisplayName = displayName,
            Email = email,
            EmailConfirmed = true,
            NormalizedUserName = username.ToUpperInvariant(),
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    [Fact]
    public void ConversationsController_HasRequiredSecurityAttributes()
    {
        var type = typeof(ConversationsController);
        Assert.True(type.IsSealed, "ConversationsController must be sealed.");
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>());

        var route = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("api/conversations", route.Template);

        var cache = type.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cache);
        Assert.True(cache.NoStore);

        var rateLimit = type.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimit);
        Assert.Equal("messages-read", rateLimit.PolicyName);

        var getMethod = type.GetMethod(nameof(ConversationsController.GetConversations));
        Assert.NotNull(getMethod);
        Assert.NotNull(getMethod.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public async Task GetConversationsApi_Unauthenticated_ReturnsChallengeOrUnauthorized()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        var response = await client.GetAsync("/api/conversations");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Redirect ||
            response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected unauthenticated rejection, got {response.StatusCode}");
    }

    [Fact]
    public async Task GetConversationsApi_ReturnsAuthoritativeUnreadCounts_Ordering_ArchiveFilter_AndNoStoreHeaders()
    {
        var now = DateTimeOffset.UtcNow;
        var me = CreateUser("sb_me", "Sidebar Owner");
        var peerPinned = CreateUser("sb_pin", "Pinned Peer");
        var peerUnread = CreateUser("sb_unr", "Unread Peer");
        var peerArchived = CreateUser("sb_arc", "Archived Peer");
        var peerResurfaced = CreateUser("sb_res", "Resurfaced Peer");

        var convPinned = Conversation.CreateDirect(new UserId(me.Id), new UserId(peerPinned.Id), now.AddMinutes(-30));
        var convUnread = Conversation.CreateDirect(new UserId(me.Id), new UserId(peerUnread.Id), now.AddMinutes(-20));
        var convArchived = Conversation.CreateDirect(new UserId(me.Id), new UserId(peerArchived.Id), now.AddMinutes(-15));
        var convResurfaced = Conversation.CreateDirect(new UserId(me.Id), new UserId(peerResurfaced.Id), now.AddMinutes(-10));

        await using (var db = _fixture.CreateDbContext())
        {
            db.Users.AddRange(me, peerPinned, peerUnread, peerArchived, peerResurfaced);
            db.Conversations.AddRange(convPinned, convUnread, convArchived, convResurfaced);
            await db.SaveChangesAsync();

            var mPinned = Message.Create(convPinned.Id, new UserId(me.Id), "sb-pin-1", "Pinned older note", now.AddMinutes(-25));
            var mUnread1 = Message.Create(convUnread.Id, new UserId(peerUnread.Id), "sb-unr-1", "First unread", now.AddMinutes(-5));
            var mUnread2 = Message.Create(convUnread.Id, new UserId(peerUnread.Id), "sb-unr-2", "Second unread", now.AddMinutes(-2));
            var mArchived = Message.Create(convArchived.Id, new UserId(peerArchived.Id), "sb-arc-1", "Archived message", now.AddMinutes(-1));
            var mResurfaced = Message.Create(convResurfaced.Id, new UserId(peerResurfaced.Id), "sb-res-1", "Back from delete", now.AddSeconds(-30));
            db.Messages.AddRange(mPinned, mUnread1, mUnread2, mArchived, mResurfaced);

            var pinnedState = UserConversationState.Create(convPinned.Id, new UserId(me.Id));
            pinnedState.Pin(now.AddMinutes(-24));

            var archivedState = UserConversationState.Create(convArchived.Id, new UserId(me.Id));
            archivedState.Archive(now.AddMinutes(-14));

            var deletedState = UserConversationState.Create(convResurfaced.Id, new UserId(me.Id));
            deletedState.DeleteConversation(now.AddMinutes(-5));

            db.UserConversationStates.AddRange(pinnedState, archivedState, deletedState);

            var sessionId = Guid.NewGuid();
            db.UserSessions.Add(UserSession.Create(sessionId, me.Id, "Test Device", "127.0.0.1", "TestAgent", now, now.AddDays(7)));
            await db.SaveChangesAsync();

            SidebarTestAuthHandler.SetUser(me.Id, me.UserName!, me.DisplayName!, sessionId, me.Email!);
        }

        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/conversations?includeArchived=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cacheControl = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(3, arr.GetArrayLength());

        // 0: Pinned conversation first
        Assert.Equal(convPinned.Id.Value.ToString(), arr[0].GetProperty("id").GetString());
        Assert.True(arr[0].GetProperty("isPinned").GetBoolean());
        Assert.Equal(0, arr[0].GetProperty("unreadCount").GetInt32());

        // 1: Resurfaced conversation (newest unpinned activity: -30s)
        Assert.Equal(convResurfaced.Id.Value.ToString(), arr[1].GetProperty("id").GetString());
        Assert.Equal("Back from delete", arr[1].GetProperty("lastMessagePreview").GetString());
        Assert.Equal(1, arr[1].GetProperty("unreadCount").GetInt32());

        // 2: Unread conversation (2 unread messages, -2m)
        Assert.Equal(convUnread.Id.Value.ToString(), arr[2].GetProperty("id").GetString());
        Assert.Equal("Second unread", arr[2].GetProperty("lastMessagePreview").GetString());
        Assert.Equal(2, arr[2].GetProperty("unreadCount").GetInt32());

        // Archived conversation is included only when includeArchived=true
        var withArchivedResp = await client.GetAsync("/api/conversations?includeArchived=true");
        Assert.Equal(HttpStatusCode.OK, withArchivedResp.StatusCode);
        using var archivedDoc = JsonDocument.Parse(await withArchivedResp.Content.ReadAsStringAsync());
        Assert.Equal(4, archivedDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task AllAuthenticatedSidebarPages_LoadRealtimeScriptsExactlyOnce_AndRenderUnreadMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var me = CreateUser("sb_pages_me", "Page Owner");
        var peer = CreateUser("sb_pages_peer", "Peer Sender");
        var conv = Conversation.CreateDirect(new UserId(me.Id), new UserId(peer.Id), now.AddMinutes(-10));

        await using (var db = _fixture.CreateDbContext())
        {
            db.Users.AddRange(me, peer);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();

            var msg = Message.Create(conv.Id, new UserId(peer.Id), "sb-pg-1", "Unread hello!", now.AddMinutes(-1));
            db.Messages.Add(msg);

            var sessionId = Guid.NewGuid();
            db.UserSessions.Add(UserSession.Create(sessionId, me.Id, "Test Device", "127.0.0.1", "TestAgent", now, now.AddDays(7)));
            await db.SaveChangesAsync();

            SidebarTestAuthHandler.SetUser(me.Id, me.UserName!, me.DisplayName!, sessionId, me.Email!);
        }

        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var chatShellRoutes = new[]
        {
            "/Chat",
            $"/chat/{conv.Id.Value}",
            $"/Chat/Conversation/{conv.Id.Value}"
        };

        var lobbyRoutes = new[]
        {
            "/Chat/Archived",
            "/Chat/NewConversation",
            "/Settings",
            "/Settings/BlockedUsers",
            "/Profile",
            "/Account/Sessions"
        };

        foreach (var route in chatShellRoutes)
        {
            var resp = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var html = await resp.Content.ReadAsStringAsync();

            Assert.Single(Regex.Matches(html, @"signalr\.min\.js"));
            Assert.Single(Regex.Matches(html, @"chat-shell\.js"));
            Assert.Empty(Regex.Matches(html, @"chat-realtime\.js"));
            Assert.Contains($"data-current-user-id=\"{me.Id}\"", html);
            Assert.Contains("data-unread-count=\"1\"", html);
            Assert.Contains("data-unread-badge=\"true\"", html);
            Assert.Contains("conversation-unread-badge", html);
            Assert.Contains("data-is-pinned=\"false\"", html);
        }

        foreach (var route in lobbyRoutes)
        {
            var resp = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var html = await resp.Content.ReadAsStringAsync();

            Assert.Single(Regex.Matches(html, @"signalr\.min\.js"));
            Assert.Single(Regex.Matches(html, @"sidebar\.js"));
            Assert.Empty(Regex.Matches(html, @"chat-realtime\.js"));
            Assert.Contains($"data-current-user-id=\"{me.Id}\"", html);
            Assert.Contains("data-unread-count=\"1\"", html);
            Assert.Contains("data-unread-badge=\"true\"", html);
            Assert.Contains("conversation-unread-badge", html);
            Assert.Contains("data-is-pinned=\"false\"", html);
        }
    }

    [Fact]
    public void ChatRealtimeJs_LobbyIncomingMessage_UpdatesPreview_IncrementsUnreadBadge_ReordersBelowPinned_AndDeduplicates()
    {
        var chatStateUrl = JsUrl("chat-state.js");
        var apiClientUrl = JsUrl("api-client.js");
        var convListUrl = JsUrl("conversation-list.js");
        var signalrClientUrl = JsUrl("signalr-client.js");

        var script = $@"
            import {{ createChatState }} from '{chatStateUrl}';
            import {{ configureApiClient }} from '{apiClientUrl}';
            import {{ setSidebarUnreadCount, closeSidebarBroadcastChannel }} from '{convListUrl}';
            import {{ createSignalRClient }} from '{signalrClientUrl}';

            // Lightweight DOM mock supporting elements, attributes, querySelector, insertBefore, and textContent
            class MockEl {{
                constructor(tag, attrs = {{}}) {{
                    this.tagName = tag.toUpperCase();
                    this.attrs = {{ ...attrs }};
                    this.className = attrs.class || '';
                    this.children = [];
                    this.parentElement = null;
                    this.style = {{}};
                    this._text = '';
                    this.id = attrs.id || '';
                    this.value = '';
                    this.classList = {{
                        add: (...cls) => {{ this.className = Array.from(new Set((this.className + ' ' + cls.join(' ')).trim().split(/\s+/))).join(' '); }},
                        remove: (...cls) => {{ this.className = this.className.split(/\s+/).filter(c => c && !cls.includes(c)).join(' '); }},
                        contains: (c) => this.className.split(/\s+/).includes(c),
                        toggle: (c, force) => {{
                            const has = this.className.split(/\s+/).includes(c);
                            const next = force !== undefined ? !!force : !has;
                            if (next) this.classList.add(c); else this.classList.remove(c);
                        }}
                    }};
                }}
                get firstChild() {{ return this.children[0] || null; }}
                get firstElementChild() {{ return this.children[0] || null; }}
                get textContent() {{ return this._text; }}
                set textContent(v) {{ this._text = String(v); }}
                setAttribute(k, v) {{ this.attrs[k] = String(v); if (k === 'id') this.id = String(v); if (k === 'class') this.className = String(v); }}
                getAttribute(k) {{ return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; }}
                removeAttribute(k) {{ delete this.attrs[k]; }}
                appendChild(child) {{
                    if (child.parentElement) child.parentElement.removeChild(child);
                    child.parentElement = this;
                    this.children.push(child);
                    return child;
                }}
                removeChild(child) {{
                    const idx = this.children.indexOf(child);
                    if (idx >= 0) {{ this.children.splice(idx, 1); child.parentElement = null; }}
                    return child;
                }}
                remove() {{ if (this.parentElement) this.parentElement.removeChild(this); }}
                insertBefore(node, ref) {{
                    if (node.parentElement) node.parentElement.removeChild(node);
                    const idx = this.children.indexOf(ref);
                    node.parentElement = this;
                    if (idx >= 0) this.children.splice(idx, 0, node);
                    else this.children.push(node);
                    return node;
                }}
                matches(sel) {{
                    if (sel.startsWith('.')) {{
                        const parts = sel.split('[');
                        const cls = parts[0].slice(1);
                        if (!this.className.split(/\s+/).includes(cls)) return false;
                        if (parts[1]) {{
                            const m = parts[1].match(/^([^=\]]+)(?:=""([^""]*)"")?\]$/);
                            if (m) {{
                                const val = this.getAttribute(m[1]);
                                if (m[2] !== undefined) return val === m[2];
                                return val !== null;
                            }}
                        }}
                        return true;
                    }}
                    if (sel.startsWith('[')) {{
                        const m = sel.match(/^\[([^=\]]+)(?:=""([^""]*)"")?\]$/);
                        if (m) {{
                            const val = this.getAttribute(m[1]);
                            if (m[2] !== undefined) return val === m[2];
                            return val !== null;
                        }}
                    }}
                    return false;
                }}
                querySelectorAll(sel) {{
                    const out = [];
                    const walk = (n) => {{
                        for (const c of n.children) {{
                            if (c.matches(sel)) out.push(c);
                            walk(c);
                        }}
                    }};
                    walk(this);
                    return out;
                }}
                querySelector(sel) {{ return this.querySelectorAll(sel)[0] || null; }}
            }}

            function makeSidebarItem(id, name, handle, pinned, unread, preview) {{
                const a = new MockEl('a', {{
                    'class': 'conversation-item',
                    'data-conversation-id': id,
                    'data-peer-name': name,
                    'data-peer-handle': handle,
                    'data-is-pinned': pinned ? 'true' : 'false',
                    'data-unread-count': String(unread),
                    'data-search': name + ' ' + handle + ' ' + preview
                }});
                const badges = new MockEl('div', {{ 'class': 'conversation-badges flex items-center gap-1 flex-shrink-0' }});
                if (unread > 0) {{
                    const b = new MockEl('span', {{ 'class': 'conversation-unread-badge', 'data-unread-badge': 'true' }});
                    b.textContent = String(unread);
                    badges.appendChild(b);
                }}
                const prev = new MockEl('span', {{ 'class': 'conversation-last-message' }});
                prev.textContent = preview;
                a.appendChild(badges);
                a.appendChild(prev);
                return a;
            }}

            const listEl = new MockEl('div', {{ id: 'conversationList', 'data-current-user-id': 'user-me' }});
            const container = new MockEl('div', {{ 'class': 'conversation-items-container' }});
            const pinnedItem = makeSidebarItem('conv-pinned', 'Pinned Alice', 'alice', true, 0, 'Pinned msg');
            const topItem = makeSidebarItem('conv-top', 'Bob Top', 'bob', false, 0, 'Older top');
            const bottomItem = makeSidebarItem('conv-bottom', 'Charlie Bottom', 'charlie', false, 0, 'Oldest');
            container.appendChild(pinnedItem);
            container.appendChild(topItem);
            container.appendChild(bottomItem);
            listEl.appendChild(container);

            const searchInput = new MockEl('input', {{ id: 'conversationSearchInput' }});

            globalThis.document = {{
                title: 'A♡S Messenger',
                visibilityState: 'visible',
                hasFocus: () => true,
                addEventListener: () => {{}},
                createElement: (tag) => new MockEl(tag),
                getElementById: (id) => id === 'conversationList' ? listEl : (id === 'conversationSearchInput' ? searchInput : null),
                querySelector: (sel) => listEl.querySelector(sel),
                querySelectorAll: (sel) => {{
                    if (sel === '#conversationList .conversation-item' || sel === '.conversation-item') return listEl.querySelectorAll('.conversation-item');
                    return listEl.querySelectorAll(sel);
                }}
            }};

            const handlers = {{}};
            const deliveredCalls = [];
            const signalRLib = {{
                HubConnectionState: {{ Connected: 'Connected' }},
                HubConnectionBuilder: class {{
                    withUrl() {{ return this; }}
                    withAutomaticReconnect() {{ return this; }}
                    build() {{
                        return {{
                            state: 'Connected',
                            on: (name, fn) => {{ handlers[name] = fn; }},
                            onreconnecting: () => {{}},
                            onreconnected: () => {{}},
                            onclose: () => {{}},
                            start: () => Promise.resolve()
                        }};
                    }}
                }}
            }};
            globalThis.window = globalThis;

            configureApiClient({{
                getCsrfToken: () => 'csrf-lobby',
                fetchImpl: async (url, opts) => {{
                    const convMatch = url.match(/\/api\/conversations\/([^/]+)\/delivered$/);
                    if (convMatch) {{
                        const body = JSON.parse(opts.body);
                        deliveredCalls.push([convMatch[1], String(body.upToMessageId)]);
                    }}
                    return {{
                        ok: true,
                        status: 200,
                        redirected: false,
                        headers: {{ get: () => 'application/json' }},
                        text: async () => '{{}}'
                    }};
                }}
            }});

            const state = createChatState();
            state.currentUserId = 'user-me';
            const client = createSignalRClient({{
                state,
                onSessionExpired: () => {{}},
                signalRLib
            }});
            await client.start();

            // 1. Incoming message 1 from peer on conv-bottom
            handlers['ReceiveMessage']({{
                id: 101,
                conversationId: 'conv-bottom',
                senderId: 'user-charlie',
                content: 'Hello <script>xss</script>',
                isDeletedForEveryone: false,
                isOutgoing: true // Server DTO sets IsOutgoing=true; client MUST use senderId vs currentUserId!
            }});

            const badge1 = bottomItem.querySelector('.conversation-unread-badge');
            if (!badge1 || badge1.textContent !== '1') {{ console.error('Expected badge 1, got', badge1 && badge1.textContent); process.exit(1); }}
            if (bottomItem.getAttribute('data-unread-count') !== '1') process.exit(2);
            if (bottomItem.querySelector('.conversation-last-message').textContent !== 'Hello <script>xss</script>') process.exit(3);
            if (container.children[0] !== pinnedItem || container.children[1] !== bottomItem || container.children[2] !== topItem) {{
                console.error('Expected order [pinned, bottom, top], got', container.children.map(c => c.getAttribute('data-conversation-id')));
                process.exit(4);
            }}
            if (globalThis.document.title !== '(1) A♡S Messenger') {{ console.error('Bad title:', globalThis.document.title); process.exit(5); }}
            if (deliveredCalls.length !== 1 || deliveredCalls[0][0] !== 'conv-bottom') process.exit(6);

            // 2. Duplicate replay of msg 101 must NOT double-increment unread count
            handlers['ReceiveMessage']({{
                id: 101,
                conversationId: 'conv-bottom',
                senderId: 'user-charlie',
                content: 'Hello <script>xss</script>',
                isDeletedForEveryone: false
            }});
            if (bottomItem.querySelector('.conversation-unread-badge').textContent !== '1') process.exit(7);

            // 3. Second distinct incoming message 102 increments badge to 2
            handlers['ReceiveMessage']({{
                id: 102,
                conversationId: 'conv-bottom',
                senderId: 'user-charlie',
                content: 'Second live ping',
                isDeletedForEveryone: false
            }});
            if (bottomItem.querySelector('.conversation-unread-badge').textContent !== '2') process.exit(8);
            if (bottomItem.getAttribute('data-search').indexOf('Second live ping') < 0) process.exit(9);
            if (globalThis.document.title !== '(2) A♡S Messenger') process.exit(10);

            // 4. Outgoing message on conv-top updates preview & bumps above conv-bottom WITHOUT creating unread badge
            handlers['ReceiveMessage']({{
                id: 103,
                conversationId: 'conv-top',
                senderId: 'user-me',
                content: 'My own message from another tab',
                isDeletedForEveryone: false
            }});
            if (topItem.querySelector('.conversation-unread-badge') !== null) process.exit(11);
            if (topItem.getAttribute('data-unread-count') !== '0') process.exit(12);
            if (container.children[0] !== pinnedItem || container.children[1] !== topItem || container.children[2] !== bottomItem) process.exit(13);

            // 5. Clearing unread count removes badge and restores base document title
            setSidebarUnreadCount('conv-bottom', 0);
            if (bottomItem.querySelector('.conversation-unread-badge') !== null) process.exit(14);
            if (globalThis.document.title !== 'A♡S Messenger') process.exit(15);

            client.destroy();
            closeSidebarBroadcastChannel();
            console.log('SUCCESS');
        ";

        var (exitCode, output, error) = RunNodeScript(script);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS", output);
    }

    [Fact]
    public void ChatRealtimeJs_ActiveConversation_HandlesFocusedUnfocusedAndOtherConversationUnreadBadges()
    {
        var chatStateUrl = JsUrl("chat-state.js");
        var apiClientUrl = JsUrl("api-client.js");
        var convListUrl = JsUrl("conversation-list.js");
        var signalrClientUrl = JsUrl("signalr-client.js");

        var script = $@"
            import {{ createChatState }} from '{chatStateUrl}';
            import {{ configureApiClient }} from '{apiClientUrl}';
            import {{ closeSidebarBroadcastChannel }} from '{convListUrl}';
            import {{ createSignalRClient }} from '{signalrClientUrl}';

            class MockEl {{
                constructor(tag, attrs = {{}}) {{
                    this.tagName = tag.toUpperCase();
                    this.attrs = {{ ...attrs }};
                    this.className = attrs.class || '';
                    this.children = [];
                    this.parentElement = null;
                    this.style = {{}};
                    this._text = '';
                    this.id = attrs.id || '';
                    this.classList = {{
                        add: (...cls) => {{ this.className = Array.from(new Set((this.className + ' ' + cls.join(' ')).trim().split(/\s+/))).join(' '); }},
                        remove: (...cls) => {{ this.className = this.className.split(/\s+/).filter(c => c && !cls.includes(c)).join(' '); }},
                        contains: (c) => this.className.split(/\s+/).includes(c),
                        toggle: (c, force) => {{
                            const has = this.className.split(/\s+/).includes(c);
                            const next = force !== undefined ? !!force : !has;
                            if (next) this.classList.add(c); else this.classList.remove(c);
                        }}
                    }};
                }}
                get firstChild() {{ return this.children[0] || null; }}
                get firstElementChild() {{ return this.children[0] || null; }}
                get textContent() {{ return this._text; }}
                set textContent(v) {{ this._text = String(v); }}
                setAttribute(k, v) {{ this.attrs[k] = String(v); if (k === 'id') this.id = String(v); if (k === 'class') this.className = String(v); }}
                getAttribute(k) {{ return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; }}
                removeAttribute(k) {{ delete this.attrs[k]; }}
                appendChild(child) {{
                    if (child.parentElement) child.parentElement.removeChild(child);
                    child.parentElement = this;
                    this.children.push(child);
                    return child;
                }}
                removeChild(child) {{
                    const idx = this.children.indexOf(child);
                    if (idx >= 0) {{ this.children.splice(idx, 1); child.parentElement = null; }}
                    return child;
                }}
                remove() {{ if (this.parentElement) this.parentElement.removeChild(this); }}
                insertBefore(node, ref) {{
                    if (node.parentElement) node.parentElement.removeChild(node);
                    const idx = this.children.indexOf(ref);
                    node.parentElement = this;
                    if (idx >= 0) this.children.splice(idx, 0, node);
                    else this.children.push(node);
                    return node;
                }}
                matches(sel) {{
                    if (sel.startsWith('.')) {{
                        const parts = sel.split('[');
                        const cls = parts[0].slice(1);
                        if (!this.className.split(/\s+/).includes(cls)) return false;
                        if (parts[1]) {{
                            const m = parts[1].match(/^([^=\]]+)(?:=""([^""]*)"")?\]$/);
                            if (m) {{
                                const val = this.getAttribute(m[1]);
                                if (m[2] !== undefined) return val === m[2];
                                return val !== null;
                            }}
                        }}
                        return true;
                    }}
                    if (sel.startsWith('[')) {{
                        const m = sel.match(/^\[([^=\]]+)(?:=""([^""]*)"")?\]$/);
                        if (m) {{
                            const val = this.getAttribute(m[1]);
                            if (m[2] !== undefined) return val === m[2];
                            return val !== null;
                        }}
                    }}
                    return false;
                }}
                querySelectorAll(sel) {{
                    const out = [];
                    const walk = (n) => {{
                        for (const c of n.children) {{
                            if (c.matches(sel)) out.push(c);
                            walk(c);
                        }}
                    }};
                    walk(this);
                    return out;
                }}
                querySelector(sel) {{ return this.querySelectorAll(sel)[0] || null; }}
            }}

            function makeItem(id, name, unread) {{
                const a = new MockEl('a', {{
                    'class': 'conversation-item',
                    'data-conversation-id': id,
                    'data-peer-name': name,
                    'data-peer-handle': name.toLowerCase(),
                    'data-is-pinned': 'false',
                    'data-unread-count': String(unread),
                    'data-search': name
                }});
                const badges = new MockEl('div', {{ 'class': 'conversation-badges' }});
                if (unread > 0) {{
                    const b = new MockEl('span', {{ 'class': 'conversation-unread-badge', 'data-unread-badge': 'true' }});
                    b.textContent = String(unread);
                    badges.appendChild(b);
                }}
                const prev = new MockEl('span', {{ 'class': 'conversation-last-message' }});
                a.appendChild(badges);
                a.appendChild(prev);
                return a;
            }}

            const listEl = new MockEl('div', {{ id: 'conversationList', 'data-current-user-id': 'user-me', 'data-active-conversation-id': 'conv-active' }});
            const itemsWrap = new MockEl('div', {{ 'class': 'conversation-items-container' }});
            const activeItem = makeItem('conv-active', 'Alice', 0);
            const otherItem = makeItem('conv-other', 'Bob', 0);
            itemsWrap.appendChild(activeItem);
            itemsWrap.appendChild(otherItem);
            listEl.appendChild(itemsWrap);

            const messagesContainer = new MockEl('div', {{ id: 'messages-container' }});
            let isFocused = true;
            let visState = 'visible';

            globalThis.document = {{
                title: 'Alice - A♡S Messenger',
                get visibilityState() {{ return visState; }},
                hasFocus: () => isFocused,
                addEventListener: () => {{}},
                createElement: (tag) => new MockEl(tag),
                getElementById: (id) => {{
                    if (id === 'conversationList') return listEl;
                    if (id === 'messages-container') return messagesContainer;
                    return messagesContainer.querySelectorAll('.message-bubble-row').find(r => r.id === id) || null;
                }},
                querySelector: (sel) => listEl.querySelector(sel) || messagesContainer.querySelector(sel),
                querySelectorAll: (sel) => {{
                    if (sel === '#conversationList .conversation-item' || sel === '.conversation-item') return listEl.querySelectorAll('.conversation-item');
                    return messagesContainer.querySelectorAll(sel);
                }}
            }};

            // Execute markRead debounce synchronously inside unit test
            globalThis.setTimeout = (fn) => {{ fn(); return 1; }};
            globalThis.clearTimeout = () => {{}};

            const handlers = {{}};
            const delivered = [];
            const read = [];
            const signalRLib = {{
                HubConnectionState: {{ Connected: 'Connected' }},
                HubConnectionBuilder: class {{
                    withUrl() {{ return this; }}
                    withAutomaticReconnect() {{ return this; }}
                    build() {{
                        return {{
                            state: 'Connected',
                            on: (name, fn) => {{ handlers[name] = fn; }},
                            onreconnecting: () => {{}},
                            onreconnected: () => {{}},
                            onclose: () => {{}},
                            start: () => Promise.resolve(),
                            invoke: () => Promise.resolve()
                        }};
                    }}
                }}
            }};
            globalThis.window = globalThis;
            globalThis.addEventListener = () => {{}};

            configureApiClient({{
                getCsrfToken: () => 'csrf-active',
                fetchImpl: async (url, opts) => {{
                    const delivMatch = url.match(/\/api\/conversations\/([^/]+)\/delivered$/);
                    if (delivMatch) {{
                        const body = JSON.parse(opts.body);
                        delivered.push([delivMatch[1], String(body.upToMessageId)]);
                    }}
                    const readMatch = url.match(/\/api\/conversations\/([^/]+)\/read$/);
                    if (readMatch) {{
                        const body = JSON.parse(opts.body);
                        read.push([readMatch[1], String(body.upToMessageId)]);
                    }}
                    return {{
                        ok: true,
                        status: 200,
                        redirected: false,
                        headers: {{ get: () => 'application/json' }},
                        text: async () => '{{}}'
                    }};
                }}
            }});

            const state = createChatState();
            state.currentUserId = 'user-me';
            state.commitActiveConversation('conv-active', {{ id: 'conv-active', canSend: true }}, []);

            const client = createSignalRClient({{
                state,
                onSessionExpired: () => {{}},
                signalRLib
            }});
            await client.start();

            // 1. Incoming on OTHER chat increments otherItem unread badge to 1, does NOT append to active stream, does NOT markRead
            handlers['ReceiveMessage']({{
                id: 201,
                conversationId: 'conv-other',
                senderId: 'user-bob',
                senderName: 'Bob',
                senderHandle: 'bob',
                content: 'Hey from Bob',
                createdAtUtc: '2026-11-23T12:00:00Z',
                isDeletedForEveryone: false
            }});
            if (otherItem.querySelector('.conversation-unread-badge')?.textContent !== '1') process.exit(1);
            if (messagesContainer.children.length !== 0) process.exit(2);
            if (delivered.length !== 1 || read.length !== 0) process.exit(3);

            // 2. Incoming on ACTIVE chat while focused -> appends bubble, calls markRead, badge stays 0
            handlers['ReceiveMessage']({{
                id: 202,
                conversationId: 'conv-active',
                senderId: 'user-alice',
                senderName: 'Alice',
                senderHandle: 'alice',
                content: 'Active focused msg',
                createdAtUtc: '2026-11-23T12:01:00Z',
                isDeletedForEveryone: false
            }});
            if (activeItem.querySelector('.conversation-unread-badge') !== null) process.exit(4);
            if (messagesContainer.children.length !== 1) process.exit(5);
            if (read.length !== 1 || read[0][1] !== '202') process.exit(6);

            // 3. Incoming on ACTIVE chat while tab is hidden/unfocused -> appends bubble, increments badge to 1, does NOT markRead
            visState = 'hidden';
            isFocused = false;
            handlers['ReceiveMessage']({{
                id: 203,
                conversationId: 'conv-active',
                senderId: 'user-alice',
                senderName: 'Alice',
                senderHandle: 'alice',
                content: 'Active unfocused msg',
                createdAtUtc: '2026-11-23T12:02:00Z',
                isDeletedForEveryone: false
            }});
            if (activeItem.querySelector('.conversation-unread-badge')?.textContent !== '1') process.exit(7);
            if (messagesContainer.children.length !== 2) process.exit(8);
            if (read.length !== 1) process.exit(9);

            client.destroy();
            closeSidebarBroadcastChannel();
            console.log('SUCCESS');
        ";

        var (exitCode, output, error) = RunNodeScript(script);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS", output);
    }

    [Fact]
    public void ChatRealtimeJs_NewConversationArrival_TriggersSyncAndReplacesEmptyStateSafely()
    {
        var chatStateUrl = JsUrl("chat-state.js");
        var apiClientUrl = JsUrl("api-client.js");
        var convListUrl = JsUrl("conversation-list.js");
        var signalrClientUrl = JsUrl("signalr-client.js");

        var script = $@"
            import {{ createChatState }} from '{chatStateUrl}';
            import {{ configureApiClient }} from '{apiClientUrl}';
            import {{ closeSidebarBroadcastChannel }} from '{convListUrl}';
            import {{ createSignalRClient }} from '{signalrClientUrl}';

            class MockEl {{
                constructor(tag, attrs = {{}}) {{
                    this.tagName = tag.toUpperCase();
                    this.attrs = {{ ...attrs }};
                    this.className = attrs.class || '';
                    this.children = [];
                    this.parentElement = null;
                    this.style = {{}};
                    this._text = '';
                    this.id = attrs.id || '';
                    this.href = '';
                    this.title = '';
                    this.classList = {{ add: () => {{}}, remove: () => {{}}, contains: () => false, toggle: () => {{}} }};
                }}
                get firstChild() {{ return this.children[0] || null; }}
                get textContent() {{ return this._text; }}
                set textContent(v) {{ this._text = String(v); }}
                setAttribute(k, v) {{ this.attrs[k] = String(v); if (k === 'id') this.id = String(v); if (k === 'class') this.className = String(v); }}
                getAttribute(k) {{ return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; }}
                appendChild(child) {{
                    if (child.tagName === '#FRAGMENT') {{
                        for (const c of [...child.children]) this.appendChild(c);
                        return child;
                    }}
                    if (child.parentElement) child.parentElement.removeChild(child);
                    child.parentElement = this;
                    this.children.push(child);
                    return child;
                }}
                removeChild(child) {{
                    const idx = this.children.indexOf(child);
                    if (idx >= 0) {{ this.children.splice(idx, 1); child.parentElement = null; }}
                    return child;
                }}
                remove() {{ if (this.parentElement) this.parentElement.removeChild(this); }}
                matches(sel) {{
                    if (sel.startsWith('.')) {{
                        const parts = sel.split('[');
                        const cls = parts[0].slice(1);
                        if (!this.className.split(/\s+/).includes(cls)) return false;
                        if (parts[1]) {{
                            const m = parts[1].match(/^([^=\]]+)(?:=""([^""]*)"")?\]$/);
                            if (m) {{
                                const val = this.getAttribute(m[1]);
                                if (m[2] !== undefined) return val === m[2];
                                return val !== null;
                            }}
                        }}
                        return true;
                    }}
                    if (sel.startsWith('[')) {{
                        const m = sel.match(/^\[([^=\]]+)(?:=""([^""]*)"")?\]$/);
                        if (m) {{
                            const val = this.getAttribute(m[1]);
                            if (m[2] !== undefined) return val === m[2];
                            return val !== null;
                        }}
                    }}
                    return false;
                }}
                querySelectorAll(sel) {{
                    const out = [];
                    const walk = (n) => {{
                        for (const c of n.children) {{
                            if (c.matches(sel)) out.push(c);
                            walk(c);
                        }}
                    }};
                    walk(this);
                    return out;
                }}
                querySelector(sel) {{ return this.querySelectorAll(sel)[0] || null; }}
            }}

            const listEl = new MockEl('div', {{ id: 'conversationList', 'data-current-user-id': 'user-me' }});
            const emptyState = new MockEl('div', {{ 'data-sidebar-empty-state': 'true' }});
            listEl.appendChild(emptyState);

            globalThis.document = {{
                title: 'A♡S Messenger',
                visibilityState: 'visible',
                addEventListener: () => {{}},
                createElement: (tag) => new MockEl(tag),
                createDocumentFragment: () => new MockEl('#FRAGMENT'),
                getElementById: (id) => id === 'conversationList' ? listEl : null,
                querySelector: (sel) => listEl.querySelector(sel),
                querySelectorAll: (sel) => listEl.querySelectorAll(sel.replace('#conversationList ', ''))
            }};

            const fetchUrls = [];
            configureApiClient({{
                getCsrfToken: () => 'csrf-new-conv',
                fetchImpl: async (url) => {{
                    if (url.startsWith('/api/conversations?')) {{
                        fetchUrls.push(url);
                        return {{
                            ok: true,
                            status: 200,
                            redirected: false,
                            url,
                            headers: {{ get: () => 'application/json' }},
                            text: async () => JSON.stringify([
                                {{
                                    id: 'conv-brand-new',
                                    otherParticipantId: 'user-eve',
                                    otherParticipantName: '<b>Eve</b>',
                                    otherParticipantHandle: 'eve',
                                    lastMessagePreview: 'First message ever!',
                                    unreadCount: 1,
                                    isPinned: false,
                                    isMuted: false,
                                    isOnline: true,
                                    lastMessageId: 501
                                }}
                            ])
                        }};
                    }}
                    return {{
                        ok: true,
                        status: 200,
                        redirected: false,
                        url,
                        headers: {{ get: () => 'application/json' }},
                        text: async () => '{{}}'
                    }};
                }}
            }});

            const handlers = {{}};
            const signalRLib = {{
                HubConnectionState: {{ Connected: 'Connected' }},
                HubConnectionBuilder: class {{
                    withUrl() {{ return this; }}
                    withAutomaticReconnect() {{ return this; }}
                    build() {{
                        return {{
                            state: 'Connected',
                            on: (name, fn) => {{ handlers[name] = fn; }},
                            onreconnecting: () => {{}},
                            onreconnected: () => {{}},
                            onclose: () => {{}},
                            start: () => Promise.resolve()
                        }};
                    }}
                }}
            }};
            globalThis.window = globalThis;

            const state = createChatState();
            state.currentUserId = 'user-me';
            const client = createSignalRClient({{
                state,
                onSessionExpired: () => {{}},
                signalRLib
            }});
            await client.start();

            handlers['ReceiveMessage']({{
                id: 501,
                conversationId: 'conv-brand-new',
                senderId: 'user-eve',
                content: 'First message ever!',
                isDeletedForEveryone: false
            }});

            // Allow async fetch microtasks to settle
            await new Promise(r => setTimeout(r, 50));

            if (fetchUrls.length !== 1 || fetchUrls[0] !== '/api/conversations?includeArchived=false') process.exit(1);
            if (listEl.querySelector('[data-sidebar-empty-state]') !== null) process.exit(2);

            const newItem = listEl.querySelector('.conversation-item');
            if (!newItem || newItem.getAttribute('data-conversation-id') !== 'conv-brand-new') process.exit(3);
            if (newItem.getAttribute('data-peer-name') !== '<b>Eve</b>') process.exit(4);
            if (newItem.querySelector('.conversation-last-message').textContent !== 'First message ever!') process.exit(5);
            if (newItem.querySelector('.conversation-unread-badge').textContent !== '1') process.exit(6);
            if (globalThis.document.title !== '(1) A♡S Messenger') process.exit(7);

            client.destroy();
            closeSidebarBroadcastChannel();
            console.log('SUCCESS');
        ";

        var (exitCode, output, error) = RunNodeScript(script);
        Assert.True(exitCode == 0, $"Node script failed with exitCode {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("SUCCESS", output);
    }

    private sealed class SidebarTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
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

        public SidebarTestAuthHandler(
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
            var identity = new ClaimsIdentity(claims, "SidebarTestScheme");
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "SidebarTestScheme")));
        }
    }
}
