/**
 * AOS Messenger - Inline Voice Message Player Module
 *
 * Manages inline playback for rendered voice bubbles (Play/Pause, interactive waveform scrubbing,
 * speed toggle 1x/1.5x/2x, sequential auto-play, single-active-media coordination across audio & video,
 * lazy signed-URL resolution with retry, and recipient Played receipt dispatch/sync).
 *
 * Security & CSP invariants:
 * - Zero innerHTML/outerHTML/insertAdjacentHTML; zero inline style attributes.
 * - Never sets crossOrigin on <audio> so R2 range streaming works with existing bucket CORS policy.
 */

import { getMediaDownloadUrl, markVoicePlayed as apiMarkVoicePlayed } from './api-client.js';
import { resolveCachedMediaUrl } from './message-renderer.js';

export const VOICE_SPEEDS = [1, 1.5, 2];

export const DEFAULT_VOICE_PEAKS = Object.freeze([
    20, 38, 56, 72, 44, 66, 84, 52, 34, 60, 78, 50,
    68, 88, 56, 42, 64, 48, 30, 38, 60, 74, 46, 26
]);

/**
 * Formats milliseconds into m:ss (matching server MediaPreviewFormatter).
 * @param {number|null|undefined} durationMs
 * @returns {string}
 */
export function formatVoiceDuration(durationMs) {
    const n = Number(durationMs);
    const totalSeconds = Number.isFinite(n) && n > 0
        ? Math.max(0, Math.round(n / 1000))
        : 0;
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

/**
 * Maps a normalized 0..100 peak to a discrete Tailwind height class (CSP-safe without inline styles).
 * @param {number} peak
 * @returns {string}
 */
export function getVoiceBarHeightClass(peak) {
    const val = Number(peak) || 0;
    if (val >= 90) return 'h-6';
    if (val >= 75) return 'h-5';
    if (val >= 60) return 'h-4';
    if (val >= 45) return 'h-3';
    if (val >= 30) return 'h-2';
    if (val >= 15) return 'h-1.5';
    return 'h-1';
}

/**
 * Parses waveform peaks from a CSV attribute string or numeric array.
 * @param {string|number[]|null|undefined} rawPeaks
 * @returns {number[]}
 */
export function parseWaveformPeaks(rawPeaks) {
    if (Array.isArray(rawPeaks) && rawPeaks.length > 0) {
        return rawPeaks
            .slice(0, 64)
            .map(v => Math.max(0, Math.min(100, Math.round(Number(v) || 0))));
    }
    if (typeof rawPeaks === 'string' && rawPeaks.trim().length > 0) {
        const parsed = rawPeaks
            .split(',')
            .map(s => s.trim())
            .filter(Boolean)
            .slice(0, 64)
            .map(s => Number(s))
            .filter(n => Number.isFinite(n) && n >= 0)
            .map(n => Math.max(0, Math.min(100, Math.round(n))));
        if (parsed.length > 0) {
            return parsed;
        }
    }
    return [...DEFAULT_VOICE_PEAKS];
}

function setPlayedBarClasses(bar, isActive, isOutgoing) {
    if (!bar || !bar.classList) return;
    if (isOutgoing) {
        bar.classList.toggle('bg-white', isActive);
        bar.classList.toggle('bg-white/45', !isActive);
    } else {
        bar.classList.toggle('bg-[#735761]', isActive);
        bar.classList.toggle('dark:bg-[#E1BDC9]', isActive);
        bar.classList.toggle('bg-[#B3929D]/35', !isActive);
        bar.classList.toggle('dark:bg-[#E1BDC9]/30', !isActive);
    }
}

/**
 * Applies a persisted or SignalR-received PlayedAtUtc update onto a rendered voice bubble.
 * @param {string|number} messageId
 * @param {string|null} mediaId
 * @param {string|null} playedAtUtc
 * @returns {boolean}
 */
export function applyVoicePlayedToRow(messageId, mediaId, playedAtUtc) {
    if (typeof document === 'undefined' || !playedAtUtc) return false;

    const normMediaId = mediaId ? String(mediaId).trim().toLowerCase() : '';
    const matchByMediaId = (root) => {
        if (!root || typeof root.querySelectorAll !== 'function') return null;
        const nodes = root.querySelectorAll('[data-voice-player="true"]');
        if (normMediaId) {
            for (const node of nodes) {
                const attr = (node.getAttribute('data-media-id') || '').trim().toLowerCase();
                if (attr === normMediaId) return node;
            }
        }
        return null;
    };

    let card = null;
    if (messageId && typeof document.getElementById === 'function') {
        const row = document.getElementById(`msg-${messageId}`);
        if (row) {
            card = matchByMediaId(row) || (typeof row.querySelector === 'function' ? row.querySelector('[data-voice-player="true"]') : null);
        }
    }
    if (!card && normMediaId) {
        card = matchByMediaId(document);
    }
    if (!card) return false;

    card.setAttribute('data-played-at', String(playedAtUtc));
    const isOutgoing = card.getAttribute('data-is-outgoing') === 'true';

    const dotEl = typeof card.querySelector === 'function' ? card.querySelector('[data-voice-played-dot="true"]') : null;
    if (dotEl && dotEl.classList) {
        dotEl.classList.add('hidden');
        dotEl.setAttribute('title', 'Played');
    }

    const labelEl = typeof card.querySelector === 'function' ? card.querySelector('[data-voice-played-label="true"]') : null;
    if (labelEl && labelEl.classList && isOutgoing) {
        labelEl.className = 'inline-flex items-center gap-0.5 text-[10px] font-medium text-sky-200';
    }

    return true;
}

/**
 * Builds the voice player card DOM element matching server-rendered Conversation.cshtml markup.
 */
export function createVoiceBubbleElement({
    conversationId,
    messageId,
    mediaId,
    durationMs,
    waveformPeaks,
    playedAtUtc,
    isOutgoing
}) {
    const peaks = parseWaveformPeaks(waveformPeaks);
    const durMs = Math.max(0, Number(durationMs) || 0);
    const durText = formatVoiceDuration(durMs);
    const isPlayed = Boolean(playedAtUtc);
    const convIdStr = String(conversationId || '');
    const msgIdStr = String(messageId || '');
    const mediaIdStr = String(mediaId || '');

    const card = document.createElement('div');
    card.className = 'voice-player-card w-[230px] sm:w-72 max-w-full flex flex-col gap-1.5 select-none';
    card.setAttribute('data-voice-player', 'true');
    card.setAttribute('data-conversation-id', convIdStr);
    if (msgIdStr) card.setAttribute('data-message-id', msgIdStr);
    card.setAttribute('data-media-id', mediaIdStr);
    card.setAttribute('data-duration-ms', String(durMs));
    card.setAttribute('data-waveform-peaks', peaks.join(','));
    card.setAttribute('data-played-at', playedAtUtc ? String(playedAtUtc) : '');
    card.setAttribute('data-is-outgoing', isOutgoing ? 'true' : 'false');

    const topRow = document.createElement('div');
    topRow.className = 'flex items-center gap-2.5 min-w-0';

    const playBtn = document.createElement('button');
    playBtn.type = 'button';
    playBtn.setAttribute('data-voice-play-btn', 'true');
    playBtn.setAttribute('aria-label', `Play voice message (${durText})`);
    playBtn.className = isOutgoing
        ? 'voice-player-control bg-white text-[#735761] hover:bg-white/90 w-9 h-9 rounded-full flex items-center justify-center shrink-0 shadow-2xs transition-transform active:scale-95 cursor-pointer'
        : 'voice-player-control bg-[#B3929D] text-white hover:opacity-95 w-9 h-9 rounded-full flex items-center justify-center shrink-0 shadow-2xs transition-transform active:scale-95 cursor-pointer';

    const playIcon = document.createElement('span');
    playIcon.className = 'material-symbols-outlined text-[20px] icon-filled';
    playIcon.setAttribute('data-voice-play-icon', 'true');
    playIcon.textContent = 'play_arrow';
    playBtn.appendChild(playIcon);
    topRow.appendChild(playBtn);

    const bodyCol = document.createElement('div');
    bodyCol.className = 'flex-1 min-w-0 flex flex-col gap-1';

    const waveform = document.createElement('div');
    waveform.className = 'voice-player-control flex items-center gap-[1px] sm:gap-0.5 h-7 cursor-pointer touch-none';
    waveform.setAttribute('data-voice-waveform', 'true');
    waveform.setAttribute('role', 'slider');
    waveform.tabIndex = 0;
    waveform.setAttribute('aria-label', 'Voice message seek bar');
    waveform.setAttribute('aria-valuemin', '0');
    waveform.setAttribute('aria-valuemax', String(durMs));
    waveform.setAttribute('aria-valuenow', '0');

    for (let i = 0; i < peaks.length; i++) {
        const bar = document.createElement('span');
        bar.setAttribute('data-voice-bar', String(i));
        bar.className = isOutgoing
            ? `flex-1 min-w-[1px] max-w-[4px] rounded-full ${getVoiceBarHeightClass(peaks[i])} bg-white/45 transition-colors`
            : `flex-1 min-w-[1px] max-w-[4px] rounded-full ${getVoiceBarHeightClass(peaks[i])} bg-[#B3929D]/35 dark:bg-[#E1BDC9]/30 transition-colors`;
        waveform.appendChild(bar);
    }
    bodyCol.appendChild(waveform);

    const metaRow = document.createElement('div');
    metaRow.className = 'flex items-center justify-between gap-2 text-[10px]';

    const timeSpan = document.createElement('span');
    timeSpan.className = isOutgoing
        ? 'font-mono font-semibold tabular-nums text-white/90'
        : 'font-mono font-semibold tabular-nums text-[#6E6560] dark:text-[#C9C0B9]';
    timeSpan.setAttribute('data-voice-time', 'true');
    timeSpan.textContent = durText;
    metaRow.appendChild(timeSpan);

    const rightControls = document.createElement('div');
    rightControls.className = 'flex items-center gap-1.5 shrink-0';

    const playedDot = document.createElement('span');
    playedDot.setAttribute('data-voice-played-dot', 'true');
    playedDot.setAttribute('title', isPlayed ? 'Played' : 'Unplayed');
    playedDot.className = isPlayed || isOutgoing ? 'hidden' : 'w-1.5 h-1.5 rounded-full bg-[#B3929D]';
    rightControls.appendChild(playedDot);

    const playedLabel = document.createElement('span');
    playedLabel.setAttribute('data-voice-played-label', 'true');
    playedLabel.className = isOutgoing && isPlayed
        ? 'inline-flex items-center gap-0.5 text-[10px] font-medium text-sky-200'
        : 'hidden';
    const playedIcon = document.createElement('span');
    playedIcon.className = 'material-symbols-outlined text-[12px]';
    playedIcon.textContent = 'headphones';
    playedLabel.appendChild(playedIcon);
    const playedText = document.createElement('span');
    playedText.textContent = 'Played';
    playedLabel.appendChild(playedText);
    rightControls.appendChild(playedLabel);

    const speedBtn = document.createElement('button');
    speedBtn.type = 'button';
    speedBtn.setAttribute('data-voice-speed-btn', 'true');
    speedBtn.setAttribute('aria-label', 'Playback speed 1x');
    speedBtn.className = isOutgoing
        ? 'voice-player-control px-1.5 py-0.5 rounded-full text-[10px] font-bold bg-black/15 text-white hover:bg-black/25 transition-colors cursor-pointer'
        : 'voice-player-control px-1.5 py-0.5 rounded-full text-[10px] font-bold bg-[#F0E9DF] dark:bg-[#45423E] text-[#735761] dark:text-[#E1BDC9] hover:bg-[#E3DCD7] dark:hover:bg-[#514D48] transition-colors cursor-pointer';
    speedBtn.textContent = '1x';
    rightControls.appendChild(speedBtn);

    metaRow.appendChild(rightControls);
    bodyCol.appendChild(metaRow);
    topRow.appendChild(bodyCol);
    card.appendChild(topRow);

    const errDiv = document.createElement('div');
    errDiv.setAttribute('data-voice-error', 'true');
    errDiv.className = isOutgoing ? 'hidden text-[10px] text-white/90' : 'hidden text-[10px] text-red-600 dark:text-red-400';
    errDiv.textContent = 'Audio unavailable';
    card.appendChild(errDiv);

    const audio = document.createElement('audio');
    audio.preload = 'none';
    audio.className = 'hidden';
    audio.setAttribute('data-media-audio', 'true');
    card.appendChild(audio);

    return card;
}

/**
 * Releases <audio> elements inside a row/subtree cleanly on tombstone or removal.
 * @param {HTMLElement|Document|null} rootEl
 */
export function teardownRowAudio(rootEl) {
    if (!rootEl || typeof rootEl.querySelectorAll !== 'function') return;
    const audios = rootEl.querySelectorAll('audio');
    for (const audio of audios) {
        if (!audio) continue;
        try {
            if (typeof audio.pause === 'function') audio.pause();
            if (typeof audio.removeAttribute === 'function') audio.removeAttribute('src');
            if (typeof audio.load === 'function') audio.load();
        } catch {}
    }
}

/**
 * Creates a coordinator managing voice bubble playback within the active chat shell.
 */
export function createVoicePlayer({
    resolveUrlFn = null,
    getMediaUrlFn = getMediaDownloadUrl,
    markVoicePlayedFn = apiMarkVoicePlayed,
    onBeforeStartPlayback = null,
    nowFn = () => Date.now()
} = {}) {
    let activeCard = null;
    let speedIndex = 0;
    let playSequence = 0;
    const stateByCard = new WeakMap();
    const allBoundCards = new Set();

    function pruneDisconnectedCards() {
        for (const card of [...allBoundCards]) {
            if (!card || card.isConnected === false) {
                allBoundCards.delete(card);
            }
        }
    }

    function getCurrentSpeed() {
        return VOICE_SPEEDS[speedIndex] || 1;
    }

    function getCardState(card) {
        return stateByCard.get(card) || null;
    }

    function getEffectiveDurationSec(audio, state) {
        if (audio && Number.isFinite(audio.duration) && audio.duration > 0) {
            return audio.duration;
        }
        return state.knownDurationMs > 0 ? state.knownDurationMs / 1000 : 0;
    }

    function updateProgressUi(card, state, forceCurrentSec = null) {
        const audio = state.audio;
        const durationSec = getEffectiveDurationSec(audio, state);
        const rawCur = forceCurrentSec !== null
            ? forceCurrentSec
            : (state.pendingSeekSec ?? (Number(audio?.currentTime) || 0));
        const clampedCur = durationSec > 0 ? Math.max(0, Math.min(durationSec, rawCur)) : Math.max(0, rawCur);
        const fraction = durationSec > 0 ? Math.max(0, Math.min(1, clampedCur / durationSec)) : 0;

        const isPlaying = Boolean(audio && !audio.paused && !audio.ended);
        if (state.timeEl) {
            if (clampedCur > 0 || isPlaying) {
                state.timeEl.textContent = `${formatVoiceDuration(clampedCur * 1000)} / ${formatVoiceDuration(durationSec * 1000)}`;
            } else {
                state.timeEl.textContent = formatVoiceDuration(durationSec * 1000);
            }
        }

        if (state.waveformEl) {
            state.waveformEl.setAttribute('aria-valuenow', String(Math.round(clampedCur * 1000)));
            if (durationSec > 0) {
                state.waveformEl.setAttribute('aria-valuemax', String(Math.round(durationSec * 1000)));
            }
            const bars = state.bars;
            if (bars && bars.length > 0) {
                const activeCount = fraction <= 0 ? 0 : Math.ceil(fraction * bars.length);
                for (let i = 0; i < bars.length; i++) {
                    setPlayedBarClasses(bars[i], i < activeCount, state.isOutgoing);
                }
            }
        }
    }

    function setPlayingUi(card, state, playing) {
        if (state.playIcon) {
            state.playIcon.textContent = playing ? 'pause' : 'play_arrow';
        }
        if (state.playBtn) {
            const totalText = formatVoiceDuration(getEffectiveDurationSec(state.audio, state) * 1000);
            state.playBtn.setAttribute('aria-label', playing ? `Pause voice message (${totalText})` : `Play voice message (${totalText})`);
        }
    }

    function showError(state, message) {
        if (!state.errorEl) return;
        state.errorEl.textContent = message || 'Audio unavailable';
        state.errorEl.classList.remove('hidden');
    }

    function clearError(state) {
        if (!state.errorEl) return;
        state.errorEl.classList.add('hidden');
    }

    function syncCardSpeed(state, speed = getCurrentSpeed()) {
        if (!state) return;
        const label = `${speed}x`;
        if (state.speedBtn) {
            state.speedBtn.textContent = label;
            state.speedBtn.setAttribute('aria-label', `Playback speed ${label}`);
        }
        if (state.audio) {
            state.audio.playbackRate = speed;
        }
    }

    function syncAllSpeedButtons(triggerCard = null) {
        const speed = getCurrentSpeed();
        if (triggerCard && stateByCard.has(triggerCard)) {
            if (triggerCard.isConnected !== false) {
                allBoundCards.add(triggerCard);
            } else {
                syncCardSpeed(stateByCard.get(triggerCard), speed);
            }
        }
        if (typeof document !== 'undefined' && typeof document.getElementById === 'function') {
            const container = document.getElementById('messages-container');
            if (container && typeof container.querySelectorAll === 'function') {
                const liveCards = container.querySelectorAll('[data-voice-player="true"]');
                for (const card of liveCards) {
                    if (card && card.isConnected !== false && stateByCard.has(card)) {
                        allBoundCards.add(card);
                    }
                }
            }
        }
        for (const card of [...allBoundCards]) {
            if (card.isConnected === false) {
                allBoundCards.delete(card);
                continue;
            }
            const st = getCardState(card);
            if (!st) continue;
            syncCardSpeed(st, speed);
        }
    }

    function pauseVideoElements(exceptVideo = null) {
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
        const container = document.getElementById('messages-container');
        if (!container || typeof container.querySelectorAll !== 'function') return;
        const videos = container.querySelectorAll('video');
        for (const v of videos) {
            if (!v || v === exceptVideo) continue;
            try {
                if (!v.paused && typeof v.pause === 'function') {
                    v.pause();
                }
            } catch {}
        }
    }

    function stopAllPlayback(exceptCard = null, { includeVideos = true } = {}) {
        if (!exceptCard) {
            playSequence++;
        }
        pruneDisconnectedCards();

        if (activeCard && activeCard !== exceptCard) {
            const prevState = getCardState(activeCard);
            if (prevState?.audio && typeof prevState.audio.pause === 'function') {
                try { prevState.audio.pause(); } catch {}
            }
            if (prevState) {
                prevState.loadingUrlAttemptId = 0;
                setPlayingUi(activeCard, prevState, false);
            }
            activeCard = null;
        }

        if (includeVideos) {
            pauseVideoElements(null);
        }
    }

    function applyPendingSeekIfReady(state) {
        if (!state?.audio || state.pendingSeekSec === null || state.pendingSeekSec === undefined) {
            return;
        }
        if (typeof state.audio.readyState === 'number' && state.audio.readyState < 1) {
            return;
        }
        const target = state.pendingSeekSec;
        state.pendingSeekSec = null;
        try {
            state.audio.currentTime = target;
        } catch {}
    }

    function isCurrentAttempt(card, state, attemptId) {
        return Boolean(
            card &&
            state &&
            card.isConnected !== false &&
            activeCard === card &&
            attemptId === playSequence &&
            state.activeAttemptId === attemptId
        );
    }

    async function loadAudioSource(card, state, bypassCache = false, isStillCurrent = null) {
        const convId = card.getAttribute('data-conversation-id') || '';
        const msgId = card.getAttribute('data-message-id') || '';
        const mediaId = card.getAttribute('data-media-id') || '';
        if (!convId || !msgId || !mediaId || !state.audio) return false;

        try {
            const url = typeof resolveUrlFn === 'function'
                ? await resolveUrlFn(convId, msgId, mediaId, { bypassCache })
                : await resolveCachedMediaUrl(convId, msgId, mediaId, {
                    download: false,
                    bypassCache,
                    getMediaUrlFn,
                    nowFn
                });
            if (card.isConnected === false) return false;
            if (typeof isStillCurrent === 'function' && !isStillCurrent()) return false;
            if (url) {
                state.audio.src = url;
                return true;
            }
        } catch {}
        return false;
    }

    async function ensureAudioAndPlay(card, state, initialSeekSec = null) {
        if (!state.audio) return;
        if (typeof onBeforeStartPlayback === 'function') {
            onBeforeStartPlayback(card);
        }

        const attemptId = ++playSequence;
        stopAllPlayback(card);
        clearError(state);
        state.retried = false;
        state.activeAttemptId = attemptId;
        state.refreshingAttemptId = 0;
        activeCard = card;
        state.audio.playbackRate = getCurrentSpeed();

        if (initialSeekSec !== null && initialSeekSec >= 0) {
            state.pendingSeekSec = initialSeekSec;
        }

        const hasSrc = Boolean(state.audio.getAttribute('src') || state.audio.src);
        const hasMediaError = Boolean(state.audio.error);
        if (!hasSrc || hasMediaError) {
            if (hasMediaError && state.pendingSeekSec === null) {
                const resumeSec = Number(state.audio.currentTime) || 0;
                const durSec = getEffectiveDurationSec(state.audio, state);
                const isAtEnd = Boolean(state.audio.ended) || (durSec > 0 && resumeSec >= durSec - 0.05);
                if (resumeSec > 0 && !isAtEnd) {
                    state.pendingSeekSec = resumeSec;
                }
            }
            state.loadingUrlAttemptId = attemptId;
            let ok = false;
            try {
                ok = await loadAudioSource(card, state, hasMediaError, () => isCurrentAttempt(card, state, attemptId));
            } finally {
                if (state.loadingUrlAttemptId === attemptId) {
                    state.loadingUrlAttemptId = 0;
                }
            }
            if (!isCurrentAttempt(card, state, attemptId)) {
                if (card.isConnected === false && activeCard === card && attemptId === playSequence) {
                    activeCard = null;
                    setPlayingUi(card, state, false);
                }
                return;
            }
            if (!ok) {
                showError(state, 'Audio unavailable');
                setPlayingUi(card, state, false);
                activeCard = null;
                return;
            }
        }

        applyPendingSeekIfReady(state);

        try {
            await state.audio.play();
            if (!isCurrentAttempt(card, state, attemptId)) {
                if (state.activeAttemptId === attemptId) {
                    try { state.audio.pause(); } catch {}
                    setPlayingUi(card, state, false);
                }
                if (card.isConnected === false && activeCard === card && attemptId === playSequence) {
                    activeCard = null;
                    setPlayingUi(card, state, false);
                }
                return;
            }
        } catch (err) {
            if (!isCurrentAttempt(card, state, attemptId)) {
                return;
            }
            if (state.refreshingAttemptId === attemptId) {
                return;
            }
            if (err && err.name === 'NotSupportedError') {
                showError(state, 'Audio format not supported by this browser');
            } else if (err && err.name !== 'AbortError') {
                showError(state, 'Audio unavailable');
            }
            setPlayingUi(card, state, false);
            activeCard = null;
        }
    }

    async function handlePlayedReceipt(card, state) {
        if (state.isOutgoing || state.playedSent || card.getAttribute('data-played-at')) {
            return;
        }
        const convId = card.getAttribute('data-conversation-id') || '';
        const msgId = card.getAttribute('data-message-id') || '';
        const mediaId = card.getAttribute('data-media-id') || '';
        if (!convId || !msgId || !mediaId || typeof markVoicePlayedFn !== 'function') return;

        state.playedSent = true;
        try {
            const dto = await markVoicePlayedFn(convId, msgId, mediaId);
            const playedAt = dto?.playedAtUtc ?? dto?.PlayedAtUtc ?? null;
            if (playedAt && card.isConnected !== false) {
                applyVoicePlayedToRow(msgId, mediaId, playedAt);
            }
        } catch (err) {
            const status = Number(err?.status || 0);
            if (status !== 403 && status !== 404) {
                state.playedSent = false;
            }
        }
    }

    function bindVoiceCard(card) {
        if (!card || card.getAttribute('data-voice-bound') === 'true') return;
        card.setAttribute('data-voice-bound', 'true');

        const audio = card.querySelector('audio[data-media-audio="true"]');
        const playBtn = card.querySelector('[data-voice-play-btn="true"]');
        const playIcon = card.querySelector('[data-voice-play-icon="true"]');
        const waveformEl = card.querySelector('[data-voice-waveform="true"]');
        const timeEl = card.querySelector('[data-voice-time="true"]');
        const speedBtn = card.querySelector('[data-voice-speed-btn="true"]');
        const errorEl = card.querySelector('[data-voice-error="true"]');
        const bars = waveformEl && typeof waveformEl.querySelectorAll === 'function'
            ? Array.from(waveformEl.querySelectorAll('[data-voice-bar]'))
            : [];

        const state = {
            audio,
            playBtn,
            playIcon,
            waveformEl,
            bars,
            timeEl,
            speedBtn,
            errorEl,
            isOutgoing: card.getAttribute('data-is-outgoing') === 'true',
            knownDurationMs: Number(card.getAttribute('data-duration-ms') || '0') || 0,
            retried: false,
            activeAttemptId: 0,
            refreshingAttemptId: 0,
            loadingUrlAttemptId: 0,
            pendingSeekSec: null,
            playedSent: Boolean(card.getAttribute('data-played-at'))
        };

        stateByCard.set(card, state);
        allBoundCards.add(card);

        if (speedBtn) {
            speedBtn.addEventListener('click', (e) => {
                if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
                speedIndex = (speedIndex + 1) % VOICE_SPEEDS.length;
                syncAllSpeedButtons(card);
            });
        }
        syncCardSpeed(state);

        if (playBtn) {
            playBtn.addEventListener('click', (e) => {
                if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
                if (!audio) return;
                if (!audio.paused && !audio.ended) {
                    if (activeCard === card) {
                        playSequence++;
                        activeCard = null;
                    }
                    state.loadingUrlAttemptId = 0;
                    audio.pause();
                    setPlayingUi(card, state, false);
                } else {
                    ensureAudioAndPlay(card, state);
                }
            });
        }

        if (waveformEl) {
            const seekToFraction = async (fraction) => {
                const clamped = Math.max(0, Math.min(1, fraction));
                const durSec = getEffectiveDurationSec(audio, state);
                const targetSec = durSec > 0 ? clamped * durSec : 0;
                const hasSrc = Boolean(audio && (audio.getAttribute('src') || audio.src));
                if (!hasSrc && audio) {
                    await ensureAudioAndPlay(card, state, targetSec);
                    return;
                }
                if (audio) {
                    const isLoadingReplacement =
                        state.loadingUrlAttemptId !== 0 &&
                        state.loadingUrlAttemptId === state.activeAttemptId;
                    const hasMetadata = !isLoadingReplacement && (typeof audio.readyState !== 'number' || audio.readyState >= 1);
                    if (!hasMetadata) {
                        state.pendingSeekSec = targetSec;
                    } else {
                        state.pendingSeekSec = null;
                        try { audio.currentTime = targetSec; } catch {}
                    }
                }
                updateProgressUi(card, state, targetSec);
            };

            const clientXToFraction = (clientX) => {
                if (typeof waveformEl.getBoundingClientRect !== 'function') return 0;
                const rect = waveformEl.getBoundingClientRect();
                if (!rect || rect.width <= 0) return 0;
                return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
            };

            let scrubbing = false;
            let lastPointerUpAt = null;
            waveformEl.addEventListener('pointerdown', (e) => {
                if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
                if (e && typeof e.preventDefault === 'function') e.preventDefault();
                scrubbing = true;
                lastPointerUpAt = null;
                if (typeof waveformEl.setPointerCapture === 'function' && e.pointerId !== undefined) {
                    try { waveformEl.setPointerCapture(e.pointerId); } catch {}
                }
                const frac = clientXToFraction(e.clientX);
                const durSec = getEffectiveDurationSec(audio, state);
                updateProgressUi(card, state, frac * durSec);
            });

            waveformEl.addEventListener('pointermove', (e) => {
                if (!scrubbing) return;
                const frac = clientXToFraction(e.clientX);
                const durSec = getEffectiveDurationSec(audio, state);
                updateProgressUi(card, state, frac * durSec);
            });

            waveformEl.addEventListener('pointerup', (e) => {
                if (!scrubbing) return;
                scrubbing = false;
                lastPointerUpAt = nowFn();
                seekToFraction(clientXToFraction(e.clientX));
            });

            waveformEl.addEventListener('pointercancel', () => {
                scrubbing = false;
                lastPointerUpAt = null;
                updateProgressUi(card, state);
            });

            waveformEl.addEventListener('click', (e) => {
                if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
                if (lastPointerUpAt !== null) {
                    const delta = nowFn() - lastPointerUpAt;
                    lastPointerUpAt = null;
                    if (delta >= 0 && delta < 80) {
                        return;
                    }
                }
                if (typeof e.clientX === 'number' && typeof waveformEl.getBoundingClientRect === 'function') {
                    seekToFraction(clientXToFraction(e.clientX));
                }
            });

            waveformEl.addEventListener('keydown', (e) => {
                const durSec = getEffectiveDurationSec(audio, state);
                if (durSec <= 0) return;
                const cur = state.pendingSeekSec ?? (Number(audio?.currentTime) || 0);
                let next = null;
                if (e.key === 'ArrowRight' || e.key === 'ArrowUp') {
                    next = Math.min(durSec, cur + 5);
                } else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') {
                    next = Math.max(0, cur - 5);
                } else if (e.key === 'Home') {
                    next = 0;
                } else if (e.key === 'End') {
                    next = durSec;
                }
                if (next !== null) {
                    if (typeof e.preventDefault === 'function') e.preventDefault();
                    seekToFraction(next / durSec);
                }
            });
        }

        if (audio) {
            audio.addEventListener('playing', () => {
                if (audio.paused || activeCard !== card || state.activeAttemptId !== playSequence || card.isConnected === false) {
                    if (!audio.paused) {
                        try { audio.pause(); } catch {}
                    }
                    return;
                }
                stopAllPlayback(card);
                activeCard = card;
                state.retried = false;
                state.refreshingAttemptId = 0;
                audio.playbackRate = getCurrentSpeed();
                clearError(state);
                setPlayingUi(card, state, true);
                updateProgressUi(card, state);
                handlePlayedReceipt(card, state);
            });

            audio.addEventListener('pause', () => {
                if (!audio.paused) return;
                setPlayingUi(card, state, false);
                if (activeCard === card && state.refreshingAttemptId !== state.activeAttemptId) {
                    activeCard = null;
                }
            });

            audio.addEventListener('timeupdate', () => {
                updateProgressUi(card, state);
            });

            audio.addEventListener('loadedmetadata', () => {
                audio.playbackRate = getCurrentSpeed();
                if (state.pendingSeekSec !== null && state.pendingSeekSec >= 0) {
                    const target = state.pendingSeekSec;
                    state.pendingSeekSec = null;
                    try { audio.currentTime = target; } catch {}
                }
                updateProgressUi(card, state);
            });

            audio.addEventListener('ended', () => {
                try { audio.currentTime = 0; } catch {}
                setPlayingUi(card, state, false);
                updateProgressUi(card, state, 0);
                if (activeCard === card) activeCard = null;

                const row = typeof card.closest === 'function' ? card.closest('.message-bubble-row') : null;
                const nextRow = row ? row.nextElementSibling : null;
                if (nextRow &&
                    nextRow.getAttribute('data-is-voice') === 'true' &&
                    nextRow.getAttribute('data-deleted') !== 'true') {
                    const nextCard = nextRow.querySelector('[data-voice-player="true"]');
                    const nextState = nextCard ? getCardState(nextCard) : null;
                    if (nextCard && nextState) {
                        ensureAudioAndPlay(nextCard, nextState);
                    }
                }
            });

            audio.addEventListener('error', async () => {
                const hasSrc = Boolean(audio.getAttribute('src') || audio.src);
                if (!hasSrc) return;

                if (activeCard !== card || state.activeAttemptId !== playSequence || card.isConnected === false) {
                    return;
                }

                const mediaErrorCode = audio.error?.code || 0;
                if (state.retried) {
                    if (state.refreshingAttemptId === state.activeAttemptId) {
                        state.refreshingAttemptId = 0;
                    }
                    showError(
                        state,
                        mediaErrorCode === 4
                            ? 'Audio format not supported by this browser'
                            : 'Audio unavailable');
                    setPlayingUi(card, state, false);
                    activeCard = null;
                    return;
                }

                const capturedSeq = playSequence;
                state.retried = true;
                state.refreshingAttemptId = capturedSeq;
                state.loadingUrlAttemptId = capturedSeq;
                const savedTime = Number(audio.currentTime) || 0;
                const durSec = getEffectiveDurationSec(audio, state);
                const isAtEnd = Boolean(audio.ended) || (durSec > 0 && savedTime >= durSec - 0.05);
                try {
                    let ok = false;
                    try {
                        ok = await loadAudioSource(card, state, true, () => isCurrentAttempt(card, state, capturedSeq));
                    } finally {
                        if (state.loadingUrlAttemptId === capturedSeq) {
                            state.loadingUrlAttemptId = 0;
                        }
                    }
                    if (!isCurrentAttempt(card, state, capturedSeq)) {
                        if (card.isConnected === false && activeCard === card && capturedSeq === playSequence) {
                            activeCard = null;
                            setPlayingUi(card, state, false);
                        }
                        return;
                    }
                    if (!ok) {
                        showError(state, 'Audio unavailable');
                        setPlayingUi(card, state, false);
                        activeCard = null;
                        return;
                    }

                    if (state.pendingSeekSec === null && savedTime > 0 && !isAtEnd) {
                        state.pendingSeekSec = savedTime;
                    }
                    applyPendingSeekIfReady(state);
                    try {
                        await audio.play();
                        if (!isCurrentAttempt(card, state, capturedSeq)) {
                            if (state.activeAttemptId === capturedSeq) {
                                try { audio.pause(); } catch {}
                                setPlayingUi(card, state, false);
                            }
                            if (card.isConnected === false && activeCard === card && capturedSeq === playSequence) {
                                activeCard = null;
                                setPlayingUi(card, state, false);
                            }
                            return;
                        }
                    } catch (err) {
                        if (!isCurrentAttempt(card, state, capturedSeq)) {
                            return;
                        }
                        if (err && err.name !== 'AbortError') {
                            showError(
                                state,
                                (err.name === 'NotSupportedError' || mediaErrorCode === 4)
                                    ? 'Audio format not supported by this browser'
                                    : 'Audio unavailable');
                        }
                        setPlayingUi(card, state, false);
                        activeCard = null;
                    }
                } finally {
                    if (state.refreshingAttemptId === capturedSeq) {
                        state.refreshingAttemptId = 0;
                    }
                }
            });
        }

        updateProgressUi(card, state, 0);
    }

    function hydrateRoot(rootEl) {
        if (!rootEl || typeof rootEl.querySelectorAll !== 'function') return;
        if (rootEl.isConnected !== false) {
            pruneDisconnectedCards();
        }

        const cards = [];
        if (typeof rootEl.getAttribute === 'function' && rootEl.getAttribute('data-voice-player') === 'true') {
            cards.push(rootEl);
        }
        for (const card of rootEl.querySelectorAll('[data-voice-player="true"]')) {
            cards.push(card);
        }
        for (const card of cards) {
            bindVoiceCard(card);
            const st = getCardState(card);
            if (st) {
                syncCardSpeed(st);
            }
            if (card && card.isConnected !== false) {
                allBoundCards.add(card);
            }
        }

        const videos = rootEl.querySelectorAll('video[data-media-video="true"]');
        for (const video of videos) {
            if (!video || video.getAttribute('data-voice-video-sync') === 'true') continue;
            video.setAttribute('data-voice-video-sync', 'true');
            if (typeof video.addEventListener === 'function') {
                video.addEventListener('play', () => {
                    stopAllPlayback(null, { includeVideos: false });
                    pauseVideoElements(video);
                });
            }
        }
    }

    return {
        hydrateRoot,
        stopAllPlayback,
        teardownRowAudio,
        applyVoicePlayedToRow,
        getCurrentSpeed,
        getActiveCard: () => activeCard,
        destroy: () => {
            stopAllPlayback();
            for (const card of [...allBoundCards]) {
                teardownRowAudio(card);
            }
            allBoundCards.clear();
        }
    };
}
