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
                _src: '',
                _error: null,
                get src() { return this._src; },
                set src(v) { this._src = String(v ?? ''); if (tag.toUpperCase() === 'AUDIO') { this._error = null; } },
                get error() { return this._error; },
                set error(v) { this._error = v; },
                currentTime: 0,
                duration: Infinity,
                paused: true,
                ended: false,
                playbackRate: 1,
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
                start() { this.state = 'recording'; if (typeof this.onstart === 'function') this.onstart(); }
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
            v1.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
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

            // 1. Expired presigned GET URL emitting error code 4 + rejecting first play() with NotSupportedError
            // keeps ownership while bypassCache refresh is pending, then recovers cleanly
            const c1 = makeCard(201, 'media-retry', 10000);
            const c1Calls = [];
            let c1ShouldFailOnce = true;
            let resolveC1Refresh = null;
            c1.audio.play = async () => {
                if (c1ShouldFailOnce) {
                    c1ShouldFailOnce = false;
                    c1.audio.error = { code: 4 };
                    c1.audio.dispatchEvent({ type: 'error' });
                    const err = new Error('NotSupportedError');
                    err.name = 'NotSupportedError';
                    throw err;
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
                        if (opts?.bypassCache) {
                            return await new Promise(r => { resolveC1Refresh = r; });
                        }
                        return 'https://r2.invalid/expired.m4a';
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
            // Refresh is still pending after play() rejection catch has run: no error shown, card still owns playback
            assert.deepEqual(c1Calls, [false, true]);
            assert.ok(c1.errEl.classList.contains('hidden'));
            assert.equal(player.getActiveCard(), c1.card);
            resolveC1Refresh('https://r2.invalid/fresh.m4a');
            await new Promise(r => setTimeout(r, 25));
            assert.equal(c1.audio.paused, false);
            assert.ok(c1.errEl.classList.contains('hidden'));

            // If retried URL also fails with code 4 + NotSupportedError -> surfaces format error cleanly without loop
            const failCard = makeCard(204, 'media-fail', 8000);
            let failResolveCount = 0;
            const failPlayer = createVoicePlayer({
                resolveUrlFn: async () => { failResolveCount++; return 'https://r2.invalid/bad.m4a'; }
            });
            failPlayer.hydrateRoot(container);
            failCard.audio.play = async () => {
                failCard.audio.error = { code: 4 };
                failCard.audio.dispatchEvent({ type: 'error' });
                const err = new Error('NotSupportedError');
                err.name = 'NotSupportedError';
                throw err;
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

            // R2-12: Entering voice lock while dropzone overlay is visible hides overlay on dragover
            if (!document.getElementById('chat-active-pane')) {
                rootBody.appendChild(createEl('div', 'chat-active-pane'));
            }
            const activePane = document.getElementById('chat-active-pane');
            const dzOverlay = createEl('div', 'chat-dropzone-overlay');
            dzOverlay.className = 'hidden';
            rootBody.appendChild(dzOverlay);
            let voiceLocked = false;
            const dzUploader = createMediaUploader({
                state: chatState,
                isVoiceDockActive: () => voiceLocked
            });
            dzUploader.init();
            dzUploader.setLocked(false);
            activePane.dispatchEvent({
                type: 'dragenter',
                dataTransfer: { types: ['Files'] },
                preventDefault() {},
                stopPropagation() {}
            });
            assert.equal(dzOverlay.classList.contains('hidden'), false);
            voiceLocked = true;
            activePane.dispatchEvent({
                type: 'dragover',
                dataTransfer: { types: ['Files'] },
                preventDefault() {},
                stopPropagation() {}
            });
            assert.equal(dzOverlay.classList.contains('hidden'), true);
            dzUploader.destroy();

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
                start() { this.state = 'recording'; if (typeof this.onstart === 'function') this.onstart(); }
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
                start() { this.state = 'recording'; if (typeof this.onstart === 'function') this.onstart(); }
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
    public async Task VoicePlayerModule_HandlesOwnershipRaces_ScrubCancel_PendingSeek_And_Rehydration()
    {
        var playerUrl = JsUrl("voice-player.js");
        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const { createVoiceBubbleElement, createVoicePlayer } = await import('{{playerUrl}}');
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
                    timeEl: card.querySelector('[data-voice-time]'),
                    errEl: card.querySelector('[data-voice-error]')
                };
            }

            let resolveSlowFirst = null;
            let resolveSlowSecond = null;
            let resolveStaleSrc = null;
            let staleCallCount = 0;
            let stoppedCardRefreshes = 0;

            const player = createVoicePlayer({
                resolveUrlFn: async (convId, msgId, mediaId, opts) => {
                    if (mediaId === 'm-double') {
                        staleCallCount++;
                        if (staleCallCount === 1) return await new Promise(r => { resolveSlowFirst = r; });
                        return await new Promise(r => { resolveSlowSecond = r; });
                    }
                    if (mediaId === 'm-stale-src') {
                        staleCallCount++;
                        if (staleCallCount === 3) return await new Promise(r => { resolveStaleSrc = r; });
                        return 'https://r2.invalid/fast-second.m4a';
                    }
                    if (mediaId === 'm-stopped-err' && opts?.bypassCache) {
                        stoppedCardRefreshes++;
                    }
                    return `https://r2.invalid/${mediaId}.m4a`;
                }
            });
            player.hydrateRoot(container);

            // 1. Rapid double-click on same card while URL lookup is pending: first attempt must NOT clear second attempt
            const dbl = makeCard(301, 'm-double', 10000);
            player.hydrateRoot(container);
            dbl.playBtn.click();
            dbl.playBtn.click();
            resolveSlowSecond('https://r2.invalid/dbl-second.m4a');
            await new Promise(r => setTimeout(r, 15));
            assert.equal(player.getActiveCard(), dbl.card);
            assert.equal(dbl.audio.paused, false);
            resolveSlowFirst('https://r2.invalid/dbl-first.m4a');
            await new Promise(r => setTimeout(r, 15));
            assert.equal(player.getActiveCard(), dbl.card);
            assert.equal(dbl.audio.src, 'https://r2.invalid/dbl-second.m4a');
            assert.equal(dbl.audio.paused, false);
            player.stopAllPlayback();

            // 2. Late error on stopped/idle card must NOT refresh URL or restart playback
            const stoppedCard = makeCard(302, 'm-stopped-err', 10000);
            player.hydrateRoot(container);
            stoppedCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            player.stopAllPlayback();
            assert.equal(player.getActiveCard(), null);
            stoppedCard.audio.error = { code: 4 };
            stoppedCard.audio.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(stoppedCardRefreshes, 0);
            assert.equal(player.getActiveCard(), null);
            assert.equal(stoppedCard.audio.paused, true);

            // 3. Stale playing event on paused card A while card B is active must NOT pause card B
            const cardA = makeCard(303, 'm-card-a', 10000);
            const cardB = makeCard(304, 'm-card-b', 10000);
            player.hydrateRoot(container);
            cardA.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            cardB.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.equal(player.getActiveCard(), cardB.card);
            assert.equal(cardB.audio.paused, false);
            // Simulate queued 'playing' task firing on cardA even though cardA is paused
            cardA.audio.dispatchEvent({ type: 'playing' });
            assert.equal(player.getActiveCard(), cardB.card);
            assert.equal(cardB.audio.paused, false);
            player.stopAllPlayback();

            // 4. Stale resolveUrlFn must NOT overwrite audio.src once another card/request took ownership
            const staleSrcCard = makeCard(305, 'm-stale-src', 10000);
            player.hydrateRoot(container);
            staleSrcCard.playBtn.click(); // call #3 (slow)
            cardB.playBtn.click(); // switches ownership to cardB
            await new Promise(r => setTimeout(r, 15));
            resolveStaleSrc('https://r2.invalid/should-not-assign.m4a');
            await new Promise(r => setTimeout(r, 15));
            assert.equal(staleSrcCard.audio.src, '');
            assert.equal(player.getActiveCard(), cardB.card);
            player.stopAllPlayback();

            // 5. pointercancel on waveform restores visual progress to actual audio.currentTime
            const scrubCard = makeCard(306, 'm-scrub-cancel', 20000);
            player.hydrateRoot(container);
            scrubCard.audio.src = 'https://r2.invalid/m-scrub-cancel.m4a';
            scrubCard.audio.readyState = 1;
            scrubCard.audio.duration = 20;
            scrubCard.audio.currentTime = 2;
            scrubCard.wf.dispatchEvent({ type: 'pointerdown', clientX: 100 }); // 50% -> 10s
            assert.equal(scrubCard.wf.getAttribute('aria-valuenow'), '10000');
            scrubCard.wf.dispatchEvent({ type: 'pointercancel' });
            assert.equal(scrubCard.wf.getAttribute('aria-valuenow'), '2000');

            // 6. Second seek while src is set and readyState < 1 updates pendingSeekSec to latest target
            const preMetaCard = makeCard(307, 'm-pre-meta', 20000);
            player.hydrateRoot(container);
            preMetaCard.wf.dispatchEvent({ type: 'pointerdown', clientX: 100 }); // 10s
            preMetaCard.wf.dispatchEvent({ type: 'pointerup', clientX: 100 });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(preMetaCard.audio.readyState, 0);
            preMetaCard.wf.dispatchEvent({ type: 'pointerdown', clientX: 150 }); // 15s
            preMetaCard.wf.dispatchEvent({ type: 'pointerup', clientX: 150 });
            preMetaCard.audio.readyState = 1;
            preMetaCard.audio.duration = 20;
            preMetaCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(preMetaCard.audio.currentTime, 15);
            player.stopAllPlayback();

            // 7. Detached-then-attached re-hydration re-registers card in allBoundCards
            const detachedCard = makeCard(308, 'm-rehydrate', 10000);
            detachedCard.card.isConnected = false;
            player.hydrateRoot(detachedCard.row); // bound while detached
            const otherRoot = createEl('div', 'other-root');
            player.hydrateRoot(otherRoot); // prunes detachedCard from allBoundCards
            detachedCard.card.isConnected = true;
            player.hydrateRoot(detachedCard.row); // re-registers already-bound attached card
            cardB.speedBtn.click(); // cycles 1x -> 1.5x
            assert.equal(detachedCard.speedBtn.textContent, '1.5x');

            // 8. R2-3 / R3-8 / R3-9 / R3-10: Interrupted media error on paused/replayed card triggers bypassCache URL refresh,
            // relies on browser src-reset to clear getter-only audio.error, preserves resume position unless explicit seek or ended,
            // and reports refresh failures as "Audio unavailable"
            const retryErrCalls = [];
            let refreshShouldFail = false;
            const retryErrPlayer = createVoicePlayer({
                resolveUrlFn: async (convId, msgId, mediaId, opts) => {
                    retryErrCalls.push(Boolean(opts?.bypassCache));
                    if (refreshShouldFail && opts?.bypassCache) return null;
                    return opts?.bypassCache ? 'https://r2.invalid/refreshed.m4a' : 'https://r2.invalid/initial.m4a';
                }
            });
            const errRetryCard = makeCard(309, 'm-err-retry', 30000);
            let retrySrc = '';
            let retryBackingErr = null;
            Object.defineProperty(errRetryCard.audio, 'error', {
                get: () => retryBackingErr,
                configurable: true
            });
            Object.defineProperty(errRetryCard.audio, 'src', {
                get: () => retrySrc,
                set: (v) => {
                    retrySrc = String(v ?? '');
                    retryBackingErr = null;
                    errRetryCard.audio.currentTime = 0;
                    errRetryCard.audio.readyState = 0;
                },
                configurable: true
            });
            retryErrPlayer.hydrateRoot(container);
            errRetryCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.equal(errRetryCard.audio.paused, false);
            errRetryCard.audio.readyState = 1;
            errRetryCard.audio.duration = 30;
            errRetryCard.audio.currentTime = 12;
            errRetryCard.audio.pause();
            retryBackingErr = { code: 2 };
            errRetryCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.deepEqual(retryErrCalls, [false, true]);
            assert.equal(errRetryCard.audio.error, null);
            assert.equal(errRetryCard.audio.src, 'https://r2.invalid/refreshed.m4a');
            assert.equal(errRetryCard.audio.paused, false);
            errRetryCard.audio.readyState = 1;
            errRetryCard.audio.duration = 30;
            errRetryCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(errRetryCard.audio.currentTime, 12);

            // R3-8: Explicit pending seek takes precedence over saved currentTime when replaying after error
            errRetryCard.audio.pause();
            errRetryCard.audio.currentTime = 8;
            errRetryCard.audio.readyState = 0;
            retryBackingErr = { code: 2 };
            errRetryCard.wf.dispatchEvent({ type: 'pointerdown', clientX: 150 }); // 75% -> 22.5s pending seek
            errRetryCard.wf.dispatchEvent({ type: 'pointerup', clientX: 150 });
            errRetryCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            errRetryCard.audio.readyState = 1;
            errRetryCard.audio.duration = 30;
            errRetryCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(errRetryCard.audio.currentTime, 22.5);

            // R3-8: Ended clip replay resets to 0 instead of saving EOF currentTime
            errRetryCard.audio.pause();
            errRetryCard.audio.ended = true;
            errRetryCard.audio.currentTime = 30;
            retryBackingErr = { code: 2 };
            errRetryCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            errRetryCard.audio.readyState = 1;
            errRetryCard.audio.duration = 30;
            errRetryCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(errRetryCard.audio.currentTime, 0);

            // R3-9: Failed URL refresh during error replay shows "Audio unavailable" (not format unsupported)
            errRetryCard.audio.pause();
            refreshShouldFail = true;
            retryBackingErr = { code: 2 };
            errRetryCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.ok(!errRetryCard.errEl.classList.contains('hidden'));
            assert.equal(errRetryCard.errEl.textContent, 'Audio unavailable');
            retryErrPlayer.destroy();

            // R4-6: Seeking while URL refresh is in-flight (with old readyState >= 1) queues seek for replacement source
            // in BOTH replay refresh and automatic error-handler refresh (never clobbered by savedTime)
            let resolveInFlightRefresh = null;
            let inFlightRefreshCount = 0;
            const seekRefreshPlayer = createVoicePlayer({
                resolveUrlFn: async (convId, msgId, mediaId, opts) => {
                    inFlightRefreshCount++;
                    if (!opts?.bypassCache) return 'https://r2.invalid/old-src.m4a';
                    return await new Promise(r => { resolveInFlightRefresh = r; });
                }
            });
            const seekRefreshCard = makeCard(312, 'm-seek-refresh', 30000);
            let srSrc = '';
            let srErr = null;
            Object.defineProperty(seekRefreshCard.audio, 'error', { get: () => srErr, configurable: true });
            Object.defineProperty(seekRefreshCard.audio, 'src', {
                get: () => srSrc,
                set: (v) => {
                    srSrc = String(v ?? '');
                    srErr = null;
                    seekRefreshCard.audio.currentTime = 0;
                    seekRefreshCard.audio.readyState = 0;
                },
                configurable: true
            });
            seekRefreshPlayer.hydrateRoot(container);
            seekRefreshCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            seekRefreshCard.audio.readyState = 1;
            seekRefreshCard.audio.duration = 30;
            seekRefreshCard.audio.currentTime = 12;
            seekRefreshCard.audio.pause();

            // (a) Replay refresh in flight while old readyState === 1; user seeks to 22.5s before new URL arrives
            srErr = { code: 2 };
            seekRefreshCard.playBtn.click();
            await Promise.resolve();
            assert.equal(seekRefreshCard.audio.readyState, 1); // old source still mounted
            seekRefreshCard.wf.dispatchEvent({ type: 'pointerdown', clientX: 150 }); // 75% -> 22.5s
            seekRefreshCard.wf.dispatchEvent({ type: 'pointerup', clientX: 150 });
            resolveInFlightRefresh('https://r2.invalid/new-src-1.m4a');
            await new Promise(r => setTimeout(r, 15));
            seekRefreshCard.audio.readyState = 1;
            seekRefreshCard.audio.duration = 30;
            seekRefreshCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(seekRefreshCard.audio.currentTime, 22.5);

            // (b) Automatic error-handler refresh in flight while playing at 12s; user seeks to 22.5s before refresh finishes
            seekRefreshCard.audio.currentTime = 12;
            srErr = { code: 4 };
            seekRefreshCard.audio.dispatchEvent({ type: 'error' });
            await Promise.resolve();
            assert.equal(seekRefreshCard.audio.readyState, 1);
            seekRefreshCard.wf.dispatchEvent({ type: 'pointerdown', clientX: 150 }); // 22.5s
            seekRefreshCard.wf.dispatchEvent({ type: 'pointerup', clientX: 150 });
            resolveInFlightRefresh('https://r2.invalid/new-src-2.m4a');
            await new Promise(r => setTimeout(r, 15));
            seekRefreshCard.audio.readyState = 1;
            seekRefreshCard.audio.duration = 30;
            seekRefreshCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(seekRefreshCard.audio.currentTime, 22.5);
            seekRefreshPlayer.destroy();

            // 9. R2-13: Repeated ArrowRight on waveform before readyState>=1 steps from pendingSeekSec instead of resetting to 0
            const keySeekCard = makeCard(310, 'm-key-seek', 20000);
            player.hydrateRoot(container);
            keySeekCard.wf.dispatchEvent({ key: 'ArrowRight', type: 'keydown', preventDefault() {} });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(keySeekCard.audio.readyState, 0);
            assert.equal(keySeekCard.wf.getAttribute('aria-valuenow'), '5000');
            keySeekCard.wf.dispatchEvent({ key: 'ArrowRight', type: 'keydown', preventDefault() {} });
            assert.equal(keySeekCard.wf.getAttribute('aria-valuenow'), '10000');
            keySeekCard.audio.readyState = 1;
            keySeekCard.audio.duration = 20;
            keySeekCard.audio.dispatchEvent({ type: 'loadedmetadata' });
            assert.equal(keySeekCard.audio.currentTime, 10);

            // 10. R2-14: Queued 'pause' event arriving after play() resumed (!audio.paused) leaves pause icon intact
            const pauseRaceCard = makeCard(311, 'm-pause-race', 10000);
            player.hydrateRoot(container);
            pauseRaceCard.playBtn.click();
            await new Promise(r => setTimeout(r, 15));
            assert.equal(pauseRaceCard.audio.paused, false);
            const pauseRaceIcon = pauseRaceCard.card.querySelector('[data-voice-play-icon]');
            assert.equal(pauseRaceIcon.textContent, 'pause');
            pauseRaceCard.audio.dispatchEvent({ type: 'pause' }); // stale task while still playing
            assert.equal(pauseRaceIcon.textContent, 'pause');
            assert.equal(player.getActiveCard(), pauseRaceCard.card);

            player.destroy();
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task VoiceRecorderModule_HandlesStaleOnStop_PendingAudioContextCancel_ReRecordPreviewPreservation_And_MonotonicZeroClock()
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

            const { createVoiceRecorder } = await import('{{recUrl}}');

            // 1. Stale MediaRecorder.onstop after discard + restart must NOT shut down new recording
            let recInstances = [];
            let track1Stopped = 0;
            let track2Stopped = 0;
            let ctx1Closed = 0;
            let ctx2Closed = 0;
            let recIndex = 0;
            let nowMs = 0; // Also tests timestamp === 0 (#16)!

            class QueuedStopRecorder {
                constructor(stream) {
                    this.stream = stream;
                    this.state = 'inactive';
                    this.mimeType = 'audio/webm';
                    this.idx = ++recIndex;
                    recInstances.push(this);
                }
                start() { this.state = 'recording'; if (typeof this.onstart === 'function') this.onstart(); }
                stop() { this.state = 'inactive'; } // leaves onstop to be invoked asynchronously later
            }

            let micCall = 0;
            const rec1 = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => QueuedStopRecorder,
                getUserMediaFn: async () => {
                    micCall++;
                    const idx = micCall;
                    return {
                        getTracks: () => [{ stop: () => { if (idx === 1) track1Stopped++; else track2Stopped++; } }],
                        getAudioTracks: () => []
                    };
                },
                createAudioContextFn: () => {
                    const idx = micCall + 1;
                    return {
                        state: 'running',
                        close: async () => { if (idx === 1) ctx1Closed++; else ctx2Closed++; }
                    };
                },
                createObjectUrlFn: () => 'blob:preview-1',
                nowFn: () => nowMs
            });
            rec1.init();

            assert.equal(await rec1.startRecording(), true); // activeSegmentStartMs === 0!
            nowMs = 1500;
            rec1.stopToPreview();
            const firstRecorder = recInstances[0];
            const staleStopFn = firstRecorder.onstop;
            rec1.discard();
            assert.equal(await rec1.startRecording(), true);
            assert.equal(rec1.getMode(), 'recording');
            // Now fire the stale onstop callback captured from the first recording:
            if (typeof staleStopFn === 'function') staleStopFn();
            assert.equal(rec1.getMode(), 'recording');
            assert.equal(track2Stopped, 0);
            assert.equal(ctx2Closed, 0);
            rec1.destroy();

            // 2. Pending AudioContext is closed immediately when start is canceled while getUserMedia is unanswered
            let resolvePendingMic = null;
            let pendingMicTracksStopped = 0;
            let pendingCtxClosed = 0;
            const rec2 = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => QueuedStopRecorder,
                getUserMediaFn: () => new Promise(r => { resolvePendingMic = r; }),
                createAudioContextFn: () => ({
                    state: 'running',
                    close: async () => { pendingCtxClosed++; }
                })
            });
            rec2.init();
            const startPromise = rec2.startRecording();
            rec2.forceAbortAndWipe();
            assert.equal(pendingCtxClosed, 1);
            resolvePendingMic({
                getTracks: () => [{ stop: () => { pendingMicTracksStopped++; } }],
                getAudioTracks: () => []
            });
            assert.equal(await startPromise, false);
            assert.equal(pendingMicTracksStopped, 1);
            rec2.destroy();

            // 3. Re-record from preview keeps preview usable while permission is pending & on denial,
            // and replaces preview only once the replacement recording actually starts
            let reRecordReject = false;
            let reRecordFirst = true;
            let resolveSecondMic = null;
            const sentVoices = [];
            class SyncPreviewRec {
                constructor() { this.state = 'inactive'; this.mimeType = 'audio/webm'; }
                start() { this.state = 'recording'; if (typeof this.onstart === 'function') this.onstart(); }
                stop() {
                    this.state = 'inactive';
                    if (this.ondataavailable) this.ondataavailable({ data: new Blob([new Uint8Array([1, 2, 3])], { type: 'audio/webm' }) });
                    if (this.onstop) this.onstop();
                }
            }
            nowMs = 0;
            const rec3 = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => SyncPreviewRec,
                getUserMediaFn: () => {
                    if (reRecordFirst) {
                        reRecordFirst = false;
                        return Promise.resolve({ getTracks: () => [{ stop: () => {} }], getAudioTracks: () => [] });
                    }
                    if (reRecordReject) {
                        const err = new Error('Denied');
                        err.name = 'NotAllowedError';
                        return Promise.reject(err);
                    }
                    return new Promise(r => { resolveSecondMic = r; });
                },
                createObjectUrlFn: () => 'blob:preview-keep',
                onStartSendVoice: (p) => { sentVoices.push(p); return p; },
                nowFn: () => nowMs
            });
            rec3.init();
            assert.equal(await rec3.startRecording(), true);
            nowMs = 1200; // started at 0 -> duration 1200ms!
            rec3.stopToPreview();
            assert.equal(rec3.getMode(), 'preview');

            // R2-15: Re-record rejected -> original preview stays intact and is immediately sendable via sendFromPreview()
            reRecordReject = true;
            assert.equal(await rec3.startRecording(), false);
            assert.equal(rec3.getMode(), 'preview');
            rec3.sendFromPreview();
            assert.equal(sentVoices.length, 1);
            assert.equal(rec3.getMode(), 'idle');
            rec3.destroy();

            // R2-15 / R3-1 / R3-11 / R2-4 / R2-10: Deferred MediaRecorder commit on onstart, pre-start failure recovery,
            // mid-startup sendFromPreview cancellation, accessible label sync, and distinct replacement duration + bytes
            if (!document.getElementById('voice-preview-play-btn')) {
                rootBody.appendChild(createEl('button', 'voice-preview-play-btn'));
            }
            const previewPlayBtn = document.getElementById('voice-preview-play-btn');
            const previewPlayIcon = document.getElementById('voice-preview-play-icon');
            const previewAudioEl = document.getElementById('voice-preview-audio');

            let rec4Phase = 0;
            let resolveRec4SecondMic = null;
            let rec4FailStart = false;
            let rec4DeferOnStart = false;
            let rec4ChunkBytes = [1, 2, 3];
            let rec4RepTracksStopped = 0;
            let rec4RepCtxClosed = 0;
            const rec4Instances = [];
            const rec4Sent = [];
            const revokedUrls = [];
            let blobUrlSeq = 0;
            class Rec4Recorder {
                constructor() { this.state = 'inactive'; this.mimeType = 'audio/webm'; rec4Instances.push(this); }
                start() {
                    if (rec4FailStart) throw new Error('start blocked');
                    this.state = 'recording';
                    if (!rec4DeferOnStart && typeof this.onstart === 'function') this.onstart();
                }
                stop() {
                    this.state = 'inactive';
                    if (this.ondataavailable) this.ondataavailable({ data: new Blob([new Uint8Array(rec4ChunkBytes)], { type: 'audio/webm' }) });
                    if (this.onstop) this.onstop();
                }
            }
            const rec4 = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => Rec4Recorder,
                getUserMediaFn: async () => {
                    rec4Phase++;
                    if (rec4Phase === 1) return { getTracks: () => [{ stop: () => {} }], getAudioTracks: () => [] };
                    return new Promise(r => { resolveRec4SecondMic = r; });
                },
                createAudioContextFn: () => ({
                    state: 'running',
                    close: async () => { if (rec4Phase > 1) rec4RepCtxClosed++; }
                }),
                createObjectUrlFn: () => `blob:rec4-${++blobUrlSeq}`,
                revokeObjectUrlFn: (u) => revokedUrls.push(u),
                nowFn: () => nowMs,
                onStartSendVoice: (payload) => rec4Sent.push(payload)
            });
            rec4.init();
            nowMs = 10000;
            assert.equal(await rec4.startRecording(), true);
            nowMs = 11200; // 1200ms original duration
            rec4.stopToPreview();
            assert.equal(rec4.getMode(), 'preview');

            // R2-10: preview play button toggles icon + accessible labels/title on play, pause, and ended
            assert.equal(previewPlayIcon.textContent, 'play_arrow');
            assert.match(previewPlayBtn.getAttribute('aria-label') || '', /play/i);
            await previewAudioEl.play();
            assert.equal(previewAudioEl.paused, false);
            assert.equal(previewPlayIcon.textContent, 'pause');
            assert.match(previewPlayBtn.getAttribute('aria-label') || '', /pause/i);
            assert.match((previewPlayBtn.getAttribute('title') || previewPlayBtn.title || ''), /pause/i);
            rec4.pausePreview();
            assert.equal(previewPlayIcon.textContent, 'play_arrow');
            assert.match(previewPlayBtn.getAttribute('aria-label') || '', /play/i);
            await previewAudioEl.play();
            previewAudioEl.dispatchEvent({ type: 'ended' });
            assert.equal(previewPlayIcon.textContent, 'play_arrow');
            assert.match(previewPlayBtn.getAttribute('aria-label') || '', /play/i);

            // R2-4: synchronous MediaRecorder.start() throw keeps original preview blob & UI intact
            rec4FailStart = true;
            const rec4FailPromise = rec4.startRecording();
            resolveRec4SecondMic({ getTracks: () => [{ stop: () => {} }], getAudioTracks: () => [] });
            assert.equal(await rec4FailPromise, false);
            assert.equal(rec4.getMode(), 'preview');
            assert.ok(!revokedUrls.includes('blob:rec4-1'));
            rec4FailStart = false;

            // R3-1 / R4-8: async onerror BEFORE MediaRecorder fires start preserves original preview & closes THIS attempt's AudioContext
            rec4DeferOnStart = true;
            const rec4AsyncErrPromise = rec4.startRecording();
            resolveRec4SecondMic({ getTracks: () => [{ stop: () => { rec4RepTracksStopped++; } }], getAudioTracks: () => [] });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(rec4.getMode(), 'preview');
            assert.ok(!revokedUrls.includes('blob:rec4-1'));
            const ctxClosedBeforeAsyncErr = rec4RepCtxClosed;
            rec4Instances[rec4Instances.length - 1].onerror({ error: new Error('async capture failure') });
            assert.equal(await rec4AsyncErrPromise, false);
            assert.equal(rec4.getMode(), 'preview');
            assert.ok(!revokedUrls.includes('blob:rec4-1'));
            assert.ok(rec4RepTracksStopped >= 1);
            assert.equal(rec4RepCtxClosed, ctxClosedBeforeAsyncErr + 1);

            // R3-1 / R4-8: async onstop WITHOUT onstart settles cleanly without hanging or clobbering preview and closes its AudioContext
            const rec4NoStartStopPromise = rec4.startRecording();
            resolveRec4SecondMic({ getTracks: () => [{ stop: () => {} }], getAudioTracks: () => [] });
            await new Promise(r => setTimeout(r, 10));
            const ctxClosedBeforeNoStart = rec4RepCtxClosed;
            rec4Instances[rec4Instances.length - 1].onstop();
            assert.equal(await rec4NoStartStopPromise, false);
            assert.equal(rec4.getMode(), 'preview');
            assert.ok(!revokedUrls.includes('blob:rec4-1'));
            assert.equal(rec4RepCtxClosed, ctxClosedBeforeNoStart + 1);

            // R3-1 + R3-11: preview stays intact while awaiting onstart, then commits replacement on onstart with distinct duration & bytes
            const rec4PendingPromise = rec4.startRecording();
            assert.equal(rec4.getMode(), 'preview');
            resolveRec4SecondMic({ getTracks: () => [{ stop: () => {} }], getAudioTracks: () => [] });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(rec4.getMode(), 'preview');
            assert.ok(!revokedUrls.includes('blob:rec4-1'));
            rec4Instances[rec4Instances.length - 1].onstart();
            assert.equal(await rec4PendingPromise, true);
            assert.equal(rec4.getMode(), 'recording');
            assert.ok(revokedUrls.includes('blob:rec4-1'));
            nowMs = 13500; // 2300ms replacement duration (distinct from 1200ms original)
            rec4ChunkBytes = [9, 8, 7, 6, 5];
            rec4.sendNow();
            assert.equal(rec4Sent.length, 1);
            assert.equal(rec4Sent[0].durationMs, 2300);
            assert.equal(rec4Sent[0].blob.size, 5);
            assert.deepEqual([...new Uint8Array(await rec4Sent[0].blob.arrayBuffer())], [9, 8, 7, 6, 5]);
            assert.equal(previewPlayIcon.textContent, 'play_arrow');
            rec4.destroy();
            rec4DeferOnStart = false;

            // R4-7: Delayed onstart delivery (400ms after start()) still uses capture-based timing (700ms >= 500ms min) while gating UI on onstart
            const rec6Sent = [];
            let rec6Instance = null;
            class DelayedStartRecorder {
                constructor() { this.state = 'inactive'; this.mimeType = 'audio/webm'; rec6Instance = this; }
                start() { this.state = 'recording'; }
                stop() {
                    this.state = 'inactive';
                    if (this.ondataavailable) this.ondataavailable({ data: new Blob([new Uint8Array([7, 8, 9])], { type: 'audio/webm' }) });
                    if (this.onstop) this.onstop();
                }
            }
            const rec6 = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => DelayedStartRecorder,
                getUserMediaFn: async () => ({ getTracks: () => [{ stop: () => {} }], getAudioTracks: () => [] }),
                nowFn: () => nowMs,
                onStartSendVoice: (p) => rec6Sent.push(p)
            });
            rec6.init();
            nowMs = 30000;
            const rec6StartP = rec6.startRecording();
            await new Promise(r => setTimeout(r, 10));
            assert.equal(rec6.getMode(), 'idle'); // UI remains gated until onstart fires
            nowMs = 30400; // 400ms delayed onstart delivery
            rec6Instance.onstart();
            assert.equal(await rec6StartP, true);
            assert.equal(rec6.getMode(), 'recording');
            nowMs = 30700; // 700ms total capture (300ms post-onstart)
            rec6.sendNow();
            assert.equal(rec6Sent.length, 1);
            assert.equal(rec6Sent[0].durationMs, 700);
            rec6.destroy();

            // R3-1: Clicking Send from preview while replacement MediaRecorder.start() is still awaiting onstart cleanly cancels startup and sends original clip
            let rec5Phase = 0;
            let rec5Defer = false;
            let rec5RepTracksStopped = 0;
            const rec5Sent = [];
            class Rec5Recorder {
                constructor() { this.state = 'inactive'; this.mimeType = 'audio/webm'; }
                start() {
                    this.state = 'recording';
                    if (!rec5Defer && typeof this.onstart === 'function') this.onstart();
                }
                stop() {
                    this.state = 'inactive';
                    if (this.ondataavailable) this.ondataavailable({ data: new Blob([new Uint8Array([4, 5, 6])], { type: 'audio/webm' }) });
                    if (this.onstop) this.onstop();
                }
            }
            const rec5 = createVoiceRecorder({
                getActiveConversationId: () => 'conv-1',
                getMediaRecorderClass: () => Rec5Recorder,
                getUserMediaFn: async () => {
                    rec5Phase++;
                    return { getTracks: () => [{ stop: () => { if (rec5Phase > 1) rec5RepTracksStopped++; } }], getAudioTracks: () => [] };
                },
                createObjectUrlFn: () => 'blob:rec5-orig',
                nowFn: () => nowMs,
                onStartSendVoice: (p) => rec5Sent.push(p)
            });
            rec5.init();
            nowMs = 20000;
            assert.equal(await rec5.startRecording(), true);
            nowMs = 21500;
            rec5.stopToPreview();
            rec5Defer = true;
            const rec5PendingStart = rec5.startRecording();
            await new Promise(r => setTimeout(r, 10));
            rec5.sendFromPreview();
            assert.equal(await rec5PendingStart, false);
            assert.equal(rec5Sent.length, 1);
            assert.equal(rec5Sent[0].durationMs, 1500);
            assert.ok(rec5RepTracksStopped >= 1);
            assert.equal(rec5.getMode(), 'idle');
            rec5.destroy();
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public async Task VoiceIntegration_PreservesTextDraft_CancelsDropWhenVoiceLocked_And_IgnoresWaveformContextMenu()
    {
        var uploaderUrl = JsUrl("media-uploader.js");
        var stateUrl = JsUrl("chat-state.js");
        var actionsUrl = JsUrl("message-actions.js");
        var shellCode = File.ReadAllText(Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-shell.js"));

        var script = $$"""
            import assert from 'node:assert/strict';
            {{MockDomPrelude}}

            const { createMediaUploader } = await import('{{uploaderUrl}}');
            const { createChatState } = await import('{{stateUrl}}');
            const { setupMessageActions } = await import('{{actionsUrl}}');

            globalThis.XMLHttpRequest = class MockXhr {
                constructor() { this.upload = {}; this.status = 200; }
                open() {}
                setRequestHeader() {}
                send() { queueMicrotask(() => { if (this.onload) this.onload(); }); }
                abort() {}
            };

            // 1. Voice upload job passes { isVoice: true } while image upload passes { isVoice: false }
            const state = createChatState();
            state.activeConversationId = 'conv-a';
            state.setDraft('conv-a', 'Keep my unsent text draft');

            let sentMetas = [];
            const uploader = createMediaUploader({
                state,
                isVoiceDockActive: () => true,
                onMessageSent: (convId, msg, meta) => {
                    sentMetas.push({ convId, meta });
                    if (!meta?.isVoice) {
                        state.deleteDraft(convId);
                    }
                },
                api: {
                    initiateMediaUpload: async () => ({ mediaId: 'm-v1', uploadUrl: 'https://r2.invalid/put', requiredContentType: 'audio/webm', requiredSizeBytes: 8 }),
                    completeMediaUpload: async () => ({ id: 'm-v1', kind: 'voice', fileName: 'v.webm', sizeBytes: 8 }),
                    cancelMediaUpload: async () => {},
                    sendMessage: async () => ({ id: 1001, senderId: 'u1', content: null, attachments: [] })
                }
            });

            // Simulate switching to conv-b before voice upload completes
            const voiceJob = uploader.startVoiceUploadJob({
                conversationId: 'conv-a',
                blob: new Blob([new Uint8Array([1, 2, 3, 4])], { type: 'audio/webm' }),
                fileName: 'voice-message.webm',
                contentType: 'audio/webm',
                durationMs: 2500,
                waveformPeaks: [10, 40, 80, 30]
            });
            state.activeConversationId = 'conv-b';
            await voiceJob.promise;
            assert.equal(sentMetas.length, 1);
            assert.equal(sentMetas[0].meta?.isVoice, true);
            assert.equal(state.getDraft('conv-a'), 'Keep my unsent text draft');

            // Normal attachment upload passes { isVoice: false } and clears draft
            const imgUploader = createMediaUploader({
                state,
                isVoiceDockActive: () => false,
                onMessageSent: (convId, msg, meta) => {
                    sentMetas.push({ convId, meta });
                    if (!meta?.isVoice) {
                        state.deleteDraft(convId);
                    }
                },
                api: {
                    initiateMediaUpload: async () => ({ mediaId: 'm-i1', uploadUrl: 'https://r2.invalid/put', requiredContentType: 'image/png', requiredSizeBytes: 8 }),
                    completeMediaUpload: async () => ({ id: 'm-i1', kind: 'image', fileName: 'p.png', sizeBytes: 8 }),
                    cancelMediaUpload: async () => {},
                    sendMessage: async () => ({ id: 1002, senderId: 'u1', content: null, attachments: [] })
                }
            });
            state.activeConversationId = 'conv-a';
            imgUploader.setLocked(false);
            imgUploader.addFiles([{ name: 'p.png', type: 'image/png', size: 8 }]);
            const imgJob = imgUploader.startUploadJob({ conversationId: 'conv-a', caption: '' });
            await imgJob.promise;
            assert.equal(sentMetas.length, 2);
            assert.equal(sentMetas[1].meta?.isVoice, false);
            assert.equal(state.getDraft('conv-a'), '');
            imgUploader.destroy();

            // 2. Dragover & drop with Files while isVoiceLocked() is true must call preventDefault() and set dropEffect='none' without adding files
            const activePane = createEl('div', 'chat-active-pane');
            rootBody.appendChild(activePane);
            uploader.setLocked(false);
            uploader.init();
            let dragOverPrevented = false;
            const dragDataTransfer = { types: ['Files'], dropEffect: 'copy' };
            activePane.dispatchEvent({
                type: 'dragover',
                target: activePane,
                dataTransfer: dragDataTransfer,
                preventDefault() { dragOverPrevented = true; }
            });
            assert.equal(dragOverPrevented, true);
            assert.equal(dragDataTransfer.dropEffect, 'none');

            let dropPrevented = false;
            activePane.dispatchEvent({
                type: 'drop',
                target: activePane,
                dataTransfer: { types: ['Files'], files: [{ name: 'pic.png', type: 'image/png', size: 128 }] },
                preventDefault() { dropPrevented = true; }
            });
            assert.equal(dropPrevented, true);
            assert.equal(uploader.hasPendingFiles(), false);
            uploader.destroy();

            // 3. Mobile contextmenu on voice waveform/controls must NOT open mobile action sheet,
            // while contextmenu on bubble text/padding outside voice controls DOES open it
            const mobileSheet = createEl('div', 'mobile-message-action-sheet');
            mobileSheet.classList.add('hidden');
            rootBody.appendChild(mobileSheet);

            const msgRow = createEl('div', 'msg-500');
            msgRow.className = 'message-bubble-row';
            msgRow.setAttribute('data-message-id', '500');
            msgRow.setAttribute('data-is-outgoing', 'false');
            msgRow.setAttribute('data-is-voice', 'true');
            msgRow.setAttribute('data-deleted', 'false');
            const bubble = createEl('div');
            bubble.className = 'message-bubble';
            const waveform = createEl('div');
            waveform.setAttribute('data-voice-waveform', 'true');
            const playBtn = createEl('button');
            playBtn.className = 'voice-player-control';
            bubble.appendChild(waveform);
            bubble.appendChild(playBtn);
            msgRow.appendChild(bubble);
            rootBody.appendChild(msgRow);

            globalThis.window.innerWidth = 390;
            const actions = setupMessageActions(() => 'conv-a', () => 'user-1', {
                isMobileViewport: () => true
            });

            let wfCtxPrevented = false;
            document.dispatchEvent({
                type: 'contextmenu',
                target: waveform,
                preventDefault() { wfCtxPrevented = true; }
            });
            assert.equal(wfCtxPrevented, false);
            assert.ok(mobileSheet.classList.contains('hidden'));

            let ctrlCtxPrevented = false;
            document.dispatchEvent({
                type: 'contextmenu',
                target: playBtn,
                preventDefault() { ctrlCtxPrevented = true; }
            });
            assert.equal(ctrlCtxPrevented, false);
            assert.ok(mobileSheet.classList.contains('hidden'));

            let bubbleCtxPrevented = false;
            document.dispatchEvent({
                type: 'contextmenu',
                target: bubble,
                preventDefault() { bubbleCtxPrevented = true; }
            });
            assert.equal(bubbleCtxPrevented, true);
            assert.ok(!mobileSheet.classList.contains('hidden'));
            actions.destroy();
            """;

        Assert.Contains("!meta?.isVoice && typeof state.deleteDraft === 'function'", shellCode, StringComparison.Ordinal);

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

            // 3. Router confirmBeforeSwitch aborts switch on cancel AND restores history index via history.go on popstate
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

            // 4. Real popstate rollback preserves forward history stack via history.go(+1) without calling pushState
            const stack = [
                { s: { conversationId: 'conv-a', historyIdx: 0 }, u: '/chat/conv-a' },
                { s: { conversationId: 'conv-b', historyIdx: 1 }, u: '/chat/conv-b' },
                { s: { conversationId: 'conv-c', historyIdx: 2 }, u: '/chat/conv-c' }
            ];
            let histIdx = 1; // currently on B, with C in forward history
            let pushCallsAfterInit = 0;
            let goDeltas = [];
            const popListeners = new Set();
            globalThis.window.location = { href: 'https://localhost:7001/chat/conv-b', pathname: '/chat/conv-b', origin: 'https://localhost:7001', search: '' };
            globalThis.window.addEventListener = (t, fn) => { if (t === 'popstate') popListeners.add(fn); };
            globalThis.window.removeEventListener = (t, fn) => { if (t === 'popstate') popListeners.delete(fn); };
            globalThis.history = {
                get state() { return stack[histIdx].s; },
                replaceState(s, t, u) { stack[histIdx] = { s, u }; },
                pushState(s, t, u) { pushCallsAfterInit++; stack.splice(histIdx + 1); stack.push({ s, u }); histIdx = stack.length - 1; },
                go(delta) {
                    goDeltas.push(delta);
                    histIdx += delta;
                    globalThis.window.location.pathname = stack[histIdx].u;
                    for (const fn of popListeners) fn({ state: stack[histIdx].s });
                }
            };
            const popState = createChatState();
            popState.activeConversationId = 'conv-b';
            let confirmCount = 0;
            const popRouter = createChatRouter({
                state: popState,
                api: { getConversationBundle: async () => ({}) },
                renderer: {},
                hooks: { confirmBeforeSwitch: async () => { confirmCount++; return false; } }
            });
            popRouter.init();
            pushCallsAfterInit = 0;
            // Simulate user clicking browser Back from B (idx 1) to A (idx 0)
            histIdx = 0;
            globalThis.window.location.pathname = stack[0].u;
            for (const fn of popListeners) await fn({ state: stack[0].s });
            assert.equal(confirmCount, 1); // restoration popstate did NOT trigger confirm a second time
            assert.deepEqual(goDeltas, [1]);
            assert.equal(histIdx, 1);
            assert.equal(pushCallsAfterInit, 0);
            assert.equal(stack.length, 3); // forward entry C preserved!
            assert.equal(stack[2].u, '/chat/conv-c');
            assert.equal(globalThis.window.location.pathname, '/chat/conv-b');
            popRouter.destroy();

            // 5. R2-2 / R2-11: Double-Back (C->B->A) while confirmation pending coalesces into active prompt and rolls back to C (+2); destroy() aborts pending rollback
            histIdx = 2; // start on C
            goDeltas = [];
            let resolveFirstConfirm = null;
            let doubleConfirmCalls = 0;
            let switchApiCalls = 0;
            const doubleState = createChatState();
            doubleState.activeConversationId = 'conv-c';
            const doubleRouter = createChatRouter({
                state: doubleState,
                api: { getConversationBundle: async () => { switchApiCalls++; return { messages: [] }; } },
                renderer: {},
                hooks: {
                    confirmBeforeSwitch: () => {
                        doubleConfirmCalls++;
                        return new Promise(r => { resolveFirstConfirm = r; });
                    }
                }
            });
            doubleRouter.init();
            // Back #1: C (2) -> B (1)
            histIdx = 1;
            globalThis.window.location.pathname = stack[1].u;
            const p1 = Promise.all(Array.from(popListeners, fn => fn({ state: stack[1].s })));
            await Promise.resolve();
            // User presses Back #2 immediately before answering first confirm: B (1) -> A (0)
            histIdx = 0;
            globalThis.window.location.pathname = stack[0].u;
            const p2 = Promise.all(Array.from(popListeners, fn => fn({ state: stack[0].s })));
            await Promise.resolve();
            assert.equal(doubleConfirmCalls, 1);
            assert.deepEqual(goDeltas, []);
            resolveFirstConfirm(false);
            await Promise.all([p1, p2]);
            assert.deepEqual(goDeltas, [2]);
            assert.equal(histIdx, 2);
            assert.equal(switchApiCalls, 0);

            // R2-2 destroy(): resolving confirmBeforeSwitch AFTER destroy() must NOT mutate history or switch conversation
            goDeltas = [];
            let resolveAfterDestroy = null;
            doubleConfirmCalls = 0;
            const destroyState = createChatState();
            destroyState.activeConversationId = 'conv-c';
            const destroyRouter = createChatRouter({
                state: destroyState,
                api: { getConversationBundle: async () => { switchApiCalls++; return {}; } },
                renderer: {},
                hooks: { confirmBeforeSwitch: () => new Promise(r => { resolveAfterDestroy = r; }) }
            });
            // Reset listeners clean via doubleRouter.destroy()
            doubleRouter.destroy();
            destroyRouter.init();
            histIdx = 1;
            const pDestroy = Promise.all(Array.from(popListeners, fn => fn({ state: stack[1].s })));
            await Promise.resolve();
            destroyRouter.destroy();
            resolveAfterDestroy(false);
            await pDestroy;
            assert.deepEqual(goDeltas, []);
            assert.equal(switchApiCalls, 0);

            // R3-7: Direct switchConversation and switchToEmptyState confirmations invalidated if destroy() runs while awaiting
            let directSwitchFetchCalls = 0;
            let directEmptyResetCalls = 0;
            let resolveDirectSwitchConfirm = null;
            let resolveDirectEmptyConfirm = null;
            let confirmCallIdx = 0;
            const directDestroyState = createChatState();
            directDestroyState.activeConversationId = 'conv-1';
            const directDestroyRouter = createChatRouter({
                state: directDestroyState,
                api: { getConversationBundle: async () => { directSwitchFetchCalls++; return {}; } },
                renderer: { showEmptyState: () => { directEmptyResetCalls++; } },
                hooks: {
                    confirmBeforeSwitch: () => {
                        confirmCallIdx++;
                        return confirmCallIdx === 1
                            ? new Promise(r => { resolveDirectSwitchConfirm = r; })
                            : new Promise(r => { resolveDirectEmptyConfirm = r; });
                    }
                }
            });
            directDestroyRouter.init();
            const directSwitchP = directDestroyRouter.switchConversation('conv-2');
            await Promise.resolve();
            directDestroyRouter.destroy();
            resolveDirectSwitchConfirm(true);
            assert.equal(await directSwitchP, false);
            assert.equal(directSwitchFetchCalls, 0);
            assert.equal(directDestroyState.activeConversationId, 'conv-1');

            directDestroyRouter.init();
            const directEmptyP = directDestroyRouter.switchToEmptyState();
            await Promise.resolve();
            directDestroyRouter.destroy();
            resolveDirectEmptyConfirm(true);
            assert.equal(await directEmptyP, false);
            assert.equal(directEmptyResetCalls, 0);
            assert.equal(directDestroyState.activeConversationId, 'conv-1');

            // R4-1: destroy() aborts in-flight getConversationBundle(), clears pendingConversationId, and prevents post-destroy commits/errors
            let abortedSignal = null;
            let resolveInFlightBundle = null;
            let rejectInFlightBundle = null;
            let postDestroyAfterSwitchCalls = 0;
            let postDestroyRenderCalls = 0;
            let postDestroyErrorCalls = 0;
            let inFlightPushCalls = 0;
            const origPush = globalThis.history.pushState;
            globalThis.history.pushState = (...args) => { inFlightPushCalls++; return origPush.apply(globalThis.history, args); };
            const inflightState = createChatState();
            inflightState.activeConversationId = 'conv-1';
            let bundleAttempt = 0;
            const inflightRouter = createChatRouter({
                state: inflightState,
                api: {
                    getConversationBundle: (id, { signal }) => {
                        bundleAttempt++;
                        abortedSignal = signal;
                        if (bundleAttempt === 1) return new Promise(r => { resolveInFlightBundle = r; });
                        if (bundleAttempt === 2) return new Promise((_, rej) => { rejectInFlightBundle = rej; });
                        return Promise.resolve({ conversation: { id }, messages: [] });
                    }
                },
                renderer: {
                    renderMessages: () => { postDestroyRenderCalls++; },
                    showSafeError: () => { postDestroyErrorCalls++; }
                },
                hooks: {
                    onAfterSwitch: () => { postDestroyAfterSwitchCalls++; }
                }
            });
            inflightRouter.init();
            const inFlightP1 = inflightRouter.switchConversation('conv-2');
            await Promise.resolve();
            assert.equal(abortedSignal.aborted, false);
            inflightRouter.destroy();
            assert.equal(abortedSignal.aborted, true);
            assert.equal(inflightState.pendingConversationId, null);
            resolveInFlightBundle({ conversation: { id: 'conv-2' }, messages: [] });
            await inFlightP1;
            assert.equal(inflightState.activeConversationId, null); // beginSwitch cleared conv-1; conv-2 must NOT commit after destroy()
            assert.equal(postDestroyRenderCalls, 0);
            assert.equal(postDestroyAfterSwitchCalls, 0);
            assert.equal(inFlightPushCalls, 0);

            // Also verify non-AbortError rejection after destroy does not invoke showSafeError, and re-init can switch to conv-2 cleanly
            inflightRouter.init();
            const inFlightP2 = inflightRouter.switchConversation('conv-2');
            await Promise.resolve();
            inflightRouter.destroy();
            rejectInFlightBundle(new Error('late network error'));
            await inFlightP2;
            assert.equal(postDestroyErrorCalls, 0);
            assert.equal(inflightState.pendingConversationId, null);

            inflightRouter.init();
            await inflightRouter.switchConversation('conv-2');
            assert.equal(inflightState.activeConversationId, 'conv-2');
            inflightRouter.destroy();
            globalThis.history.pushState = origPush;
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public void RazorViews_HaveNoInlineStyles_NoAudioCrossOrigin_And_SharedVoiceDockPartial()
    {
        var slnRoot = FindSolutionRoot();
        var convPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var emptyPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "_ChatActivePaneEmpty.cshtml");
        var dockPartialPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "_ComposerVoiceDock.cshtml");

        var convHtml = File.ReadAllText(convPath);
        var emptyHtml = File.ReadAllText(emptyPath);
        var dockHtml = File.ReadAllText(dockPartialPath);

        foreach (var html in new[] { convHtml, emptyHtml, dockHtml })
        {
            Assert.DoesNotMatch(new Regex(@"\bstyle\s*=", RegexOptions.IgnoreCase), html);
            Assert.DoesNotContain("crossorigin", html, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var parentHtml in new[] { convHtml, emptyHtml })
        {
            Assert.Contains("@await Html.PartialAsync(\"_ComposerVoiceDock\")", parentHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"composer-voice-dock\"", parentHtml, StringComparison.Ordinal);
            Assert.Contains("id=\"composer-input-row\"", parentHtml, StringComparison.Ordinal);
            Assert.Contains("id=\"composer-voice-btn\"", parentHtml, StringComparison.Ordinal);
        }

        foreach (var id in new[]
        {
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
            Assert.Contains($"id=\"{id}\"", dockHtml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task VoicePlayerSpeedSync_UpdatesAllCardsAcrossConversationSwitchesPaginationAndDirectMounts()
    {
        var playerUrl = JsUrl("voice-player.js");
        var rendererUrl = JsUrl("message-renderer.js");

        var script = $$"""
            import assert from 'node:assert/strict';

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

            let rootBody = null;

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
                    _src: '',
                    get src() { return this._src; },
                    set src(v) { this._src = String(v ?? ''); },
                    currentTime: 0,
                    duration: Infinity,
                    paused: true,
                    ended: false,
                    playbackRate: 1,
                    scrollHeight: 600,
                    scrollTop: 0,
                    clientHeight: 400,
                    style: {},
                    get isConnected() {
                        let cur = this;
                        while (cur) {
                            if (cur === rootBody) return true;
                            cur = cur.parentNode;
                        }
                        return false;
                    },
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
                            const moved = [...child.children];
                            child.children = [];
                            for (const node of moved) {
                                node.parentNode = this;
                                this.children.push(node);
                            }
                            return child;
                        }
                        if (child.parentNode && typeof child.parentNode.removeChild === 'function') {
                            child.parentNode.removeChild(child);
                        }
                        child.parentNode = this;
                        this.children.push(child);
                        return child;
                    },
                    insertBefore(newNode, refNode) {
                        if (!refNode) return this.appendChild(newNode);
                        const idx = this.children.indexOf(refNode);
                        if (idx < 0) return this.appendChild(newNode);
                        if (newNode.nodeType === 11) {
                            const moved = [...newNode.children];
                            newNode.children = [];
                            for (let i = 0; i < moved.length; i++) {
                                moved[i].parentNode = this;
                                this.children.splice(idx + i, 0, moved[i]);
                            }
                            return newNode;
                        }
                        if (newNode.parentNode && typeof newNode.parentNode.removeChild === 'function') {
                            newNode.parentNode.removeChild(newNode);
                        }
                        newNode.parentNode = this;
                        this.children.splice(idx, 0, newNode);
                        return newNode;
                    },
                    removeChild(child) {
                        const idx = this.children.indexOf(child);
                        if (idx >= 0) { this.children.splice(idx, 1); child.parentNode = null; }
                        return child;
                    },
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
                    pause() { this.paused = true; },
                    async play() { this.paused = false; this.ended = false; },
                    load() {},
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

            rootBody = createEl('body');
            globalThis.document = {
                body: rootBody,
                createElement: (tag) => createEl(tag),
                createTextNode: (text) => ({ nodeType: 3, textContent: String(text ?? ''), parentNode: null }),
                createDocumentFragment() {
                    return {
                        nodeType: 11,
                        children: [],
                        parentNode: null,
                        isConnected: false,
                        get firstChild() { return this.children[0] || null; },
                        appendChild(child) {
                            if (!child) return child;
                            if (child.parentNode && typeof child.parentNode.removeChild === 'function') {
                                child.parentNode.removeChild(child);
                            }
                            child.parentNode = this;
                            this.children.push(child);
                            return child;
                        },
                        removeChild(child) {
                            const idx = this.children.indexOf(child);
                            if (idx >= 0) { this.children.splice(idx, 1); child.parentNode = null; }
                            return child;
                        }
                    };
                },
                getElementById: (id) => walk(rootBody, n => n.id === id)[0] || null,
                querySelector: (sel) => walk(rootBody, n => matchesSelector(n, sel))[0] || null,
                querySelectorAll: (sel) => walk(rootBody, n => matchesSelector(n, sel))
            };
            globalThis.window = {
                location: { href: 'https://localhost:7001/chat', pathname: '/chat', origin: 'https://localhost:7001', search: '' },
                addEventListener() {},
                removeEventListener() {}
            };

            const streamEl = createEl('div', 'message-stream');
            const containerEl = createEl('div', 'messages-container');
            streamEl.appendChild(containerEl);
            rootBody.appendChild(streamEl);

            const { createVoicePlayer } = await import('{{playerUrl}}');
            const renderer = await import('{{rendererUrl}}');

            const player = createVoicePlayer({
                resolveUrlFn: async (_c, _m, mediaId) => `https://r2.invalid/${mediaId}.webm`
            });
            renderer.setVoicePlayerCoordinator(player);

            const makeVoiceMsg = (id, convId, mediaId, secOffset, isOutgoing = true) => ({
                id,
                conversationId: convId,
                senderId: isOutgoing ? 'user-1' : 'user-2',
                senderName: isOutgoing ? 'Me' : 'Peer',
                content: '',
                createdAtUtc: new Date(Date.UTC(2026, 10, 27, 12, 0, secOffset)).toISOString(),
                attachments: [{
                    id: mediaId,
                    kind: 'voice',
                    fileName: 'voice-message.webm',
                    sizeBytes: 4096,
                    durationMs: 7000,
                    waveformPeaks: [20, 45, 70, 55]
                }]
            });

            const makeImageMsg = (id, convId, mediaId, secOffset) => ({
                id,
                conversationId: convId,
                senderId: 'user-2',
                senderName: 'Peer',
                content: '',
                createdAtUtc: new Date(Date.UTC(2026, 10, 27, 12, 0, secOffset)).toISOString(),
                attachments: [{
                    id: mediaId,
                    kind: 'image',
                    fileName: 'photo.jpg',
                    sizeBytes: 2048,
                    width: 400,
                    height: 300
                }]
            });

            const getSpeedLabels = () =>
                containerEl.querySelectorAll('[data-voice-speed-btn="true"]').map(b => b.textContent);
            const getAudioRates = () =>
                containerEl.querySelectorAll('audio[data-media-audio="true"]').map(a => a.playbackRate);

            // 1. Switch to Conversation A containing 2 voice messages followed by an image attachment
            // (previously the DocumentFragment hydration pruned ALL earlier voice cards!)
            renderer.renderMessages(
                containerEl,
                [
                    makeVoiceMsg(1, 'conv-a', 'm-a1', 1, true),
                    makeVoiceMsg(2, 'conv-a', 'm-a2', 2, false),
                    makeImageMsg(3, 'conv-a', 'm-a3', 3)
                ],
                'user-1',
                { id: 'conv-a' },
                { scrollToEnd: false }
            );

            assert.deepEqual(getSpeedLabels(), ['1x', '1x']);
            const convABtns = containerEl.querySelectorAll('[data-voice-speed-btn="true"]');
            convABtns[0].click();
            assert.equal(player.getCurrentSpeed(), 1.5);
            assert.deepEqual(getSpeedLabels(), ['1.5x', '1.5x']);
            assert.deepEqual(getAudioRates(), [1.5, 1.5]);

            convABtns[1].click();
            assert.equal(player.getCurrentSpeed(), 2);
            assert.deepEqual(getSpeedLabels(), ['2x', '2x']);
            assert.deepEqual(getAudioRates(), [2, 2]);

            // 2. Switch to Conversation B (3 voice messages + trailing image) while speed is 2x
            renderer.clearPane();
            renderer.renderMessages(
                containerEl,
                [
                    makeVoiceMsg(11, 'conv-b', 'm-b1', 10, true),
                    makeVoiceMsg(12, 'conv-b', 'm-b2', 11, false),
                    makeVoiceMsg(13, 'conv-b', 'm-b3', 12, true),
                    makeImageMsg(14, 'conv-b', 'm-b4', 13)
                ],
                'user-1',
                { id: 'conv-b' },
                { scrollToEnd: false }
            );
            assert.deepEqual(getSpeedLabels(), ['2x', '2x', '2x']);
            assert.deepEqual(getAudioRates(), [2, 2, 2]);

            const convBBtns = containerEl.querySelectorAll('[data-voice-speed-btn="true"]');
            convBBtns[0].click(); // 2x -> 1x
            assert.deepEqual(getSpeedLabels(), ['1x', '1x', '1x']);
            convBBtns[0].click(); // 1x -> 1.5x
            assert.deepEqual(getSpeedLabels(), ['1.5x', '1.5x', '1.5x']);

            // 3. Prepend older voice messages via pagination while speed is 1.5x
            renderer.prependOlderMessages(
                containerEl,
                streamEl,
                [
                    makeVoiceMsg(9, 'conv-b', 'm-b0a', 8, false),
                    makeVoiceMsg(10, 'conv-b', 'm-b0b', 9, true)
                ],
                'user-1',
                { id: 'conv-b' }
            );
            assert.deepEqual(getSpeedLabels(), ['1.5x', '1.5x', '1.5x', '1.5x', '1.5x']);
            containerEl.querySelectorAll('[data-voice-speed-btn="true"]')[0].click(); // 1.5x -> 2x
            assert.deepEqual(getSpeedLabels(), ['2x', '2x', '2x', '2x', '2x']);

            // 4. Insert live voice message via insertMessageOrdered while speed is 2x
            renderer.insertMessageOrdered(
                containerEl,
                makeVoiceMsg(15, 'conv-b', 'm-b5', 15, false),
                'user-1',
                { id: 'conv-b' }
            );
            assert.deepEqual(getSpeedLabels(), ['2x', '2x', '2x', '2x', '2x', '2x']);
            containerEl.querySelectorAll('[data-voice-speed-btn="true"]')[5].click(); // 2x -> 1x
            assert.deepEqual(getSpeedLabels(), ['1x', '1x', '1x', '1x', '1x', '1x']);

            // 5. Detached-bound card whose speed changed while detached re-syncs immediately when re-hydrated directly
            const detachedRow = renderer.createMessageBubbleElement(makeVoiceMsg(20, 'conv-b', 'm-b20', 20, true), 'user-1');
            const detachedCard = detachedRow.querySelector('[data-voice-player="true"]');
            assert.equal(detachedCard.isConnected, false);
            assert.equal(detachedCard.querySelector('[data-voice-speed-btn="true"]').textContent, '1x');
            // Change speed while detachedRow is not in DOM
            containerEl.querySelectorAll('[data-voice-speed-btn="true"]')[0].click(); // 1x -> 1.5x
            containerEl.appendChild(detachedRow);
            player.hydrateRoot(detachedCard); // direct card hydration must sync already-bound card to 1.5x
            assert.equal(detachedCard.querySelector('[data-voice-speed-btn="true"]').textContent, '1.5x');
            assert.equal(detachedCard.querySelector('audio[data-media-audio="true"]').playbackRate, 1.5);

            // 6. Direct createMessageBubbleElement callers mounting two voice rows without second hydrateRoot stay synced
            const directRow1 = renderer.createMessageBubbleElement(makeVoiceMsg(21, 'conv-b', 'm-b21', 21, true), 'user-1');
            const directRow2 = renderer.createMessageBubbleElement(makeVoiceMsg(22, 'conv-b', 'm-b22', 22, false), 'user-1');
            containerEl.appendChild(directRow1);
            containerEl.appendChild(directRow2);
            directRow1.querySelector('[data-voice-speed-btn="true"]').click(); // 1.5x -> 2x
            assert.equal(directRow1.querySelector('[data-voice-speed-btn="true"]').textContent, '2x');
            assert.equal(directRow2.querySelector('[data-voice-speed-btn="true"]').textContent, '2x');
            assert.ok(getSpeedLabels().every(l => l === '2x'));

            renderer.setVoicePlayerCoordinator(null);
            player.destroy();
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }
}
