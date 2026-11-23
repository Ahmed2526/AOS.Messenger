using System.Diagnostics;
using System.Text;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ZeroRefreshClientCoreTests
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
            const parts = selector.trim();
            if (parts.startsWith('#')) {
                return el.id === parts.slice(1);
            }
            const tagAttrMatch = /^([a-z0-9]+)?((?:\[[^\]]+\])+)$/i.exec(parts);
            if (tagAttrMatch) {
                const tag = tagAttrMatch[1];
                if (tag && el.tagName.toLowerCase() !== tag.toLowerCase()) return false;
                const attrExprs = [...tagAttrMatch[2].matchAll(/\[([^\]=]+)(?:="([^"]*)")?\]/g)];
                return attrExprs.every(([, attr, val]) => {
                    if (val === undefined) return el.hasAttribute(attr);
                    return el.getAttribute(attr) === val;
                });
            }
            if (parts.startsWith('.')) {
                return el.classList.contains(parts.slice(1));
            }
            return el.tagName.toLowerCase() === parts.toLowerCase();
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
                documentElement: { clientWidth: 1024, clientHeight: 768 },
                title: 'Initial Title',
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

            const historyCalls = [];
            const history = {
                pushState: (state, title, url) => historyCalls.push({ op: 'push', state, url }),
                replaceState: (state, title, url) => historyCalls.push({ op: 'replace', state, url })
            };

            const windowObj = {
                innerWidth: 1024,
                innerHeight: 768,
                location: { pathname: '/chat', search: '', href: 'https://localhost/chat' },
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

            return { document, window: windowObj, historyCalls };
        }
        """;

    [Fact]
    public void ChatState_ManagesEpochs_PendingBuffer_LruDrafts_AndFullWipe()
    {
        var statePath = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-state.js");
        var nonCommentSource = string.Join("\n", File.ReadAllLines(statePath)
            .Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("*")));
        Assert.DoesNotContain("localStorage", nonCommentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", nonCommentSource, StringComparison.Ordinal);

        var script = $$"""
            import assert from 'node:assert/strict';
            import { createChatState, MAX_DRAFTS, MAX_DRAFT_LENGTH } from '{{JsUrl("chat-state.js")}}';

            const state = createChatState();
            state.initFromBootstrap({
                initialConversationId: 'conv-1',
                currentUserId: 'user-1',
                loginUrl: '/Account/Login',
                meta: { id: 'conv-1', canSend: true },
                nextCursor: 'cursor-1',
                hasMore: true
            });

            assert.equal(state.activeConversationId, 'conv-1');
            assert.equal(state.currentUserId, 'user-1');

            // Switch A -> B: activeConversationId clears immediately, pendingConversationId tracks B
            const epoch1 = state.beginSwitch('CONV-B');
            assert.equal(epoch1, 1);
            assert.equal(state.activeConversationId, null);
            assert.equal(state.pendingConversationId, 'CONV-B');

            // Realtime buffering only matches pendingConversationId
            assert.equal(state.bufferRealtimeEvent('conv-1', { id: 99 }), false);
            assert.equal(state.bufferRealtimeEvent('conv-b', { id: 100 }), true);

            const drained = state.commitActiveConversation('CONV-B', { id: 'CONV-B', canSend: true }, [{ id: 10, content: 'hi' }], null, false);
            assert.equal(drained.length, 1);
            assert.equal(drained[0].id, 100);
            assert.equal(state.activeConversationId, 'CONV-B');
            assert.equal(state.pendingConversationId, null);
            assert.equal(state.messagesById.size, 1);

            // LRU Draft cap (MAX_DRAFTS = 50) & whitespace deletion
            for (let i = 0; i < MAX_DRAFTS + 5; i++) {
                state.setDraft(`c-${i}`, `draft-${i}`);
            }
            assert.equal(state.drafts.size, MAX_DRAFTS);
            assert.equal(state.getDraft('c-0'), '');
            assert.equal(state.getDraft(`c-${MAX_DRAFTS + 4}`), `draft-${MAX_DRAFTS + 4}`);

            state.setDraft('c-10', '   ');
            assert.equal(state.getDraft('c-10'), '');

            // Bounded max draft length
            state.setDraft('long', 'x'.repeat(MAX_DRAFT_LENGTH + 250));
            assert.equal(state.getDraft('long').length, MAX_DRAFT_LENGTH);

            // resetActiveChatState keeps user & drafts; wipeAllSensitiveState purges everything
            state.resetActiveChatState();
            assert.equal(state.activeConversationId, null);
            assert.equal(state.currentUserId, 'user-1');
            assert.ok(state.drafts.size > 0);

            state.wipeAllSensitiveState();
            assert.equal(state.currentUserId, null);
            assert.equal(state.drafts.size, 0);
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void ApiClient_HandlesAuth_Csrf_204_ProblemDetails_AndRateLimits()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            import {
                ANTIFORGERY_PROBLEM_TYPE,
                ApiError,
                apiFetch,
                configureApiClient,
                sendMessage,
                deleteForMe,
                getConversationBundle
            } from '{{JsUrl("api-client.js")}}';

            let sessionExpiredCount = 0;
            let csrfMismatchCount = 0;
            let lastFetchArgs = null;

            const makeResp = ({ status = 200, ok = status >= 200 && status < 300, contentType = 'application/json', body = '', redirected = false, url = 'https://localhost/api/test' }) => ({
                status,
                ok,
                redirected,
                url,
                headers: { get: (name) => name.toLowerCase() === 'content-type' ? contentType : null },
                text: async () => body
            });

            let nextResponse = null;
            configureApiClient({
                onSessionExpired: () => { sessionExpiredCount++; },
                onCsrfMismatch: () => { csrfMismatchCount++; },
                getCsrfToken: () => 'csrf-token-123',
                fetchImpl: async (url, opts) => {
                    lastFetchArgs = { url, opts };
                    return nextResponse;
                }
            });

            // 1. Mutating POST attaches RequestVerificationToken, credentials: same-origin, cache: no-store
            nextResponse = makeResp({ status: 200, body: JSON.stringify({ id: 1 }) });
            const sent = await sendMessage('conv-1', { content: 'hello', idempotencyKey: 'k1' });
            assert.equal(sent.id, 1);
            assert.equal(lastFetchArgs.opts.credentials, 'same-origin');
            assert.equal(lastFetchArgs.opts.cache, 'no-store');
            assert.equal(lastFetchArgs.opts.headers['RequestVerificationToken'], 'csrf-token-123');
            assert.equal(lastFetchArgs.opts.headers['Accept'], 'application/json');

            // 2. 204 No Content (DeleteForMe) returns null without triggering logout
            nextResponse = makeResp({ status: 204, contentType: '', body: '' });
            const delRes = await deleteForMe('conv-1', 42);
            assert.equal(delRes, null);
            assert.equal(sessionExpiredCount, 0);

            // 3. 400 DomainException (ProblemDetails without antiforgery type) does NOT trigger CsrfMismatch or logout
            nextResponse = makeResp({
                status: 400,
                contentType: 'application/problem+json',
                body: JSON.stringify({ status: 400, title: 'Bad Request', detail: 'Empty text' })
            });
            await assert.rejects(() => sendMessage('conv-1', { content: '', idempotencyKey: 'k2' }), (e) => e instanceof ApiError && e.code === 'BAD_REQUEST' && e.status === 400);
            assert.equal(csrfMismatchCount, 0);
            assert.equal(sessionExpiredCount, 0);

            // 4. 400 CSRF mismatch ProblemDetails triggers onCsrfMismatch
            nextResponse = makeResp({
                status: 400,
                contentType: 'application/problem+json',
                body: JSON.stringify({ type: ANTIFORGERY_PROBLEM_TYPE, status: 400, detail: 'Bad CSRF' })
            });
            await assert.rejects(() => sendMessage('conv-1', { content: 'hi', idempotencyKey: 'k3' }), (e) => e instanceof ApiError && e.code === 'CSRF_MISMATCH');
            assert.equal(csrfMismatchCount, 1);
            assert.equal(sessionExpiredCount, 0);

            // 4b. Empty-body non-JSON 400 triggers onCsrfMismatch ONLY on mutating requests (never on GET)
            nextResponse = makeResp({ status: 400, contentType: '', body: '' });
            await assert.rejects(() => getConversationBundle('conv-1'), (e) => e instanceof ApiError && e.code === 'BAD_REQUEST' && e.status === 400);
            assert.equal(csrfMismatchCount, 1);
            assert.equal(sessionExpiredCount, 0);

            nextResponse = makeResp({ status: 400, contentType: '', body: '' });
            await assert.rejects(() => sendMessage('conv-1', { content: 'hi', idempotencyKey: 'k4' }), (e) => e instanceof ApiError && e.code === 'CSRF_MISMATCH' && e.status === 400);
            assert.equal(csrfMismatchCount, 2);
            assert.equal(sessionExpiredCount, 0);

            // 5. 403 / 404 / 429 do NOT trigger logout
            for (const [status, code] of [[403, 'FORBIDDEN'], [404, 'NOT_FOUND'], [429, 'RATE_LIMITED']]) {
                nextResponse = makeResp({
                    status,
                    contentType: 'application/problem+json',
                    body: JSON.stringify({ status, detail: `Err ${status}` })
                });
                await assert.rejects(() => getConversationBundle('conv-1'), (e) => e instanceof ApiError && e.code === code && e.status === status);
            }
            assert.equal(sessionExpiredCount, 0);

            // 6. 401, login redirect, and text/html DO trigger onSessionExpired
            nextResponse = makeResp({ status: 401, contentType: '' });
            await assert.rejects(() => getConversationBundle('conv-1'), (e) => e.code === 'UNAUTHORIZED');
            nextResponse = makeResp({ status: 200, redirected: true, url: 'https://localhost/Account/Login', contentType: 'text/html', body: '<html>login</html>' });
            await assert.rejects(() => getConversationBundle('conv-1'), (e) => e.code === 'UNAUTHORIZED');
            nextResponse = makeResp({ status: 200, contentType: 'text/html; charset=utf-8', body: '<html>unexpected</html>' });
            await assert.rejects(() => apiFetch('/api/conversations'), (e) => e.code === 'UNAUTHORIZED');
            assert.equal(sessionExpiredCount, 3);
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void ChatRouter_EnforcesGuards_AbortEpochRace_IdentityCheck_Popstate_AndPagination()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const { createChatState } = await import('{{JsUrl("chat-state.js")}}');
            const { createChatRouter, parseConversationIdFromPath } = await import('{{JsUrl("chat-router.js")}}');

            // Helper to test non-chat pages skip activation
            const stateNoShell = createChatState();
            const noShellRouter = createChatRouter({ state: stateNoShell, api: {}, renderer: {} });
            assert.equal(noShellRouter.init(), false);

            // Mount chat shell DOM
            const shellRoot = dom.document.createElement('div');
            shellRoot.id = 'chat-shell-root';
            const sidebarPane = dom.document.createElement('div');
            sidebarPane.id = 'app-sidebar-pane';
            const mainPane = dom.document.createElement('main');
            mainPane.id = 'app-main-pane';
            const containerEl = dom.document.createElement('div');
            containerEl.id = 'messages-container';
            const streamEl = dom.document.createElement('div');
            streamEl.id = 'message-stream';
            const loadingTop = dom.document.createElement('div');
            loadingTop.id = 'messages-loading-top';
            const bootstrapEl = dom.document.createElement('script');
            bootstrapEl.id = 'chat-bootstrap';
            bootstrapEl.textContent = JSON.stringify({
                initialConversationId: '11111111-1111-1111-1111-111111111111',
                currentUserId: 'user-a',
                loginUrl: '/Account/Login',
                meta: { id: '11111111-1111-1111-1111-111111111111', canSend: true },
                nextCursor: 'c1',
                hasMore: true
            });
            const linkB = dom.document.createElement('a');
            linkB.setAttribute('data-conversation-link', 'true');
            linkB.setAttribute('data-conversation-id', '22222222-2222-2222-2222-222222222222');
            linkB.href = '/chat/22222222-2222-2222-2222-222222222222';
            const backLink = dom.document.createElement('a');
            backLink.setAttribute('data-back-to-chats', 'true');
            backLink.href = '/chat';

            dom.document.body.appendChild(shellRoot);
            dom.document.body.appendChild(sidebarPane);
            dom.document.body.appendChild(mainPane);
            dom.document.body.appendChild(streamEl);
            streamEl.appendChild(loadingTop);
            streamEl.appendChild(containerEl);
            dom.document.body.appendChild(bootstrapEl);
            dom.document.body.appendChild(linkB);
            dom.document.body.appendChild(backLink);

            const state = createChatState();
            const pendingFetchResolvers = [];
            const renderedHeaders = [];
            const safeErrors = [];
            const switchErrors = [];
            let sessionExpiredCalls = 0;
            let identityChangedCalls = 0;
            let draftValue = 'draft for A';
            let olderPrependedCalls = 0;

            const fakeApi = {
                getConversationBundle: (id, { signal }) => new Promise((resolve, reject) => {
                    const rec = { id, signal, resolve, reject, aborted: false };
                    signal.addEventListener('abort', () => {
                        rec.aborted = true;
                        const abortErr = new Error('Aborted');
                        abortErr.name = 'AbortError';
                        reject(abortErr);
                    });
                    pendingFetchResolvers.push(rec);
                }),
                getMessagesPage: (id, { signal }) => new Promise((resolve, reject) => {
                    const rec = { id, signal, resolve, reject, aborted: false };
                    signal.addEventListener('abort', () => {
                        rec.aborted = true;
                        const abortErr = new Error('Aborted');
                        abortErr.name = 'AbortError';
                        reject(abortErr);
                    });
                    pendingFetchResolvers.push(rec);
                })
            };

            const fakeRenderer = {
                lockAndClearHeaderActions: () => {},
                showActivePane: () => {},
                showLoadingSkeleton: () => {},
                showEmptyState: () => {},
                clearPane: () => {},
                renderConversationHeader: (m) => renderedHeaders.push(m.id),
                renderMessages: () => {},
                prependOlderMessages: () => { olderPrependedCalls++; },
                showSafeError: (msg) => safeErrors.push(msg)
            };

            const router = createChatRouter({
                state,
                api: fakeApi,
                renderer: fakeRenderer,
                hooks: {
                    getDraftText: () => draftValue,
                    onSwitchError: (failedId, err) => { switchErrors.push({ failedId, status: err?.status }); },
                    onSessionExpired: () => { sessionExpiredCalls++; },
                    onIdentityChanged: () => { identityChangedCalls++; }
                }
            });

            assert.equal(router.init(), true);
            assert.equal(state.activeConversationId, '11111111-1111-1111-1111-111111111111');

            // Ctrl+click is preserved (not prevented)
            let prevented = false;
            dom.document.dispatchEvent({ type: 'click', button: 0, ctrlKey: true, target: linkB, defaultPrevented: false, preventDefault: () => { prevented = true; } });
            assert.equal(prevented, false);

            // Rapid switch B -> C: B is aborted and only C commits; draft for A is saved
            const pB = router.switchConversation('22222222-2222-2222-2222-222222222222');
            assert.equal(state.getDraft('11111111-1111-1111-1111-111111111111'), 'draft for A');
            assert.equal(state.activeConversationId, null);
            assert.equal(state.pendingConversationId, '22222222-2222-2222-2222-222222222222');

            // Clicking back to A while B is pending works (is NOT blocked as no-op)
            const pBackToA = router.switchConversation('11111111-1111-1111-1111-111111111111');
            assert.equal(pendingFetchResolvers[0].aborted, true);

            const pC = router.switchConversation('33333333-3333-3333-3333-333333333333');
            assert.equal(pendingFetchResolvers[1].aborted, true);

            pendingFetchResolvers[2].resolve({
                viewerUserId: 'user-a',
                conversation: { id: '33333333-3333-3333-3333-333333333333', canSend: true },
                messages: [],
                nextCursor: 'c-page-2',
                hasMore: true
            });
            await Promise.all([pB, pBackToA, pC]);

            assert.equal(state.activeConversationId, '33333333-3333-3333-3333-333333333333');
            assert.deepEqual(renderedHeaders, ['33333333-3333-3333-3333-333333333333']);

            // Pagination race: start loadOlderMessages on C, switch to B -> older response dropped
            const pOld = router.loadOlderMessages();
            const pSwitchB2 = router.switchConversation('22222222-2222-2222-2222-222222222222');
            assert.equal(pendingFetchResolvers[3].aborted, true);
            pendingFetchResolvers[4].resolve({
                viewerUserId: 'user-a',
                conversation: { id: '22222222-2222-2222-2222-222222222222', canSend: true },
                messages: [],
                nextCursor: null,
                hasMore: false
            });
            await Promise.all([pOld, pSwitchB2]);
            assert.equal(olderPrependedCalls, 0);

            // Cross-tab viewerUserId mismatch triggers onIdentityChanged
            const pMismatch = router.switchConversation('44444444-4444-4444-4444-444444444444');
            pendingFetchResolvers[5].resolve({
                viewerUserId: 'user-b',
                conversation: { id: '44444444-4444-4444-4444-444444444444', canSend: true },
                messages: []
            });
            await pMismatch;
            assert.equal(identityChangedCalls, 1);

            // 403 clears pending state, invokes onSwitchError, and renders safe error without logging out
            const p403 = router.switchConversation('55555555-5555-5555-5555-555555555555');
            pendingFetchResolvers[6].reject({ status: 403, code: 'FORBIDDEN' });
            await p403;
            assert.equal(state.pendingConversationId, null);
            assert.equal(safeErrors[0], 'This conversation is no longer available.');
            assert.deepEqual(switchErrors, [{ failedId: '55555555-5555-5555-5555-555555555555', status: 403 }]);
            assert.equal(sessionExpiredCalls, 0);

            // 401 triggers onSessionExpired
            const p401 = router.switchConversation('66666666-6666-6666-6666-666666666666');
            pendingFetchResolvers[7].reject({ status: 401, code: 'UNAUTHORIZED' });
            await p401;
            assert.equal(sessionExpiredCalls, 1);

            // popstate to /chat switches to empty state without pushing URL
            const pushCountBeforePop = dom.historyCalls.filter(c => c.op === 'push').length;
            dom.window.location.pathname = '/chat';
            dom.window.dispatchEvent({ type: 'popstate', state: null });
            assert.equal(state.activeConversationId, null);
            assert.equal(dom.historyCalls.filter(c => c.op === 'push').length, pushCountBeforePop);
            assert.equal(parseConversationIdFromPath('/chat/22222222-2222-2222-2222-222222222222'), '22222222-2222-2222-2222-222222222222');

            router.destroy();
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void MessageRenderer_OrdersDeterministically_SanitizesXss_SyncsAllHeaderForms_AndWipesSidebar()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const {
                compareMessages,
                insertMessageOrdered,
                renderMessages,
                lockAndClearHeaderActions,
                renderConversationHeader,
                wipeSensitiveDom
            } = await import('{{JsUrl("message-renderer.js")}}');

            // Build minimal header + 10 forms + stream + composer + sidebar DOM
            const header = dom.document.createElement('div');
            header.id = 'active-conversation-header';
            const initials = dom.document.createElement('div'); initials.setAttribute('data-peer-initials', 'true');
            const nameEl = dom.document.createElement('h2'); nameEl.setAttribute('data-peer-name', 'true');
            const handleEl = dom.document.createElement('span'); handleEl.setAttribute('data-peer-handle', 'true');
            const dot = dom.document.createElement('span'); dot.id = 'header-presence-dot';
            const pText = dom.document.createElement('span'); pText.id = 'header-presence-text';
            header.appendChild(initials); header.appendChild(nameEl); header.appendChild(handleEl);
            header.appendChild(dot); header.appendChild(pText);

            const actionTypes = ['pin', 'mute', 'archive', 'clear', 'delete'];
            const allForms = [];
            for (const copy of [0, 1]) {
                for (const type of actionTypes) {
                    const f = dom.document.createElement('form');
                    f.setAttribute('data-header-action-form', type);
                    const cIn = dom.document.createElement('input'); cIn.name = 'conversationId'; cIn.value = 'old-conv';
                    f.appendChild(cIn);
                    if (type === 'pin') {
                        const pIn = dom.document.createElement('input'); pIn.name = 'isPinned'; pIn.value = 'true';
                        f.appendChild(pIn);
                    }
                    if (type === 'mute') {
                        const mIn = dom.document.createElement('input'); mIn.name = 'isMuted'; mIn.value = 'true';
                        f.appendChild(mIn);
                    }
                    if (type === 'archive') {
                        const aIn = dom.document.createElement('input'); aIn.name = 'isArchived'; aIn.value = 'true';
                        f.appendChild(aIn);
                    }
                    const btn = dom.document.createElement('button'); btn.type = 'submit'; btn.title = type;
                    const icon = dom.document.createElement('span'); icon.className = 'material-symbols-outlined';
                    const lbl = dom.document.createElement('span');
                    btn.appendChild(icon); btn.appendChild(lbl);
                    f.appendChild(btn);
                    allForms.push(f);
                    header.appendChild(f);
                }
            }

            const stream = dom.document.createElement('div'); stream.id = 'message-stream';
            const container = dom.document.createElement('div'); container.id = 'messages-container';
            const noMsgs = dom.document.createElement('div'); noMsgs.id = 'no-messages-placeholder';
            stream.appendChild(noMsgs);
            stream.appendChild(container);

            const sidebar = dom.document.createElement('div'); sidebar.id = 'conversationList';
            const sbRow = dom.document.createElement('a'); sbRow.textContent = 'Private Peer Preview';
            sidebar.appendChild(sbRow);

            const announcer = dom.document.createElement('div'); announcer.id = 'chat-live-announcer';
            const msgInput = dom.document.createElement('textarea'); msgInput.id = 'message-content-input'; msgInput.value = 'secret draft';
            const sendBtn = dom.document.createElement('button'); sendBtn.id = 'send-message-btn';

            dom.document.body.appendChild(header);
            dom.document.body.appendChild(stream);
            dom.document.body.appendChild(sidebar);
            dom.document.body.appendChild(announcer);
            dom.document.body.appendChild(msgInput);
            dom.document.body.appendChild(sendBtn);

            // 1. Lock & clear header forms disables all 10 forms and wipes conversationId
            lockAndClearHeaderActions();
            for (const f of allForms) {
                assert.equal(f.querySelector('input[name="conversationId"]').value, '');
                assert.equal(f.querySelector('button[type="submit"]').disabled, true);
            }

            // 2. renderConversationHeader populates peer + enables all 10 forms with new conversationId & inverted pin/mute/archive
            renderConversationHeader({
                id: 'conv-new',
                otherParticipantId: 'peer-2',
                otherParticipantName: '<img src=x onerror=alert(1)>',
                otherParticipantHandle: 'bob_2',
                isPinned: true,
                isMuted: false,
                isArchived: true,
                isOnline: true
            });
            assert.equal(nameEl.textContent, '<img src=x onerror=alert(1)>');
            assert.equal(pText.textContent, 'Online');
            assert.ok(announcer.textContent.includes('<img src=x onerror=alert(1)>'));
            for (const f of allForms) {
                assert.equal(f.querySelector('input[name="conversationId"]').value, 'conv-new');
                assert.equal(f.querySelector('button[type="submit"]').disabled, false);
            }
            assert.equal(allForms[0].querySelector('input[name="isPinned"]').value, 'false');
            const archiveForms = allForms.filter(f => f.getAttribute('data-header-action-form') === 'archive');
            for (const af of archiveForms) {
                assert.equal(af.querySelector('input[name="isArchived"]').value, 'false');
                assert.equal(af.querySelector('.material-symbols-outlined').textContent, 'unarchive');
                assert.equal(af.querySelector('button[type="submit"]').getAttribute('title'), 'Unarchive conversation');
                const spans = af.querySelectorAll('span');
                assert.equal(spans[spans.length - 1].textContent, 'Unarchive conversation');
            }

            // 2b. lockAndClearHeaderActions after renderConversationHeader clears peer identity as well as forms
            lockAndClearHeaderActions();
            assert.equal(header.getAttribute('data-peer-user-id'), '');
            assert.equal(nameEl.textContent, '');
            assert.equal(handleEl.textContent, '');
            assert.equal(initials.textContent, '');
            renderConversationHeader({
                id: 'conv-new',
                otherParticipantId: 'peer-2',
                otherParticipantName: '<img src=x onerror=alert(1)>',
                otherParticipantHandle: 'bob_2',
                isPinned: true,
                isMuted: false,
                isArchived: false,
                isOnline: true
            });
            for (const af of archiveForms) {
                assert.equal(af.querySelector('input[name="isArchived"]').value, 'true');
                assert.equal(af.querySelector('.material-symbols-outlined').textContent, 'archive');
                assert.equal(af.querySelector('button[type="submit"]').getAttribute('title'), 'Archive conversation');
                const spans = af.querySelectorAll('span');
                assert.equal(spans[spans.length - 1].textContent, 'Archive conversation');
            }

            // 3. Deterministic (CreatedAtUtc, Id) ordering & dedup
            const tSame = '2026-11-23T12:00:00.000Z';
            renderMessages(container, [
                { id: 12, senderId: 'u1', senderName: 'A', content: 'second-same-ts', createdAtUtc: tSame },
                { id: 10, senderId: 'u1', senderName: 'A', content: 'first-same-ts', createdAtUtc: tSame },
                { id: 12, senderId: 'u1', senderName: 'A', content: 'duplicate', createdAtUtc: tSame }
            ], 'u1');
            assert.equal(container.children.length, 2);
            assert.equal(container.children[0].getAttribute('data-message-id'), '10');
            assert.equal(container.children[1].getAttribute('data-message-id'), '12');

            // Out-of-order insertion places id 11 between 10 and 12
            const inserted = insertMessageOrdered(container, { id: 11, senderId: 'u2', senderName: 'Bob', content: '<script>xss()</script>', createdAtUtc: tSame }, 'u1');
            assert.ok(inserted);
            assert.equal(container.children.length, 3);
            assert.equal(container.children[1].getAttribute('data-message-id'), '11');
            assert.equal(container.children[1].querySelector('.message-text-content').textContent, '<script>xss()</script>');

            // 4. wipeSensitiveDom clears messages, sidebar, composer, header, and title
            wipeSensitiveDom();
            assert.equal(container.children.length, 0);
            assert.equal(sidebar.children.length, 0);
            assert.equal(msgInput.value, '');
            assert.equal(msgInput.disabled, true);
            assert.equal(nameEl.textContent, '');
            assert.equal(dom.document.title, 'A♡S Messenger');
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void Phase7And8Modules_SupportDynamicConversationId_AndResetForSwitch()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const { setupTypingEmitter, setupTypingReceiver } = await import('{{JsUrl("typing.js")}}');
            const { initReceipts, markRead, cancelPendingRead } = await import('{{JsUrl("receipts.js")}}');
            const { setupEmojiPicker } = await import('{{JsUrl("reactions.js")}}');
            const { setupMessageActions } = await import('{{JsUrl("message-actions.js")}}');

            let activeConvId = 'conv-old';
            let currentEpoch = 1;
            const invoked = [];
            const handlers = new Map();

            const fakeConnection = {
                state: 'Connected',
                invoke: async (...args) => { invoked.push(args); },
                on: (evt, fn) => { handlers.set(evt, fn); },
                off: (evt) => { handlers.delete(evt); }
            };

            const inputEl = dom.document.createElement('textarea');
            inputEl.id = 'message-content-input';
            const typingEl = dom.document.createElement('span');
            typingEl.id = 'typing-indicator';
            const popoverEl = dom.document.createElement('div');
            popoverEl.id = 'reaction-picker-popover';
            const editModal = dom.document.createElement('div');
            editModal.id = 'edit-message-modal';
            const deleteModal = dom.document.createElement('div');
            deleteModal.id = 'delete-message-modal';
            dom.document.body.appendChild(inputEl);
            dom.document.body.appendChild(typingEl);
            dom.document.body.appendChild(popoverEl);
            dom.document.body.appendChild(editModal);
            dom.document.body.appendChild(deleteModal);

            // 1. Typing emitter emits stopTyping for OLD conversation even if getter changes
            const emitter = setupTypingEmitter(fakeConnection, () => activeConvId, inputEl);
            const receiver = setupTypingReceiver(fakeConnection, () => activeConvId, typingEl);

            inputEl.value = 'typing in old';
            inputEl.dispatchEvent({ type: 'input' });
            assert.deepEqual(invoked[0], ['SendTypingIndicator', 'conv-old', true]);

            handlers.get('ReceiveTypingIndicator')({ conversationId: 'conv-old', isTyping: true });
            assert.equal(typingEl.textContent, 'typing...');
            assert.equal(typingEl.classList.contains('hidden'), false);

            // Switch happens
            activeConvId = 'conv-new';
            currentEpoch = 2;
            emitter.resetForSwitch();
            receiver.resetForSwitch();
            assert.deepEqual(invoked[1], ['SendTypingIndicator', 'conv-old', false]);
            assert.equal(typingEl.textContent, '');
            assert.equal(typingEl.classList.contains('hidden'), true);

            // Receiver ignores events from old conversation after switch
            handlers.get('ReceiveTypingIndicator')({ conversationId: 'conv-old', isTyping: true });
            assert.equal(typingEl.textContent, '');
            handlers.get('ReceiveTypingIndicator')({ conversationId: 'conv-new', isTyping: true });
            assert.equal(typingEl.textContent, 'typing...');

            // 2. Receipts cancelPendingRead resolves pending promise with null cleanly
            const receipts = initReceipts(fakeConnection, () => activeConvId, () => 'user-1');
            const pendingReadPromise = markRead('conv-new', 50);
            receipts.resetForSwitch();
            const readRes = await pendingReadPromise;
            assert.equal(readRes, null);

            // 3. Reactions & MessageActions expose resetForSwitch and close popovers/modals
            const picker = setupEmojiPicker(() => activeConvId, () => 'user-1', { getSwitchEpoch: () => currentEpoch });
            const actions = setupMessageActions(() => activeConvId, () => 'user-1', { getSwitchEpoch: () => currentEpoch });
            popoverEl.classList.remove('hidden');
            editModal.classList.remove('hidden');
            deleteModal.classList.remove('hidden');

            picker.resetForSwitch();
            actions.resetForSwitch();
            assert.equal(popoverEl.classList.contains('hidden'), true);
            assert.equal(editModal.classList.contains('hidden'), true);
            assert.equal(deleteModal.classList.contains('hidden'), true);

            emitter.destroy();
            receiver.destroy();
            receipts.destroy();
            picker.destroy();
            actions.destroy();
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }

    [Fact]
    public void Presence_RequiresMatchingNonEmptyPeerId_AndSupportsSkipInitialFetch()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const dom = createMockDom();
            const { configureApiClient } = await import('{{JsUrl("api-client.js")}}');
            const { setupPresence } = await import('{{JsUrl("presence.js")}}');

            let presenceFetches = 0;
            configureApiClient({
                fetchImpl: async (url) => {
                    if (url === '/api/presence') {
                        presenceFetches++;
                        return {
                            status: 200,
                            ok: true,
                            redirected: false,
                            headers: { get: () => 'application/json' },
                            text: async () => JSON.stringify({
                                'conv-other': { userId: 'peer-other', isOnline: true, lastSeenAtUtc: null }
                            })
                        };
                    }
                    throw new Error(`Unexpected URL: ${url}`);
                }
            });

            const header = dom.document.createElement('div');
            header.id = 'active-conversation-header';
            header.setAttribute('data-peer-user-id', '');

            const headerDot = dom.document.createElement('span');
            headerDot.id = 'header-presence-dot';
            headerDot.className = 'bg-stone-400 dark:bg-stone-500';
            header.appendChild(headerDot);

            const headerText = dom.document.createElement('span');
            headerText.id = 'header-presence-text';
            headerText.textContent = 'Offline';
            header.appendChild(headerText);

            const sidebarList = dom.document.createElement('div');
            sidebarList.id = 'conversationList';
            const sidebarItem = dom.document.createElement('a');
            sidebarItem.className = 'conversation-item';
            sidebarItem.setAttribute('data-conversation-id', 'conv-other');
            sidebarItem.setAttribute('data-peer-user-id', 'peer-other');
            const avatarWrap = dom.document.createElement('div');
            avatarWrap.className = 'relative';
            const sbDot = dom.document.createElement('span');
            sbDot.className = 'presence-indicator-dot bg-stone-400';
            avatarWrap.appendChild(sbDot);
            sidebarItem.appendChild(avatarWrap);
            sidebarList.appendChild(sidebarItem);

            dom.document.body.appendChild(header);
            dom.document.body.appendChild(sidebarList);

            // 1. skipInitialFetch: true performs 0 fetches on setup
            const deferredPresence = setupPresence(null, sidebarList, header, { skipInitialFetch: true });
            assert.equal(presenceFetches, 0);

            // 2. Empty data-peer-user-id ignores unrelated peer SignalR + REST updates while sidebar updates
            deferredPresence.handlePresenceChanged({ userId: 'peer-other', isOnline: true });
            assert.equal(headerText.textContent, 'Offline');
            assert.ok(headerDot.className.includes('bg-stone-400'));
            assert.ok(sbDot.className.includes('bg-[#6F9473]'));

            sbDot.className = 'presence-indicator-dot bg-stone-400';
            await deferredPresence.fetchPresence();
            assert.equal(presenceFetches, 1);
            assert.equal(headerText.textContent, 'Offline');
            assert.ok(headerDot.className.includes('bg-stone-400'));
            assert.ok(sbDot.className.includes('bg-[#6F9473]'));

            // 3. Missing data-peer-user-id attribute also prevents overwrite
            header.removeAttribute('data-peer-user-id');
            deferredPresence.handlePresenceChanged({ userId: 'peer-other', isOnline: true });
            assert.equal(headerText.textContent, 'Offline');

            // 4. Matching non-empty data-peer-user-id updates header (case-insensitive)
            header.setAttribute('data-peer-user-id', 'PEER-TARGET');
            deferredPresence.handlePresenceChanged({ userId: 'peer-target', isOnline: true });
            assert.equal(headerText.textContent, 'Online');
            assert.ok(headerDot.className.includes('bg-[#6F9473]'));

            // 5. resetHeaderPresence clears data-peer-user-id so subsequent events do not bleed in
            deferredPresence.resetHeaderPresence();
            assert.equal(headerText.textContent, 'Offline');
            deferredPresence.handlePresenceChanged({ userId: 'peer-target', isOnline: true });
            assert.equal(headerText.textContent, 'Offline');
            assert.ok(headerDot.className.includes('bg-stone-400'));

            // 6. Default options (omitted) preserves eager fetch for backward compatibility
            const eagerPresence = setupPresence(null, sidebarList, header);
            await Promise.resolve();
            assert.equal(presenceFetches, 2);

            deferredPresence.destroy();
            eagerPresence.destroy();
            console.log('OK');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {error}\n{output}");
    }
}
