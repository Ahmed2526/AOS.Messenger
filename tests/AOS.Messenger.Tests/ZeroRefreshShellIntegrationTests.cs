using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ZeroRefreshShellIntegrationTests
{
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
        Assert.True(File.Exists(path), $"JS module not found: {path}");
        return new Uri(path).AbsoluteUri;
    }

    private const string MockDomPrelude = """
        function createClassList(el) {
            const getSet = () => new Set((el.className || '').split(/\s+/).filter(Boolean));
            const writeSet = (set) => { el.className = [...set].join(' '); };
            return {
                add(...tokens) { const s = getSet(); tokens.forEach(t => s.add(t)); writeSet(s); },
                remove(...tokens) { const s = getSet(); tokens.forEach(t => s.delete(t)); writeSet(s); },
                toggle(token, force) {
                    const s = getSet();
                    const shouldAdd = force !== undefined ? !!force : !s.has(token);
                    if (shouldAdd) s.add(token); else s.delete(token);
                    writeSet(s);
                    return shouldAdd;
                },
                contains(token) { return getSet().has(token); }
            };
        }

        function matchesSimpleSelector(el, selector) {
            if (!el || el.nodeType !== 1) return false;
            const rawParts = selector.trim().split(/\s+/);
            const tail = rawParts[rawParts.length - 1];
            const matchesSingle = (node, part) => {
                if (!node || node.nodeType !== 1) return false;
                if (part.startsWith('#')) return node.id === part.slice(1);
                const attrMatch = /^([a-z0-9_-]+|\.[a-z0-9_-]+)?((?:\[[^\]]+\])+)$/i.exec(part);
                if (attrMatch) {
                    const head = attrMatch[1];
                    if (head && head.startsWith('.') && !node.classList.contains(head.slice(1))) return false;
                    if (head && !head.startsWith('.') && node.tagName.toLowerCase() !== head.toLowerCase()) return false;
                    const exprs = [...attrMatch[2].matchAll(/\[([^\]=*~]+)(\*)?(?:="([^"]*)")?\]/g)];
                    return exprs.every(([, attr, star, val]) => {
                        if (val === undefined) return node.hasAttribute(attr);
                        const cur = node.getAttribute(attr) || '';
                        return star ? cur.includes(val) : cur === val;
                    });
                }
                if (part.startsWith('.')) return node.classList.contains(part.slice(1));
                return node.tagName.toLowerCase() === part.toLowerCase();
            };

            if (!matchesSingle(el, tail)) return false;
            if (rawParts.length === 1) return true;

            let cur = el.parentElement;
            for (let i = rawParts.length - 2; i >= 0; i--) {
                while (cur && !matchesSingle(cur, rawParts[i])) {
                    cur = cur.parentElement;
                }
                if (!cur) return false;
                cur = cur.parentElement;
            }
            return true;
        }

        function createMockDom() {
            const docListeners = new Map();
            const winListeners = new Map();

            class MockNode {
                constructor(nodeType, tagName = '') {
                    this.nodeType = nodeType;
                    this.tagName = tagName;
                    this.children = [];
                    this.parentNode = null;
                    this.parentElement = null;
                    this._attrs = new Map();
                    this._listeners = new Map();
                    this._textContent = '';
                    this.className = '';
                    this.style = {};
                    this.value = '';
                    this.disabled = false;
                    this.dataset = {};
                    this.scrollHeight = 400;
                    this.scrollTop = 0;
                    this.classList = createClassList(this);
                }
                get id() { return this._attrs.get('id') || ''; }
                set id(v) { if (v) this._attrs.set('id', String(v)); else this._attrs.delete('id'); }
                get name() { return this._attrs.get('name') || ''; }
                set name(v) { if (v !== undefined && v !== null) this._attrs.set('name', String(v)); else this._attrs.delete('name'); }
                get type() { return this._attrs.get('type') || ''; }
                set type(v) { if (v !== undefined && v !== null) this._attrs.set('type', String(v)); else this._attrs.delete('type'); }
                get title() { return this._attrs.get('title') || ''; }
                set title(v) { if (v !== undefined && v !== null) this._attrs.set('title', String(v)); else this._attrs.delete('title'); }
                get href() { return this._attrs.get('href') || ''; }
                set href(v) { if (v !== undefined && v !== null) this._attrs.set('href', String(v)); else this._attrs.delete('href'); }
                get firstChild() { return this.children[0] || null; }
                get firstElementChild() { return this.children.find(c => c.nodeType === 1) || null; }
                get textContent() {
                    if (this.nodeType === 3) return this._textContent;
                    if (this.children.length === 0) return this._textContent;
                    return this.children.map(c => c.textContent).join('');
                }
                set textContent(v) {
                    this.children = [];
                    this._textContent = v === null || v === undefined ? '' : String(v);
                }
                setAttribute(k, v) { this._attrs.set(k, String(v)); }
                getAttribute(k) { return this._attrs.has(k) ? this._attrs.get(k) : null; }
                hasAttribute(k) { return this._attrs.has(k); }
                removeAttribute(k) { this._attrs.delete(k); }
                appendChild(node) {
                    if (node.nodeType === 11) {
                        const kids = [...node.children];
                        node.children = [];
                        kids.forEach(k => this.appendChild(k));
                        return node;
                    }
                    if (node.parentNode) node.parentNode.removeChild(node);
                    node.parentNode = this;
                    node.parentElement = this.nodeType === 1 ? this : null;
                    this.children.push(node);
                    return node;
                }
                insertBefore(node, refNode) {
                    if (!refNode) return this.appendChild(node);
                    if (node.nodeType === 11) {
                        const kids = [...node.children];
                        node.children = [];
                        kids.forEach(k => this.insertBefore(k, refNode));
                        return node;
                    }
                    if (node.parentNode) node.parentNode.removeChild(node);
                    const idx = this.children.indexOf(refNode);
                    if (idx === -1) return this.appendChild(node);
                    node.parentNode = this;
                    node.parentElement = this.nodeType === 1 ? this : null;
                    this.children.splice(idx, 0, node);
                    return node;
                }
                removeChild(node) {
                    const idx = this.children.indexOf(node);
                    if (idx !== -1) {
                        this.children.splice(idx, 1);
                        node.parentNode = null;
                        node.parentElement = null;
                    }
                    return node;
                }
                remove() {
                    if (this.parentNode) this.parentNode.removeChild(this);
                }
                querySelectorAll(selector) {
                    const out = [];
                    const walk = (node) => {
                        for (const child of node.children) {
                            if (child.nodeType === 1) {
                                if (matchesSimpleSelector(child, selector)) out.push(child);
                                walk(child);
                            }
                        }
                    };
                    walk(this);
                    return out;
                }
                querySelector(selector) {
                    return this.querySelectorAll(selector)[0] || null;
                }
                closest(selector) {
                    let cur = this;
                    while (cur && cur.nodeType === 1) {
                        if (matchesSimpleSelector(cur, selector)) return cur;
                        cur = cur.parentElement;
                    }
                    return null;
                }
                contains(target) {
                    let cur = target;
                    while (cur) {
                        if (cur === this) return true;
                        cur = cur.parentNode;
                    }
                    return false;
                }
                addEventListener(type, fn) {
                    if (!this._listeners.has(type)) this._listeners.set(type, new Set());
                    this._listeners.get(type).add(fn);
                }
                removeEventListener(type, fn) {
                    this._listeners.get(type)?.delete(fn);
                }
                dispatchEvent(evt) {
                    evt.target = evt.target || this;
                    for (const fn of this._listeners.get(evt.type) || []) fn(evt);
                }
                focus() {}
                scrollIntoView() {}
                scrollTo() {}
                getBoundingClientRect() { return { left: 100, top: 100, width: 40, height: 30, bottom: 130, right: 140 }; }
            }

            const body = new MockNode(1, 'BODY');
            const document = {
                body,
                readyState: 'complete',
                documentElement: { clientWidth: 1024, clientHeight: 768 },
                title: 'A♡S Messenger - Desktop App',
                visibilityState: 'visible',
                hasFocus: () => true,
                createElement: (tag) => new MockNode(1, tag.toUpperCase()),
                createTextNode: (text) => {
                    const n = new MockNode(3, '#text');
                    n._textContent = String(text ?? '');
                    return n;
                },
                createDocumentFragment: () => new MockNode(11, '#fragment'),
                getElementById: (id) => body.querySelector(`#${id}`),
                querySelector: (sel) => body.querySelector(sel),
                querySelectorAll: (sel) => body.querySelectorAll(sel),
                addEventListener: (t, fn) => {
                    if (!docListeners.has(t)) docListeners.set(t, new Set());
                    docListeners.get(t).add(fn);
                },
                removeEventListener: (t, fn) => docListeners.get(t)?.delete(fn),
                dispatchEvent: (evt) => {
                    for (const fn of docListeners.get(evt.type) || []) fn(evt);
                }
            };

            const history = {
                pushState: () => {},
                replaceState: () => {}
            };

            const windowObj = {
                innerWidth: 1024,
                innerHeight: 768,
                location: { pathname: '/chat', search: '', href: 'https://localhost/chat', replace: () => {}, reload: () => {} },
                history,
                addEventListener: (t, fn) => {
                    if (!winListeners.has(t)) winListeners.set(t, new Set());
                    winListeners.get(t).add(fn);
                },
                removeEventListener: (t, fn) => winListeners.get(t)?.delete(fn),
                dispatchEvent: (evt) => {
                    for (const fn of winListeners.get(evt.type) || []) fn(evt);
                }
            };

            globalThis.document = document;
            globalThis.window = windowObj;
            globalThis.history = history;

            return { document, window: windowObj };
        }
        """;

    [Fact]
    public async Task ConversationList_ManagesActiveRow_XssEscaping_PinnedReorder_AndTitleCount()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const {
                setActiveRow,
                setDocumentBaseTitle,
                setSidebarUnreadCount,
                updateSidebarLastMessage,
                renderSidebarConversations,
                createSidebarConversationItemElement,
                closeSidebarBroadcastChannel
            } = await import('{{JsUrl("conversation-list.js")}}');

            const list = dom.document.createElement('div');
            list.id = 'conversationList';
            const itemsWrap = dom.document.createElement('div');
            itemsWrap.className = 'p-2 space-y-1 conversation-items-container';
            const searchInput = dom.document.createElement('input');
            searchInput.id = 'conversationSearchInput';

            const elPinned = createSidebarConversationItemElement({
                id: 'conv-pinned',
                otherParticipantId: 'u-pin',
                otherParticipantName: 'Pinned Peer',
                otherParticipantHandle: 'pinned',
                lastMessagePreview: 'Old pinned',
                unreadCount: 0,
                isPinned: true
            }, null);
            const elA = createSidebarConversationItemElement({
                id: 'conv-a',
                otherParticipantId: 'u-a',
                otherParticipantName: 'Alice',
                otherParticipantHandle: 'alice',
                lastMessagePreview: 'Hi A',
                unreadCount: 2,
                isPinned: false
            }, 'conv-a');
            const elB = createSidebarConversationItemElement({
                id: 'conv-b',
                otherParticipantId: 'u-b',
                otherParticipantName: '<script>alert(1)</script>',
                otherParticipantHandle: 'bob',
                lastMessagePreview: '<img src=x onerror=alert(1)>',
                unreadCount: 0,
                isPinned: false
            }, null);

            itemsWrap.appendChild(elPinned);
            itemsWrap.appendChild(elA);
            itemsWrap.appendChild(elB);
            list.appendChild(itemsWrap);
            dom.document.body.appendChild(searchInput);
            dom.document.body.appendChild(list);

            // Dynamic item contract: data-conversation-link + /chat/{id} + safe textContent
            assert.equal(elB.getAttribute('data-conversation-link'), 'true');
            assert.equal(elB.href, '/chat/conv-b');
            assert.equal(elA.getAttribute('aria-current'), 'page');
            assert.ok(elB.textContent.includes('<script>alert(1)</script>'));

            // Active row highlight & aria-current switching + graceful handling of unlisted archived IDs
            setActiveRow('conv-b');
            assert.equal(elA.hasAttribute('aria-current'), false);
            assert.equal(elB.getAttribute('aria-current'), 'page');

            setActiveRow('conv-archived-unlisted');
            assert.equal(elB.hasAttribute('aria-current'), false);
            assert.equal(list.getAttribute('data-active-conversation-id'), 'conv-archived-unlisted');

            // Pinned-aware reorder moves unpinned conv-b above conv-a while keeping conv-pinned at index 0
            updateSidebarLastMessage('conv-b', 'Latest in B', 901, false, false, true);
            assert.equal(itemsWrap.children[0], elPinned);
            assert.equal(itemsWrap.children[1], elB);
            assert.equal(itemsWrap.children[2], elA);

            // Dynamic document title with unread count updates cleanly on switch
            setDocumentBaseTitle('<script>alert(1)</script> - A♡S Messenger');
            assert.equal(dom.document.title, '(2) <script>alert(1)</script> - A♡S Messenger');
            setSidebarUnreadCount('conv-a', 0);
            assert.equal(dom.document.title, '<script>alert(1)</script> - A♡S Messenger');

            // [S5] renderSidebarConversations patches matching items in-place instead of recreating nodes
            renderSidebarConversations([
                {
                    id: 'conv-b',
                    otherParticipantId: 'u-b',
                    otherParticipantName: 'Bob Patched',
                    otherParticipantHandle: 'bob',
                    lastMessagePreview: 'Patched preview',
                    unreadCount: 3,
                    isPinned: false,
                    isOnline: true
                },
                {
                    id: 'conv-pinned',
                    otherParticipantId: 'u-pin',
                    otherParticipantName: 'Pinned Peer',
                    otherParticipantHandle: 'pinned',
                    lastMessagePreview: 'Still pinned',
                    unreadCount: 0,
                    isPinned: true
                }
            ], 'conv-b');
            assert.equal(itemsWrap.children.length, 2, 'Removed conversation must be removed cleanly');
            assert.equal(itemsWrap.children[0], elB, 'Existing element node for conv-b must be patched and reordered in-place');
            assert.equal(itemsWrap.children[1], elPinned, 'Existing element node for conv-pinned must be reused in-place');
            assert.equal(elB.querySelector('.conversation-last-message').textContent, 'Patched preview');
            assert.equal(elB.getAttribute('data-unread-count'), '3');

            // [S4] Shared #chat-action-error timer clears prior module error timers instead of truncating newer alerts
            const errEl = dom.document.createElement('div');
            errEl.id = 'chat-action-error';
            errEl.className = 'hidden';
            dom.document.body.appendChild(errEl);
            const { showChatActionError, hideChatActionError } = await import('{{JsUrl("chat-action-error.js")}}');
            const scheduledTimers = [];
            const clearedTimers = [];
            const origSetTimeout = globalThis.setTimeout;
            const origClearTimeout = globalThis.clearTimeout;
            globalThis.setTimeout = (fn, ms) => {
                const id = scheduledTimers.length + 1;
                scheduledTimers.push({ id, fn, ms });
                return id;
            };
            globalThis.clearTimeout = (id) => { clearedTimers.push(id); };
            try {
                showChatActionError('Reaction failed', 4000);
                assert.equal(errEl.classList.contains('hidden'), false);
                showChatActionError('Send failed', 5000);
                assert.deepEqual(clearedTimers, [1], 'Newer error must cancel earlier module hide timer');
                assert.equal(errEl.textContent, 'Send failed');
                scheduledTimers[1].fn();
                assert.equal(errEl.classList.contains('hidden'), true);
                hideChatActionError();
            } finally {
                globalThis.setTimeout = origSetTimeout;
                globalThis.clearTimeout = origClearTimeout;
            }

            // [S5] Shared IntersectionObserver across multiple rendered <video> elements
            const { hydrateMediaElements, disconnectActiveMediaObservers } = await import('{{JsUrl("message-renderer.js")}}');
            let observerCreateCount = 0;
            const observedTargets = [];
            const origIO = globalThis.IntersectionObserver;
            globalThis.IntersectionObserver = class {
                constructor(cb) {
                    observerCreateCount++;
                    this.cb = cb;
                }
                observe(el) { observedTargets.push(el); }
                unobserve() {}
                disconnect() {}
            };
            try {
                const mediaWrap = dom.document.createElement('div');
                for (let i = 0; i < 3; i++) {
                    const v = dom.document.createElement('video');
                    v.setAttribute('data-media-video', 'true');
                    v.setAttribute('data-conversation-id', 'conv-b');
                    v.setAttribute('data-message-id', String(10 + i));
                    v.setAttribute('data-media-id', `00000000-0000-0000-0000-00000000000${i}`);
                    mediaWrap.appendChild(v);
                }
                hydrateMediaElements(mediaWrap);
                assert.equal(observerCreateCount, 1, 'Multiple videos must share a single IntersectionObserver instance');
                assert.equal(observedTargets.length, 3);
                disconnectActiveMediaObservers();
            } finally {
                globalThis.IntersectionObserver = origIO;
            }

            closeSidebarBroadcastChannel();
            console.log('OK');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public async Task Composer_IsolatesDraftsAndTransientState_EnforcesPolicy_AndGuardsMidFlightSendSwitch()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
            const { configureApiClient } = await import('{{JsUrl("api-client.js")}}');
            const { createComposer } = await import('{{JsUrl("composer.js")}}');
            const { createSidebarConversationItemElement, closeSidebarBroadcastChannel } = await import('{{JsUrl("conversation-list.js")}}');

            const container = dom.document.createElement('div'); container.id = 'messages-container';
            const noMsgs = dom.document.createElement('div'); noMsgs.id = 'no-messages-placeholder';
            const notice = dom.document.createElement('div'); notice.id = 'chat-policy-notice'; notice.className = 'hidden';
            const actionErr = dom.document.createElement('div'); actionErr.id = 'chat-action-error'; actionErr.className = 'hidden';
            const replyBanner = dom.document.createElement('div'); replyBanner.id = 'composer-reply-banner'; replyBanner.className = 'hidden';
            const replySender = dom.document.createElement('span'); replySender.id = 'reply-banner-sender';
            const replyPreview = dom.document.createElement('p'); replyPreview.id = 'reply-banner-preview';
            const cancelReply = dom.document.createElement('button'); cancelReply.id = 'cancel-reply-btn';
            const inputEl = dom.document.createElement('textarea'); inputEl.id = 'message-content-input';
            const sendBtn = dom.document.createElement('button'); sendBtn.id = 'send-message-btn';

            const sidebar = dom.document.createElement('div'); sidebar.id = 'conversationList';
            const sbA = createSidebarConversationItemElement({ id: 'conv-a', otherParticipantName: 'Alice', otherParticipantHandle: 'alice', lastMessagePreview: 'Old A', unreadCount: 0 }, 'conv-a');
            const sbB = createSidebarConversationItemElement({ id: 'conv-b', otherParticipantName: 'Bob', otherParticipantHandle: 'bob', lastMessagePreview: 'Old B', unreadCount: 0 }, null);
            sidebar.appendChild(sbA);
            sidebar.appendChild(sbB);

            replyBanner.appendChild(replySender);
            replyBanner.appendChild(replyPreview);
            dom.document.body.appendChild(container);
            dom.document.body.appendChild(noMsgs);
            dom.document.body.appendChild(notice);
            dom.document.body.appendChild(actionErr);
            dom.document.body.appendChild(replyBanner);
            dom.document.body.appendChild(cancelReply);
            dom.document.body.appendChild(inputEl);
            dom.document.body.appendChild(sendBtn);
            dom.document.body.appendChild(sidebar);

            const pendingPosts = [];
            let sessionExpiredCount = 0;

            configureApiClient({
                onSessionExpired: () => { sessionExpiredCount++; },
                getCsrfToken: () => 'csrf-abc',
                fetchImpl: (url, opts) => new Promise((resolve) => {
                    pendingPosts.push({ url, opts, resolve });
                })
            });

            const state = createChatState();
            state.currentUserId = 'user-1';
            state.commitActiveConversation('conv-a', { id: 'conv-a', canSend: true, isBlocked: false, isPeerDeleted: false }, []);

            const composer = createComposer({ state });
            composer.init();

            // 1. Policy checks (blocked vs soft-deleted peer)
            composer.applyConversationPolicy({ isBlocked: true, canSend: false });
            assert.equal(inputEl.disabled, true);
            assert.equal(sendBtn.disabled, true);
            assert.ok(notice.textContent.includes('cannot send'));

            composer.applyConversationPolicy({ isBlocked: false, isPeerDeleted: true, canSend: true });
            assert.equal(inputEl.disabled, false);
            assert.equal(sendBtn.disabled, false);
            assert.ok(notice.textContent.includes('deleted'));

            composer.applyConversationPolicy({ isBlocked: false, isPeerDeleted: false, canSend: true });
            assert.equal(notice.classList.contains('hidden'), true);

            // 2. Transient reset hook & reply/error banner reset
            let hookResetCount = 0;
            composer.registerTransientResetHook(() => { hookResetCount++; });
            composer.setReply('10', 'Alice', 'Quote A');
            composer.showActionError('Temp err');
            assert.equal(replyBanner.classList.contains('hidden'), false);
            assert.equal(actionErr.classList.contains('hidden'), false);
            composer.resetTransientState();
            assert.equal(replyBanner.classList.contains('hidden'), true);
            assert.equal(actionErr.classList.contains('hidden'), true);
            assert.equal(hookResetCount, 1);

            // 3. Mid-flight send-during-switch isolation
            state.setDraft('conv-a', 'Hello A');
            composer.setText('Hello A');
            const sendPromise = composer.sendMessage();
            assert.equal(pendingPosts.length, 1);
            const postBody = JSON.parse(pendingPosts[0].opts.body);
            assert.equal(postBody.content, 'Hello A');
            assert.ok(/^[0-9a-f-]{36}$/i.test(postBody.idempotencyKey));
            assert.equal(pendingPosts[0].opts.headers['RequestVerificationToken'], 'csrf-abc');

            // User clicks Chat B (which is blocked) BEFORE conv-a's POST resolves
            state.beginSwitch('conv-b');
            composer.resetTransientState();
            composer.setLocked(true);
            state.commitActiveConversation('conv-b', { id: 'conv-b', isBlocked: true, canSend: false }, []);
            composer.applyConversationPolicy(state.activeMeta);

            pendingPosts[0].resolve({
                status: 200,
                ok: true,
                redirected: false,
                headers: { get: () => 'application/json' },
                text: async () => JSON.stringify({
                    id: 777,
                    conversationId: 'conv-a',
                    senderId: 'user-1',
                    senderName: 'Me',
                    content: 'Hello A',
                    createdAtUtc: '2026-11-23T12:00:00Z'
                })
            });
            await sendPromise;

            // conv-b pane MUST stay empty and locked; conv-a sidebar updates and draft cleared
            assert.equal(container.children.length, 0);
            assert.equal(inputEl.disabled, true);
            assert.equal(sendBtn.disabled, true);
            assert.equal(sbA.querySelector('.conversation-last-message').textContent, 'Hello A');
            assert.equal(state.getDraft('conv-a'), '');

            // 4. 403 mid-chat block on send locks composer WITHOUT logging out
            state.beginSwitch('conv-c');
            state.commitActiveConversation('conv-c', { id: 'conv-c', isBlocked: false, canSend: true }, []);
            composer.applyConversationPolicy(state.activeMeta);
            composer.setText('Am I blocked?');
            const send403 = composer.sendMessage();
            assert.equal(pendingPosts.length, 2);
            pendingPosts[1].resolve({
                status: 403,
                ok: false,
                redirected: false,
                headers: { get: () => 'application/problem+json' },
                text: async () => JSON.stringify({ status: 403, detail: 'Blocked' })
            });
            await send403;

            assert.equal(sessionExpiredCount, 0);
            assert.equal(inputEl.disabled, true);
            assert.equal(sendBtn.disabled, true);
            assert.ok(notice.textContent.includes('cannot send'));

            composer.destroy();
            closeSidebarBroadcastChannel();
            console.log('OK');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public async Task SignalRClient_SingleSocket_FocusSync_BufferReplay_Reconnect_AndRevocation()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
            const { configureApiClient } = await import('{{JsUrl("api-client.js")}}');
            const { renderMessages } = await import('{{JsUrl("message-renderer.js")}}');
            const { createSidebarConversationItemElement, findSidebarItem, closeSidebarBroadcastChannel } = await import('{{JsUrl("conversation-list.js")}}');
            const { createSignalRClient, isSessionRevokedSignalRError, MAX_SEEN_INCOMING_IDS } = await import('{{JsUrl("signalr-client.js")}}');

            const container = dom.document.createElement('div'); container.id = 'messages-container';
            const sidebar = dom.document.createElement('div'); sidebar.id = 'conversationList';
            sidebar.appendChild(createSidebarConversationItemElement({ id: 'conv-1', otherParticipantName: 'A', otherParticipantHandle: 'a', lastMessagePreview: '', unreadCount: 0 }, 'conv-1'));
            sidebar.appendChild(createSidebarConversationItemElement({ id: 'conv-2', otherParticipantName: 'B', otherParticipantHandle: 'b', lastMessagePreview: '', unreadCount: 0 }, null));
            dom.document.body.appendChild(container);
            dom.document.body.appendChild(sidebar);

            let buildCount = 0;
            let startCount = 0;
            let stopCount = 0;
            const invocations = [];
            const handlers = new Map();
            let onReconnectingCb = null;
            let onReconnectedCb = null;
            let onCloseCb = null;
            let sessionExpiredCalls = 0;

            const fakeConnection = {
                state: 'Disconnected',
                start: async () => { startCount++; fakeConnection.state = 'Connected'; },
                stop: async () => { stopCount++; fakeConnection.state = 'Disconnected'; },
                invoke: async (...args) => { invocations.push(args); },
                on: (name, fn) => handlers.set(name, fn),
                onreconnecting: (fn) => { onReconnectingCb = fn; },
                onreconnected: (fn) => { onReconnectedCb = fn; },
                onclose: (fn) => { onCloseCb = fn; }
            };

            const signalRLib = {
                HubConnectionState: { Connected: 'Connected' },
                HubConnectionBuilder: class {
                    withUrl() { return this; }
                    withAutomaticReconnect() { return this; }
                    build() { buildCount++; return fakeConnection; }
                }
            };

            const fetchLog = [];
            let reconciledMetaSnapshot = null;
            configureApiClient({
                onSessionExpired: () => { sessionExpiredCalls++; },
                fetchImpl: async (url) => {
                    fetchLog.push(url);
                    if (url.includes('/bundle?pageSize=20') || url.includes('/messages?pageSize=20')) {
                        return {
                            status: 200,
                            ok: true,
                            redirected: false,
                            headers: { get: () => 'application/json' },
                            text: async () => JSON.stringify({
                                viewerUserId: 'u1',
                                conversation: {
                                    id: 'conv-2',
                                    otherParticipantId: 'u2',
                                    otherParticipantName: 'Bob',
                                    otherParticipantHandle: 'b',
                                    isBlocked: true,
                                    canSend: false,
                                    peerLastDeliveredMessageId: 501,
                                    peerLastReadMessageId: 501
                                },
                                messages: [
                                    { id: 503, conversationId: 'conv-2', senderId: 'u2', senderName: 'Bob', content: 'Missed during disconnect', createdAtUtc: '2026-11-23T12:05:00Z' },
                                    { id: 501, conversationId: 'conv-2', senderId: 'u1', senderName: 'Alice', content: 'In bundle 2', createdAtUtc: '2026-11-23T12:03:00Z', isOutgoing: true },
                                    {
                                        id: 500,
                                        conversationId: 'conv-2',
                                        senderId: 'u2',
                                        senderName: 'Bob',
                                        content: 'Edited while offline',
                                        editedAtUtc: '2026-11-23T12:04:00Z',
                                        createdAtUtc: '2026-11-23T12:02:00Z',
                                        reactions: [{ emoji: '🔥', count: 2, reactedByCurrentUser: true }]
                                    },
                                    {
                                        id: 499,
                                        conversationId: 'conv-2',
                                        senderId: 'u2',
                                        senderName: 'Bob',
                                        content: '',
                                        isDeletedForEveryone: true,
                                        createdAtUtc: '2026-11-23T12:01:00Z'
                                    },
                                    { id: 498, conversationId: 'conv-2', senderId: 'u2', senderName: 'Bob', content: 'Ancient history', createdAtUtc: '2026-11-23T11:50:00Z' }
                                ]
                            })
                        };
                    }
                    return {
                        status: 200,
                        ok: true,
                        redirected: false,
                        headers: { get: () => 'application/json' },
                        text: async () => JSON.stringify([
                            { id: 'conv-1', otherParticipantName: 'A', otherParticipantHandle: 'a', lastMessagePreview: '', unreadCount: 0 },
                            { id: 'conv-2', otherParticipantName: 'B', otherParticipantHandle: 'b', lastMessagePreview: '', unreadCount: 0 }
                        ])
                    };
                }
            });

            const state = createChatState();
            state.currentUserId = 'u1';
            state.commitActiveConversation('conv-1', { id: 'conv-1', canSend: true }, []);

            const client = createSignalRClient({
                state,
                onSessionExpired: () => { sessionExpiredCalls++; },
                onConversationReconciled: (meta) => { reconciledMetaSnapshot = meta; },
                signalRLib
            });

            await client.start();
            assert.equal(buildCount, 1);
            assert.equal(startCount, 1);
            assert.deepEqual(invocations[0], ['JoinConversation', 'conv-1']);

            // Begin switch conv-1 -> conv-2; ReceiveMessage for conv-2 arrives mid-fetch -> buffered!
            state.beginSwitch('conv-2');
            handlers.get('ReceiveMessage')({
                id: 500,
                conversationId: 'conv-2',
                senderId: 'u2',
                senderName: 'Bob',
                content: 'Arrived mid-switch',
                createdAtUtc: '2026-11-23T12:02:00Z'
            });
            assert.equal(container.children.length, 0);

            const bundleMsgs = [
                { id: 499, conversationId: 'conv-2', senderId: 'u2', senderName: 'Bob', content: 'In bundle 1', createdAtUtc: '2026-11-23T12:01:00Z' },
                { id: 501, conversationId: 'conv-2', senderId: 'u1', senderName: 'Alice', content: 'In bundle 2', createdAtUtc: '2026-11-23T12:03:00Z', isOutgoing: true }
            ];
            const buffered = state.commitActiveConversation('conv-2', { id: 'conv-2', canSend: true, isBlocked: false }, bundleMsgs);
            renderMessages(container, bundleMsgs, 'u1', state.activeMeta);
            client.replayBufferedEvents('conv-2', buffered);
            await client.notifyActiveConversation('conv-2');

            // Still only 1 socket start, NO redundant LeaveConversation on A->B, deterministic 499, 500, 501 order
            assert.equal(startCount, 1);
            assert.equal(invocations.filter(c => c[0] === 'LeaveConversation').length, 0);
            assert.deepEqual(invocations[1], ['JoinConversation', 'conv-2']);
            assert.deepEqual(container.children.map(n => n.getAttribute('data-message-id')), ['499', '500', '501']);

            // Before reconnect: #499 is not deleted, #500 is unedited with no reactions, #501 receipt is 'sent'
            assert.equal(dom.document.getElementById('msg-499').getAttribute('data-deleted'), 'false');
            assert.equal(dom.document.getElementById('msg-500').querySelector('.message-text-content').textContent, 'Arrived mid-switch');
            assert.equal(dom.document.getElementById('msg-501').querySelector('span[data-receipt-status]').getAttribute('data-receipt-status'), 'sent');

            // Ticket 5 (W3): onreconnected reconciles missing newer (503), skips ancient (498),
            // AND reconciles tombstones (499), edits + reactions (500), receipt watermarks (501), and conversation policy!
            await onReconnectedCb();
            assert.deepEqual(invocations[2], ['JoinConversation', 'conv-2']);
            assert.ok(fetchLog.some(u => u.includes('/api/conversations/conv-2/bundle?pageSize=20')));
            assert.deepEqual(container.children.map(n => n.getAttribute('data-message-id')), ['499', '500', '501', '503']);

            const row499 = dom.document.getElementById('msg-499');
            assert.equal(row499.getAttribute('data-deleted'), 'true');
            assert.ok(row499.textContent.includes('This message was deleted'));

            const row500 = dom.document.getElementById('msg-500');
            assert.equal(row500.querySelector('.message-text-content').textContent, 'Edited while offline');
            assert.equal(row500.querySelector('.edited-label').classList.contains('hidden'), false);
            const pill500 = row500.querySelector('.reaction-pill');
            assert.ok(pill500, 'Expected reaction pill on #msg-500 after reconnect reconciliation');
            assert.equal(pill500.getAttribute('data-emoji'), '🔥');
            assert.ok(pill500.textContent.includes('2'));

            const row501 = dom.document.getElementById('msg-501');
            assert.equal(row501.querySelector('span[data-receipt-status]').getAttribute('data-receipt-status'), 'read');
            assert.equal(container.getAttribute('data-peer-last-read-id'), '501');

            assert.ok(reconciledMetaSnapshot);
            assert.equal(reconciledMetaSnapshot.isBlocked, true);
            assert.equal(reconciledMetaSnapshot.canSend, false);
            assert.equal(state.activeMeta.isBlocked, true);
            assert.equal(state.activeMeta.canSend, false);

            // Navigating to empty /chat invokes LeaveConversation without closing socket
            state.resetActiveChatState();
            await client.leaveActiveConversation('conv-2');
            assert.deepEqual(invocations[3], ['LeaveConversation', 'conv-2']);
            assert.equal(stopCount, 0);

            // Ticket 4 (W4): Rapid A -> B -> A switching while JoinConversation(B) is still in flight
            // must NOT skip re-invoking JoinConversation(A) due to stale joinedConversationId === A.
            state.commitActiveConversation('conv-1', { id: 'conv-1', canSend: true }, []);
            await client.notifyActiveConversation('conv-1');
            assert.deepEqual(invocations[4], ['JoinConversation', 'conv-1']);

            let resolveJoinConv2 = null;
            const origInvoke = fakeConnection.invoke;
            fakeConnection.invoke = (...args) => {
                invocations.push(args);
                if (args[0] === 'JoinConversation' && args[1] === 'conv-2' && !resolveJoinConv2) {
                    return new Promise((resolve) => { resolveJoinConv2 = resolve; });
                }
                return Promise.resolve();
            };

            state.commitActiveConversation('conv-2', { id: 'conv-2', canSend: true }, []);
            const joinConv2Promise = client.notifyActiveConversation('conv-2');
            assert.deepEqual(invocations[5], ['JoinConversation', 'conv-2']);

            // User switches back to conv-1 BEFORE JoinConversation(conv-2) resolves on the wire
            state.commitActiveConversation('conv-1', { id: 'conv-1', canSend: true }, []);
            const joinConv1BackPromise = client.notifyActiveConversation('conv-1');
            assert.deepEqual(invocations[6], ['JoinConversation', 'conv-1']);

            resolveJoinConv2();
            await joinConv2Promise;
            await joinConv1BackPromise;
            fakeConnection.invoke = origInvoke;

            // Subsequent notifyActiveConversation('conv-1') is a no-op because focus settled on conv-1
            await client.notifyActiveConversation('conv-1');
            assert.equal(invocations.length, 7);

            // Ticket 4 (S5): seenIncomingIds is bounded at MAX_SEEN_INCOMING_IDS (500)
            assert.equal(MAX_SEEN_INCOMING_IDS, 500);
            const conv2Row = findSidebarItem('conv-2');
            const initialUnread = parseInt(conv2Row.getAttribute('data-unread-count') || '0', 10) || 0;
            for (let i = 1; i <= MAX_SEEN_INCOMING_IDS + 5; i++) {
                handlers.get('ReceiveMessage')({
                    id: 1000 + i,
                    conversationId: 'conv-2',
                    senderId: 'u2',
                    senderName: 'Bob',
                    content: `Msg ${i}`,
                    createdAtUtc: '2026-11-23T12:10:00Z'
                });
            }
            const afterBatchUnread = parseInt(conv2Row.getAttribute('data-unread-count') || '0', 10) || 0;
            assert.equal(afterBatchUnread, initialUnread + MAX_SEEN_INCOMING_IDS + 5);

            // Re-delivering recent ID (1505) is still deduplicated by seenIncomingIds
            handlers.get('ReceiveMessage')({
                id: 1000 + MAX_SEEN_INCOMING_IDS + 5,
                conversationId: 'conv-2',
                senderId: 'u2',
                senderName: 'Bob',
                content: 'Duplicate recent',
                createdAtUtc: '2026-11-23T12:10:00Z'
            });
            assert.equal(parseInt(conv2Row.getAttribute('data-unread-count') || '0', 10), afterBatchUnread);

            // Oldest ID (1001) was evicted once capacity (500) was exceeded
            handlers.get('ReceiveMessage')({
                id: 1001,
                conversationId: 'conv-2',
                senderId: 'u2',
                senderName: 'Bob',
                content: 'Evicted oldest',
                createdAtUtc: '2026-11-23T12:10:00Z'
            });
            assert.equal(parseInt(conv2Row.getAttribute('data-unread-count') || '0', 10), afterBatchUnread + 1);

            // Non-session HubExceptions / 403 must NOT log the user out
            assert.equal(isSessionRevokedSignalRError(new Error('Rate limit exceeded. Please wait before sending more requests.')), false);
            assert.equal(isSessionRevokedSignalRError({ statusCode: 403, message: 'Forbidden' }), false);
            assert.equal(isSessionRevokedSignalRError(new Error('User is not a participant in this conversation.')), false);
            onReconnectingCb(new Error('Transient socket reset'));
            assert.equal(sessionExpiredCalls, 0);

            // Revoked session triggers full termination + session expired handler
            assert.equal(isSessionRevokedSignalRError(new Error('Session has been revoked or expired.')), true);
            onCloseCb(new Error('Session has been revoked or expired.'));
            assert.equal(sessionExpiredCalls, 1);
            assert.equal(stopCount, 1);

            client.destroy();
            closeSidebarBroadcastChannel();
            console.log('OK');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public async Task ChatShell_HydratesSsrWithoutInitialBundleFetch_Switches_AndWipesOn401()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            globalThis.__AOS_TEST_MANUAL_SHELL_INIT__ = true;
            const dom = createMockDom();
            const { initChatShell } = await import('{{JsUrl("chat-shell.js")}}');
            const { createSidebarConversationItemElement, closeSidebarBroadcastChannel } = await import('{{JsUrl("conversation-list.js")}}');

            const convA = '11111111-1111-1111-1111-111111111111';
            const convB = '22222222-2222-2222-2222-222222222222';

            const shellRoot = dom.document.createElement('div'); shellRoot.id = 'chat-shell-root';
            const sidebarPane = dom.document.createElement('div'); sidebarPane.id = 'app-sidebar-pane';
            const mainPane = dom.document.createElement('main'); mainPane.id = 'app-main-pane';
            const announcer = dom.document.createElement('div'); announcer.id = 'chat-live-announcer';
            const emptyState = dom.document.createElement('div'); emptyState.id = 'chat-empty-state'; emptyState.className = 'hidden';
            const activePane = dom.document.createElement('div'); activePane.id = 'chat-active-pane';
            const header = dom.document.createElement('div'); header.id = 'active-conversation-header';
            const initialsEl = dom.document.createElement('div'); initialsEl.setAttribute('data-peer-initials', 'true'); initialsEl.textContent = 'AL';
            const nameEl = dom.document.createElement('h2'); nameEl.setAttribute('data-peer-name', 'true'); nameEl.textContent = 'Alice';
            const handleEl = dom.document.createElement('span'); handleEl.setAttribute('data-peer-handle', 'true'); handleEl.textContent = '@alice';
            const pDot = dom.document.createElement('span'); pDot.id = 'header-presence-dot';
            const pText = dom.document.createElement('span'); pText.id = 'header-presence-text';
            const typingEl = dom.document.createElement('span'); typingEl.id = 'typing-indicator';
            header.appendChild(initialsEl); header.appendChild(nameEl); header.appendChild(handleEl);
            header.appendChild(pDot); header.appendChild(pText); header.appendChild(typingEl);

            const stream = dom.document.createElement('div'); stream.id = 'message-stream';
            const container = dom.document.createElement('div'); container.id = 'messages-container';
            const noMsgs = dom.document.createElement('div'); noMsgs.id = 'no-messages-placeholder';
            stream.appendChild(noMsgs);
            stream.appendChild(container);

            const notice = dom.document.createElement('div'); notice.id = 'chat-policy-notice'; notice.className = 'hidden';
            const inputEl = dom.document.createElement('textarea'); inputEl.id = 'message-content-input';
            const sendBtn = dom.document.createElement('button'); sendBtn.id = 'send-message-btn';

            const sidebar = dom.document.createElement('div'); sidebar.id = 'conversationList';
            const itemA = createSidebarConversationItemElement({ id: convA, otherParticipantName: 'Alice', otherParticipantHandle: 'alice', lastMessagePreview: 'Hi', unreadCount: 0 }, convA);
            const itemB = createSidebarConversationItemElement({ id: convB, otherParticipantName: 'Bob', otherParticipantHandle: 'bob', lastMessagePreview: 'Yo', unreadCount: 0 }, null);
            sidebar.appendChild(itemA);
            sidebar.appendChild(itemB);

            const bootstrap = dom.document.createElement('script');
            bootstrap.id = 'chat-bootstrap';
            bootstrap.textContent = JSON.stringify({
                initialConversationId: convA,
                currentUserId: 'user-a',
                loginUrl: '/Account/Login',
                meta: { id: convA, otherParticipantId: 'peer-a', otherParticipantName: 'Alice', otherParticipantHandle: 'alice', isBlocked: false, isPeerDeleted: false, canSend: true },
                nextCursor: null,
                hasMore: false
            });

            shellRoot.appendChild(announcer);
            shellRoot.appendChild(emptyState);
            shellRoot.appendChild(activePane);
            activePane.appendChild(header);
            activePane.appendChild(stream);
            activePane.appendChild(notice);
            activePane.appendChild(inputEl);
            activePane.appendChild(sendBtn);
            shellRoot.appendChild(bootstrap);
            dom.document.body.appendChild(sidebarPane);
            sidebarPane.appendChild(sidebar);
            dom.document.body.appendChild(mainPane);
            mainPane.appendChild(shellRoot);

            let stopCalls = 0;
            const hubInvocations = [];
            const signalRLib = {
                HubConnectionState: { Connected: 'Connected' },
                HubConnectionBuilder: class {
                    withUrl() { return this; }
                    withAutomaticReconnect() { return this; }
                    build() {
                        return {
                            state: 'Disconnected',
                            start: async function() { this.state = 'Connected'; },
                            stop: async function() { stopCalls++; this.state = 'Disconnected'; },
                            invoke: async (...args) => { hubInvocations.push(args); },
                            on: () => {},
                            onreconnecting: () => {},
                            onreconnected: () => {},
                            onclose: () => {}
                        };
                    }
                }
            };

            const fetchedUrls = [];
            let force403 = false;
            let force401 = false;
            const fetchImpl = async (url) => {
                fetchedUrls.push(url);
                if (url === '/api/presence') {
                    return { status: 200, ok: true, redirected: false, headers: { get: () => 'application/json' }, text: async () => JSON.stringify({}) };
                }
                if (force403) {
                    return {
                        status: 403,
                        ok: false,
                        redirected: false,
                        headers: { get: () => 'application/problem+json' },
                        text: async () => JSON.stringify({ status: 403, detail: 'Forbidden' })
                    };
                }
                if (force401) {
                    return { status: 401, ok: false, redirected: false, headers: { get: () => '' }, text: async () => '' };
                }
                return {
                    status: 200,
                    ok: true,
                    redirected: false,
                    headers: { get: () => 'application/json' },
                    text: async () => JSON.stringify({
                        viewerUserId: 'user-a',
                        conversation: { id: convB, otherParticipantId: 'peer-b', otherParticipantName: 'Bob', otherParticipantHandle: 'bob', isBlocked: false, isPeerDeleted: false, canSend: true },
                        messages: [{ id: 101, conversationId: convB, senderId: 'peer-b', senderName: 'Bob', content: 'Hello from B', createdAtUtc: '2026-11-23T12:00:00Z' }],
                        nextCursor: null,
                        hasMore: false
                    })
                };
            };

            const replacedUrls = [];
            const shell = initChatShell({
                isolatedState: true,
                signalRLib,
                fetchImpl,
                navigateReplace: (u) => replacedUrls.push(u)
            });

            // 1. SSR hydration must NOT trigger an initial /bundle fetch, and must emit strictly 1 /api/presence fetch
            await new Promise(r => setTimeout(r, 0));
            assert.equal(fetchedUrls.some(u => u.includes('/bundle')), false);
            assert.equal(fetchedUrls.filter(u => u === '/api/presence').length, 1);
            assert.equal(shell.state.activeConversationId, convA);

            // 2. Click conversation B -> switches without reload, preserves draft, moves aria-current
            shell.composer.setText('Draft A');
            await shell.router.switchConversation(convB);
            assert.equal(shell.state.activeConversationId, convB);
            assert.equal(shell.state.getDraft(convA), 'Draft A');
            assert.equal(itemB.getAttribute('aria-current'), 'page');
            assert.equal(itemA.hasAttribute('aria-current'), false);
            assert.equal(nameEl.textContent, 'Bob');
            assert.equal(container.children.length, 1);

            // 2b. Switch B -> A with 403 error clears Bob's header identity, clears sidebar aria-current, resets title, and invokes LeaveConversation(convB)
            force403 = true;
            await shell.router.switchConversation(convA);
            force403 = false;
            assert.equal(shell.state.activeConversationId, null);
            assert.equal(nameEl.textContent, '');
            assert.equal(handleEl.textContent, '');
            assert.equal(initialsEl.textContent, '');
            assert.equal(header.getAttribute('data-peer-user-id'), '');
            assert.equal(itemA.hasAttribute('aria-current'), false);
            assert.equal(itemB.hasAttribute('aria-current'), false);
            assert.equal(dom.document.title, 'A♡S Messenger - Desktop App');
            assert.ok(hubInvocations.some(args => args[0] === 'LeaveConversation' && args[1] === convB));
            assert.equal(stopCalls, 0);

            // 2c. [S4] Header action [data-confirm] opens custom #chat-confirm-modal instead of native window.confirm
            let nativeConfirmCount = 0;
            globalThis.window.confirm = () => { nativeConfirmCount++; return true; };
            const clearForm = dom.document.createElement('form');
            clearForm.setAttribute('data-header-action-form', 'clear');
            clearForm.setAttribute('data-confirm', 'Clear chat history for yourself?');
            const clearConvInput = dom.document.createElement('input');
            clearConvInput.name = 'conversationId';
            clearConvInput.setAttribute('name', 'conversationId');
            clearConvInput.value = convB;
            clearForm.appendChild(clearConvInput);
            let submittedCount = 0;
            clearForm.submit = () => { submittedCount++; };
            dom.document.body.appendChild(clearForm);

            let firstSubmitPrevented = false;
            dom.document.dispatchEvent({ type: 'submit', target: clearForm, preventDefault() { firstSubmitPrevented = true; } });
            assert.equal(firstSubmitPrevented, true);
            assert.equal(nativeConfirmCount, 0, 'Header action confirm must never call native window.confirm');
            assert.equal(submittedCount, 0);
            const chatConfirmModal = dom.document.getElementById('chat-confirm-modal');
            assert.ok(chatConfirmModal && !chatConfirmModal.classList.contains('hidden'));
            assert.equal(dom.document.getElementById('chat-confirm-message').textContent, 'Clear chat history for yourself?');

            // Escape closes modal without submitting
            dom.document.dispatchEvent({ type: 'keydown', key: 'Escape', preventDefault() {} });
            assert.equal(chatConfirmModal.classList.contains('hidden'), true);
            assert.equal(submittedCount, 0);

            // Confirming triggers form submission without invoking window.confirm
            dom.document.dispatchEvent({ type: 'submit', target: clearForm, preventDefault() {} });
            const chatConfirmAccept = dom.document.getElementById('chat-confirm-accept-btn');
            dom.document.dispatchEvent({ type: 'click', target: chatConfirmAccept, preventDefault() {} });
            assert.equal(chatConfirmModal.classList.contains('hidden'), true);
            assert.equal(submittedCount, 1);
            assert.equal(nativeConfirmCount, 0);
            clearForm.remove();

            // 3. 401 session revocation wipes messages, sidebar, drafts, stops SignalR, and replaces URL
            force401 = true;
            await shell.router.switchConversation(convA);
            assert.equal(stopCalls, 1);
            assert.equal(shell.state.drafts.size, 0);
            assert.equal(container.children.length, 0);
            assert.equal(sidebar.children.length, 0);
            assert.deepEqual(replacedUrls, ['/Account/Login?sessionExpired=true']);

            shell.destroy();
            assert.equal(dom.document.getElementById('chat-confirm-modal'), null, 'destroy() must remove fallback chat confirm modal');
            closeSidebarBroadcastChannel();
            console.log('OK');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public async Task ChatShell_PresenceFallback_FetchesOnceOnTransientSignalRFailure_AndSkipsOn401()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            globalThis.__AOS_TEST_MANUAL_SHELL_INIT__ = true;

            function mountMinimalShell(dom) {
                const shellRoot = dom.document.createElement('div'); shellRoot.id = 'chat-shell-root';
                const sidebarPane = dom.document.createElement('div'); sidebarPane.id = 'app-sidebar-pane';
                const mainPane = dom.document.createElement('main'); mainPane.id = 'app-main-pane';
                const header = dom.document.createElement('div'); header.id = 'active-conversation-header';
                header.setAttribute('data-peer-user-id', '');
                const pDot = dom.document.createElement('span'); pDot.id = 'header-presence-dot';
                const pText = dom.document.createElement('span'); pText.id = 'header-presence-text'; pText.textContent = 'Offline';
                const typingEl = dom.document.createElement('span'); typingEl.id = 'typing-indicator';
                header.appendChild(pDot); header.appendChild(pText); header.appendChild(typingEl);

                const stream = dom.document.createElement('div'); stream.id = 'message-stream';
                const container = dom.document.createElement('div'); container.id = 'messages-container';
                stream.appendChild(container);
                const inputEl = dom.document.createElement('textarea'); inputEl.id = 'message-content-input';
                const sendBtn = dom.document.createElement('button'); sendBtn.id = 'send-message-btn';
                const sidebar = dom.document.createElement('div'); sidebar.id = 'conversationList';
                const bootstrap = dom.document.createElement('script');
                bootstrap.id = 'chat-bootstrap';
                bootstrap.textContent = JSON.stringify({
                    initialConversationId: null,
                    currentUserId: 'user-a',
                    loginUrl: '/Account/Login',
                    meta: null,
                    nextCursor: null,
                    hasMore: false
                });

                shellRoot.appendChild(header);
                shellRoot.appendChild(stream);
                shellRoot.appendChild(inputEl);
                shellRoot.appendChild(sendBtn);
                shellRoot.appendChild(bootstrap);
                dom.document.body.appendChild(sidebarPane);
                sidebarPane.appendChild(sidebar);
                dom.document.body.appendChild(mainPane);
                mainPane.appendChild(shellRoot);
            }

            // 1. Transient SignalR start failure still executes fallback presence fetch once
            {
                const dom = createMockDom();
                mountMinimalShell(dom);
                const { initChatShell } = await import('{{JsUrl("chat-shell.js")}}');
                const { closeSidebarBroadcastChannel } = await import('{{JsUrl("conversation-list.js")}}');

                const fetchedUrls = [];
                const replacedUrls = [];
                const shell = initChatShell({
                    isolatedState: true,
                    signalRLib: {
                        HubConnectionState: { Connected: 'Connected' },
                        HubConnectionBuilder: class {
                            withUrl() { return this; }
                            withAutomaticReconnect() { return this; }
                            build() {
                                return {
                                    state: 'Disconnected',
                                    start: async () => { throw new Error('Transient network error'); },
                                    stop: async () => {},
                                    invoke: async () => {},
                                    on: () => {},
                                    onreconnecting: () => {},
                                    onreconnected: () => {},
                                    onclose: () => {}
                                };
                            }
                        }
                    },
                    fetchImpl: async (url) => {
                        fetchedUrls.push(url);
                        return {
                            status: 200,
                            ok: true,
                            redirected: false,
                            headers: { get: () => 'application/json' },
                            text: async () => JSON.stringify({})
                        };
                    },
                    navigateReplace: (u) => replacedUrls.push(u)
                });

                await new Promise(r => setTimeout(r, 0));
                assert.equal(fetchedUrls.filter(u => u === '/api/presence').length, 1);
                assert.equal(replacedUrls.length, 0);

                shell.destroy();
                closeSidebarBroadcastChannel();
            }

            // 2. 401 SignalR start failure triggers session expiry and skips presence fetch (0 requests)
            {
                const dom = createMockDom();
                mountMinimalShell(dom);
                const { initChatShell } = await import('{{JsUrl("chat-shell.js")}}');
                const { closeSidebarBroadcastChannel } = await import('{{JsUrl("conversation-list.js")}}');

                const fetchedUrls = [];
                const replacedUrls = [];
                const shell = initChatShell({
                    isolatedState: true,
                    signalRLib: {
                        HubConnectionState: { Connected: 'Connected' },
                        HubConnectionBuilder: class {
                            withUrl() { return this; }
                            withAutomaticReconnect() { return this; }
                            build() {
                                return {
                                    state: 'Disconnected',
                                    start: async () => {
                                        const err = new Error('Unauthorized');
                                        err.statusCode = 401;
                                        throw err;
                                    },
                                    stop: async () => {},
                                    invoke: async () => {},
                                    on: () => {},
                                    onreconnecting: () => {},
                                    onreconnected: () => {},
                                    onclose: () => {}
                                };
                            }
                        }
                    },
                    fetchImpl: async (url) => {
                        fetchedUrls.push(url);
                        return {
                            status: 200,
                            ok: true,
                            redirected: false,
                            headers: { get: () => 'application/json' },
                            text: async () => JSON.stringify({})
                        };
                    },
                    navigateReplace: (u) => replacedUrls.push(u)
                });

                await new Promise(r => setTimeout(r, 0));
                assert.equal(fetchedUrls.filter(u => u === '/api/presence').length, 0);
                assert.deepEqual(replacedUrls, ['/Account/Login?sessionExpired=true']);

                shell.destroy();
                closeSidebarBroadcastChannel();
            }

            // 3. [W13] Backed-off start() retry, onclose recovery, online trigger, and onreconnected catch reset
            {
                const dom = createMockDom();
                mountMinimalShell(dom);
                const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
                const { configureApiClient } = await import('{{JsUrl("api-client.js")}}');
                const { createSignalRClient } = await import('{{JsUrl("signalr-client.js")}}');
                const { closeSidebarBroadcastChannel } = await import('{{JsUrl("conversation-list.js")}}');

                const state = createChatState();
                state.activeConversationId = 'conv-recover';
                state.currentUserId = 'u1';

                const onlineListeners = [];
                const origAddEvt = globalThis.window.addEventListener?.bind(globalThis.window);
                const origRemEvt = globalThis.window.removeEventListener?.bind(globalThis.window);
                globalThis.window.addEventListener = (type, fn) => {
                    if (type === 'online') onlineListeners.push(fn);
                    else origAddEvt?.(type, fn);
                };
                globalThis.window.removeEventListener = (type, fn) => {
                    if (type === 'online') {
                        const idx = onlineListeners.indexOf(fn);
                        if (idx >= 0) onlineListeners.splice(idx, 1);
                    } else origRemEvt?.(type, fn);
                };

                let startAttempts = 0;
                let failStart = true;
                let failJoin = false;
                let onCloseCallback = null;
                let onReconnectedCallback = null;
                const joinCalls = [];
                const reconcileCalls = [];

                configureApiClient({
                    onSessionExpired: () => {},
                    fetchImpl: async (url) => {
                        reconcileCalls.push(url);
                        return {
                            status: 200,
                            ok: true,
                            redirected: false,
                            headers: { get: () => 'application/json' },
                            text: async () => JSON.stringify(
                                url.includes('/api/conversations') && !url.includes('bundle')
                                    ? []
                                    : { viewerUserId: 'u1', conversation: { id: 'conv-recover', otherParticipantName: 'Peer', otherParticipantHandle: 'peer' }, messages: [] }
                            )
                        };
                    }
                });

                const fakeConn = {
                    state: 'Disconnected',
                    start: async () => {
                        startAttempts++;
                        if (failStart) throw new Error('Transient start failure');
                        fakeConn.state = 'Connected';
                    },
                    stop: async () => { fakeConn.state = 'Disconnected'; },
                    invoke: async (method, arg) => {
                        if (method === 'JoinConversation') {
                            joinCalls.push(arg);
                            if (failJoin) throw new Error('Transient JoinConversation error');
                        }
                    },
                    on: () => {},
                    onreconnecting: () => {},
                    onreconnected: (fn) => { onReconnectedCallback = fn; },
                    onclose: (fn) => { onCloseCallback = fn; }
                };

                const client = createSignalRClient({
                    state,
                    onSessionExpired: () => {},
                    retryDelaysMs: [10, 20],
                    signalRLib: {
                        HubConnectionState: { Connected: 'Connected' },
                        HubConnectionBuilder: class {
                            withUrl() { return this; }
                            withAutomaticReconnect() { return this; }
                            build() { return fakeConn; }
                        }
                    }
                });

                const firstOk = await client.start();
                assert.equal(firstOk, false);
                assert.equal(startAttempts, 1);

                // Backoff timer recovers automatically
                failStart = false;
                await new Promise(r => setTimeout(r, 35));
                assert.equal(startAttempts, 2);
                assert.equal(fakeConn.state, 'Connected');
                assert.ok(reconcileCalls.some(u => u.includes('/conv-recover/')));

                // onclose followed by browser 'online' event triggers immediate reconnect
                fakeConn.state = 'Disconnected';
                onCloseCallback(new Error('Idle close'));
                assert.equal(onlineListeners.length, 1);
                onlineListeners[0]();
                await new Promise(r => setTimeout(r, 20));
                assert.equal(startAttempts, 3);
                assert.equal(fakeConn.state, 'Connected');

                // onreconnected JoinConversation error resets requestedConversationId so retry succeeds
                joinCalls.length = 0;
                failJoin = true;
                await onReconnectedCallback();
                assert.equal(joinCalls.length, 1);
                failJoin = false;
                await client.notifyActiveConversation('conv-recover');
                assert.equal(joinCalls.length, 2);

                client.destroy();
                assert.equal(onlineListeners.length, 0);
                closeSidebarBroadcastChannel();
            }

            console.log('OK');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }
}
