using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase9MediaFrontendTests
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

        function matchesSingle(node, part) {
            if (!node || node.nodeType !== 1) return false;
            if (part.startsWith('#')) return node.id === part.slice(1);
            const attrMatch = /^([a-z0-9_.-]+)?((?:\[[^\]]+\])+)$/i.exec(part);
            if (attrMatch) {
                const base = attrMatch[1] || '';
                if (base) {
                    if (base.startsWith('.')) {
                        if (!node.classList.contains(base.slice(1))) return false;
                    } else if (node.tagName.toLowerCase() !== base.toLowerCase()) {
                        return false;
                    }
                }
                const attrs = [...attrMatch[2].matchAll(/\[([^=\]]+)(?:="([^"]*)")?\]/g)];
                return attrs.every((m) => {
                    const val = node.getAttribute(m[1]);
                    return m[2] === undefined ? val !== null : val === m[2];
                });
            }
            if (part.startsWith('.')) return node.classList.contains(part.slice(1));
            return node.tagName.toLowerCase() === part.toLowerCase();
        }

        function matchesSelector(el, selector) {
            const parts = selector.trim().split(/\s+/);
            return matchesSingle(el, parts[parts.length - 1]);
        }

        function walk(root, predicate, acc = []) {
            for (const child of root.children || []) {
                if (predicate(child)) acc.push(child);
                walk(child, predicate, acc);
            }
            return acc;
        }

        function createEl(tag, id = '') {
            const attrs = new Map();
            const listeners = new Map();
            const el = {
                nodeType: 1,
                tagName: tag.toUpperCase(),
                id,
                className: '',
                children: [],
                parentNode: null,
                disabled: false,
                value: '',
                src: '',
                alt: '',
                href: '',
                controls: false,
                preload: '',
                playsInline: false,
                max: 100,
                files: [],
                style: {},
                _textContent: '',
                get textContent() {
                    if (this.children.length === 0) return this._textContent;
                    return this._textContent + this.children.map(c => c.textContent).join('');
                },
                set textContent(v) {
                    this.children = [];
                    this._textContent = v === null || v === undefined ? '' : String(v);
                },
                get firstChild() { return this.children[0] || null; },
                appendChild(child) {
                    if (!child) return child;
                    if (child.nodeType === 11) {
                        for (const sub of [...child.children]) this.appendChild(sub);
                        return child;
                    }
                    child.parentNode = this;
                    this.children.push(child);
                    return child;
                },
                insertBefore(newNode, refNode) {
                    if (!refNode) return this.appendChild(newNode);
                    const idx = this.children.indexOf(refNode);
                    if (idx < 0) return this.appendChild(newNode);
                    newNode.parentNode = this;
                    this.children.splice(idx, 0, newNode);
                    return newNode;
                },
                removeChild(child) {
                    const idx = this.children.indexOf(child);
                    if (idx >= 0) {
                        this.children.splice(idx, 1);
                        child.parentNode = null;
                    }
                    return child;
                },
                remove() {
                    if (this.parentNode) this.parentNode.removeChild(this);
                },
                setAttribute(k, v) {
                    attrs.set(k, String(v));
                    if (k === 'id') this.id = String(v);
                    if (k === 'src') this.src = String(v);
                },
                getAttribute(k) { return attrs.has(k) ? attrs.get(k) : null; },
                removeAttribute(k) {
                    attrs.delete(k);
                    if (k === 'src') this.src = '';
                },
                hasAttribute(k) { return attrs.has(k); },
                addEventListener(type, fn) {
                    if (!listeners.has(type)) listeners.set(type, new Set());
                    listeners.get(type).add(fn);
                },
                removeEventListener(type, fn) {
                    listeners.get(type)?.delete(fn);
                },
                dispatchEvent(evt) {
                    evt.target = evt.target || this;
                    for (const fn of listeners.get(evt.type) || []) fn(evt);
                },
                click() { this._clicked = true; },
                focus() { if (globalThis.document) globalThis.document.activeElement = this; },
                contains(target) {
                    let cur = target;
                    while (cur) {
                        if (cur === this) return true;
                        cur = cur.parentNode;
                    }
                    return false;
                },
                closest(sel) {
                    let cur = this;
                    while (cur) {
                        if (matchesSelector(cur, sel)) return cur;
                        cur = cur.parentNode;
                    }
                    return null;
                },
                querySelector(sel) { return walk(this, n => matchesSelector(n, sel))[0] || null; },
                querySelectorAll(sel) { return walk(this, n => matchesSelector(n, sel)); }
            };
            el.classList = createClassList(el);
            if (id) el.setAttribute('id', id);
            return el;
        }

        const docListeners = new Map();
        const rootBody = createEl('body');
        globalThis.document = {
            body: rootBody,
            activeElement: null,
            visibilityState: 'visible',
            hasFocus: () => true,
            title: 'Test',
            createElement: (tag) => createEl(tag),
            createTextNode: (txt) => ({ nodeType: 3, textContent: String(txt), parentNode: null }),
            createDocumentFragment: () => ({ nodeType: 11, children: [], appendChild(c) { this.children.push(c); return c; } }),
            getElementById: (id) => walk(rootBody, n => n.id === id)[0] || null,
            querySelector: (sel) => rootBody.querySelector(sel),
            querySelectorAll: (sel) => rootBody.querySelectorAll(sel),
            addEventListener(type, fn) {
                if (!docListeners.has(type)) docListeners.set(type, new Set());
                docListeners.get(type).add(fn);
            },
            removeEventListener(type, fn) {
                docListeners.get(type)?.delete(fn);
            },
            dispatchEvent(evt) {
                for (const fn of docListeners.get(evt.type) || []) fn(evt);
            }
        };
        globalThis.window = {
            addEventListener: () => {},
            removeEventListener: () => {}
        };
        """;

    [Fact]
    public async Task MessageRenderer_MediaFormattingEditingTombstoneAndWipe_BehaveAccuratelyInNode()
    {
        var rendererUrl = JsUrl("message-renderer.js");
        var lightboxUrl = JsUrl("media-lightbox.js");

        var script = MockDomPrelude + $$"""
            import assert from 'node:assert/strict';
            import * as renderer from '{{rendererUrl}}';
            import { isSafeMediaUrl } from '{{lightboxUrl}}';

            // 1. Safe URL validation
            assert.equal(isSafeMediaUrl('https://cdn.example.com/media/123'), true);
            assert.equal(isSafeMediaUrl('/dev-media/download?token=abc'), true);
            assert.equal(isSafeMediaUrl('//evil.example.com/x.jpg'), false);
            assert.equal(isSafeMediaUrl('javascript:alert(1)'), false);
            assert.equal(isSafeMediaUrl('data:text/html,<script>'), false);
            assert.equal(isSafeMediaUrl('http://insecure.example.com/a.jpg'), false);

            // 2. formatMessagePreview parity
            assert.equal(renderer.formatMessagePreview({ content: '\u200B  ', attachments: [{ kind: 'image', fileName: 'a.png' }] }), '📷 Photo');
            assert.equal(renderer.formatMessagePreview({ content: 'Trip', attachments: [{ kind: 'image' }, { kind: 'image' }] }), '📷 Trip');
            assert.equal(renderer.formatMessagePreview({ content: '', attachments: [{ kind: 'image' }, { kind: 'image' }] }), '📷 2 photos');
            assert.equal(renderer.formatMessagePreview({ content: '', attachments: [{ kind: 'image' }, { kind: 'document', fileName: 'x.pdf' }] }), '📎 2 attachments');
            assert.equal(renderer.formatMessagePreview({ content: '', attachments: [{ kind: 'document', fileName: 'spec.pdf' }] }), '📎 spec.pdf');
            assert.equal(renderer.formatMessagePreview({ isDeletedForEveryone: true, attachments: [{ kind: 'image' }] }), 'This message was deleted');

            // 3. Render bubble with media -> edit caption cleared -> delete for everyone scrubs .message-media-container
            const messagesContainer = document.createElement('div');
            messagesContainer.id = 'messages-container';
            const pendingContainer = document.createElement('div');
            pendingContainer.id = 'pending-uploads-container';
            const tray = document.createElement('div');
            tray.id = 'composer-attachments-tray';
            const attachMenu = document.createElement('div');
            attachMenu.id = 'composer-attach-menu';
            const dropzone = document.createElement('div');
            dropzone.id = 'chat-dropzone-overlay';
            const attachBtn = document.createElement('button');
            attachBtn.id = 'composer-attach-btn';
            const lightboxModal = document.createElement('div');
            lightboxModal.id = 'media-lightbox-modal';
            const lightboxImg = document.createElement('img');
            lightboxImg.id = 'media-lightbox-img';
            lightboxImg.setAttribute('src', '/api/conversations/c1/messages/1/media/m1');
            lightboxModal.appendChild(lightboxImg);

            for (const node of [messagesContainer, pendingContainer, tray, attachMenu, dropzone, attachBtn, lightboxModal]) {
                document.body.appendChild(node);
            }

            const convId = '11111111-1111-1111-1111-111111111111';
            const mediaId = '22222222-2222-2222-2222-222222222222';
            const msg = {
                id: 501,
                conversationId: convId,
                senderId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                senderName: 'Alice',
                senderHandle: 'alice',
                content: 'Original caption',
                createdAtUtc: '2026-11-24T12:00:00Z',
                isDeletedForEveryone: false,
                isOutgoing: true,
                attachments: [{ id: mediaId, kind: 'image', fileName: 'photo.jpg', contentType: 'image/jpeg', sizeBytes: 1234, width: 100, height: 80, displayOrder: 0 }]
            };

            const row = renderer.createMessageBubbleElement(msg, msg.senderId);
            messagesContainer.appendChild(row);
            assert.equal(row.getAttribute('data-has-attachments'), 'true');
            assert.equal(row.getAttribute('data-message-preview'), '📷 Original caption');
            assert.ok(row.querySelector('.message-media-container'));
            assert.equal(row.querySelector('img').getAttribute('width'), '100');
            assert.equal(row.querySelector('img').getAttribute('height'), '80');

            // Edit to clear caption -> toggles .hidden on .message-text-content & keeps preview
            renderer.applyMessageEditedToRow(501, '', '2026-11-24T12:01:00Z', '📷 Photo');
            const textEl = row.querySelector('.message-text-content');
            assert.equal(textEl.classList.contains('hidden'), true);
            assert.equal(row.getAttribute('data-message-preview'), '📷 Photo');

            // Delete for everyone -> removes .message-media-container
            renderer.applyMessageDeletedForEveryone(row, 501);
            assert.equal(row.querySelector('.message-media-container'), null);
            assert.equal(row.getAttribute('data-has-attachments'), 'false');
            assert.equal(row.getAttribute('data-message-preview'), 'This message was deleted');

            // wipeSensitiveDom -> clears media cache, tray, lightbox img, dropzone, pending container & disables attach btn
            renderer.wipeSensitiveDom();
            assert.equal(attachBtn.disabled, true);
            assert.equal(lightboxModal.classList.contains('hidden'), true);
            assert.equal(lightboxImg.hasAttribute('src'), false);
            assert.equal(lightboxImg.src, '');
            assert.equal(tray.classList.contains('hidden'), true);
            assert.equal(pendingContainer.children.length, 0);

            // 4. [W6] resolveCachedMediaUrl respects short expiresAtUtc (5 min -> expires at 4 min with 60s safety margin)
            let urlNow = 1_700_000_000_000;
            let urlFetches = 0;
            const getShortTtlUrl = async () => {
                urlFetches++;
                return {
                    url: `https://cdn.example.com/media/video-${urlFetches}.mp4`,
                    expiresAtUtc: new Date(urlNow + 5 * 60 * 1000).toISOString()
                };
            };
            const firstResolved = await renderer.resolveCachedMediaUrl(convId, 777, mediaId, {
                getMediaUrlFn: getShortTtlUrl,
                nowFn: () => urlNow
            });
            assert.equal(urlFetches, 1);
            // Still valid at +3 min
            urlNow += 3 * 60 * 1000;
            const cachedResolved = await renderer.resolveCachedMediaUrl(convId, 777, mediaId, {
                getMediaUrlFn: getShortTtlUrl,
                nowFn: () => urlNow
            });
            assert.equal(cachedResolved, firstResolved);
            assert.equal(urlFetches, 1);
            // Expired at +4m01s (inside 60s margin before 5m expiry) -> re-fetches!
            urlNow += 61 * 1000;
            const refreshedResolved = await renderer.resolveCachedMediaUrl(convId, 777, mediaId, {
                getMediaUrlFn: getShortTtlUrl,
                nowFn: () => urlNow
            });
            assert.notEqual(refreshedResolved, firstResolved);
            assert.equal(urlFetches, 2);

            // 5. [W6] <video> retry resets on loadeddata and recovers again after cooldown
            const videoWrapper = document.createElement('div');
            videoWrapper.setAttribute('data-video-container', 'true');
            const videoEl = document.createElement('video');
            videoEl.setAttribute('data-media-video', 'true');
            videoEl.setAttribute('data-conversation-id', convId);
            videoEl.setAttribute('data-message-id', '778');
            videoEl.setAttribute('data-media-id', mediaId);
            const videoFallback = document.createElement('div');
            videoFallback.classList.add('media-video-fallback', 'hidden');
            videoWrapper.appendChild(videoEl);
            videoWrapper.appendChild(videoFallback);
            document.body.appendChild(videoWrapper);

            let vidCalls = 0;
            renderer.hydrateMediaElements(videoWrapper, {
                getMediaUrlFn: async () => ({
                    url: `https://cdn.example.com/v/${++vidCalls}.mp4`,
                    expiresAtUtc: new Date(urlNow + 10 * 60 * 1000).toISOString()
                }),
                nowFn: () => urlNow
            });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(vidCalls, 1);

            // First error triggers retry
            videoEl.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(vidCalls, 2);
            // Playback recovers -> clears data-video-retried
            videoEl.dispatchEvent({ type: 'loadeddata' });
            assert.equal(videoEl.getAttribute('data-video-retried'), null);
            // Advance past 15s cooldown -> second expiry error retries cleanly
            urlNow += 20_000;
            videoEl.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(vidCalls, 3);
            assert.equal(videoFallback.classList.contains('hidden'), true);

            // 6. [W3] DeleteForEveryone and DeleteForMe evict cached URLs and pause/unload detached <video> elements
            let delUrlFetches = 0;
            const fetchDelUrl = async () => ({
                url: `https://cdn.example.com/del/${++delUrlFetches}.mp4`,
                expiresAtUtc: new Date(urlNow + 10 * 60 * 1000).toISOString()
            });
            await renderer.resolveCachedMediaUrl(convId, 888, mediaId, { getMediaUrlFn: fetchDelUrl, nowFn: () => urlNow });
            await renderer.resolveCachedMediaUrl(convId, 888, mediaId, { getMediaUrlFn: fetchDelUrl, nowFn: () => urlNow });
            assert.equal(delUrlFetches, 1);

            const delRow = document.createElement('div');
            delRow.id = 'msg-888';
            const delContainer = document.createElement('div');
            delContainer.classList.add('message-media-container');
            const delVid = document.createElement('video');
            delVid.src = 'https://cdn.example.com/del/1.mp4';
            let vidPaused = false;
            let vidLoaded = false;
            delVid.pause = () => { vidPaused = true; };
            delVid.load = () => { vidLoaded = true; };
            delContainer.appendChild(delVid);
            delRow.appendChild(delContainer);
            messagesContainer.appendChild(delRow);

            renderer.applyMessageDeletedForEveryone(delRow, 888);
            assert.equal(vidPaused, true);
            assert.equal(vidLoaded, true);
            assert.equal(delVid.src, '');
            await renderer.resolveCachedMediaUrl(convId, 888, mediaId, { getMediaUrlFn: fetchDelUrl, nowFn: () => urlNow });
            assert.equal(delUrlFetches, 2);

            await renderer.resolveCachedMediaUrl(convId, 889, mediaId, { getMediaUrlFn: fetchDelUrl, nowFn: () => urlNow });
            assert.equal(delUrlFetches, 3);
            const delMeRow = document.createElement('div');
            delMeRow.id = 'msg-889';
            const delMeVid = document.createElement('video');
            delMeVid.src = 'https://cdn.example.com/del/3.mp4';
            let meVidPaused = false;
            delMeVid.pause = () => { meVidPaused = true; };
            delMeVid.load = () => {};
            delMeRow.appendChild(delMeVid);
            messagesContainer.appendChild(delMeRow);

            renderer.applyMessageDeletedForMe(889, convId, null, null, true);
            assert.equal(meVidPaused, true);
            await renderer.resolveCachedMediaUrl(convId, 889, mediaId, { getMediaUrlFn: fetchDelUrl, nowFn: () => urlNow });
            assert.equal(delUrlFetches, 4);

            console.log('OK_RENDERER');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node script failed ({exitCode}):\nSTDOUT:\n{output}\nSTDERR:\n{error}");
        Assert.Contains("OK_RENDERER", output);
    }

    [Fact]
    public async Task MediaUploader_And_MediaLightbox_EnforceConcurrencyRetryExpiryDedupAndNavigationInNode()
    {
        var uploaderUrl = JsUrl("media-uploader.js");
        var lightboxUrl = JsUrl("media-lightbox.js");

        var script = MockDomPrelude + $$"""
            import assert from 'node:assert/strict';
            import { createMediaUploader, validateSelectedFile } from '{{uploaderUrl}}';
            import { createMediaLightbox } from '{{lightboxUrl}}';

            // Blob URL tracking
            const revokedUrls = [];
            let blobCounter = 0;
            globalThis.URL = class extends globalThis.URL {
                static createObjectURL() { return `blob:mock/${++blobCounter}`; }
                static revokeObjectURL(u) { revokedUrls.push(u); }
            };

            // Setup required DOM nodes
            const tray = document.createElement('div'); tray.id = 'composer-attachments-tray';
            const pending = document.createElement('div'); pending.id = 'pending-uploads-container';
            const attachBtn = document.createElement('button'); attachBtn.id = 'composer-attach-btn';
            const attachMenu = document.createElement('div'); attachMenu.id = 'composer-attach-menu';
            const dropzone = document.createElement('div'); dropzone.id = 'chat-dropzone-overlay';
            const inputEl = document.createElement('textarea'); inputEl.id = 'message-content-input';
            const activePane = document.createElement('div'); activePane.id = 'chat-active-pane';

            const lbModal = document.createElement('div'); lbModal.id = 'media-lightbox-modal'; lbModal.classList.add('hidden');
            const lbTitle = document.createElement('h3'); lbTitle.id = 'media-lightbox-title';
            const lbCounter = document.createElement('span'); lbCounter.id = 'media-lightbox-counter';
            const lbImg = document.createElement('img'); lbImg.id = 'media-lightbox-img';
            const lbErr = document.createElement('div'); lbErr.id = 'media-lightbox-error'; lbErr.classList.add('hidden');
            const lbPrev = document.createElement('button'); lbPrev.id = 'media-lightbox-prev-btn';
            const lbNext = document.createElement('button'); lbNext.id = 'media-lightbox-next-btn';
            const lbClose = document.createElement('button'); lbClose.id = 'media-lightbox-close-btn';
            const lbDl = document.createElement('button'); lbDl.id = 'media-lightbox-download-btn';
            for (const c of [lbTitle, lbCounter, lbImg, lbErr, lbPrev, lbNext, lbClose, lbDl]) lbModal.appendChild(c);

            for (const n of [tray, pending, attachBtn, attachMenu, dropzone, inputEl, activePane, lbModal]) {
                document.body.appendChild(n);
            }

            // 1. Clipboard extension fallback & size/MIME rejections (including Office MIME fallback parity)
            assert.equal(validateSelectedFile({ name: 'image', type: 'image/png', size: 100 }).fileName, 'image.png');
            assert.equal(
                validateSelectedFile({
                    name: 'report',
                    type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
                    size: 100
                }).fileName,
                'report.docx');
            assert.equal(validateSelectedFile({ name: 'bad.svg', type: 'image/svg+xml', size: 100 }).ok, false);
            assert.equal(validateSelectedFile({ name: 'empty.jpg', type: 'image/jpeg', size: 0 }).ok, false);
            assert.equal(validateSelectedFile({ name: 'big.jpg', type: 'image/jpeg', size: 20 * 1024 * 1024 }).ok, false);

            // 2. Partial-failure retry + 90-min expiry + max 2 concurrent XHR + withCredentials=false
            let nowMs = 1_700_000_000_000;
            const convId = '11111111-1111-1111-1111-111111111111';
            const state = { activeConversationId: convId, pendingConversationId: null };

            let inFlightXhr = 0;
            let maxObservedConcurrentXhr = 0;
            let failUploadPutForFile2Once = true;
            let initSeq = 0;
            const sentPayloads = [];
            const cancelledIds = [];

            const fakeApi = {
                async initiateMediaUpload(_cId, dto) {
                    const id = `00000000-0000-0000-0000-00000000000${++initSeq}`;
                    return { mediaId: id, uploadUrl: `https://r2.example.com/upload/${id}`, requiredContentType: dto.contentType };
                },
                async completeMediaUpload(_cId, mediaId) {
                    return { id: mediaId, kind: 'image' };
                },
                async cancelMediaUpload(_cId, mediaId) {
                    cancelledIds.push(mediaId);
                },
                async sendMessage(_cId, payload) {
                    sentPayloads.push(payload);
                    if (payload.mediaIds.includes('CONFLICT_TRIGGER')) {
                        const e = new Error('Conflict'); e.status = 409; throw e;
                    }
                    return { id: 900, conversationId: convId, attachments: payload.mediaIds.map(id => ({ id, kind: 'image' })) };
                }
            };

            const createFakeXhr = () => {
                const headers = {};
                const xhr = {
                    withCredentials: true,
                    status: 200,
                    upload: {},
                    open() {},
                    setRequestHeader(k, v) { headers[k] = v; },
                    abort() { this.onabort?.(); },
                    send(file) {
                        assert.equal(this.withCredentials, false);
                        assert.ok(headers['Content-Type']);
                        inFlightXhr++;
                        maxObservedConcurrentXhr = Math.max(maxObservedConcurrentXhr, inFlightXhr);
                        setTimeout(() => {
                            inFlightXhr--;
                            this.upload.onprogress?.({ lengthComputable: true, loaded: 50, total: 100 });
                            if (file.name === 'two.png' && failUploadPutForFile2Once) {
                                failUploadPutForFile2Once = false;
                                this.status = 500;
                                this.onload?.();
                            } else {
                                this.status = 200;
                                this.onload?.();
                            }
                        }, 10);
                    }
                };
                return xhr;
            };

            const uploader = createMediaUploader({
                state,
                api: fakeApi,
                createXhr: createFakeXhr,
                nowFn: () => nowMs
            });
            uploader.init();
            uploader.setLocked(false);

            // Paste skips image when text/plain is non-empty
            let prevented = false;
            document.dispatchEvent({
                type: 'paste',
                target: inputEl,
                preventDefault() { prevented = true; },
                clipboardData: {
                    getData: (t) => t === 'text/plain' ? 'Hello' : '',
                    files: [{ name: 'pasted.png', type: 'image/png', size: 100 }]
                }
            });
            assert.equal(prevented, false);
            assert.equal(uploader.hasPendingFiles(), false);

            // Add 3 files -> start job -> file 2 fails, files 1 & 3 succeed -> retry reuses 1 & 3 under 90 min!
            uploader.addFiles([
                { name: 'one.png', type: 'image/png', size: 100 },
                { name: 'two.png', type: 'image/png', size: 100 },
                { name: 'three.png', type: 'image/png', size: 100 }
            ]);
            const job = uploader.startUploadJob({ conversationId: convId, caption: 'Batch' });
            await job.promise;

            assert.ok(maxObservedConcurrentXhr <= 2);
            assert.equal(job.status, 'failed');
            assert.equal(job.retryable, true);
            assert.equal(job.items[0].status, 'verified');
            assert.equal(job.items[1].status, 'failed');
            assert.equal(job.items[2].status, 'verified');

            const firstKey = job.idempotencyKey;
            // Still under 90 mins: retry reuses verified items[0] and items[2], keeps same IdempotencyKey!
            nowMs += 30 * 60 * 1000;
            uploader.retryJob(job.id);
            await job.promise;

            assert.equal(sentPayloads.length, 1);
            assert.equal(sentPayloads[0].idempotencyKey, firstKey);
            assert.equal(sentPayloads[0].mediaIds.length, 3);
            assert.ok(revokedUrls.length >= 3);

            // Dedup on ReceiveMessage removes matching pending job cleanly
            uploader.addFiles([{ name: 'dedup.png', type: 'image/png', size: 100 }]);
            const job2 = uploader.startUploadJob({ conversationId: convId });
            // Assign mediaId to simulate initiated/completed upload racing with ReceiveMessage
            job2.items[0].mediaId = 'dedup-media-id';
            const removed = uploader.reconcileIncomingMessage(convId, { attachments: [{ id: 'dedup-media-id', kind: 'image' }] });
            assert.equal(removed, true);
            assert.equal(uploader.getJobsForConversation(convId).length, 0);

            // 3. Lightbox navigation, image-only triggers, closeIfMessage, focus management & 1-shot retry -> Media unavailable
            let lbUrlCalls = 0;
            let lbFailUrls = false;
            const lightbox = createMediaLightbox({
                getMediaUrlFn: async (_cId, _mId, mediaId) => {
                    lbUrlCalls++;
                    if (lbFailUrls) {
                        throw new Error('gone');
                    }
                    return { url: `https://cdn.example.com/media/${mediaId}.jpg` };
                },
                fetchFn: async () => {
                    throw new TypeError('Failed to fetch');
                }
            });
            const openerTrigger = document.createElement('button');
            openerTrigger.setAttribute('data-lightbox-trigger', 'true');
            document.body.appendChild(openerTrigger);
            openerTrigger.focus();

            lightbox.open([
                { conversationId: convId, messageId: '10', mediaId: 'img1', fileName: 'a.jpg' },
                { conversationId: convId, messageId: '10', mediaId: 'img2', fileName: 'b.jpg' }
            ], 0);
            assert.equal(lightbox.isOpen(), true);
            assert.equal(document.activeElement, lbClose);
            assert.equal(lbCounter.textContent, '1 / 2');

            // Tab wraps cleanly at both ends of visible lightbox controls [lbDl, lbClose, lbPrev, lbNext]
            lbDl.focus();
            document.dispatchEvent({ type: 'keydown', key: 'Tab', shiftKey: true, preventDefault() {} });
            assert.equal(document.activeElement, lbNext);
            document.dispatchEvent({ type: 'keydown', key: 'Tab', shiftKey: false, preventDefault() {} });
            assert.equal(document.activeElement, lbDl);

            // Enter while focus leaked outside modal is intercepted and pulled back to lbClose
            openerTrigger.focus();
            let leakedEnterPrevented = false;
            document.dispatchEvent({ type: 'keydown', key: 'Enter', preventDefault() { leakedEnterPrevented = true; } });
            assert.equal(leakedEnterPrevented, true);
            assert.equal(document.activeElement, lbClose);

            document.dispatchEvent({ type: 'keydown', key: 'ArrowRight', preventDefault() {} });
            assert.equal(lbCounter.textContent, '2 / 2');
            await new Promise(r => setTimeout(r, 15));
            assert.equal(lbImg.src, 'https://cdn.example.com/media/img2.jpg');

            // 1st error performs 1-shot retry via getMediaUrlFn -> fails -> falls back to 'Media unavailable' (never loops)
            lbFailUrls = true;
            const callsBeforeError = lbUrlCalls;
            lbImg.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(lbUrlCalls - callsBeforeError, 1);
            assert.equal(lbErr.classList.contains('hidden'), false);
            assert.equal(lbErr.textContent, 'Media unavailable');

            lightbox.closeIfMessage('10');
            assert.equal(lightbox.isOpen(), false);
            assert.equal(lbImg.hasAttribute('src'), false);
            assert.equal(document.activeElement, openerTrigger);

            // [S3] Cancel while initiateMediaUpload is in flight still issues server-side cancel
            let resolveInflightInit = null;
            const delayedInitApi = {
                ...fakeApi,
                initiateMediaUpload() {
                    return new Promise((resolve) => { resolveInflightInit = resolve; });
                }
            };
            const uploaderCancelRace = createMediaUploader({
                state,
                api: delayedInitApi,
                createXhr: createFakeXhr,
                nowFn: () => nowMs
            });
            uploaderCancelRace.init();
            uploaderCancelRace.setLocked(false);
            uploaderCancelRace.addFiles([{ name: 'inflight.png', type: 'image/png', size: 100 }]);
            const cancelRaceJob = uploaderCancelRace.startUploadJob({ conversationId: convId });
            await new Promise(r => setTimeout(r, 5));
            uploaderCancelRace.cancelJob(cancelRaceJob.id);
            resolveInflightInit({
                mediaId: '00000000-0000-0000-0000-000000000099',
                uploadUrl: 'https://r2.example.com/upload/99',
                requiredContentType: 'image/png'
            });
            await cancelRaceJob.promise;
            await new Promise(r => setTimeout(r, 10));
            assert.ok(cancelledIds.includes('00000000-0000-0000-0000-000000000099'));
            uploaderCancelRace.destroy();

            // [PR3-M3] 503 during completeMediaUpload preserves mediaId so Retry runs completion ONLY & Cancel cleans pending slot;
            //           409 during completeMediaUpload cancels old mediaId and re-uploads from scratch on Retry.
            let m3InitCalls = 0;
            let m3PutCalls = 0;
            let m3CompleteCalls = 0;
            let m3CancelCalls = [];
            let m3NextCompleteStatus = 503;
            const m3Api = {
                async initiateMediaUpload(_cId, dto) {
                    const id = `00000000-0000-0000-0000-00000000020${++m3InitCalls}`;
                    return { mediaId: id, uploadUrl: `https://r2.example.com/upload/${id}`, requiredContentType: dto.contentType };
                },
                async completeMediaUpload(_cId, mediaId) {
                    m3CompleteCalls++;
                    if (m3NextCompleteStatus !== 200) {
                        const err = new Error(`Status ${m3NextCompleteStatus}`);
                        err.status = m3NextCompleteStatus;
                        throw err;
                    }
                    return { id: mediaId, kind: 'image' };
                },
                async cancelMediaUpload(_cId, mediaId) {
                    m3CancelCalls.push(mediaId);
                },
                async sendMessage(_cId, payload) {
                    return { id: 901, conversationId: convId, attachments: payload.mediaIds.map(id => ({ id, kind: 'image' })) };
                }
            };
            const m3CreateXhr = () => ({
                withCredentials: true,
                status: 200,
                upload: {},
                open() {},
                setRequestHeader() {},
                abort() { this.onabort?.(); },
                send() {
                    m3PutCalls++;
                    this.status = 200;
                    this.onload?.();
                }
            });
            const m3Uploader = createMediaUploader({ state, api: m3Api, createXhr: m3CreateXhr, nowFn: () => nowMs });
            m3Uploader.init();
            m3Uploader.setLocked(false);

            // Case A: 503 timeout preserves mediaId -> Retry skips initiate & PUT and succeeds on complete only
            m3Uploader.addFiles([{ name: 'timeout.png', type: 'image/png', size: 120 }]);
            const job503 = m3Uploader.startUploadJob({ conversationId: convId });
            await job503.promise;
            assert.equal(job503.status, 'failed');
            assert.equal(job503.retryable, true);
            assert.equal(job503.items[0].mediaId, '00000000-0000-0000-0000-000000000201');
            assert.equal(m3CancelCalls.length, 0);

            m3NextCompleteStatus = 200;
            m3Uploader.retryJob(job503.id);
            await job503.promise;
            assert.equal(m3InitCalls, 1, '503 retry must not re-initiate upload');
            assert.equal(m3PutCalls, 1, '503 retry must not re-PUT to R2');
            assert.equal(m3CompleteCalls, 2, '503 retry must invoke completeMediaUpload only');

            // Case B: 503 followed by Cancel cancels the pending server upload
            m3NextCompleteStatus = 503;
            m3Uploader.addFiles([{ name: 'cancel503.png', type: 'image/png', size: 120 }]);
            const job503Cancel = m3Uploader.startUploadJob({ conversationId: convId });
            await job503Cancel.promise;
            assert.equal(job503Cancel.items[0].mediaId, '00000000-0000-0000-0000-000000000202');
            await m3Uploader.cancelJob(job503Cancel.id);
            assert.ok(m3CancelCalls.includes('00000000-0000-0000-0000-000000000202'));

            // Case C: 409 staged conflict cancels old mediaId and clears it so Retry performs a fresh upload
            m3NextCompleteStatus = 409;
            m3Uploader.addFiles([{ name: 'conflict409.png', type: 'image/png', size: 120 }]);
            const job409 = m3Uploader.startUploadJob({ conversationId: convId });
            await job409.promise;
            assert.equal(job409.items[0].mediaId, null);
            assert.ok(m3CancelCalls.includes('00000000-0000-0000-0000-000000000203'));
            m3NextCompleteStatus = 200;
            m3Uploader.retryJob(job409.id);
            await job409.promise;
            assert.equal(m3InitCalls, 4);
            assert.equal(m3PutCalls, 4);
            m3Uploader.destroy();

            uploader.destroy();
            lightbox.destroy();

            console.log('OK_UPLOADER_AND_LIGHTBOX');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node script failed ({exitCode}):\nSTDOUT:\n{output}\nSTDERR:\n{error}");
        Assert.Contains("OK_UPLOADER_AND_LIGHTBOX", output);
    }

    [Fact]
    public async Task MediaLightbox_And_MemoryCache_PreventRepeatFetches_AndEnforceBoundsAndEvictionInNode()
    {
        var lightboxUrl = JsUrl("media-lightbox.js");
        var cacheUrl = JsUrl("memory-image-cache.js");
        var rendererUrl = JsUrl("message-renderer.js");

        var script = MockDomPrelude + $$"""
            import assert from 'node:assert/strict';
            import { createMediaLightbox } from '{{lightboxUrl}}';
            import { createMemoryImageCache } from '{{cacheUrl}}';
            import {
                resolveCachedMediaUrl,
                clearMediaUrlCache,
                evictMediaUrlCacheForMessage,
                hydrateMediaElements
            } from '{{rendererUrl}}';

            // Build lightbox DOM
            const lbModal = document.createElement('div'); lbModal.id = 'media-lightbox-modal'; lbModal.classList.add('hidden');
            const lbTitle = document.createElement('h3'); lbTitle.id = 'media-lightbox-title';
            const lbCounter = document.createElement('span'); lbCounter.id = 'media-lightbox-counter';
            const lbImg = document.createElement('img'); lbImg.id = 'media-lightbox-img';
            const lbErr = document.createElement('div'); lbErr.id = 'media-lightbox-error'; lbErr.classList.add('hidden');
            const lbPrev = document.createElement('button'); lbPrev.id = 'media-lightbox-prev-btn';
            const lbNext = document.createElement('button'); lbNext.id = 'media-lightbox-next-btn';
            const lbClose = document.createElement('button'); lbClose.id = 'media-lightbox-close-btn';
            const lbDl = document.createElement('button'); lbDl.id = 'media-lightbox-download-btn';
            for (const c of [lbTitle, lbCounter, lbImg, lbErr, lbPrev, lbNext, lbClose, lbDl]) lbModal.appendChild(c);
            document.body.appendChild(lbModal);

            const convId = '11111111-1111-1111-1111-111111111111';
            let signedUrlCalls = 0;
            let blobFetchCalls = 0;
            let blobSeq = 0;
            const revokedUrls = [];

            const mockCreateObjectUrl = () => `blob:https://localhost/mock-${++blobSeq}`;
            const mockRevokeObjectUrl = (u) => revokedUrls.push(u);

            // 1. RAM Blob ObjectURL cache: 1st open fetches once; closing + re-opening does 0 fetches
            const lightbox = createMediaLightbox({
                maxCachedItems: 2,
                maxCachedBytes: 2500,
                getMediaUrlFn: async (_cId, _mId, mediaId) => {
                    signedUrlCalls++;
                    if (mediaId === 'unsafe') return { url: 'javascript:alert(1)' };
                    return { url: `https://r2.example.com/photos/${mediaId}?variant=original` };
                },
                fetchFn: async (url) => {
                    blobFetchCalls++;
                    if (url.includes('svg-attack')) {
                        return {
                            ok: true,
                            status: 200,
                            headers: { get: () => 'image/svg+xml' },
                            blob: async () => ({ size: 120, type: 'image/svg+xml' })
                        };
                    }
                    if (url.includes('cors-blocked')) {
                        throw new TypeError('Failed to fetch');
                    }
                    return {
                        ok: true,
                        status: 200,
                        headers: { get: () => 'image/jpeg' },
                        blob: async () => ({ size: 1000, type: 'image/jpeg' })
                    };
                },
                createObjectUrlFn: mockCreateObjectUrl,
                revokeObjectUrlFn: mockRevokeObjectUrl
            });

            // Open photo #1 -> fetches /url once + Blob once
            lightbox.open([{ conversationId: convId, messageId: '101', mediaId: 'p1', fileName: 'p1.jpg' }], 0);
            await new Promise(r => setTimeout(r, 15));
            assert.equal(signedUrlCalls, 1);
            assert.equal(blobFetchCalls, 1);
            assert.equal(lbImg.src, 'blob:https://localhost/mock-1');
            assert.equal(lightbox.getCacheStats().count, 1);
            assert.equal(lightbox.getCacheStats().totalBytes, 1000);

            // Close lightbox -> clears <img src>, keeps blob: in RAM cache
            lightbox.close();
            assert.equal(lbImg.hasAttribute('src'), false);
            assert.equal(revokedUrls.length, 0);

            // Re-open photo #1 -> 0 extra /url calls, 0 extra fetch calls, instant blob: URL reuse
            lightbox.open([{ conversationId: convId, messageId: '101', mediaId: 'p1', fileName: 'p1.jpg' }], 0);
            assert.equal(signedUrlCalls, 1);
            assert.equal(blobFetchCalls, 1);
            assert.equal(lbImg.src, 'blob:https://localhost/mock-1');
            lightbox.close();

            // 2. LRU eviction by item count (maxCachedItems=2): touch p1, open p2 & p3 -> evicts LRU p2, keeps p1 & p3
            lightbox.open([{ conversationId: convId, messageId: '102', mediaId: 'p2', fileName: 'p2.jpg' }], 0);
            await new Promise(r => setTimeout(r, 15));
            lightbox.close();
            // Touch p1 so p2 becomes oldest
            lightbox.open([{ conversationId: convId, messageId: '101', mediaId: 'p1', fileName: 'p1.jpg' }], 0);
            assert.equal(blobFetchCalls, 2); // still 2 (p1 cache hit!)
            lightbox.close();

            // Open p3 (1000 bytes, total would be 3000 > maxCachedBytes=2500 & count 3 > maxCachedItems=2) -> evicts p2 (mock-2)
            lightbox.open([{ conversationId: convId, messageId: '103', mediaId: 'p3', fileName: 'p3.jpg' }], 0);
            await new Promise(r => setTimeout(r, 15));
            assert.equal(lightbox.getCacheStats().count, 2);
            assert.equal(lightbox.getCacheStats().totalBytes, 2000);
            assert.deepEqual(revokedUrls, ['blob:https://localhost/mock-2']);
            lightbox.close();

            // 3. Message deletion evicts & revokes even when lightbox is CLOSED
            lightbox.closeIfMessage('103');
            assert.equal(lightbox.getCacheStats().count, 1);
            assert.deepEqual(revokedUrls, ['blob:https://localhost/mock-2', 'blob:https://localhost/mock-3']);

            // 4. Switching conversations (resetForSwitch) wipes ALL remaining cached Blobs cleanly
            lightbox.resetForSwitch();
            assert.equal(lightbox.getCacheStats().count, 0);
            assert.equal(lightbox.getCacheStats().totalBytes, 0);
            assert.deepEqual(revokedUrls, [
                'blob:https://localhost/mock-2',
                'blob:https://localhost/mock-3',
                'blob:https://localhost/mock-1'
            ]);

            // After switching back, re-opening p1 fetches cleanly again
            lightbox.open([{ conversationId: convId, messageId: '101', mediaId: 'p1', fileName: 'p1.jpg' }], 0);
            await new Promise(r => setTimeout(r, 15));
            assert.equal(blobFetchCalls, 4);
            assert.equal(lbImg.src, 'blob:https://localhost/mock-4');
            lightbox.wipe();
            assert.equal(lightbox.getCacheStats().count, 0);

            // 5. Security: unsafe URL and SVG blob rejection
            const callsBeforeUnsafe = blobFetchCalls;
            lightbox.open([{ conversationId: convId, messageId: '104', mediaId: 'unsafe', fileName: 'x.jpg' }], 0);
            await new Promise(r => setTimeout(r, 15));
            assert.equal(blobFetchCalls, callsBeforeUnsafe);
            assert.equal(lbErr.classList.contains('hidden'), false);
            assert.equal(lbErr.textContent, 'Media unavailable');
            lightbox.close();

            const blobCountBeforeSvg = blobSeq;
            lightbox.open([{ conversationId: convId, messageId: '105', mediaId: 'svg-attack', fileName: 'x.svg' }], 0);
            await new Promise(r => setTimeout(r, 15));
            assert.equal(blobSeq, blobCountBeforeSvg);
            assert.equal(lbErr.classList.contains('hidden'), false);
            lightbox.close();

            // 6. CORS fallback: when fetch throws TypeError, falls back to signed URL, parks loaded <img>, and reuses with 0 fetches
            const fetchCallsBeforeCors = blobFetchCalls;
            lightbox.open([{ conversationId: convId, messageId: '106', mediaId: 'cors-blocked', fileName: 'cors.jpg' }], 0);
            await new Promise(r => setTimeout(r, 15));
            assert.equal(blobFetchCalls, fetchCallsBeforeCors + 1);
            assert.equal(document.getElementById('media-lightbox-img').src, 'https://r2.example.com/photos/cors-blocked?variant=original');

            // Simulate browser finishing image load, then close -> parks loaded <img> safely while #media-lightbox-img resets src
            document.getElementById('media-lightbox-img').dispatchEvent({ type: 'load' });
            lightbox.close();
            assert.equal(document.getElementById('media-lightbox-img').hasAttribute('src'), false);
            assert.equal(lightbox.getCacheStats().count, 1);

            // Re-open -> 0 extra /url or fetch calls!
            const urlCallsBeforeReopen = signedUrlCalls;
            const fetchCallsBeforeReopen = blobFetchCalls;
            lightbox.open([{ conversationId: convId, messageId: '106', mediaId: 'cors-blocked', fileName: 'cors.jpg' }], 0);
            assert.equal(signedUrlCalls, urlCallsBeforeReopen);
            assert.equal(blobFetchCalls, fetchCallsBeforeReopen);

            // resetForSwitch tears down parked <img> element cleanly
            lightbox.resetForSwitch();
            assert.equal(lightbox.getCacheStats().count, 0);

            // 7. Standalone createMemoryImageCache refuses blobs larger than maxBytes
            const revokes = [];
            const smallCache = createMemoryImageCache({ maxItems: 5, maxBytes: 500, revokeObjectUrlFn: (u) => revokes.push(u) });
            assert.equal(smallCache.setBlob('huge', 'blob:https://localhost/huge', 900, {}), false);
            assert.equal(smallCache.stats().count, 0);

            // 8. Coalesced in-flight transient (unstored) Blob stays alive on same-key re-open, revokes on permanent close
            let releaseTinyUrl;
            const tinyRevokes = [];
            let tinyBlobSeq = 0;
            const tinyLightbox = createMediaLightbox({
                maxCachedItems: 2,
                maxCachedBytes: 100,
                getMediaUrlFn: async () => new Promise(resolve => {
                    releaseTinyUrl = () => resolve({ url: 'https://r2.example.com/photos/huge-item?variant=original' });
                }),
                fetchFn: async () => ({
                    ok: true,
                    status: 200,
                    headers: { get: () => 'image/jpeg' },
                    blob: async () => ({ size: 500, type: 'image/jpeg' })
                }),
                createObjectUrlFn: () => `blob:https://localhost/tiny-${++tinyBlobSeq}`,
                revokeObjectUrlFn: (u) => tinyRevokes.push(u)
            });

            const hugeItem = [{ conversationId: convId, messageId: '201', mediaId: 'huge-item', fileName: 'huge.jpg' }];
            tinyLightbox.open(hugeItem, 0);
            tinyLightbox.close();
            tinyLightbox.open(hugeItem, 0);
            releaseTinyUrl();
            await new Promise(r => setTimeout(r, 20));
            assert.deepEqual(tinyRevokes, []);
            assert.equal(document.getElementById('media-lightbox-img').src, 'blob:https://localhost/tiny-1');
            tinyLightbox.close();
            assert.deepEqual(tinyRevokes, ['blob:https://localhost/tiny-1']);

            // Permanent close before transient resolution must revoke stale unstored Blob
            tinyLightbox.open(hugeItem, 0);
            tinyLightbox.close();
            releaseTinyUrl();
            await new Promise(r => setTimeout(r, 20));
            assert.deepEqual(tinyRevokes, ['blob:https://localhost/tiny-1', 'blob:https://localhost/tiny-2']);
            tinyLightbox.destroy();

            // 9. Parked CORS fallback <img> error hides element, retries /url, and shows Media unavailable on failure
            let errUrlCalls = 0;
            let returnEmptyUrl = false;
            const fallbackErrLightbox = createMediaLightbox({
                maxCachedItems: 2,
                maxCachedBytes: 2500,
                getMediaUrlFn: async () => {
                    errUrlCalls++;
                    return returnEmptyUrl ? { url: '' } : { url: 'https://r2.example.com/photos/cors-fail?variant=original' };
                },
                fetchFn: async () => {
                    throw new TypeError('Failed to fetch');
                }
            });

            fallbackErrLightbox.open([{ conversationId: convId, messageId: '202', mediaId: 'cors-fail', fileName: 'cors-fail.jpg' }], 0);
            await new Promise(r => setTimeout(r, 20));
            const parkedFallbackEl = document.getElementById('media-lightbox-img');
            parkedFallbackEl.dispatchEvent({ type: 'load' });
            fallbackErrLightbox.close();

            fallbackErrLightbox.open([{ conversationId: convId, messageId: '202', mediaId: 'cors-fail', fileName: 'cors-fail.jpg' }], 0);
            assert.equal(errUrlCalls, 1);
            returnEmptyUrl = true;
            parkedFallbackEl.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 20));
            assert.equal(errUrlCalls, 2);
            assert.equal(parkedFallbackEl.classList.contains('hidden'), true);
            assert.equal(document.getElementById('media-lightbox-error').textContent, 'Media unavailable');
            assert.equal(document.getElementById('media-lightbox-error').classList.contains('hidden'), false);
            assert.equal(fallbackErrLightbox.getCacheStats().count, 0);
            fallbackErrLightbox.destroy();

            // 10. Step 7.5: lightbox clickedIdx aligns with filtered galleryItems; resolveCachedMediaUrl epoch & coalescing; detached video guard
            const clickLightbox = createMediaLightbox({
                getMediaUrlFn: async (_c, _m, mediaId) => ({ url: `https://r2.example.com/photos/${mediaId}` }),
                fetchFn: async () => { throw new TypeError('cors'); }
            });
            const rowWithInvalid = document.createElement('div');
            rowWithInvalid.className = 'message-bubble-row';
            const badTrigger = document.createElement('button');
            badTrigger.setAttribute('data-lightbox-trigger', 'true');
            badTrigger.setAttribute('data-conversation-id', convId);
            badTrigger.setAttribute('data-message-id', '300');
            badTrigger.setAttribute('data-media-id', '');
            const goodTrigger1 = document.createElement('button');
            goodTrigger1.setAttribute('data-lightbox-trigger', 'true');
            goodTrigger1.setAttribute('data-conversation-id', convId);
            goodTrigger1.setAttribute('data-message-id', '300');
            goodTrigger1.setAttribute('data-media-id', 'good-1');
            const goodTrigger2 = document.createElement('button');
            goodTrigger2.setAttribute('data-lightbox-trigger', 'true');
            goodTrigger2.setAttribute('data-conversation-id', convId);
            goodTrigger2.setAttribute('data-message-id', '300');
            goodTrigger2.setAttribute('data-media-id', 'good-2');
            rowWithInvalid.appendChild(badTrigger);
            rowWithInvalid.appendChild(goodTrigger1);
            rowWithInvalid.appendChild(goodTrigger2);
            document.body.appendChild(rowWithInvalid);
            document.dispatchEvent({ type: 'click', target: goodTrigger1, preventDefault() {} });
            assert.equal(document.getElementById('media-lightbox-counter').textContent, '1 / 2');
            clickLightbox.destroy();

            let coalesceUrlCalls = 0;
            let resolveCoalesce;
            const c1 = resolveCachedMediaUrl(convId, '401', 'm401', {
                getMediaUrlFn: () => {
                    coalesceUrlCalls++;
                    return new Promise(r => { resolveCoalesce = r; });
                }
            });
            const c2 = resolveCachedMediaUrl(convId, '401', 'm401', {
                getMediaUrlFn: () => {
                    coalesceUrlCalls++;
                    return Promise.resolve({ url: 'https://r2.example.com/other' });
                }
            });
            assert.equal(coalesceUrlCalls, 1);
            resolveCoalesce({ url: 'https://r2.example.com/coalesced', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            assert.equal(await c1, 'https://r2.example.com/coalesced');
            assert.equal(await c2, 'https://r2.example.com/coalesced');

            let resEvictedMsgUrl;
            const pEvictedMsg = resolveCachedMediaUrl(convId, '402', 'm402', {
                getMediaUrlFn: () => new Promise(r => { resEvictedMsgUrl = r; })
            });
            evictMediaUrlCacheForMessage('402');
            resEvictedMsgUrl({ url: 'https://r2.example.com/stale-402', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            assert.equal(await pEvictedMsg, null);

            let resClearedMediaUrl;
            const pClearedMedia = resolveCachedMediaUrl(convId, '403', 'm403', {
                getMediaUrlFn: () => new Promise(r => { resClearedMediaUrl = r; })
            });
            clearMediaUrlCache();
            resClearedMediaUrl({ url: 'https://r2.example.com/stale-403', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            assert.equal(await pClearedMedia, null);

            // Detached <video> does not assign video.src after await
            const origIO = globalThis.IntersectionObserver;
            delete globalThis.IntersectionObserver;
            const vidHost = document.createElement('div');
            const detachedVideo = document.createElement('video');
            detachedVideo.setAttribute('data-media-video', 'true');
            detachedVideo.setAttribute('data-conversation-id', convId);
            detachedVideo.setAttribute('data-message-id', '404');
            detachedVideo.setAttribute('data-media-id', 'm404');
            detachedVideo.isConnected = true;
            vidHost.appendChild(detachedVideo);
            let resVideoUrl;
            hydrateMediaElements(vidHost, {
                getMediaUrlFn: () => new Promise(r => { resVideoUrl = r; })
            });
            detachedVideo.isConnected = false;
            resVideoUrl({ url: 'https://r2.example.com/video-404.mp4', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(Boolean(detachedVideo.src), false);
            if (origIO) globalThis.IntersectionObserver = origIO;

            lightbox.destroy();
            console.log('OK_MEDIA_LIGHTBOX_MEMORY_CACHE');
            """;

        var (exitCode, output, error) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node script failed ({exitCode}):\nSTDOUT:\n{output}\nSTDERR:\n{error}");
        Assert.Contains("OK_MEDIA_LIGHTBOX_MEMORY_CACHE", output);
    }
}
