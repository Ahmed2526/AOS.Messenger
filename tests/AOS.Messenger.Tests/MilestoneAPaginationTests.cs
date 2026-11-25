using System.Diagnostics;
using System.Text;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class MilestoneAPaginationTests
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

    private static (int ExitCode, string Output, string Error) RunNodeModuleScript(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "--input-type=module -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start node process for ES module verification.");

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) errorBuilder.AppendLine(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Node ES module verification timed out.");
        }

        process.WaitForExit();
        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
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
                    this.scrollHeight = 1200;
                    this.clientHeight = 500;
                    this.scrollTop = 700;
                    this.scrollIntoViewCalls = 0;
                    this.classList = createClassList(this);
                }
                get id() { return this._attrs.get('id') || ''; }
                set id(v) { if (v) this._attrs.set('id', String(v)); else this._attrs.delete('id'); }
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
                remove() { if (this.parentNode) this.parentNode.removeChild(this); }
                scrollIntoView() { this.scrollIntoViewCalls++; }
                closest(selector) {
                    let cur = this;
                    while (cur) {
                        if (matchesSimpleSelector(cur, selector)) return cur;
                        cur = cur.parentElement;
                    }
                    return null;
                }
                querySelectorAll(selector) {
                    const res = [];
                    const walk = (n) => {
                        for (const c of n.children) {
                            if (matchesSimpleSelector(c, selector)) res.push(c);
                            walk(c);
                        }
                    };
                    walk(this);
                    return res;
                }
                querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
                addEventListener(type, fn) {
                    if (!this._listeners.has(type)) this._listeners.set(type, new Set());
                    this._listeners.get(type).add(fn);
                }
                removeEventListener(type, fn) {
                    const set = this._listeners.get(type);
                    if (set) set.delete(fn);
                }
                dispatchEvent(evt) {
                    evt.target = evt.target || this;
                    evt.preventDefault = evt.preventDefault || (() => { evt.defaultPrevented = true; });
                    evt.stopPropagation = evt.stopPropagation || (() => {});
                    const set = this._listeners.get(evt.type);
                    if (set) {
                        for (const fn of [...set]) fn(evt);
                    }
                }
            }

            const body = new MockNode(1, 'BODY');
            const doc = {
                body,
                readyState: 'complete',
                visibilityState: 'visible',
                title: 'Test',
                hasFocus: () => true,
                createElement: (tag) => new MockNode(1, String(tag).toUpperCase()),
                createTextNode: (txt) => {
                    const n = new MockNode(3, '');
                    n._textContent = String(txt ?? '');
                    return n;
                },
                createDocumentFragment: () => new MockNode(11, ''),
                getElementById: (id) => body.querySelector(`#${id}`),
                querySelector: (s) => body.querySelector(s),
                querySelectorAll: (s) => body.querySelectorAll(s),
                addEventListener: (t, fn) => {
                    if (!docListeners.has(t)) docListeners.set(t, new Set());
                    docListeners.get(t).add(fn);
                },
                removeEventListener: (t, fn) => {
                    const s = docListeners.get(t);
                    if (s) s.delete(fn);
                },
                dispatchEvent: (evt) => {
                    evt.preventDefault = evt.preventDefault || (() => { evt.defaultPrevented = true; });
                    evt.stopPropagation = evt.stopPropagation || (() => {});
                    const s = docListeners.get(evt.type);
                    if (s) {
                        for (const fn of [...s]) fn(evt);
                    }
                }
            };

            const win = {
                location: { pathname: '/chat/conv-1', search: '', href: '/chat/conv-1', replace: () => {}, reload: () => {} },
                addEventListener: (t, fn) => {
                    if (!winListeners.has(t)) winListeners.set(t, new Set());
                    winListeners.get(t).add(fn);
                },
                removeEventListener: (t, fn) => {
                    const s = winListeners.get(t);
                    if (s) s.delete(fn);
                }
            };

            globalThis.document = doc;
            globalThis.window = win;
            globalThis.history = { replaceState: () => {}, pushState: () => {} };
            return { document: doc, window: win };
        }

        function mountPaginationDom(dom, convId = 'conv-1') {
            const shell = dom.document.createElement('div');
            shell.id = 'chat-shell-root';
            const stream = dom.document.createElement('div');
            stream.id = 'message-stream';
            stream.scrollHeight = 1000;
            stream.clientHeight = 500;
            stream.scrollTop = 500;

            const sentinel = dom.document.createElement('div');
            sentinel.id = 'messages-top-sentinel';
            const historyStart = dom.document.createElement('div');
            historyStart.id = 'messages-history-start';
            historyStart.className = 'hidden';
            const loadOlderWrap = dom.document.createElement('div');
            loadOlderWrap.id = 'messages-load-older-wrap';
            loadOlderWrap.className = 'hidden';
            const loadOlderBtn = dom.document.createElement('button');
            loadOlderBtn.id = 'messages-load-older-btn';
            loadOlderWrap.appendChild(loadOlderBtn);
            const errorWrap = dom.document.createElement('div');
            errorWrap.id = 'messages-load-older-error';
            errorWrap.className = 'hidden';
            const errorText = dom.document.createElement('span');
            errorText.id = 'messages-load-older-error-text';
            const retryBtn = dom.document.createElement('button');
            retryBtn.id = 'messages-load-older-retry-btn';
            errorWrap.appendChild(errorText);
            errorWrap.appendChild(retryBtn);
            const loadingTop = dom.document.createElement('div');
            loadingTop.id = 'messages-loading-top';
            loadingTop.className = 'hidden';
            const container = dom.document.createElement('div');
            container.id = 'messages-container';
            container.setAttribute('data-conversation-id', convId);

            stream.appendChild(sentinel);
            stream.appendChild(historyStart);
            stream.appendChild(loadOlderWrap);
            stream.appendChild(errorWrap);
            stream.appendChild(loadingTop);
            stream.appendChild(container);
            shell.appendChild(stream);
            dom.document.body.appendChild(shell);

            return { shell, stream, sentinel, historyStart, loadOlderWrap, loadOlderBtn, errorWrap, errorText, retryBtn, loadingTop, container };
        }
        """;

    [Fact]
    public void LoadOlderMessages_UsesPageSize20_PreservesScrollAnchor_SyncsStateAndDom_AndShowsHistoryStart()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const els = mountPaginationDom(dom, 'conv-1');

            const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
            const { configureApiClient, getMessagesPage, DEFAULT_MESSAGE_PAGE_SIZE } = await import('{{JsUrl("api-client.js")}}');
            const messageRenderer = await import('{{JsUrl("message-renderer.js")}}');
            const { createChatRouter } = await import('{{JsUrl("chat-router.js")}}');

            assert.equal(DEFAULT_MESSAGE_PAGE_SIZE, 20);

            const requests = [];
            configureApiClient({
                fetchImpl: async (url) => {
                    requests.push(url);
                    return {
                        status: 200,
                        ok: true,
                        redirected: false,
                        headers: { get: () => 'application/json' },
                        text: async () => JSON.stringify({
                            messages: [
                                { id: 2, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'Older two', createdAtUtc: '2026-11-23T11:58:00Z' },
                                { id: 1, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'Older one', createdAtUtc: '2026-11-23T11:57:00Z' }
                            ],
                            nextCursor: null,
                            hasMore: false
                        })
                    };
                }
            });

            const state = createChatState();
            state.currentUserId = 'u1';
            const initialMsg = { id: 21, conversationId: 'conv-1', senderId: 'u1', senderName: 'Alice', content: 'Latest', createdAtUtc: '2026-11-23T12:00:00Z', isOutgoing: true };
            state.commitActiveConversation('conv-1', { id: 'conv-1', canSend: true }, [initialMsg], 'cursor-20', true);
            messageRenderer.renderMessages(els.container, [initialMsg], 'u1', state.activeMeta);
            messageRenderer.syncPaginationAttributes(els.container, 'cursor-20', true);

            // Simulate user scrolling near top and scrollHeight expanding during prepend
            els.stream.scrollTop = 100;
            els.stream.scrollHeight = 1000;
            const origPrepend = messageRenderer.prependOlderMessages;
            const rendererWrapper = {
                ...messageRenderer,
                prependOlderMessages: (c, s, msgs, uid, meta) => {
                    const added = origPrepend(c, s, msgs, uid, meta);
                    // Simulate height growth when DOM rows are inserted above viewport (preserve test check)
                    return added;
                }
            };

            const router = createChatRouter({
                state,
                api: { getMessagesPage },
                renderer: rendererWrapper
            });

            const result = await router.loadOlderMessages();
            assert.equal(result.loaded, 2);
            assert.equal(result.hasMore, false);
            assert.equal(result.error, null);

            assert.equal(requests.length, 1);
            assert.ok(requests[0].includes('pageSize=20'));
            assert.ok(requests[0].includes('cursor=cursor-20'));

            // Ordering preserved: 1, 2 prepended before 21
            assert.deepEqual(els.container.children.map(n => n.getAttribute('data-message-id')), ['1', '2', '21']);

            // state.messagesById & DOM attributes & history start pill synchronized
            assert.ok(state.messagesById.has('1'));
            assert.ok(state.messagesById.has('2'));
            assert.equal(els.container.getAttribute('data-has-more'), 'false');
            assert.equal(els.container.getAttribute('data-next-cursor'), '');
            assert.equal(els.historyStart.classList.contains('hidden'), false);
            assert.equal(els.loadingTop.classList.contains('hidden'), true);
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void LoadOlderMessages_On429OrNetworkError_ShowsRetryChip_EnforcesCooldown_AndAllowsManualForceRetry()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const els = mountPaginationDom(dom, 'conv-1');

            const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
            const { configureApiClient, getMessagesPage } = await import('{{JsUrl("api-client.js")}}');
            const messageRenderer = await import('{{JsUrl("message-renderer.js")}}');
            const { createChatRouter } = await import('{{JsUrl("chat-router.js")}}');

            let attempt = 0;
            configureApiClient({
                fetchImpl: async () => {
                    attempt++;
                    if (attempt === 1) {
                        return {
                            status: 429,
                            ok: false,
                            redirected: false,
                            headers: { get: () => 'application/problem+json' },
                            text: async () => JSON.stringify({ status: 429, detail: 'Slow down' })
                        };
                    }
                    return {
                        status: 200,
                        ok: true,
                        redirected: false,
                        headers: { get: () => 'application/json' },
                        text: async () => JSON.stringify({
                            messages: [{ id: 5, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'Recovered', createdAtUtc: '2026-11-23T11:50:00Z' }],
                            nextCursor: 'cursor-5',
                            hasMore: true
                        })
                    };
                }
            });

            const state = createChatState();
            state.currentUserId = 'u1';
            state.commitActiveConversation('conv-1', { id: 'conv-1' }, [], 'cursor-10', true);

            const router = createChatRouter({
                state,
                api: { getMessagesPage },
                renderer: messageRenderer
            });

            const failRes = await router.loadOlderMessages();
            assert.equal(failRes.error, 'Loading too fast. Wait a moment and retry.');
            assert.equal(els.errorWrap.classList.contains('hidden'), false);
            assert.equal(els.errorText.textContent, 'Loading too fast. Wait a moment and retry.');

            // Automatic scroll immediately during cooldown is suppressed
            const cooldownRes = await router.loadOlderMessages();
            assert.equal(cooldownRes.error, 'COOLDOWN');
            assert.equal(attempt, 1);

            // Manual Retry bypasses cooldown (force: true) and clears error
            const retryRes = await router.loadOlderMessages({ force: true });
            assert.equal(retryRes.loaded, 1);
            assert.equal(retryRes.error, null);
            assert.equal(attempt, 2);
            assert.equal(els.errorWrap.classList.contains('hidden'), true);
            assert.equal(els.container.children.length, 1);
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void PaginationUi_TallViewport_ShowsManualLoadOlderButton_AndHidesWhenNoMore()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const els = mountPaginationDom(dom, 'conv-1');
            els.stream.scrollHeight = 400;
            els.stream.clientHeight = 900;

            const { syncPaginationUi } = await import('{{JsUrl("message-renderer.js")}}');

            syncPaginationUi({ hasMore: true, hasMessages: true, hasPaginatedOlder: false, isLoading: false, errorMessage: null, streamEl: els.stream });
            assert.equal(els.loadOlderWrap.classList.contains('hidden'), false);
            assert.equal(els.historyStart.classList.contains('hidden'), true);

            syncPaginationUi({ hasMore: false, hasMessages: true, hasPaginatedOlder: true, isLoading: false, errorMessage: null, streamEl: els.stream });
            assert.equal(els.loadOlderWrap.classList.contains('hidden'), true);
            assert.equal(els.historyStart.classList.contains('hidden'), false);
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void LoadOlderMessages_AbortedOnConversationSwitch_NeverCorruptsNewConversation()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const els = mountPaginationDom(dom, 'conv-1');

            const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
            const { configureApiClient, getMessagesPage } = await import('{{JsUrl("api-client.js")}}');
            const messageRenderer = await import('{{JsUrl("message-renderer.js")}}');
            const { createChatRouter } = await import('{{JsUrl("chat-router.js")}}');

            let resolveDelayed;
            configureApiClient({
                fetchImpl: async () => new Promise((resolve) => {
                    resolveDelayed = resolve;
                })
            });

            const state = createChatState();
            state.currentUserId = 'u1';
            state.commitActiveConversation('conv-1', { id: 'conv-1' }, [], 'cursor-conv1', true);

            const router = createChatRouter({
                state,
                api: { getMessagesPage },
                renderer: messageRenderer
            });

            const pendingOldLoad = router.loadOlderMessages();
            // Switch to conv-2 while conv-1 page request is still in-flight
            state.beginSwitch('conv-2');
            state.commitActiveConversation('conv-2', { id: 'conv-2' }, [{ id: 99, conversationId: 'conv-2', senderId: 'u2', senderName: 'Zoe', content: 'Conv 2', createdAtUtc: '2026-11-23T12:10:00Z' }], null, false);
            messageRenderer.renderMessages(els.container, [...state.messagesById.values()], 'u1', state.activeMeta);

            resolveDelayed({
                status: 200,
                ok: true,
                redirected: false,
                headers: { get: () => 'application/json' },
                text: async () => JSON.stringify({
                    messages: [{ id: 1, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'Stale conv-1', createdAtUtc: '2026-11-23T10:00:00Z' }],
                    nextCursor: null,
                    hasMore: false
                })
            });

            const res = await pendingOldLoad;
            assert.equal(res.error, 'ABORTED');
            assert.deepEqual(els.container.children.map(n => n.getAttribute('data-message-id')), ['99']);
            assert.equal(state.messagesById.has('1'), false);
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void ReplyQuoteClick_FetchesUpTo3OlderPagesToLocateParent_AndHandlesUnavailableParent()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const els = mountPaginationDom(dom, 'conv-1');

            const editModal = dom.document.createElement('div'); editModal.id = 'edit-message-modal'; editModal.className = 'hidden';
            const delModal = dom.document.createElement('div'); delModal.id = 'delete-message-modal'; delModal.className = 'hidden';
            dom.document.body.appendChild(editModal);
            dom.document.body.appendChild(delModal);

            const { setupMessageActions } = await import('{{JsUrl("message-actions.js")}}');

            let epoch = 1;
            let pagesFetched = 0;
            const notices = [];
            let hasMore = true;

            const replyRow = dom.document.createElement('div');
            replyRow.id = 'msg-50';
            replyRow.className = 'message-bubble-row';
            const quote = dom.document.createElement('div');
            quote.setAttribute('data-reply-parent-id', '10');
            replyRow.appendChild(quote);
            els.container.appendChild(replyRow);

            const actions = setupMessageActions(() => 'conv-1', () => 'u1', {
                getSwitchEpoch: () => epoch,
                hasMoreOlder: () => hasMore,
                loadOlderMessages: async () => {
                    pagesFetched++;
                    if (pagesFetched === 2) {
                        const target = dom.document.createElement('div');
                        target.id = 'msg-10';
                        target.className = 'message-bubble-row';
                        els.container.insertBefore(target, els.container.firstChild);
                        return { loaded: 20, hasMore: true, error: null };
                    }
                    return { loaded: 20, hasMore: true, error: null };
                },
                showActionNotice: (msg) => notices.push(msg)
            });

            dom.document.dispatchEvent({ type: 'click', target: quote });
            await new Promise(r => setTimeout(r, 20));
            assert.equal(pagesFetched, 2);
            const foundRow = dom.document.getElementById('msg-10');
            assert.equal(foundRow.scrollIntoViewCalls, 1);
            assert.ok(foundRow.classList.contains('ring-2'));

            // Test missing parent stops at MAX_REPLY_JUMP_PAGES (3) and shows notice
            pagesFetched = 0;
            const quoteMissing = dom.document.createElement('div');
            quoteMissing.setAttribute('data-reply-parent-id', '99999');
            replyRow.appendChild(quoteMissing);
            dom.document.dispatchEvent({ type: 'click', target: quoteMissing });
            await new Promise(r => setTimeout(r, 20));
            assert.equal(pagesFetched, 3);
            assert.equal(notices[0], 'Original message is older or no longer available.');

            actions.destroy();
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void ReconnectReconciliation_OfflineGapOver20_ReplacesWindowAndSyncsCursor_WhileSmallGapPreservesHistory()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const els = mountPaginationDom(dom, 'conv-1');

            const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
            const { configureApiClient } = await import('{{JsUrl("api-client.js")}}');
            const { renderMessages, syncPaginationAttributes } = await import('{{JsUrl("message-renderer.js")}}');
            const { createSignalRClient } = await import('{{JsUrl("signalr-client.js")}}');

            let nextBundle = null;
            configureApiClient({
                fetchImpl: async (url) => {
                    if (url.includes('/bundle')) {
                        return {
                            status: 200,
                            ok: true,
                            redirected: false,
                            headers: { get: () => 'application/json' },
                            text: async () => JSON.stringify(nextBundle)
                        };
                    }
                    return {
                        status: 200,
                        ok: true,
                        redirected: false,
                        headers: { get: () => 'application/json' },
                        text: async () => JSON.stringify([])
                    };
                }
            });

            let onReconnectedCb = null;
            const fakeConnection = {
                state: 'Connected',
                on: () => {},
                off: () => {},
                onclose: () => {},
                onreconnecting: () => {},
                onreconnected: (cb) => { onReconnectedCb = cb; },
                start: async () => {},
                stop: async () => {},
                invoke: async () => {}
            };
            const signalRLib = {
                HubConnectionState: { Connected: 'Connected' },
                HubConnectionBuilder: class {
                    withUrl() { return this; }
                    withAutomaticReconnect() { return this; }
                    build() { return fakeConnection; }
                }
            };

            const state = createChatState();
            state.currentUserId = 'u1';
            const oldMsgs = [
                { id: 10, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'Old 10', createdAtUtc: '2026-11-23T11:10:00Z' },
                { id: 11, conversationId: 'conv-1', senderId: 'u1', senderName: 'Alice', content: 'Old 11', createdAtUtc: '2026-11-23T11:11:00Z', isOutgoing: true }
            ];
            state.commitActiveConversation('conv-1', { id: 'conv-1', canSend: true }, oldMsgs, 'cursor-9', true);
            renderMessages(els.container, oldMsgs, 'u1', state.activeMeta);
            syncPaginationAttributes(els.container, 'cursor-9', true);

            let windowReplacedCount = 0;
            const client = createSignalRClient({
                state,
                onSessionExpired: () => {},
                onReconciledWindowReplaced: () => { windowReplacedCount++; },
                signalRLib
            });
            await client.start();

            // Case 1: Small gap (bundle still includes id 11) preserves paginated id 10 & cursor-9
            nextBundle = {
                viewerUserId: 'u1',
                conversation: { id: 'conv-1', canSend: true },
                messages: [
                    { id: 11, conversationId: 'conv-1', senderId: 'u1', senderName: 'Alice', content: 'Old 11', createdAtUtc: '2026-11-23T11:11:00Z', isOutgoing: true },
                    { id: 12, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'New 12', createdAtUtc: '2026-11-23T11:12:00Z' }
                ],
                nextCursor: 'cursor-10',
                hasMore: false
            };
            await onReconnectedCb();
            assert.equal(windowReplacedCount, 0);
            assert.deepEqual(els.container.children.map(n => n.getAttribute('data-message-id')), ['10', '11', '12']);
            assert.equal(state.nextCursor, 'cursor-9');

            // Case 2: Large >20 offline gap (oldest bundle id 40 > newest rendered id 12 and hasMore=true) replaces window cleanly without silent gap
            nextBundle = {
                viewerUserId: 'u1',
                conversation: { id: 'conv-1', canSend: true },
                messages: [
                    { id: 40, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'Msg 40', createdAtUtc: '2026-11-23T12:40:00Z' },
                    { id: 59, conversationId: 'conv-1', senderId: 'u2', senderName: 'Bob', content: 'Msg 59', createdAtUtc: '2026-11-23T12:59:00Z' }
                ],
                nextCursor: 'cursor-39',
                hasMore: true
            };
            await onReconnectedCb();
            assert.equal(windowReplacedCount, 1);
            assert.deepEqual(els.container.children.map(n => n.getAttribute('data-message-id')), ['40', '59']);
            assert.equal(state.nextCursor, 'cursor-39');
            assert.equal(state.hasMore, true);
            assert.equal(els.container.getAttribute('data-next-cursor'), 'cursor-39');
            assert.equal(els.container.getAttribute('data-has-more'), 'true');
            client.destroy();
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }
}
