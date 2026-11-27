using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ChatBottomScrollAnchorTests
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

    [Fact]
    public async Task BottomScrollPinner_KeepsPinnedAcrossAsyncMediaResizeAndRefresh_AndDisarmsOnUserScrollUp()
    {
        var script = $$"""
            import assert from 'node:assert/strict';
            const { createBottomScrollPinner } = await import('{{JsUrl("scroll-anchor.js")}}');

            function makeEmitter(extra = {}) {
                const listeners = new Map();
                return {
                    ...extra,
                    addEventListener(type, fn, capture = false) {
                        const key = `${type}:${capture === true ? 'cap' : 'bub'}`;
                        if (!listeners.has(key)) listeners.set(key, new Set());
                        listeners.get(key).add(fn);
                    },
                    removeEventListener(type, fn, capture = false) {
                        const key = `${type}:${capture === true ? 'cap' : 'bub'}`;
                        listeners.get(key)?.delete(fn);
                    },
                    dispatch(type, evt = {}, capture = false) {
                        const key = `${type}:${capture === true ? 'cap' : 'bub'}`;
                        for (const fn of [...(listeners.get(key) || [])]) fn(evt);
                    },
                    listenerCount() {
                        let count = 0;
                        for (const s of listeners.values()) count += s.size;
                        return count;
                    }
                };
            }

            let now = 1000;
            let resizeCallback = null;
            let resizeDisconnected = 0;
            let deliverInitialObservationOnObserve = false;
            globalThis.ResizeObserver = class {
                constructor(cb) { resizeCallback = cb; }
                observe(target) {
                    if (deliverInitialObservationOnObserve && resizeCallback) {
                        resizeCallback([{ target, contentRect: { height: Number(streamEl?.scrollHeight || 0) } }]);
                    }
                }
                disconnect() { resizeDisconnected++; }
            };

            const streamEl = makeEmitter({ scrollTop: 0, scrollHeight: 1200, clientHeight: 600 });
            const containerEl = makeEmitter({});
            const windowRef = makeEmitter({});
            let resolveFonts;
            const fontsReady = new Promise((r) => { resolveFonts = r; });
            const documentRef = makeEmitter({ fonts: { ready: fontsReady } });
            let blocked = false;

            const pinner = createBottomScrollPinner({
                streamEl,
                containerEl,
                windowRef,
                documentRef,
                shouldBlockPin: () => blocked,
                settleMs: 3000,
                maxWindowMs: 8000,
                nowFn: () => now
            });

            // 1. Initial pin jumps straight to current scrollHeight
            pinner.pin();
            assert.equal(pinner.isActive(), true);
            assert.equal(streamEl.scrollTop, 1200);

            // 2. Browser refresh scroll restoration snaps back within grace window (no user input)
            now += 40;
            streamEl.scrollTop = 750;
            streamEl.dispatch('scroll');
            assert.equal(streamEl.scrollTop, 1200, 'Browser refresh scroll restoration must snap back to bottom');
            assert.equal(pinner.isActive(), true);

            // 3. Async photo loads & expands #messages-container height -> re-pins to new bottom
            now += 150;
            streamEl.scrollHeight = 1650;
            containerEl.dispatch('load', { target: { tagName: 'IMG' } }, true);
            assert.equal(streamEl.scrollTop, 1650);

            // 4. Async video loadedmetadata + ResizeObserver growth -> re-pins to new bottom
            now += 100;
            streamEl.scrollHeight = 1970;
            containerEl.dispatch('loadedmetadata', { target: { tagName: 'VIDEO' } }, true);
            assert.equal(streamEl.scrollTop, 1970);

            now += 100;
            streamEl.scrollHeight = 2240;
            resizeCallback();
            assert.equal(streamEl.scrollTop, 2240);

            // 5. Window load & fonts.ready re-pin while active
            streamEl.scrollHeight = 2300;
            windowRef.dispatch('load');
            assert.equal(streamEl.scrollTop, 2300);

            streamEl.scrollHeight = 2340;
            resolveFonts();
            await Promise.resolve();
            await Promise.resolve();
            assert.equal(streamEl.scrollTop, 2340);

            // 6. User scrolls UP with mouse wheel -> immediately disarms and stops snapping on later loads
            streamEl.dispatch('wheel', { deltaY: -120 });
            assert.equal(pinner.isActive(), false);
            streamEl.scrollTop = 900;
            streamEl.scrollHeight = 2600;
            containerEl.dispatch('load', { target: { tagName: 'IMG' } }, true);
            assert.equal(streamEl.scrollTop, 900, 'Must never fight the user after upward wheel scroll');

            // 7. Re-pin and verify touchmove upward scroll disarms
            pinner.pin();
            assert.equal(pinner.isActive(), true);
            streamEl.dispatch('touchstart', { touches: [{ clientY: 300 }] });
            streamEl.dispatch('touchmove', { touches: [{ clientY: 340 }] });
            assert.equal(pinner.isActive(), false, 'Finger swipe down (scroll up) must disarm pinner');

            // 8. Re-pin and verify PageUp key disarms (outside textarea/input)
            pinner.pin();
            documentRef.dispatch('keydown', { key: 'PageUp', target: { tagName: 'TEXTAREA' } });
            assert.equal(pinner.isActive(), true, 'ArrowUp/PageUp inside composer textarea must not disarm');
            documentRef.dispatch('keydown', { key: 'PageUp', target: { tagName: 'DIV' } });
            assert.equal(pinner.isActive(), false, 'PageUp on stream must disarm pinner');

            // 9. Re-pin and verify shouldBlockPin (e.g., isLoadingOlder) disarms immediately
            pinner.pin();
            blocked = true;
            streamEl.scrollHeight = 3000;
            containerEl.dispatch('load', { target: { tagName: 'IMG' } }, true);
            assert.equal(pinner.isActive(), false);

            // 9b. Smooth scroll advancing downward must NOT snap or disarm prematurely (even on initial ResizeObserver observation),
            //     actual layout growth still pins, and upward scroll during smooth still disarms
            blocked = false;
            let smoothScrollCalls = 0;
            streamEl.scrollTo = ({ top, behavior }) => {
                if (behavior === 'smooth') smoothScrollCalls++;
            };
            streamEl.scrollTop = 1000;
            streamEl.scrollHeight = 2000;
            streamEl.clientHeight = 600;
            deliverInitialObservationOnObserve = true;
            pinner.pin({ smooth: true, settleMs: 2000, maxWindowMs: 4000 });
            deliverInitialObservationOnObserve = false;
            assert.equal(smoothScrollCalls, 1);
            assert.equal(pinner.isActive(), true);
            assert.equal(streamEl.scrollTop, 1000, 'Initial unchanged ResizeObserver observation must not interrupt smooth scroll');
            // Mid-animation frames advancing downward (1000 -> 1060 -> 1200 -> 1400)
            now += 16;
            streamEl.scrollTop = 1060;
            streamEl.dispatch('scroll');
            assert.equal(streamEl.scrollTop, 1060, 'Smooth scroll must not be snapped on early frame');
            assert.equal(pinner.isActive(), true);
            now += 200;
            streamEl.scrollTop = 1200;
            streamEl.dispatch('scroll');
            assert.equal(pinner.isActive(), true, 'Smooth scroll advancing past grace window must stay active');
            // User interrupts smooth scroll upward -> disarms immediately
            streamEl.scrollTop = 1100;
            streamEl.dispatch('scroll');
            assert.equal(pinner.isActive(), false, 'Upward scroll during smooth animation must disarm');

            // Genuine layout growth after unchanged initial ResizeObserver observation still re-pins to new bottom
            deliverInitialObservationOnObserve = true;
            streamEl.scrollTop = 1000;
            streamEl.scrollHeight = 2000;
            pinner.pin({ smooth: true, settleMs: 2000, maxWindowMs: 4000 });
            deliverInitialObservationOnObserve = false;
            assert.equal(streamEl.scrollTop, 1000, 'Second pin must also ignore unchanged initial ResizeObserver');
            streamEl.scrollHeight = 2150;
            resizeCallback([{ target: containerEl, contentRect: { height: 2150 } }]);
            assert.equal(streamEl.scrollTop, 2150, 'Actual layout growth after initial observation must re-pin');
            pinner.disarm();

            // 9c. pointercancel clears pointerDownOnStream cleanly
            streamEl.scrollHeight = 2000;
            streamEl.scrollTop = 1400;
            pinner.pin();
            streamEl.dispatch('pointerdown');
            windowRef.dispatch('pointercancel');
            now += 40;
            streamEl.scrollTop = 900;
            streamEl.dispatch('scroll');
            assert.equal(streamEl.scrollTop, 2000, 'After pointercancel, refresh restoration snap still works');

            // 10. destroy() removes all listeners cleanly
            pinner.destroy();
            assert.equal(streamEl.listenerCount(), 0);
            assert.equal(containerEl.listenerCount(), 0);
            assert.equal(windowRef.listenerCount(), 0);
            assert.equal(documentRef.listenerCount(), 0);
            assert.ok(resizeDisconnected > 0);
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task MessageRenderer_UsesEagerLoadingForNewestSixMessagesAndLiveInserts_LazyForOlder()
    {
        var script = $$"""
            import assert from 'node:assert/strict';

            function createClassList(el) {
                const getSet = () => new Set((el.className || '').split(/\s+/).filter(Boolean));
                const writeSet = (set) => { el.className = [...set].join(' '); };
                return {
                    add(...t) { const s = getSet(); t.forEach(x => s.add(x)); writeSet(s); },
                    remove(...t) { const s = getSet(); t.forEach(x => s.delete(x)); writeSet(s); },
                    toggle(tok, f) { const s = getSet(); const on = f !== undefined ? !!f : !s.has(tok); if (on) s.add(tok); else s.delete(tok); writeSet(s); return on; },
                    contains(tok) { return getSet().has(tok); }
                };
            }
            function walk(root, pred, acc = []) {
                for (const c of root.children || []) { if (pred(c)) acc.push(c); walk(c, pred, acc); }
                return acc;
            }
            function createEl(tag, id = '') {
                const attrs = new Map();
                const el = {
                    nodeType: 1, tagName: tag.toUpperCase(), id, className: '', children: [], parentNode: null,
                    src: '', alt: '', loading: '', decoding: '', draggable: true, scrollTop: 0, scrollHeight: 500,
                    style: {}, _text: '',
                    get textContent() { return this.children.length ? this.children.map(c => c.textContent).join('') : this._text; },
                    set textContent(v) { this.children = []; this._text = v == null ? '' : String(v); },
                    get firstChild() { return this.children[0] || null; },
                    appendChild(c) {
                        if (!c) return c;
                        if (c.nodeType === 11) { for (const k of [...c.children]) this.appendChild(k); return c; }
                        c.parentNode = this; this.children.push(c); return c;
                    },
                    insertBefore(c, ref) {
                        if (!ref) return this.appendChild(c);
                        const idx = this.children.indexOf(ref);
                        if (idx < 0) return this.appendChild(c);
                        c.parentNode = this; this.children.splice(idx, 0, c); return c;
                    },
                    removeChild(c) { const i = this.children.indexOf(c); if (i >= 0) this.children.splice(i, 1); return c; },
                    setAttribute(k, v) { attrs.set(k, String(v)); if (k === 'id') this.id = String(v); },
                    getAttribute(k) { return attrs.has(k) ? attrs.get(k) : null; },
                    hasAttribute(k) { return attrs.has(k); },
                    removeAttribute(k) { attrs.delete(k); },
                    querySelector(sel) {
                        if (sel.includes('data-message-id')) {
                            const m = /data-message-id="([^"]+)"/.exec(sel);
                            return m ? walk(this, n => n.getAttribute('data-message-id') === m[1])[0] || null : null;
                        }
                        if (sel === 'img[data-media-img="true"]') return walk(this, n => n.tagName === 'IMG' && n.getAttribute('data-media-img') === 'true')[0] || null;
                        return null;
                    },
                    querySelectorAll(sel) {
                        if (sel === '.message-bubble-row') return walk(this, n => n.classList.contains('message-bubble-row'));
                        if (sel === 'img[data-media-img="true"]') return walk(this, n => n.tagName === 'IMG' && n.getAttribute('data-media-img') === 'true');
                        return [];
                    }
                };
                el.classList = createClassList(el);
                if (id) el.setAttribute('id', id);
                return el;
            }
            const body = createEl('body');
            globalThis.document = {
                body,
                createElement: (t) => createEl(t),
                createTextNode: (txt) => ({ nodeType: 3, textContent: String(txt), parentNode: null }),
                createDocumentFragment: () => ({ nodeType: 11, children: [], appendChild(c) { this.children.push(c); return c; } }),
                getElementById: (id) => walk(body, n => n.id === id)[0] || null,
                querySelector: () => null,
                querySelectorAll: () => [],
                addEventListener: () => {},
                removeEventListener: () => {}
            };
            globalThis.window = { addEventListener() {}, removeEventListener() {} };

            const { renderMessages, insertMessageOrdered, setBottomScrollHandler } = await import('{{JsUrl("message-renderer.js")}}');

            const stream = document.createElement('div'); stream.id = 'message-stream';
            const container = document.createElement('div'); container.id = 'messages-container';
            stream.appendChild(container);
            body.appendChild(stream);

            let bottomScrollCalls = [];
            setBottomScrollHandler((smooth) => bottomScrollCalls.push(smooth));

            const convId = '86131524-34e7-f111-9585-047c1647fc91';
            const userId = '11111111-1111-1111-1111-111111111111';
            const messages = [];
            for (let i = 1; i <= 10; i++) {
                messages.push({
                    id: 100 + i,
                    conversationId: convId,
                    senderId: userId,
                    senderName: 'Me',
                    senderHandle: 'me',
                    createdAtUtc: new Date(1700000000000 + i * 1000).toISOString(),
                    isOutgoing: true,
                    attachments: [{ id: `00000000-0000-0000-0000-0000000000${String(i).padStart(2, '0')}`, kind: 'image', fileName: `p${i}.jpg`, width: 800, height: 600 }]
                });
            }

            renderMessages(container, messages, userId, null, { scrollToEnd: true, hasMore: false });
            assert.deepEqual(bottomScrollCalls, [false], 'renderMessages(scrollToEnd: true) must route through bottom scroll handler');

            const imgs = container.querySelectorAll('img[data-media-img="true"]');
            assert.equal(imgs.length, 10);
            // First 4 (older) are lazy; last 6 (newest) are eager
            for (let i = 0; i < 4; i++) {
                assert.equal(imgs[i].loading, 'lazy', `Older message index ${i} should be lazy`);
            }
            for (let i = 4; i < 10; i++) {
                assert.equal(imgs[i].loading, 'eager', `Newest message index ${i} should be eager`);
            }

            // Live-inserted message must be eager
            insertMessageOrdered(container, {
                id: 200,
                conversationId: convId,
                senderId: userId,
                senderName: 'Me',
                senderHandle: 'me',
                createdAtUtc: new Date(1700000099000).toISOString(),
                isOutgoing: true,
                attachments: [{ id: '00000000-0000-0000-0000-000000000099', kind: 'image', fileName: 'live.jpg', width: 800, height: 600 }]
            }, userId);
            const allImgs = container.querySelectorAll('img[data-media-img="true"]');
            assert.equal(allImgs[allImgs.length - 1].loading, 'eager');

            setBottomScrollHandler(null);
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public void ConversationView_And_ChatShell_WireEagerBottomMediaAndBottomScrollPinner()
    {
        var slnRoot = FindSolutionRoot();

        var convView = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml"));
        Assert.Contains("var imgLoading = (totalInitialMessages - initialMessageIndex) <= 6 ? \"eager\" : \"lazy\";", convView);
        Assert.Contains("loading=\"@imgLoading\"", convView);

        var chatShell = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-shell.js"));
        Assert.Contains("createBottomScrollPinner", chatShell);
        Assert.Contains("setBottomScrollHandler", chatShell);
        Assert.Contains("bottomScrollPinner.destroy()", chatShell);
        Assert.Contains("bottomScrollPinner.disarm()", chatShell);
    }
}
