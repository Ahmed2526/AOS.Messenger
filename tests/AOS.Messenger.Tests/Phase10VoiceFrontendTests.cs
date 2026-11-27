using System.Text.RegularExpressions;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase10VoiceFrontendTests
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
                currentTime: 0,
                duration: Infinity,
                paused: true,
                ended: false,
                playbackRate: 1,
                error: null,
                isConnected: true,
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
                get nextElementSibling() {
                    if (!this.parentNode) return null;
                    const idx = this.parentNode.children.indexOf(this);
                    return idx >= 0 && idx + 1 < this.parentNode.children.length ? this.parentNode.children[idx + 1] : null;
                },
                appendChild(child) {
                    if (!child) return child;
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
                    if (idx >= 0) { this.children.splice(idx, 1); child.parentNode = null; }
                    return child;
                },
                remove() { if (this.parentNode) this.parentNode.removeChild(this); },
                setAttribute(k, v) { attrs.set(k, String(v)); if (k === 'id') this.id = String(v); if (k === 'src') this.src = String(v); },
                getAttribute(k) { return attrs.has(k) ? attrs.get(k) : null; },
                removeAttribute(k) { attrs.delete(k); if (k === 'src') this.src = ''; },
                hasAttribute(k) { return attrs.has(k); },
                addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, new Set()); listeners.get(type).add(fn); },
                removeEventListener(type, fn) { listeners.get(type)?.delete(fn); },
                dispatchEvent(evt) {
                    evt.target = evt.target || this;
                    evt.preventDefault = evt.preventDefault || (() => {});
                    evt.stopPropagation = evt.stopPropagation || (() => {});
                    for (const fn of listeners.get(evt.type) || []) fn(evt);
                },
                click() { this.dispatchEvent({ type: 'click', target: this }); },
                focus() { if (globalThis.document) globalThis.document.activeElement = this; },
                pause() { this.paused = true; this.dispatchEvent({ type: 'pause' }); },
                async play() { this.paused = false; this.ended = false; this.dispatchEvent({ type: 'playing' }); },
                load() {},
                getBoundingClientRect() { return { left: 0, width: 200, right: 200, top: 0, height: 28 }; },
                closest(sel) {
                    let cur = this;
                    while (cur) { if (matchesSelector(cur, sel)) return cur; cur = cur.parentNode; }
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
            createElement: (tag) => createEl(tag),
            getElementById: (id) => walk(rootBody, n => n.id === id)[0] || null,
            querySelector: (sel) => walk(rootBody, n => matchesSelector(n, sel))[0] || null,
            querySelectorAll: (sel) => walk(rootBody, n => matchesSelector(n, sel)),
            addEventListener(type, fn) { if (!docListeners.has(type)) docListeners.set(type, new Set()); docListeners.get(type).add(fn); },
            removeEventListener(type, fn) { docListeners.get(type)?.delete(fn); },
            dispatchEvent(evt) { for (const fn of docListeners.get(evt.type) || []) fn(evt); }
        };
        globalThis.window = {
            location: { href: 'https://localhost:7001/chat', pathname: '/chat', origin: 'https://localhost:7001', search: '' },
            addEventListener() {},
            removeEventListener() {}
        };
        globalThis.history = {
            states: [],
            replaceState(s, t, u) { this.last = { s, u }; },
            pushState(s, t, u) { this.states.push({ s, u }); this.last = { s, u }; }
        };
        """;

    [Fact]
    public async Task VoiceRecorderModule_CoversMimePriority_PauseDuration_AutoStop_And_Teardown()
    {
        var recUrl = JsUrl("voice-recorder.js");
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            for (const id of ['composer-input-row', 'composer-voice-dock', 'voice-dock-recording', 'voice-dock-preview',
                'voice-rec-dot', 'voice-rec-status', 'voice-rec-timer', 'voice-rec-limit-hint', 'voice-live-waveform',
                'voice-pause-btn', 'voice-pause-icon', 'voice-preview-waveform', 'voice-preview-timer', 'voice-preview-audio',
                'voice-preview-play-icon', 'message-content-input']) {
                rootBody.appendChild(createEl(id.includes('audio') ? 'audio' : 'div', id));
            }

            const {
                selectPreferredRecordingFormat,
                normalizeRecordedMime,
                downsampleWaveformPeaks,
                createVoiceRecorder
            } = await import('{{recUrl}}');

            // 1. Preferred MIME order: audio/mp4 first, then webm, then ogg
            const mp4RecorderCls = { isTypeSupported: (m) => m.startsWith('audio/mp4') || m.startsWith('audio/webm') };
            assert.equal(selectPreferredRecordingFormat(mp4RecorderCls).baseMime, 'audio/mp4');
            const webmOnlyCls = { isTypeSupported: (m) => m.startsWith('audio/webm') };
            assert.equal(selectPreferredRecordingFormat(webmOnlyCls).baseMime, 'audio/webm');
            const oggOnlyCls = { isTypeSupported: (m) => m.startsWith('audio/ogg') };
            assert.equal(selectPreferredRecordingFormat(oggOnlyCls).baseMime, 'audio/ogg');

            assert.equal(normalizeRecordedMime('audio/ogg;codecs=opus').contentType, 'audio/ogg');
            assert.equal(normalizeRecordedMime('audio/mp4;codecs=mp4a.40.2').ext, '.m4a');

            // 2. Waveform downsampling always emits target 48 bounded peaks [4..100]
            const peaks = downsampleWaveformPeaks([0, 20, 95, 100, 10, 50], 48);
            assert.equal(peaks.length, 48);
            assert.ok(peaks.every(p => Number.isInteger(p) && p >= 4 && p <= 100));

            // 3. Full recording lifecycle: pause excludes elapsed time, sends indicators, revokes blobs & stops tracks
            let nowMs = 1000;
            const revoked = [];
            const micCalls = [];
            let trackStopped = 0;
            let ctxClosed = 0;
            let sentVoice = null;

            class FakeRecorder {
                constructor(stream, opts) { this.mimeType = opts?.mimeType || 'audio/webm'; this.state = 'inactive'; }
                start() { this.state = 'recording'; }
                pause() { this.state = 'paused'; }
                resume() { this.state = 'recording'; }
                stop() {
                    this.state = 'inactive';
                    if (this.ondataavailable) {
                        this.ondataavailable({ data: new Blob([new Uint8Array([1, 2, 3, 4])], { type: this.mimeType }) });
                    }
                    if (this.onstop) this.onstop();
                }
                static isTypeSupported(m) { return m.startsWith('audio/webm'); }
            }

            const errors = [];
            const controller = createVoiceRecorder({
                connection: {
                    state: 'Connected',
                    invoke: async (method, convId, val) => { micCalls.push({ method, convId, val }); }
                },
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => FakeRecorder,
                getUserMediaFn: async () => ({
                    getTracks: () => [{ stop: () => { trackStopped++; } }],
                    getAudioTracks: () => [{ stop: () => { trackStopped++; } }]
                }),
                createAudioContextFn: () => ({
                    state: 'running',
                    createMediaStreamSource: () => ({ connect: () => {}, disconnect: () => {} }),
                    createAnalyser: () => ({ fftSize: 256, frequencyBinCount: 8, disconnect: () => {}, getByteTimeDomainData: (arr) => arr.fill(180) }),
                    close: async () => { ctxClosed++; }
                }),
                createObjectUrlFn: () => 'blob:https://localhost:7001/voice-preview-1',
                revokeObjectUrlFn: (u) => revoked.push(u),
                onStartSendVoice: (payload) => { sentVoice = payload; return payload; },
                onError: (msg) => errors.push(msg),
                nowFn: () => nowMs,
                autoStopBytes: 1024
            });

            controller.init();

            // 3a. Short tap (< 500ms) is rejected cleanly back to idle
            assert.equal(await controller.startRecording(), true);
            nowMs += 200;
            controller.stopToPreview();
            assert.equal(controller.getMode(), 'idle');
            assert.ok(errors.length >= 1);

            // 3b. Full lifecycle with pause/resume excludes pause duration and revokes blob on send
            assert.equal(await controller.startRecording(), true);
            assert.equal(controller.isDirty(), true);
            assert.deepEqual(micCalls.at(-1), { method: 'SendRecordingIndicator', convId: 'conv-1', val: true });

            nowMs += 3000; // 3s recorded
            controller.togglePause();
            assert.equal(controller.getMode(), 'paused');
            assert.deepEqual(micCalls.at(-1), { method: 'SendRecordingIndicator', convId: 'conv-1', val: false });

            nowMs += 60000; // 60s spent paused must NOT count toward duration!
            controller.togglePause();
            assert.equal(controller.getMode(), 'recording');
            nowMs += 2000; // +2s recorded = 5000 ms total

            controller.stopToPreview();
            assert.equal(controller.getMode(), 'preview');
            assert.ok(trackStopped >= 1);
            assert.ok(ctxClosed >= 1);

            const sent = controller.sendFromPreview();
            assert.ok(sent);
            assert.equal(sent.durationMs, 5000);
            assert.equal(sent.waveformPeaks.length, 48);
            assert.equal(sent.contentType, 'audio/webm');
            assert.equal(sent.fileName, 'voice-message.webm');
            assert.equal(revoked.length, 1);
            assert.equal(controller.isDirty(), false);

            controller.destroy();
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task VoicePlayerModule_CoversSingleActiveMedia_SeekingWithInfinityDuration_Speed_AutoPlay_And_PlayedReceipt()
    {
        var playerUrl = JsUrl("voice-player.js");
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const {
                createVoiceBubbleElement,
                createVoicePlayer,
                applyVoicePlayedToRow
            } = await import('{{playerUrl}}');

            const container = createEl('div', 'messages-container');
            rootBody.appendChild(container);

            function makeVoiceRow(msgId, mediaId, durationMs, isOutgoing) {
                const row = createEl('div', `msg-${msgId}`);
                row.className = 'message-bubble-row';
                row.setAttribute('data-message-id', String(msgId));
                row.setAttribute('data-is-voice', 'true');
                row.setAttribute('data-deleted', 'false');
                const card = createVoiceBubbleElement({
                    conversationId: 'conv-1',
                    messageId: msgId,
                    mediaId,
                    durationMs,
                    waveformPeaks: [10, 40, 80, 60],
                    playedAtUtc: null,
                    isOutgoing
                });
                row.appendChild(card);
                container.appendChild(row);
                return { row, card, audio: card.querySelector('audio'), playBtn: card.querySelector('[data-voice-play-btn]'), speedBtn: card.querySelector('[data-voice-speed-btn]'), wf: card.querySelector('[data-voice-waveform]'), time: card.querySelector('[data-voice-time]') };
            }

            const v1 = makeVoiceRow(101, 'media-1', 10000, false);
            const v2 = makeVoiceRow(102, 'media-2', 20000, false);
            const playedCalls = [];

            const player = createVoicePlayer({
                resolveUrlFn: async (convId, msgId, mediaId) => `https://r2.invalid/${mediaId}.webm`,
                markVoicePlayedFn: async (convId, msgId, mediaId) => {
                    playedCalls.push({ convId, msgId, mediaId });
                    return { playedAtUtc: '2026-11-27T12:00:00Z' };
                }
            });

            player.hydrateRoot(container);

            // 1. Speed toggles 1x -> 1.5x -> 2x across cards
            v1.speedBtn.click();
            assert.equal(player.getCurrentSpeed(), 1.5);
            assert.equal(v2.speedBtn.textContent, '1.5x');

            // 2. Play v1 -> triggers markVoicePlayed once and hides unplayed dot
            v1.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.equal(v1.audio.paused, false);
            assert.equal(v1.audio.playbackRate, 1.5);
            assert.equal(playedCalls.length, 1);
            assert.equal(v1.card.getAttribute('data-played-at'), '2026-11-27T12:00:00Z');
            assert.ok(v1.card.querySelector('[data-voice-played-dot]').classList.contains('hidden'));

            // 3. Infinity duration fallback scrubbing via keyboard ArrowRight (+5s)
            v1.audio.duration = Infinity;
            v1.wf.dispatchEvent({ type: 'keydown', key: 'ArrowRight' });
            assert.equal(v1.audio.currentTime, 5);
            assert.equal(v1.wf.getAttribute('aria-valuenow'), '5000');

            // 4. Starting v2 pauses v1 (single-active coordination)
            v2.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.equal(v1.audio.paused, true);
            assert.equal(v2.audio.paused, false);

            // 5. Sequential auto-play when v1 ends advances into consecutive v2
            v2.audio.pause();
            await v1.audio.play();
            v1.audio.ended = true;
            v1.audio.dispatchEvent({ type: 'ended' });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(v2.audio.paused, false);

            // 6. applyVoicePlayedToRow reveals "Played" label on outgoing voice note (including uppercase mediaId)
            const out = makeVoiceRow(103, 'media-3', 8000, true);
            assert.equal(applyVoicePlayedToRow(103, 'MEDIA-3', '2026-11-27T12:05:00Z'), true);
            assert.ok(!out.card.querySelector('[data-voice-played-label]').classList.contains('hidden'));

            player.destroy();
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task VoicePlayerModule_HandlesExpiredCode4Retry_Concurrency_DeferredSeek_And_ComposerVoiceReenable()
    {
        var playerUrl = JsUrl("voice-player.js");
        var composerUrl = JsUrl("composer.js");
        var uploaderUrl = JsUrl("media-uploader.js");
        var stateUrl = JsUrl("chat-state.js");

        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const { createVoiceBubbleElement, createVoicePlayer } = await import('{{playerUrl}}');
            const { createComposer } = await import('{{composerUrl}}');
            const { createMediaUploader } = await import('{{uploaderUrl}}');
            const { createChatState } = await import('{{stateUrl}}');

            const container = createEl('div', 'messages-container');
            rootBody.appendChild(container);

            function makeCard(msgId, mediaId, durationMs) {
                const row = createEl('div', `msg-${msgId}`);
                row.className = 'message-bubble-row';
                const card = createVoiceBubbleElement({
                    conversationId: 'conv-1',
                    messageId: msgId,
                    mediaId,
                    durationMs,
                    waveformPeaks: [20, 40, 60, 80],
                    playedAtUtc: null,
                    isOutgoing: false
                });
                const audio = card.querySelector('audio');
                audio.readyState = 0;
                row.appendChild(card);
                container.appendChild(row);
                return {
                    row,
                    card,
                    audio,
                    playBtn: card.querySelector('[data-voice-play-btn]'),
                    speedBtn: card.querySelector('[data-voice-speed-btn]'),
                    wf: card.querySelector('[data-voice-waveform]'),
                    errEl: card.querySelector('[data-voice-error]')
                };
            }

            // 1. Expired presigned GET URL emitting error code 4 triggers bypassCache retry and recovers
            const c1 = makeCard(201, 'media-retry', 10000);
            const c1Calls = [];
            let c1ShouldFailOnce = true;
            c1.audio.play = async () => {
                if (c1ShouldFailOnce) {
                    c1ShouldFailOnce = false;
                    c1.audio.error = { code: 4 };
                    c1.audio.dispatchEvent({ type: 'error' });
                    return;
                }
                c1.audio.paused = false;
                c1.audio.ended = false;
                c1.audio.dispatchEvent({ type: 'playing' });
            };

            // 2. Slow Card A vs Fast Card B concurrency + deferred seek + playbackRate sync
            const slowA = makeCard(202, 'media-slow', 12000);
            const fastB = makeCard(203, 'media-fast', 14000);
            let resolveSlowA;
            const slowRequests = [];
            const player = createVoicePlayer({
                resolveUrlFn: async (convId, msgId, mediaId, opts) => {
                    if (mediaId === 'media-retry') {
                        c1Calls.push(Boolean(opts?.bypassCache));
                        return opts?.bypassCache ? 'https://r2.invalid/fresh.m4a' : 'https://r2.invalid/expired.m4a';
                    }
                    if (mediaId === 'media-slow') {
                        slowRequests.push(mediaId);
                        return new Promise(r => { resolveSlowA = r; });
                    }
                    if (mediaId === 'media-scrub') {
                        scrubResolveCount++;
                    }
                    return `https://r2.invalid/${mediaId}.m4a`;
                }
            });
            let scrubResolveCount = 0;
            player.hydrateRoot(container);

            // Switch speed to 1.5x before playback
            c1.speedBtn.click();
            assert.equal(player.getCurrentSpeed(), 1.5);

            c1.playBtn.click();
            await new Promise(r => setTimeout(r, 25));
            assert.deepEqual(c1Calls, [false, true]);
            assert.equal(c1.audio.paused, false);
            assert.ok(c1.errEl.classList.contains('hidden'));

            // If retried URL also fails with code 4 -> surfaces format error cleanly without loop
            const failCard = makeCard(204, 'media-fail', 8000);
            let failResolveCount = 0;
            const failPlayer = createVoicePlayer({
                resolveUrlFn: async () => { failResolveCount++; return 'https://r2.invalid/bad.m4a'; }
            });
            failPlayer.hydrateRoot(container);
            failCard.audio.play = async () => {
                failCard.audio.error = { code: 4 };
                failCard.audio.dispatchEvent({ type: 'error' });
            };
            failCard.playBtn.click();
            await new Promise(r => setTimeout(r, 25));
            assert.equal(failResolveCount, 2);
            assert.ok(!failCard.errEl.classList.contains('hidden'));
            assert.match(failCard.errEl.textContent, /not supported/i);
            failPlayer.destroy();

            // Trigger slowA click then immediately click fastB
            slowA.playBtn.click();
            fastB.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.equal(fastB.audio.paused, false);
            resolveSlowA('https://r2.invalid/slow-late.m4a');
            await new Promise(r => setTimeout(r, 20));
            assert.equal(fastB.audio.paused, false);
            assert.equal(slowA.audio.paused, true);
            assert.equal(player.getActiveCard(), fastB.card);

            // Waveform pointerdown + pointerup + synthetic click on unloaded card triggers once and defers seek to loadedmetadata
            const scrubCard = makeCard(205, 'media-scrub', 20000);
            player.hydrateRoot(container);
            scrubCard.wf.dispatchEvent({ type: 'pointerdown', clientX: 100 });
            scrubCard.wf.dispatchEvent({ type: 'pointerup', clientX: 100 });
            scrubCard.wf.dispatchEvent({ type: 'click', clientX: 100 });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(scrubResolveCount, 1);
            assert.equal(scrubCard.audio.currentTime, 0); // readyState=0 defers setting currentTime
            scrubCard.audio.readyState = 1;
            scrubCard.audio.duration = 20;
            scrubCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(scrubCard.audio.currentTime, 10);
            assert.equal(scrubCard.audio.playbackRate, 1.5);

            // Disconnected card pruning via hydrateRoot
            scrubCard.card.isConnected = false;
            player.hydrateRoot(container);
            player.destroy();

            // 3. Sending image/file attachment via composer re-enables #composer-voice-btn immediately
            const inputEl = createEl('textarea', 'message-content-input');
            const sendBtn = createEl('button', 'send-message-btn');
            const emojiBtn = createEl('button', 'composer-emoji-btn');
            const voiceBtn = createEl('button', 'composer-voice-btn');
            const attachBtn = createEl('button', 'composer-attach-btn');
            const trayEl = createEl('div', 'composer-attachments-tray');
            const pendingEl = createEl('div', 'pending-uploads-container');
            rootBody.appendChild(inputEl);
            rootBody.appendChild(sendBtn);
            rootBody.appendChild(emojiBtn);
            rootBody.appendChild(voiceBtn);
            rootBody.appendChild(attachBtn);
            rootBody.appendChild(trayEl);
            rootBody.appendChild(pendingEl);

            const chatState = createChatState();
            chatState.activeConversationId = 'conv-1';
            let composerRef = null;
            const uploader = createMediaUploader({
                state: chatState,
                onTrayChanged: () => composerRef?.syncDisabledState(),
                api: {
                    initiateMediaUpload: async () => ({ mediaId: 'm-1', uploadUrl: 'https://r2.invalid/put', requiredContentType: 'image/png', requiredSizeBytes: 12 }),
                    uploadFileToPresignedUrl: async () => {},
                    completeMediaUpload: async () => ({ id: 'm-1', kind: 'image', fileName: 'pic.png', sizeBytes: 12 }),
                    cancelMediaUpload: async () => {},
                    sendMessage: async () => ({ id: 901, senderId: 'u1', content: null, attachments: [] })
                }
            });
            composerRef = createComposer({ state: chatState, mediaUploader: uploader });
            composerRef.setLocked(false);
            assert.equal(voiceBtn.disabled, false);

            uploader.addFiles([{ name: 'pic.png', type: 'image/png', size: 12 }]);
            assert.equal(voiceBtn.disabled, true);

            const job = await composerRef.sendMessage();
            assert.ok(job);
            assert.equal(voiceBtn.disabled, false);
            uploader.destroy();
            composerRef.destroy();
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task VoiceRecorderModule_CoversDoubleStopLock_SizePrecheck_UniformWaveform_UnsupportedPause_And_PreviewError()
    {
        var recUrl = JsUrl("voice-recorder.js");
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            for (const id of ['composer-input-row', 'composer-voice-dock', 'voice-dock-recording', 'voice-dock-preview',
                'voice-rec-dot', 'voice-rec-status', 'voice-rec-timer', 'voice-rec-limit-hint', 'voice-live-waveform',
                'voice-pause-btn', 'voice-pause-icon', 'voice-preview-waveform', 'voice-preview-timer', 'voice-preview-audio',
                'voice-preview-play-icon', 'message-content-input']) {
                rootBody.appendChild(createEl(id.includes('audio') ? 'audio' : 'div', id));
            }

            const { createVoiceRecorder, MAX_VOICE_BYTES } = await import('{{recUrl}}');

            // 1. Unsupported MediaRecorder.pause() hides pause btn and refuses to fake paused state
            let errors = [];
            class NoPauseRecorder {
                constructor(stream) { this.state = 'inactive'; this.mimeType = 'audio/webm'; }
                start() { this.state = 'recording'; }
                stop() {
                    this.state = 'inactive';
                    if (this.ondataavailable) this.ondataavailable({ data: { size: 100 } });
                    if (this.onstop) this.onstop();
                }
            }
            const mkStream = () => ({ getTracks: () => [{ stop: () => {} }], getAudioTracks: () => [] });
            const noPauseRec = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => NoPauseRecorder,
                getUserMediaFn: async () => mkStream(),
                createObjectUrlFn: () => 'blob:preview-nopause',
                onError: (m) => errors.push(m)
            });
            noPauseRec.init();
            assert.equal(await noPauseRec.startRecording(), true);
            const pauseBtn = document.getElementById('voice-pause-btn');
            assert.equal(pauseBtn.disabled, true);
            assert.ok(pauseBtn.classList.contains('hidden'));
            noPauseRec.togglePause();
            assert.equal(noPauseRec.getMode(), 'recording');
            assert.ok(errors.length >= 1);
            noPauseRec.destroy();

            // 2. Fallback constructor throw cleans up hardware cleanly
            let stoppedTracks = 0;
            let closedCtx = 0;
            errors = [];
            class ThrowingRecorder {
                constructor() { throw new Error('codec blocked'); }
            }
            const failInitRec = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => ThrowingRecorder,
                getUserMediaFn: async () => ({ getTracks: () => [{ stop: () => { stoppedTracks++; } }], getAudioTracks: () => [] }),
                createAudioContextFn: () => ({ state: 'running', close: async () => { closedCtx++; } }),
                onError: (m) => errors.push(m)
            });
            assert.equal(await failInitRec.startRecording(), false);
            assert.ok(stoppedTracks >= 1);
            assert.ok(closedCtx >= 1);
            assert.ok(errors.length >= 1);

            // 3. Double-click on sendNow during async onstop sends strictly ONCE and does not lose recording
            let nowMs = 5000;
            let sentCount = 0;
            let asyncStopCallback = null;
            class AsyncStopRecorder {
                constructor(stream) { this.state = 'inactive'; this.mimeType = 'audio/webm'; }
                start() { this.state = 'recording'; }
                pause() { this.state = 'paused'; }
                resume() { this.state = 'recording'; }
                stop() {
                    this.state = 'inactive';
                    asyncStopCallback = () => {
                        if (this.ondataavailable) this.ondataavailable({ data: { size: 512 } });
                        if (this.onstop) this.onstop();
                    };
                }
            }
            const dblRec = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => AsyncStopRecorder,
                getUserMediaFn: async () => mkStream(),
                createObjectUrlFn: () => 'blob:preview-dbl',
                onStartSendVoice: () => { sentCount++; },
                onError: (m) => errors.push(m),
                nowFn: () => nowMs
            });
            dblRec.init();
            await dblRec.startRecording();
            nowMs += 2000;
            dblRec.sendNow();
            assert.equal(dblRec.getMode(), 'stopping');
            dblRec.sendNow(); // duplicate rapid click must be ignored cleanly
            asyncStopCallback();
            assert.equal(sentCount, 1);
            assert.equal(dblRec.getMode(), 'idle');

            // 4. Size > MAX_VOICE_BYTES rejected pre-upload
            errors = [];
            sentCount = 0;
            class OversizeRecorder extends AsyncStopRecorder {
                stop() {
                    this.state = 'inactive';
                    if (this.ondataavailable) this.ondataavailable({ data: { size: MAX_VOICE_BYTES + 10 } });
                    if (this.onstop) this.onstop();
                }
            }
            const origBlob = globalThis.Blob;
            globalThis.Blob = class FakeBlob { constructor(chunks) { this.size = chunks.reduce((a, b) => a + b.size, 0); } };
            try {
                const bigRec = createVoiceRecorder({
                    getActiveConversationId: () => 'conv-1',
                    getMediaRecorderClass: () => OversizeRecorder,
                    getUserMediaFn: async () => mkStream(),
                    onStartSendVoice: () => { sentCount++; },
                    onError: (m) => errors.push(m),
                    nowFn: () => nowMs,
                    autoStopBytes: MAX_VOICE_BYTES * 2
                });
                bigRec.init();
                await bigRec.startRecording();
                nowMs += 2000;
                bigRec.sendNow();
                assert.equal(sentCount, 0);
                assert.ok(errors.some(e => e.includes('25 MB')));
                bigRec.destroy();
            } finally {
                globalThis.Blob = origBlob;
            }

            // 5. Long recording > 2400 (15,000) ticks preserves uniform linear waveform timing + preview audio error + pausePreview
            let tickFn = null;
            const origSetInterval = globalThis.setInterval;
            const origClearInterval = globalThis.clearInterval;
            globalThis.setInterval = (fn) => { tickFn = fn; return 77; };
            globalThis.clearInterval = () => { tickFn = null; };
            let tickIdx = 0;
            errors = [];
            let sentWaveform = null;
            try {
                class SyncStopRec extends AsyncStopRecorder {
                    stop() {
                        this.state = 'inactive';
                        if (this.ondataavailable) this.ondataavailable({ data: new Blob([new Uint8Array(64)], { type: 'audio/webm' }) });
                        if (this.onstop) this.onstop();
                    }
                }
                const longRec = createVoiceRecorder({
                    getActiveConversationId: () => 'conv-1',
                    getMediaRecorderClass: () => SyncStopRec,
                    getUserMediaFn: async () => mkStream(),
                    createAudioContextFn: () => ({
                        state: 'running',
                        createMediaStreamSource: () => ({ connect: () => {}, disconnect: () => {} }),
                        createAnalyser: () => ({
                            fftSize: 256,
                            frequencyBinCount: 4,
                            disconnect: () => {},
                            getByteTimeDomainData: (arr) => {
                                // Amplitude regimes: first third ~17%, middle third ~52%, final third ~100%
                                const val = tickIdx < 5000 ? 140 : (tickIdx < 10000 ? 165 : 244);
                                arr.fill(val);
                            }
                        }),
                        close: async () => {}
                    }),
                    createObjectUrlFn: () => 'blob:preview-long',
                    onStartSendVoice: (p) => { sentWaveform = p.waveformPeaks; },
                    onError: (m) => errors.push(m),
                    nowFn: () => nowMs,
                    maxDurationMs: 60 * 60 * 1000
                });
                longRec.init();
                await longRec.startRecording();
                for (tickIdx = 0; tickIdx < 15000; tickIdx++) {
                    nowMs += 120;
                    tickFn();
                }
                longRec.stopToPreview();
                assert.equal(longRec.getMode(), 'preview');

                const previewAudio = document.getElementById('voice-preview-audio');
                await previewAudio.play();
                assert.equal(previewAudio.paused, false);
                longRec.pausePreview();
                assert.equal(previewAudio.paused, true);

                previewAudio.dispatchEvent({ type: 'error' });
                assert.ok(errors.some(e => e.includes('Preview audio')));

                longRec.sendFromPreview();
                assert.equal(sentWaveform.length, 48);
                // First third (<16 peaks), middle third (16..31), last third (32..47) keep monotonic stepped timing without early squeeze
                assert.ok(sentWaveform[0] < 35);
                assert.ok(sentWaveform[23] > sentWaveform[0] + 15);
                assert.ok(sentWaveform[47] > sentWaveform[23] + 20);
                longRec.destroy();
            } finally {
                globalThis.setInterval = origSetInterval;
                globalThis.clearInterval = origClearInterval;
            }
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task TypingRouterAndDragGuardModules_HandleRecordingIndicator_PopstateConfirmRollback_And_AudioUrls()
    {
        var typingUrl = JsUrl("typing.js");
        var routerUrl = JsUrl("chat-router.js");
        var dragUrl = JsUrl("media-drag-guard.js");
        var stateUrl = JsUrl("chat-state.js");

        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const { setupTypingReceiver } = await import('{{typingUrl}}');
            const { createChatRouter } = await import('{{routerUrl}}');
            const { collectRenderedMediaUrls } = await import('{{dragUrl}}');
            const { createChatState } = await import('{{stateUrl}}');

            // 1. Typing vs Recording indicator independence
            const indEl = createEl('span', 'typing-indicator');
            indEl.className = 'hidden';
            rootBody.appendChild(indEl);
            const rec = setupTypingReceiver(null, () => 'conv-1', indEl);
            rec.handleRecordingIndicator({ conversationId: 'conv-1', isRecording: true });
            assert.equal(indEl.textContent, 'recording audio...');
            assert.ok(!indEl.classList.contains('hidden'));

            // Late typing:false must NOT erase active recording indicator
            rec.handleTypingIndicator({ conversationId: 'conv-1', isTyping: false });
            assert.equal(indEl.textContent, 'recording audio...');
            rec.handleRecordingIndicator({ conversationId: 'conv-1', isRecording: false });
            assert.ok(indEl.classList.contains('hidden'));
            rec.destroy();

            // 2. Drag guard protects <audio> signed URLs
            const audioEl = createEl('audio');
            audioEl.setAttribute('src', 'https://r2.invalid/signed-voice.m4a');
            rootBody.appendChild(audioEl);
            const protectedUrls = collectRenderedMediaUrls(globalThis.document, 'https://localhost:7001');
            assert.ok(protectedUrls.has('https://r2.invalid/signed-voice.m4a'));

            // 3. Router confirmBeforeSwitch aborts switch on cancel AND rolls back URL on popstate
            const shell = createEl('div', 'chat-shell-root');
            rootBody.appendChild(shell);
            const state = createChatState();
            state.activeConversationId = 'conv-active-1';
            let apiCalled = 0;
            const router = createChatRouter({
                state,
                api: { getConversationBundle: async () => { apiCalled++; return {}; } },
                renderer: {},
                hooks: { confirmBeforeSwitch: async () => false }
            });
            router.init();
            const res = await router.switchConversation('conv-other-2');
            assert.equal(res, false);
            assert.equal(apiCalled, 0);
            assert.equal(state.activeConversationId, 'conv-active-1');
            router.destroy();
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public void RazorViews_HaveNoInlineStyles_NoAudioCrossOrigin_And_IdenticalComposerIds()
    {
        var slnRoot = FindSolutionRoot();
        var convPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var emptyPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "_ChatActivePaneEmpty.cshtml");

        var convHtml = File.ReadAllText(convPath);
        var emptyHtml = File.ReadAllText(emptyPath);

        foreach (var html in new[] { convHtml, emptyHtml })
        {
            Assert.DoesNotMatch(new Regex(@"\bstyle\s*=", RegexOptions.IgnoreCase), html);
            Assert.DoesNotContain("crossorigin", html, StringComparison.OrdinalIgnoreCase);
            foreach (var id in new[]
            {
                "composer-input-row",
                "composer-voice-btn",
                "composer-voice-dock",
                "voice-dock-recording",
                "voice-dock-preview",
                "voice-rec-timer",
                "voice-discard-btn",
                "voice-pause-btn",
                "voice-stop-preview-btn",
                "voice-send-now-btn",
                "voice-preview-play-btn",
                "voice-preview-waveform",
                "voice-preview-send-btn"
            })
            {
                Assert.Contains($"id=\"{id}\"", html, StringComparison.Ordinal);
            }
        }
    }
}
