using System.Text.RegularExpressions;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class MediaDragGuardFrontendTests
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
            // Intentionally single-selector only, matching existing mock DOMs in the test suite
            return matchesSingle(el, selector.trim());
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
                parentElement: null,
                disabled: false,
                value: '',
                src: '',
                currentSrc: '',
                alt: '',
                href: '',
                type: '',
                draggable: true,
                isContentEditable: false,
                controls: false,
                preload: '',
                playsInline: false,
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
                    child.parentElement = this;
                    this.children.push(child);
                    return child;
                },
                insertBefore(newNode, refNode) {
                    if (!refNode) return this.appendChild(newNode);
                    const idx = this.children.indexOf(refNode);
                    if (idx < 0) return this.appendChild(newNode);
                    newNode.parentNode = this;
                    newNode.parentElement = this;
                    this.children.splice(idx, 0, newNode);
                    return newNode;
                },
                removeChild(child) {
                    const idx = this.children.indexOf(child);
                    if (idx >= 0) {
                        this.children.splice(idx, 1);
                        child.parentNode = null;
                        child.parentElement = null;
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
                    if (k === 'type') this.type = String(v);
                    if (k === 'draggable') this.draggable = String(v) !== 'false';
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
            createTextNode: (txt) => ({ nodeType: 3, textContent: String(txt), parentNode: null, parentElement: null }),
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
            location: { origin: 'http://localhost:5000', href: 'http://localhost:5000/chat' },
            addEventListener: () => {},
            removeEventListener: () => {}
        };
        """;

    [Fact]
    public async Task JsRenderers_SetDraggableFalse_AcrossMediaStickersAvatarsTrayFallbackAndLinks()
    {
        var script = MockDomPrelude + $$"""
            import assert from 'node:assert/strict';
            const { createMessageBubbleElement } = await import('{{JsUrl("message-renderer.js")}}');
            const { createStickerPicker } = await import('{{JsUrl("sticker-picker.js")}}');
            const avatarLoader = await import('{{JsUrl("avatar-loader.js")}}');
            const { createMediaUploader } = await import('{{JsUrl("media-uploader.js")}}');
            const { parkLoadedFallbackImage } = await import('{{JsUrl("memory-image-cache.js")}}');
            const { createSidebarConversationItemElement } = await import('{{JsUrl("conversation-list.js")}}');

            const convId = '86131524-34e7-f111-9585-047c1647fc91';
            const userId = '11111111-1111-1111-1111-111111111111';
            const peerId = '22222222-2222-2222-2222-222222222222';
            const mediaId1 = 'b2ee83b9-fee8-f111-9586-047c1647fc91';
            const mediaId2 = 'c3ee83b9-fee8-f111-9586-047c1647fc91';
            const stickerId = 'd4ee83b9-fee8-f111-9586-047c1647fc91';

            // 1. message-renderer: single photo, multi-photo grid, and sticker
            const singlePhotoRow = createMessageBubbleElement({
                id: 100182,
                conversationId: convId,
                senderId: peerId,
                senderName: 'Alice',
                senderHandle: 'alice',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: false,
                attachments: [{ id: mediaId1, kind: 'image', fileName: 'photo.jpg', width: 800, height: 600 }]
            }, userId);
            const singleImg = singlePhotoRow.querySelector('img[data-media-img="true"]');
            assert.equal(singleImg.draggable, false);
            assert.equal(singleImg.getAttribute('draggable'), 'false');
            assert.equal(singleImg.classList.contains('select-none'), true);

            const gridRow = createMessageBubbleElement({
                id: 100183,
                conversationId: convId,
                senderId: userId,
                senderName: 'Me',
                senderHandle: 'me',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: true,
                attachments: [
                    { id: mediaId1, kind: 'image', fileName: 'a.jpg', width: 400, height: 400 },
                    { id: mediaId2, kind: 'image', fileName: 'b.jpg', width: 400, height: 400 }
                ]
            }, userId);
            const gridImgs = gridRow.querySelectorAll('img[data-media-img="true"]');
            assert.equal(gridImgs.length, 2);
            for (const im of gridImgs) {
                assert.equal(im.draggable, false);
                assert.equal(im.getAttribute('draggable'), 'false');
                assert.equal(im.classList.contains('select-none'), true);
            }

            const stickerRow = createMessageBubbleElement({
                id: 100184,
                conversationId: convId,
                senderId: peerId,
                senderName: 'Alice',
                senderHandle: 'alice',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: false,
                sticker: {
                    id: stickerId,
                    packId: '33333333-3333-3333-3333-333333333333',
                    packName: 'Greetings',
                    packAuthor: 'AOS',
                    isSystem: true,
                    staticUrl: '/stickers/packs/greetings/wave.webp',
                    emojiTags: '👋',
                    width: 512,
                    height: 512
                }
            }, userId);
            const stickerImg = stickerRow.querySelector('img[data-sticker-img="true"]');
            assert.equal(stickerImg.draggable, false);
            assert.equal(stickerImg.getAttribute('draggable'), 'false');

            // 2. sticker-picker: rendered grid sticker img
            const pickerHost = document.createElement('div');
            document.body.appendChild(pickerHost);
            const stickerPicker = createStickerPicker({
                getCatalogFn: async () => ({
                    recents: [],
                    favorites: [],
                    systemPacks: [{
                        id: '33333333-3333-3333-3333-333333333333',
                        name: 'Greetings',
                        author: 'AOS',
                        isSystem: true,
                        stickers: [{
                            id: stickerId,
                            packId: '33333333-3333-3333-3333-333333333333',
                            packName: 'Greetings',
                            packAuthor: 'AOS',
                            isSystem: true,
                            staticUrl: '/stickers/packs/greetings/wave.webp',
                            emojiTags: 'wave',
                            width: 512,
                            height: 512
                        }]
                    }],
                    customPacks: []
                }),
                getUrlFn: async () => ({ url: 'https://r2.example.com/stickers/1.webp', expiresAtUtc: new Date(Date.now() + 60000).toISOString() })
            });
            stickerPicker.mount(pickerHost);
            await stickerPicker.ensureLoaded();
            const pickerStickerImg = pickerHost.querySelector('img[data-sticker-img="true"]');
            assert.ok(pickerStickerImg);
            assert.equal(pickerStickerImg.draggable, false);
            assert.equal(pickerStickerImg.getAttribute('draggable'), 'false');

            // 3. avatar-loader: slot img and #avatar-lightbox-img
            const avatarSlot = document.createElement('div');
            avatarSlot.setAttribute('data-avatar-slot', 'true');
            avatarSlot.setAttribute('data-avatar-user-id', peerId);
            avatarSlot.setAttribute('data-avatar-version', 'v1');
            avatarSlot.setAttribute('data-avatar-name', 'Alice');
            document.body.appendChild(avatarSlot);
            const mockAvatarUrlFn = async (uid, { size }) => ({
                url: `https://r2.example.com/avatars/${uid}?size=${size}`,
                expiresAtUtc: new Date(Date.now() + 60000).toISOString(),
                version: 'v1',
                size
            });
            await avatarLoader.hydrateAvatarSlot(avatarSlot, { getAvatarUrlFn: mockAvatarUrlFn });
            const avatarImg = avatarSlot.querySelector('[data-avatar-img]');
            assert.ok(avatarImg);
            assert.equal(avatarImg.draggable, false);
            assert.equal(avatarImg.getAttribute('draggable'), 'false');

            await avatarLoader.openAvatarLightbox(peerId, 'v1', 'Alice', {
                getAvatarUrlFn: mockAvatarUrlFn,
                fetchFn: async () => { throw new TypeError('Network disabled in test'); }
            });
            const avatarLightboxImg = document.getElementById('avatar-lightbox-img');
            assert.ok(avatarLightboxImg);
            assert.equal(avatarLightboxImg.draggable, false);
            assert.equal(avatarLightboxImg.getAttribute('draggable'), 'false');

            // 4. media-uploader: pending attachments tray preview thumbnail
            const trayEl = document.createElement('div');
            trayEl.id = 'composer-attachments-tray';
            document.body.appendChild(trayEl);
            const origCreateObjUrl = globalThis.URL?.createObjectURL;
            const origRevokeObjUrl = globalThis.URL?.revokeObjectURL;
            if (typeof globalThis.URL !== 'undefined') {
                globalThis.URL.createObjectURL = () => 'blob:http://localhost:5000/preview-1';
                globalThis.URL.revokeObjectURL = () => {};
            }
            const uploader = createMediaUploader({ state: { activeConversationId: convId, switchEpoch: 0 } });
            uploader.setLocked(false);
            uploader.addFiles([{ name: 'snap.png', type: 'image/png', size: 1024 }]);
            const trayThumb = trayEl.querySelector('img');
            assert.ok(trayThumb);
            assert.equal(trayThumb.draggable, false);
            assert.equal(trayThumb.getAttribute('draggable'), 'false');
            assert.equal(trayThumb.classList.contains('select-none'), true);
            if (typeof globalThis.URL !== 'undefined') {
                globalThis.URL.createObjectURL = origCreateObjUrl;
                globalThis.URL.revokeObjectURL = origRevokeObjUrl;
            }

            // 5. memory-image-cache: parkLoadedFallbackImage preserves draggable="false"
            const wrap = document.createElement('div');
            const origPrimary = document.createElement('img');
            origPrimary.id = 'media-lightbox-img';
            origPrimary.className = 'max-h-[78vh] select-none';
            wrap.appendChild(origPrimary);
            const parked = parkLoadedFallbackImage(origPrimary);
            assert.ok(parked?.freshPrimary);
            assert.equal(parked.freshPrimary.draggable, false);
            assert.equal(parked.freshPrimary.getAttribute('draggable'), 'false');

            // 6. conversation-list: [data-conversation-link] has draggable="false"
            const convLink = createSidebarConversationItemElement({
                id: convId,
                otherParticipantId: peerId,
                otherParticipantName: 'Alice',
                otherParticipantHandle: 'alice',
                lastMessagePreview: 'Hi',
                unreadCount: 0
            }, convId);
            assert.ok(convLink);
            assert.equal(convLink.draggable, false);
            assert.equal(convLink.getAttribute('draggable'), 'false');
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task MediaDragGuard_BlocksInAppDragsAndInternalUrlDrops_WhilePreservingFilesAndText()
    {
        var script = MockDomPrelude + $$"""
            import assert from 'node:assert/strict';
            const {
                setupMediaDragGuard,
                isInternalMediaUrl,
                extractDroppedUrlCandidates,
                collectRenderedMediaUrls
            } = await import('{{JsUrl("media-drag-guard.js")}}');

            const origin = 'http://localhost:5000';
            const r2SignedImgUrl = 'https://pub.r2.cloudflarestorage.com/bucket/media/signed-photo.webp?X-Amz-Signature=abc123';
            const r2SignedVideoUrl = 'https://pub.r2.cloudflarestorage.com/bucket/media/signed-clip.mp4?X-Amz-Signature=def456';

            // Build DOM with rendered media, video, sticker, avatar, link, and plain message text
            const bubbleImg = document.createElement('img');
            bubbleImg.setAttribute('data-media-img', 'true');
            bubbleImg.src = '/api/conversations/86131524-34e7-f111-9585-047c1647fc91/messages/100182/media/b2ee83b9-fee8-f111-9586-047c1647fc91?variant=thumb';
            document.body.appendChild(bubbleImg);

            const lightboxImg = document.createElement('img');
            lightboxImg.id = 'media-lightbox-img';
            lightboxImg.src = r2SignedImgUrl;
            document.body.appendChild(lightboxImg);

            const videoEl = document.createElement('video');
            videoEl.setAttribute('data-media-video', 'true');
            videoEl.src = r2SignedVideoUrl;
            document.body.appendChild(videoEl);

            const lightboxBtn = document.createElement('button');
            lightboxBtn.setAttribute('data-lightbox-trigger', 'true');
            const nestedBtnChild = document.createElement('span');
            lightboxBtn.appendChild(nestedBtnChild);
            document.body.appendChild(lightboxBtn);

            const profileLink = document.createElement('a');
            profileLink.href = '/Profile';
            profileLink.setAttribute('draggable', 'false');
            const linkLabelText = document.createTextNode('My Profile');
            profileLink.appendChild(linkLabelText);
            document.body.appendChild(profileLink);

            const convLink = document.createElement('a');
            convLink.href = '/chat/86131524-34e7-f111-9585-047c1647fc91';
            convLink.setAttribute('data-conversation-link', 'true');
            document.body.appendChild(convLink);

            const textDiv = document.createElement('div');
            textDiv.className = 'message-text-content';
            const msgTextNode = document.createTextNode('Normal selectable message text');
            textDiv.appendChild(msgTextNode);
            document.body.appendChild(textDiv);

            const composerInput = document.createElement('textarea');
            composerInput.id = 'message-content-input';
            document.body.appendChild(composerInput);

            const editInput = document.createElement('textarea');
            editInput.id = 'edit-message-input';
            document.body.appendChild(editInput);

            const searchInput = document.createElement('input');
            searchInput.type = 'text';
            searchInput.id = 'conversationSearchInput';
            document.body.appendChild(searchInput);

            const fileInput = document.createElement('input');
            fileInput.type = 'file';
            document.body.appendChild(fileInput);

            const rangeInput = document.createElement('input');
            rangeInput.type = 'range';
            document.body.appendChild(rangeInput);

            // Unit checks for extractDroppedUrlCandidates (RFC 2483)
            const rfcCandidates = extractDroppedUrlCandidates({
                getData(kind) {
                    if (kind === 'text/uri-list') {
                        return '# comment line\r\nhttp://localhost:5000/api/stickers/86131524-34e7-f111-9585-047c1647fc91/image\r\n';
                    }
                    if (kind === 'text/plain') {
                        return 'http://localhost:5000/api/stickers/86131524-34e7-f111-9585-047c1647fc91/image';
                    }
                    return '';
                }
            });
            assert.deepEqual(rfcCandidates, ['http://localhost:5000/api/stickers/86131524-34e7-f111-9585-047c1647fc91/image']);

            // Unit checks for collectRenderedMediaUrls + isInternalMediaUrl
            const renderedUrls = collectRenderedMediaUrls(document, origin);
            assert.equal(renderedUrls.has(r2SignedImgUrl), true);
            assert.equal(renderedUrls.has(r2SignedVideoUrl), true);

            const blockedUrls = [
                'http://localhost:5000/api/conversations/86131524-34e7-f111-9585-047c1647fc91/messages/100182/media/b2ee83b9-fee8-f111-9586-047c1647fc91?variant=thumb',
                'http://localhost:5000/api/conversations/86131524-34e7-f111-9585-047c1647fc91/messages/100182/media/b2ee83b9-fee8-f111-9586-047c1647fc91/url',
                'http://localhost:5000/api/stickers/86131524-34e7-f111-9585-047c1647fc91/image',
                'http://localhost:5000/api/stickers/86131524-34e7-f111-9585-047c1647fc91/url',
                'http://localhost:5000/api/users/86131524-34e7-f111-9585-047c1647fc91/avatar?v=123',
                'http://localhost:5000/stickers/packs/classic/wave.webp?v=abc',
                'http://localhost:5000/dev-media/download?token=xyz',
                'blob:http://localhost:5000/550e8400-e29b-41d4-a716-446655440000',
                r2SignedImgUrl,
                r2SignedVideoUrl
            ];
            for (const u of blockedUrls) {
                assert.equal(isInternalMediaUrl(u, { origin, renderedMediaUrls: renderedUrls }), true, `Expected blocked: ${u}`);
            }

            const allowedUrls = [
                'https://example.com/article',
                'Hello world',
                'http://evil.example.com/api/stickers/86131524-34e7-f111-9585-047c1647fc91/image',
                'blob:https://example.com/550e8400-e29b-41d4-a716-446655440000'
            ];
            for (const u of allowedUrls) {
                assert.equal(isInternalMediaUrl(u, { origin, renderedMediaUrls: renderedUrls }), false, `Expected allowed: ${u}`);
            }

            // Setup live guard
            const guard = setupMediaDragGuard({ documentRef: document, getOrigin: () => origin });

            const fireDragStart = (target) => {
                let prevented = false;
                document.dispatchEvent({
                    type: 'dragstart',
                    target,
                    preventDefault() { prevented = true; }
                });
                return prevented;
            };

            // dragstart assertions (including Text node normalization)
            assert.equal(fireDragStart(bubbleImg), true);
            assert.equal(fireDragStart(videoEl), true);
            assert.equal(fireDragStart(nestedBtnChild), true);
            assert.equal(fireDragStart(convLink), true);
            assert.equal(fireDragStart(linkLabelText), true, 'Text node inside a[draggable="false"] must be blocked');
            assert.equal(fireDragStart(msgTextNode), false, 'Desktop text-selection drag must remain allowed');

            const fireDrop = (target, { uriList = '', plain = '', files = [], types = null } = {}) => {
                let prevented = false;
                document.dispatchEvent({
                    type: 'drop',
                    target,
                    dataTransfer: {
                        files,
                        types: types || (files.length ? ['Files'] : ['text/uri-list', 'text/plain']),
                        getData(kind) {
                            if (kind === 'text/uri-list') return uriList;
                            if (kind === 'text/plain') return plain;
                            return '';
                        }
                    },
                    preventDefault() { prevented = true; }
                });
                return prevented;
            };

            // Drop blocking into composer, edit modal, and text search input
            for (const u of blockedUrls) {
                assert.equal(fireDrop(composerInput, { uriList: `# hint\r\n${u}`, plain: u }), true, `Composer must block drop: ${u}`);
            }
            assert.equal(fireDrop(editInput, { uriList: r2SignedImgUrl, plain: r2SignedImgUrl }), true, 'Edit modal must block rendered signed URL');
            assert.equal(fireDrop(searchInput, { plain: blockedUrls[0] }), true, 'Text input must block internal API URL');

            // Phantom 'Files' type with files.length === 0 must still be blocked
            assert.equal(
                fireDrop(editInput, { uriList: blockedUrls[0], plain: blockedUrls[0], files: [], types: ['Files', 'text/uri-list'] }),
                true,
                'Phantom Files drag with files.length === 0 must be blocked'
            );

            // Real OS file drops (files.length > 0), normal text, external URLs, and non-text inputs must pass through
            assert.equal(
                fireDrop(composerInput, { files: [{ name: 'photo.jpg', size: 1234 }], types: ['Files'] }),
                false,
                'Real OS file drop must pass through'
            );
            assert.equal(fireDrop(composerInput, { plain: 'Hello world!' }), false);
            assert.equal(fireDrop(composerInput, { uriList: 'https://example.com/article', plain: 'https://example.com/article' }), false);
            assert.equal(fireDrop(fileInput, { plain: blockedUrls[0] }), false);
            assert.equal(fireDrop(rangeInput, { plain: blockedUrls[0] }), false);

            // destroy() cleanly detaches both listeners
            guard.destroy();
            guard.destroy(); // idempotent
            assert.equal(fireDragStart(bubbleImg), false);
            assert.equal(fireDrop(composerInput, { uriList: blockedUrls[0], plain: blockedUrls[0] }), false);
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public void RazorViews_Css_And_EntryPoints_EnforceDragAndTouchCalloutProtection()
    {
        var slnRoot = FindSolutionRoot();

        // 1. Every <img> in Conversation.cshtml and _ChatModals.cshtml must carry draggable="false"
        var imageViews = new[]
        {
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml"),
            Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "_ChatModals.cshtml")
        };

        var imgTagRegex = new Regex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (var viewPath in imageViews)
        {
            var html = File.ReadAllText(viewPath);
            var matches = imgTagRegex.Matches(html);
            Assert.NotEmpty(matches);
            foreach (Match m in matches)
            {
                Assert.Contains("draggable=\"false\"", m.Value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 2. Adjacent composer/header links in Conversation.cshtml and _ChatActivePaneEmpty.cshtml
        foreach (var fileName in new[] { "Conversation.cshtml", "_ChatActivePaneEmpty.cshtml" })
        {
            var html = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", fileName));
            var backLink = Regex.Match(html, @"<a\b[^>]*data-back-to-chats=""true""[^>]*>", RegexOptions.IgnoreCase);
            Assert.True(backLink.Success, $"Missing data-back-to-chats link in {fileName}");
            Assert.Contains("draggable=\"false\"", backLink.Value, StringComparison.OrdinalIgnoreCase);

            var voiceBtn = Regex.Match(html, @"<button\b[^>]*id=""composer-voice-btn""[^>]*>", RegexOptions.IgnoreCase);
            Assert.True(voiceBtn.Success, $"Missing #composer-voice-btn button in {fileName}");
            Assert.Contains("type=\"button\"", voiceBtn.Value, StringComparison.OrdinalIgnoreCase);
        }

        // 3. Avatar-wrapping links in ConversationSidebar/Default.cshtml and Chat/Archived.cshtml
        var sidebarHtml = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Shared", "Components", "ConversationSidebar", "Default.cshtml"));
        var convLinkMatch = Regex.Match(sidebarHtml, @"<a\b[^>]*data-conversation-link=""true""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.True(convLinkMatch.Success);
        Assert.Contains("draggable=\"false\"", convLinkMatch.Value, StringComparison.OrdinalIgnoreCase);

        var profileLinks = Regex.Matches(sidebarHtml, @"<a\b[^>]*asp-controller=""Profile""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.NotEmpty(profileLinks);
        foreach (Match m in profileLinks)
        {
            Assert.Contains("draggable=\"false\"", m.Value, StringComparison.OrdinalIgnoreCase);
        }

        var archivedHtml = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Archived.cshtml"));
        var archivedConvLink = Regex.Match(archivedHtml, @"<a\b[^>]*asp-action=""Conversation""[^>]*>", RegexOptions.IgnoreCase);
        Assert.True(archivedConvLink.Success);
        Assert.Contains("draggable=\"false\"", archivedConvLink.Value, StringComparison.OrdinalIgnoreCase);

        // 4. Source & compiled CSS contain drag and touch-callout rules
        var sourceCss = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Styles", "app.css"));
        Assert.Contains("-webkit-user-drag: none;", sourceCss);
        Assert.Contains("-webkit-touch-callout: none;", sourceCss);
        Assert.Contains("a[draggable=\"false\"]", sourceCss);

        var compiledCss = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "css", "app.css"));
        Assert.Contains("-webkit-user-drag:none", compiledCss);
        Assert.Contains("-webkit-touch-callout:none", compiledCss);
        Assert.Contains(".message-text-content{", compiledCss);
        Assert.Contains("user-select:text", compiledCss);

        // 5. chat-shell.js and sidebar.js both wire and tear down setupMediaDragGuard
        var chatShellJs = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-shell.js"));
        Assert.Contains("setupMediaDragGuard", chatShellJs);
        Assert.Contains("mediaDragGuard.destroy()", chatShellJs);

        var sidebarJs = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "sidebar.js"));
        Assert.Contains("setupMediaDragGuard", sidebarJs);
        Assert.Contains("mediaDragGuard.destroy()", sidebarJs);
    }
}
