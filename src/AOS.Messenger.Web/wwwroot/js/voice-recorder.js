/**
 * AOS Messenger - Inline Voice Message Recorder Module
 *
 * Captures live microphone audio inside the chat composer dock with:
 * - Preferred MIME negotiation: audio/mp4 (AAC) -> audio/webm (Opus) -> audio/ogg (Opus)
 * - Web Audio API AnalyserNode live waveform capture & 48-peak downsampling
 * - Pause/Resume, Stop-to-Preview with interactive scrubbing, Re-record, Discard, and Direct Send
 * - Pause-aware 20-minute duration cap & 24.5 MB safety auto-stop into Preview (minimum 500 ms)
 * - Throttled SignalR SendRecordingIndicator(convId, isRecording) updates (~3s heartbeat)
 * - Guaranteed teardown of MediaStreamTrack mic tracks, AudioContext, and ephemeral blob URLs
 */

import { formatVoiceDuration, getVoiceBarHeightClass } from './voice-player.js';

export const MIN_VOICE_DURATION_MS = 500;
export const MAX_VOICE_DURATION_MS = 20 * 60 * 1000;
export const WARN_VOICE_DURATION_MS = 19 * 60 * 1000 + 30 * 1000;
export const WARN_VOICE_BYTES = Math.floor(23.5 * 1024 * 1024);
export const AUTO_STOP_VOICE_BYTES = Math.floor(24.5 * 1024 * 1024);
export const MAX_VOICE_BYTES = 25 * 1024 * 1024;
export const TARGET_WAVEFORM_PEAKS = 48;
export const LIVE_WAVEFORM_BAR_COUNT = 24;
export const RECORDING_INDICATOR_THROTTLE_MS = 3000;
const MAX_COLLECTED_SAMPLES = 12000;

const MIME_CANDIDATES = [
    { mime: 'audio/mp4;codecs=mp4a.40.2', baseMime: 'audio/mp4', ext: '.m4a' },
    { mime: 'audio/mp4', baseMime: 'audio/mp4', ext: '.m4a' },
    { mime: 'audio/webm;codecs=opus', baseMime: 'audio/webm', ext: '.webm' },
    { mime: 'audio/webm', baseMime: 'audio/webm', ext: '.webm' },
    { mime: 'audio/ogg;codecs=opus', baseMime: 'audio/ogg', ext: '.ogg' },
    { mime: 'audio/ogg', baseMime: 'audio/ogg', ext: '.ogg' }
];

/**
 * Selects the best supported MediaRecorder MIME format in priority order.
 * @param {Function|null} MediaRecorderCls
 * @returns {{ mime: string, baseMime: string, ext: string }}
 */
export function selectPreferredRecordingFormat(MediaRecorderCls) {
    const canCheck = MediaRecorderCls && typeof MediaRecorderCls.isTypeSupported === 'function';
    if (canCheck) {
        for (const candidate of MIME_CANDIDATES) {
            try {
                if (MediaRecorderCls.isTypeSupported(candidate.mime)) {
                    return { ...candidate };
                }
            } catch {}
        }
    }
    return { mime: '', baseMime: 'audio/webm', ext: '.webm' };
}

/**
 * Strips codec parameters and maps a recorded MIME string to normalized baseMime + extension.
 * @param {string|null|undefined} rawMime
 * @param {{ baseMime: string, ext: string }} fallback
 * @returns {{ contentType: string, ext: string }}
 */
export function normalizeRecordedMime(rawMime, fallback = { baseMime: 'audio/webm', ext: '.webm' }) {
    const base = String(rawMime || '').split(';')[0].trim().toLowerCase();
    if (base === 'audio/mp4' || base === 'audio/x-m4a' || base === 'audio/aac') {
        return { contentType: 'audio/mp4', ext: '.m4a' };
    }
    if (base === 'audio/webm') {
        return { contentType: 'audio/webm', ext: '.webm' };
    }
    if (base === 'audio/ogg') {
        return { contentType: 'audio/ogg', ext: '.ogg' };
    }
    return { contentType: fallback.baseMime || 'audio/webm', ext: fallback.ext || '.webm' };
}

/**
 * Downsamples variable-length raw amplitude samples into count integer peaks in [minPeak..100].
 * @param {number[]} samples
 * @param {number} [targetCount]
 * @returns {number[]}
 */
export function downsampleWaveformPeaks(samples, targetCount = TARGET_WAVEFORM_PEAKS) {
    const count = Math.max(1, Math.min(64, Number(targetCount) || TARGET_WAVEFORM_PEAKS));
    if (!Array.isArray(samples) || samples.length === 0) {
        return Array.from({ length: count }, (_, i) => 12 + ((i * 7) % 18));
    }

    const peaks = new Array(count);
    const step = samples.length / count;
    for (let i = 0; i < count; i++) {
        const start = Math.floor(i * step);
        const end = Math.max(start + 1, Math.min(samples.length, Math.floor((i + 1) * step)));
        let maxVal = 0;
        for (let j = start; j < end; j++) {
            const v = Number(samples[j]) || 0;
            if (v > maxVal) maxVal = v;
        }
        peaks[i] = Math.max(4, Math.min(100, Math.round(maxVal)));
    }
    return peaks;
}

/**
 * Maps MediaDevices/getUserMedia failure errors into user-friendly notices.
 * @param {Error|any} err
 * @returns {string}
 */
export function formatMicrophoneError(err) {
    const name = String(err?.name || '');
    if (name === 'NotAllowedError' || name === 'PermissionDeniedError') {
        return 'Microphone permission was denied. Enable mic access in your browser settings to record voice messages.';
    }
    if (name === 'NotFoundError' || name === 'DevicesNotFoundError') {
        return 'No microphone was detected on this device.';
    }
    if (name === 'NotReadableError' || name === 'TrackStartError') {
        return 'Your microphone is in use by another application or could not be opened.';
    }
    if (name === 'SecurityError') {
        return 'Microphone access requires a secure HTTPS connection.';
    }
    return 'Could not start voice recording. Please check your microphone.';
}

/**
 * Creates the inline composer voice recorder controller.
 */
export function createVoiceRecorder({
    connection = null,
    getActiveConversationId = () => null,
    canRecordNow = () => true,
    onBeforeStart = null,
    onStartSendVoice = null,
    onDockStateChange = null,
    onError = null,
    getMediaRecorderClass = null,
    getUserMediaFn = null,
    createAudioContextFn = null,
    createObjectUrlFn = null,
    revokeObjectUrlFn = null,
    nowFn = () => (typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now()),
    minDurationMs = MIN_VOICE_DURATION_MS,
    maxDurationMs = MAX_VOICE_DURATION_MS,
    warnDurationMs = WARN_VOICE_DURATION_MS,
    warnBytes = WARN_VOICE_BYTES,
    autoStopBytes = AUTO_STOP_VOICE_BYTES,
    targetPeakCount = TARGET_WAVEFORM_PEAKS
} = {}) {
    let mode = 'idle'; // 'idle' | 'recording' | 'paused' | 'stopping' | 'preview'
    let opToken = 0;
    let isStarting = false;
    let activeConvId = null;
    let mediaStream = null;
    let mediaRecorder = null;
    let audioContext = null;
    let pendingAudioContext = null;
    let analyserNode = null;
    let sourceNode = null;
    let analyserBuffer = null;

    let selectedFormat = { mime: '', baseMime: 'audio/webm', ext: '.webm' };
    let recordedChunks = [];
    let cumulativeBytes = 0;
    let collectedSamples = [];
    let sampleStride = 1;
    let sampleTickCounter = 0;
    let sampleBucketMax = 0;
    let liveBars = [];

    let accumulatedActiveMs = 0;
    let activeSegmentStartMs = null;
    let pollTimerId = null;
    let lastIndicatorSentAtMs = 0;
    let activeIndicatorConvId = null;
    let indicatorActive = false;

    let previewBlob = null;
    let previewUrl = '';
    let previewDurationMs = 0;
    let previewPeaks = [];
    let previewMime = 'audio/webm';
    let previewExt = '.webm';
    let previewBars = [];
    let pendingStopMode = null; // 'preview' | 'send' | 'abort'
    let stopAutoTriggered = false;
    let cancelPendingStart = null;
    let initialized = false;

    function getEl(id) {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById(id)
            : null;
    }

    function reportError(msg) {
        if (typeof onError === 'function' && msg) {
            onError(msg);
        }
    }

    function notifyDockChanged() {
        if (typeof onDockStateChange === 'function') {
            onDockStateChange(mode !== 'idle', mode);
        }
    }

    function invokeRecordingIndicator(convId, isRec) {
        if (!connection || typeof connection.invoke !== 'function' || connection.state !== 'Connected') {
            return;
        }
        if (!convId) return;
        try {
            Promise.resolve(connection.invoke('SendRecordingIndicator', convId, Boolean(isRec)))
                .catch(() => {});
        } catch {}
    }

    function sendIndicatorTrueIfNeeded(convId, force = false) {
        const now = nowFn();
        if (force || !indicatorActive || (now - lastIndicatorSentAtMs) >= RECORDING_INDICATOR_THROTTLE_MS) {
            indicatorActive = true;
            activeIndicatorConvId = convId;
            lastIndicatorSentAtMs = now;
            invokeRecordingIndicator(convId, true);
        }
    }

    function sendIndicatorFalseIfActive() {
        if (indicatorActive && activeIndicatorConvId) {
            invokeRecordingIndicator(activeIndicatorConvId, false);
        }
        indicatorActive = false;
        activeIndicatorConvId = null;
        lastIndicatorSentAtMs = 0;
    }

    function getCurrentElapsedMs() {
        if (mode === 'recording' && activeSegmentStartMs !== null) {
            return Math.max(0, accumulatedActiveMs + Math.max(0, nowFn() - activeSegmentStartMs));
        }
        return Math.max(0, accumulatedActiveMs);
    }

    function clearPollTimer() {
        if (pollTimerId !== null) {
            clearInterval(pollTimerId);
            pollTimerId = null;
        }
    }

    function closePendingAudioContext() {
        if (pendingAudioContext && typeof pendingAudioContext.close === 'function') {
            try {
                if (pendingAudioContext.state !== 'closed') {
                    Promise.resolve(pendingAudioContext.close()).catch(() => {});
                }
            } catch {}
        }
        pendingAudioContext = null;
    }

    function stopMediaHardware() {
        clearPollTimer();
        closePendingAudioContext();

        if (sourceNode && typeof sourceNode.disconnect === 'function') {
            try { sourceNode.disconnect(); } catch {}
        }
        if (analyserNode && typeof analyserNode.disconnect === 'function') {
            try { analyserNode.disconnect(); } catch {}
        }
        sourceNode = null;
        analyserNode = null;
        analyserBuffer = null;

        if (audioContext && typeof audioContext.close === 'function') {
            try {
                if (audioContext.state !== 'closed') {
                    Promise.resolve(audioContext.close()).catch(() => {});
                }
            } catch {}
        }
        audioContext = null;

        if (mediaStream && typeof mediaStream.getTracks === 'function') {
            for (const track of mediaStream.getTracks()) {
                try {
                    if (track && typeof track.stop === 'function') {
                        track.stop();
                    }
                } catch {}
            }
        }
        mediaStream = null;
    }

    function revokePreviewObjectUrl() {
        if (previewUrl) {
            try {
                if (typeof revokeObjectUrlFn === 'function') {
                    revokeObjectUrlFn(previewUrl);
                } else if (typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
                    URL.revokeObjectURL(previewUrl);
                }
            } catch {}
        }
        previewUrl = '';
    }

    function pausePreview() {
        const audio = getEl('voice-preview-audio');
        if (audio && !audio.paused && !audio.ended && typeof audio.pause === 'function') {
            try { audio.pause(); } catch {}
        }
    }

    function setPreviewPlayUi(isPlaying) {
        const icon = getEl('voice-preview-play-icon');
        if (icon) icon.textContent = isPlaying ? 'pause' : 'play_arrow';
        const btn = getEl('voice-preview-play-btn');
        if (btn) {
            const label = isPlaying ? 'Pause voice preview' : 'Play voice preview';
            btn.setAttribute('aria-label', label);
            btn.setAttribute('title', label);
        }
    }

    function clearPreviewAudioElement() {
        const audio = getEl('voice-preview-audio');
        if (audio) {
            try {
                if (typeof audio.pause === 'function') audio.pause();
                if (typeof audio.removeAttribute === 'function') audio.removeAttribute('src');
                if (typeof audio.load === 'function') audio.load();
            } catch {}
        }
        setPreviewPlayUi(false);
    }

    function showDockState(nextMode) {
        mode = nextMode;
        const inputRow = getEl('composer-input-row');
        const dock = getEl('composer-voice-dock');
        const recPanel = getEl('voice-dock-recording');
        const previewPanel = getEl('voice-dock-preview');

        if (nextMode === 'idle') {
            if (dock && dock.classList) dock.classList.add('hidden');
            if (recPanel && recPanel.classList) recPanel.classList.add('hidden');
            if (previewPanel && previewPanel.classList) previewPanel.classList.add('hidden');
            if (inputRow && inputRow.classList) inputRow.classList.remove('hidden');
        } else {
            if (inputRow && inputRow.classList) inputRow.classList.add('hidden');
            if (dock && dock.classList) dock.classList.remove('hidden');
            if (nextMode === 'preview') {
                if (recPanel && recPanel.classList) recPanel.classList.add('hidden');
                if (previewPanel && previewPanel.classList) previewPanel.classList.remove('hidden');
            } else {
                if (previewPanel && previewPanel.classList) previewPanel.classList.add('hidden');
                if (recPanel && recPanel.classList) recPanel.classList.remove('hidden');
            }
        }

        notifyDockChanged();
    }

    function renderLiveWaveformScaffold() {
        const host = getEl('voice-live-waveform');
        liveBars = [];
        if (!host || typeof document === 'undefined') return;
        while (host.firstChild) host.removeChild(host.firstChild);

        for (let i = 0; i < LIVE_WAVEFORM_BAR_COUNT; i++) {
            const bar = document.createElement('span');
            bar.className = 'w-1 rounded-full bg-[#B3929D] transition-all duration-100';
            if (bar.style) bar.style.height = '20%';
            host.appendChild(bar);
            liveBars.push(bar);
        }
    }

    function flushPendingSample() {
        if (sampleTickCounter > 0) {
            collectedSamples.push(sampleBucketMax);
            sampleTickCounter = 0;
            sampleBucketMax = 0;
        }
    }

    function pushLiveSample(level) {
        sampleBucketMax = Math.max(sampleBucketMax, level);
        sampleTickCounter++;
        if (sampleTickCounter >= sampleStride) {
            if (collectedSamples.length >= MAX_COLLECTED_SAMPLES) {
                const compacted = [];
                for (let i = 0; i < collectedSamples.length; i += 2) {
                    compacted.push(Math.max(collectedSamples[i], collectedSamples[i + 1] ?? 0));
                }
                collectedSamples = compacted;
                sampleStride *= 2;
            }
            collectedSamples.push(sampleBucketMax);
            sampleTickCounter = 0;
            sampleBucketMax = 0;
        }

        if (liveBars.length > 0) {
            for (let i = 0; i < liveBars.length - 1; i++) {
                const nextH = liveBars[i + 1]?.style?.height || '20%';
                if (liveBars[i].style) liveBars[i].style.height = nextH;
            }
            const lastBar = liveBars[liveBars.length - 1];
            if (lastBar?.style) {
                lastBar.style.height = `${Math.max(15, Math.min(100, Math.round(level)))}%`;
            }
        }
    }

    function sampleAnalyserLevel() {
        if (!analyserNode || !analyserBuffer || typeof analyserNode.getByteTimeDomainData !== 'function') {
            return 24;
        }
        analyserNode.getByteTimeDomainData(analyserBuffer);
        let sumSq = 0;
        let peak = 0;
        for (let i = 0; i < analyserBuffer.length; i++) {
            const norm = Math.abs(analyserBuffer[i] - 128) / 128;
            if (norm > peak) peak = norm;
            sumSq += norm * norm;
        }
        const rms = Math.sqrt(sumSq / analyserBuffer.length);
        const scaled = Math.max(peak * 0.75, rms * 1.8) * 100;
        return Math.max(4, Math.min(100, Math.round(scaled)));
    }

    function updateRecordingHud() {
        const elapsedMs = getCurrentElapsedMs();
        const timerEl = getEl('voice-rec-timer');
        if (timerEl) {
            timerEl.textContent = formatVoiceDuration(elapsedMs);
        }

        const hintEl = getEl('voice-rec-limit-hint');
        if (hintEl && hintEl.classList) {
            if (elapsedMs >= warnDurationMs) {
                const remainSec = Math.max(0, Math.ceil((maxDurationMs - elapsedMs) / 1000));
                hintEl.textContent = `${remainSec}s left`;
                hintEl.classList.remove('hidden');
            } else if (cumulativeBytes >= warnBytes) {
                hintEl.textContent = 'Size limit near';
                hintEl.classList.remove('hidden');
            } else {
                hintEl.textContent = '';
                hintEl.classList.add('hidden');
            }
        }
    }

    function updatePausedBadgeUi(isPaused) {
        const dotEl = getEl('voice-rec-dot');
        const statusEl = getEl('voice-rec-status');
        const pauseIcon = getEl('voice-pause-icon');
        const pauseBtn = getEl('voice-pause-btn');

        if (dotEl && dotEl.classList) {
            dotEl.classList.toggle('animate-pulse', !isPaused);
            dotEl.classList.toggle('opacity-50', isPaused);
        }
        if (statusEl) {
            statusEl.textContent = isPaused ? 'PAUSED' : 'REC';
        }
        if (pauseIcon) {
            pauseIcon.textContent = isPaused ? 'mic' : 'pause';
        }
        if (pauseBtn) {
            pauseBtn.setAttribute('aria-label', isPaused ? 'Resume recording' : 'Pause recording');
            pauseBtn.setAttribute('title', isPaused ? 'Resume recording' : 'Pause recording');
        }
    }

    function updatePreviewProgressUi(currentSec = 0) {
        const totalSec = previewDurationMs > 0 ? previewDurationMs / 1000 : 0;
        const clamped = totalSec > 0 ? Math.max(0, Math.min(totalSec, currentSec)) : Math.max(0, currentSec);
        const fraction = totalSec > 0 ? Math.max(0, Math.min(1, clamped / totalSec)) : 0;

        const timerEl = getEl('voice-preview-timer');
        if (timerEl) {
            timerEl.textContent = clamped > 0
                ? `${formatVoiceDuration(clamped * 1000)} / ${formatVoiceDuration(previewDurationMs)}`
                : formatVoiceDuration(previewDurationMs);
        }

        const wf = getEl('voice-preview-waveform');
        if (wf) {
            wf.setAttribute('aria-valuenow', String(Math.round(clamped * 1000)));
            wf.setAttribute('aria-valuemax', String(Math.round(previewDurationMs)));
        }

        const activeCount = fraction <= 0 ? 0 : Math.ceil(fraction * previewBars.length);
        for (let i = 0; i < previewBars.length; i++) {
            const bar = previewBars[i];
            if (!bar || !bar.classList) continue;
            const active = i < activeCount;
            bar.classList.toggle('bg-[#735761]', active);
            bar.classList.toggle('dark:bg-[#E1BDC9]', active);
            bar.classList.toggle('bg-[#B3929D]/35', !active);
            bar.classList.toggle('dark:bg-[#E1BDC9]/30', !active);
        }
    }

    function renderPreviewDock() {
        const wf = getEl('voice-preview-waveform');
        previewBars = [];
        if (wf && typeof document !== 'undefined') {
            while (wf.firstChild) wf.removeChild(wf.firstChild);
            for (let i = 0; i < previewPeaks.length; i++) {
                const bar = document.createElement('span');
                bar.setAttribute('data-preview-bar', String(i));
                bar.className = `flex-1 min-w-[1px] max-w-[4px] rounded-full ${getVoiceBarHeightClass(previewPeaks[i])} bg-[#B3929D]/35 dark:bg-[#E1BDC9]/30 transition-colors`;
                wf.appendChild(bar);
                previewBars.push(bar);
            }
        }

        const audio = getEl('voice-preview-audio');
        if (audio) {
            revokePreviewObjectUrl();
            if (typeof createObjectUrlFn === 'function') {
                previewUrl = createObjectUrlFn(previewBlob);
            } else if (typeof URL !== 'undefined' && typeof URL.createObjectURL === 'function') {
                previewUrl = URL.createObjectURL(previewBlob);
            }
            if (previewUrl) {
                audio.src = previewUrl;
            }
        }

        setPreviewPlayUi(false);
        updatePreviewProgressUi(0);
    }

    function finishFinalizedRecording(blob, durationMs, peaks, contentType, ext, action) {
        if (action === 'abort') {
            return;
        }

        if (durationMs < minDurationMs || !blob || blob.size <= 0) {
            resetInternalState();
            showDockState('idle');
            reportError('Record at least 0.5 seconds to send a voice message.');
            return;
        }

        if (blob.size > MAX_VOICE_BYTES) {
            resetInternalState();
            showDockState('idle');
            reportError('Voice recording exceeds the 25 MB maximum size.');
            return;
        }

        if (action === 'send') {
            const targetConvId = activeConvId;
            const fileName = `voice-message${ext}`;
            resetInternalState();
            showDockState('idle');
            if (targetConvId && typeof onStartSendVoice === 'function') {
                onStartSendVoice({
                    conversationId: targetConvId,
                    blob,
                    fileName,
                    contentType,
                    durationMs,
                    waveformPeaks: peaks
                });
            }
            return;
        }

        previewBlob = blob;
        previewDurationMs = Math.max(minDurationMs, Math.min(maxDurationMs, Math.round(durationMs)));
        previewPeaks = peaks;
        previewMime = contentType;
        previewExt = ext;
        showDockState('preview');
        renderPreviewDock();
    }

    function resetInternalState() {
        ++opToken;
        if (typeof cancelPendingStart === 'function') {
            const cancel = cancelPendingStart;
            cancelPendingStart = null;
            cancel();
        }
        isStarting = false;
        clearPollTimer();
        sendIndicatorFalseIfActive();
        stopMediaHardware();
        clearPreviewAudioElement();
        revokePreviewObjectUrl();

        mediaRecorder = null;
        activeConvId = null;
        recordedChunks = [];
        cumulativeBytes = 0;
        collectedSamples = [];
        sampleStride = 1;
        sampleTickCounter = 0;
        sampleBucketMax = 0;
        liveBars = [];
        accumulatedActiveMs = 0;
        activeSegmentStartMs = null;
        previewBlob = null;
        previewDurationMs = 0;
        previewPeaks = [];
        previewBars = [];
        pendingStopMode = null;
        stopAutoTriggered = false;
        updatePausedBadgeUi(false);

        const pauseBtn = getEl('voice-pause-btn');
        if (pauseBtn) {
            if (pauseBtn.classList) pauseBtn.classList.remove('hidden');
            pauseBtn.disabled = false;
        }

        const hintEl = getEl('voice-rec-limit-hint');
        if (hintEl && hintEl.classList) {
            hintEl.textContent = '';
            hintEl.classList.add('hidden');
        }
        const timerEl = getEl('voice-rec-timer');
        if (timerEl) timerEl.textContent = '0:00';
    }

    function stopRecorderAndHandle(nextAction) {
        if (mode === 'stopping') {
            if (nextAction === 'abort') {
                forceAbortAndWipe();
            }
            return;
        }
        if (mode !== 'recording' && mode !== 'paused') {
            if (nextAction === 'abort') {
                resetInternalState();
                showDockState('idle');
            }
            return;
        }

        if (mode === 'recording' && activeSegmentStartMs !== null) {
            accumulatedActiveMs += Math.max(0, nowFn() - activeSegmentStartMs);
            activeSegmentStartMs = null;
        }

        mode = 'stopping';
        clearPollTimer();
        sendIndicatorFalseIfActive();
        pendingStopMode = nextAction;
        flushPendingSample();

        const durationMs = Math.min(maxDurationMs, Math.round(accumulatedActiveMs));
        const peaks = downsampleWaveformPeaks(collectedSamples, targetPeakCount);
        const rec = mediaRecorder;

        if (!rec || rec.state === 'inactive') {
            const norm = normalizeRecordedMime(rec?.mimeType || selectedFormat.mime, selectedFormat);
            const blob = typeof Blob !== 'undefined'
                ? new Blob(recordedChunks, { type: norm.contentType })
                : { size: cumulativeBytes, type: norm.contentType };
            stopMediaHardware();
            finishFinalizedRecording(blob, durationMs, peaks, norm.contentType, norm.ext, nextAction);
            return;
        }

        const expectedToken = opToken;
        rec.onstop = () => {
            if (expectedToken !== opToken) {
                return;
            }
            const action = pendingStopMode || 'preview';
            pendingStopMode = null;
            const norm = normalizeRecordedMime(rec.mimeType || selectedFormat.mime, selectedFormat);
            const blob = typeof Blob !== 'undefined'
                ? new Blob(recordedChunks, { type: norm.contentType })
                : { size: cumulativeBytes, type: norm.contentType };
            stopMediaHardware();
            finishFinalizedRecording(blob, durationMs, peaks, norm.contentType, norm.ext, action);
        };

        try {
            rec.stop();
        } catch {
            stopMediaHardware();
            resetInternalState();
            showDockState('idle');
        }
    }

    async function startRecording() {
        if (isStarting || mode === 'recording' || mode === 'paused' || mode === 'stopping') return false;
        const convId = getActiveConversationId();
        if (!convId || !canRecordNow()) return false;

        if (typeof onBeforeStart === 'function') {
            onBeforeStart();
        }

        const MediaRecorderCls = typeof getMediaRecorderClass === 'function'
            ? getMediaRecorderClass()
            : (typeof window !== 'undefined' ? window.MediaRecorder : (typeof MediaRecorder !== 'undefined' ? MediaRecorder : null));

        const acquireUserMedia = typeof getUserMediaFn === 'function'
            ? getUserMediaFn
            : (typeof navigator !== 'undefined' && navigator.mediaDevices && typeof navigator.mediaDevices.getUserMedia === 'function'
                ? (constraints) => navigator.mediaDevices.getUserMedia(constraints)
                : null);

        if (!MediaRecorderCls || !acquireUserMedia) {
            reportError('Voice recording is not supported in this browser.');
            return false;
        }

        const startedFromPreview = mode === 'preview';
        if (startedFromPreview) {
            pausePreview();
        }

        isStarting = true;
        const myToken = ++opToken;
        if (!startedFromPreview) {
            activeConvId = convId;
        }

        // Create AudioContext synchronously within the user click gesture (Safari/iOS rule)
        let createdAudioCtx = null;
        try {
            if (typeof createAudioContextFn === 'function') {
                createdAudioCtx = createAudioContextFn();
            } else if (typeof window !== 'undefined' && (window.AudioContext || window.webkitAudioContext)) {
                const Ctor = window.AudioContext || window.webkitAudioContext;
                createdAudioCtx = new Ctor();
            }
            if (createdAudioCtx && typeof createdAudioCtx.resume === 'function' && createdAudioCtx.state === 'suspended') {
                Promise.resolve(createdAudioCtx.resume()).catch(() => {});
            }
        } catch {
            createdAudioCtx = null;
        }
        pendingAudioContext = createdAudioCtx;

        let stream;
        try {
            stream = await acquireUserMedia({
                audio: {
                    echoCancellation: true,
                    noiseSuppression: true,
                    autoGainControl: true
                }
            });
        } catch (err) {
            if (pendingAudioContext === createdAudioCtx) {
                closePendingAudioContext();
            } else if (createdAudioCtx && typeof createdAudioCtx.close === 'function') {
                try { Promise.resolve(createdAudioCtx.close()).catch(() => {}); } catch {}
            }
            if (myToken !== opToken) return false;
            isStarting = false;
            if (!startedFromPreview) {
                resetInternalState();
                showDockState('idle');
            }
            reportError(formatMicrophoneError(err));
            return false;
        }

        if (myToken !== opToken || !canRecordNow() || String(getActiveConversationId() || '') !== String(convId)) {
            if (pendingAudioContext === createdAudioCtx) {
                closePendingAudioContext();
            } else if (createdAudioCtx && typeof createdAudioCtx.close === 'function') {
                try { Promise.resolve(createdAudioCtx.close()).catch(() => {}); } catch {}
            }
            if (myToken === opToken) {
                isStarting = false;
            }
            if (stream && typeof stream.getTracks === 'function') {
                for (const t of stream.getTracks()) {
                    try { t.stop(); } catch {}
                }
            }
            return false;
        }

        const nextFormat = selectPreferredRecordingFormat(MediaRecorderCls);

        const cleanupPendingReplacement = (recToDetach = null) => {
            if (recToDetach) {
                try {
                    recToDetach.onstart = null;
                    recToDetach.ondataavailable = null;
                    recToDetach.onerror = null;
                    recToDetach.onstop = null;
                    if (recToDetach.state && recToDetach.state !== 'inactive' && typeof recToDetach.stop === 'function') {
                        recToDetach.stop();
                    }
                } catch {}
            }
            if (pendingAudioContext === createdAudioCtx) {
                closePendingAudioContext();
            } else if (createdAudioCtx && typeof createdAudioCtx.close === 'function') {
                try { Promise.resolve(createdAudioCtx.close()).catch(() => {}); } catch {}
            }
            if (stream && typeof stream.getTracks === 'function') {
                for (const t of stream.getTracks()) {
                    try {
                        t.onended = null;
                        t.stop();
                    } catch {}
                }
            }
        };

        let nextRecorder;
        try {
            const opts = nextFormat.mime ? { mimeType: nextFormat.mime } : undefined;
            nextRecorder = opts ? new MediaRecorderCls(stream, opts) : new MediaRecorderCls(stream);
        } catch (firstErr) {
            try {
                nextRecorder = new MediaRecorderCls(stream);
            } catch (fallbackErr) {
                isStarting = false;
                cleanupPendingReplacement();
                if (!startedFromPreview) {
                    forceAbortAndWipe();
                }
                reportError(formatMicrophoneError(fallbackErr || firstErr));
                return false;
            }
        }

        const pendingChunks = [];
        let pendingBytes = 0;
        let committed = false;

        return await new Promise((resolve) => {
            let settled = false;

            const settleFailure = (errMsg) => {
                if (settled) return;
                settled = true;
                if (cancelPendingStart === abortPendingStart) {
                    cancelPendingStart = null;
                }
                isStarting = false;
                cleanupPendingReplacement(nextRecorder);
                if (!startedFromPreview && myToken === opToken) {
                    forceAbortAndWipe();
                }
                if (errMsg) {
                    reportError(errMsg);
                }
                resolve(false);
            };

            const abortPendingStart = () => {
                if (settled) return;
                settled = true;
                isStarting = false;
                cleanupPendingReplacement(nextRecorder);
                resolve(false);
            };

            cancelPendingStart = abortPendingStart;

            nextRecorder.ondataavailable = (evt) => {
                const data = evt?.data;
                if (!data || data.size <= 0) return;
                if (!committed) {
                    if (!settled && myToken === opToken) {
                        pendingChunks.push(data);
                        pendingBytes += data.size;
                    }
                    return;
                }
                if (myToken !== opToken || mediaRecorder !== nextRecorder) return;
                recordedChunks.push(data);
                cumulativeBytes += data.size;
                if (!stopAutoTriggered && cumulativeBytes >= autoStopBytes && (mode === 'recording' || mode === 'paused')) {
                    stopAutoTriggered = true;
                    stopRecorderAndHandle('preview');
                }
            };

            nextRecorder.onerror = (evt) => {
                if (!settled) {
                    settleFailure(formatMicrophoneError(evt?.error || new Error('Audio capture failed to start.')));
                    return;
                }
                if (myToken !== opToken || mediaRecorder !== nextRecorder) return;
                forceAbortAndWipe();
                reportError('Recording stopped due to an audio capture error.');
            };

            nextRecorder.onstop = () => {
                if (!settled) {
                    settleFailure('Voice recording stopped before capture started.');
                }
            };

            if (typeof stream.getAudioTracks === 'function') {
                for (const track of stream.getAudioTracks()) {
                    if (!track) continue;
                    track.onended = () => {
                        if (!settled) {
                            settleFailure('Microphone disconnected before recording started.');
                            return;
                        }
                        if (myToken !== opToken || mediaRecorder !== nextRecorder || (mode !== 'recording' && mode !== 'paused')) return;
                        const elapsed = getCurrentElapsedMs();
                        if (elapsed >= minDurationMs) {
                            stopRecorderAndHandle('preview');
                        } else {
                            forceAbortAndWipe();
                            reportError('Microphone disconnected before recording completed.');
                        }
                    };
                }
            }

            let captureStartMs = nowFn();

            nextRecorder.onstart = () => {
                if (settled) return;
                if (myToken !== opToken || !canRecordNow() || String(getActiveConversationId() || '') !== String(convId)) {
                    settleFailure(null);
                    return;
                }

                settled = true;
                committed = true;
                if (cancelPendingStart === abortPendingStart) {
                    cancelPendingStart = null;
                }
                isStarting = false;
                nextRecorder.onstart = null;
                nextRecorder.onstop = null;

                // Discard prior preview only after the replacement MediaRecorder has actually started.
                if (startedFromPreview) {
                    clearPreviewAudioElement();
                    revokePreviewObjectUrl();
                    previewBlob = null;
                    previewDurationMs = 0;
                    previewPeaks = [];
                    previewBars = [];
                }

                activeConvId = convId;
                mediaStream = stream;
                audioContext = createdAudioCtx;
                if (pendingAudioContext === createdAudioCtx) {
                    pendingAudioContext = null;
                }
                mediaRecorder = nextRecorder;
                selectedFormat = nextFormat;
                recordedChunks = pendingChunks;
                cumulativeBytes = pendingBytes;
                collectedSamples = [];
                sampleStride = 1;
                sampleTickCounter = 0;
                sampleBucketMax = 0;
                accumulatedActiveMs = 0;
                stopAutoTriggered = false;

                const supportsPause = Boolean(
                    mediaRecorder &&
                    typeof mediaRecorder.pause === 'function' &&
                    typeof mediaRecorder.resume === 'function');
                const pauseBtn = getEl('voice-pause-btn');
                if (pauseBtn) {
                    if (pauseBtn.classList) pauseBtn.classList.toggle('hidden', !supportsPause);
                    pauseBtn.disabled = !supportsPause;
                }

                if (audioContext && typeof audioContext.createMediaStreamSource === 'function' && typeof audioContext.createAnalyser === 'function') {
                    try {
                        sourceNode = audioContext.createMediaStreamSource(stream);
                        analyserNode = audioContext.createAnalyser();
                        analyserNode.fftSize = 256;
                        sourceNode.connect(analyserNode);
                        analyserBuffer = new Uint8Array(analyserNode.frequencyBinCount || 128);
                    } catch {
                        sourceNode = null;
                        analyserNode = null;
                        analyserBuffer = null;
                    }
                }

                renderLiveWaveformScaffold();
                updatePausedBadgeUi(false);
                showDockState('recording');
                activeSegmentStartMs = captureStartMs;

                sendIndicatorTrueIfNeeded(convId, true);
                updateRecordingHud();

                pollTimerId = setInterval(() => {
                    if (myToken !== opToken || mode !== 'recording') return;
                    const elapsed = getCurrentElapsedMs();
                    const level = sampleAnalyserLevel();
                    pushLiveSample(level);
                    updateRecordingHud();
                    sendIndicatorTrueIfNeeded(convId, false);

                    if (!stopAutoTriggered && elapsed >= maxDurationMs) {
                        stopAutoTriggered = true;
                        stopRecorderAndHandle('preview');
                    }
                }, 120);

                resolve(true);
            };

            try {
                captureStartMs = nowFn();
                nextRecorder.start(1000);
            } catch (err) {
                settleFailure(formatMicrophoneError(err));
            }
        });
    }

    function togglePause() {
        if (!mediaRecorder) return;
        if (mode === 'recording') {
            if (typeof mediaRecorder.pause !== 'function' || mediaRecorder.state !== 'recording') {
                reportError('Pausing recordings is not supported in this browser.');
                return;
            }
            try {
                mediaRecorder.pause();
            } catch {
                reportError('Could not pause voice recording.');
                return;
            }
            if (activeSegmentStartMs !== null) {
                accumulatedActiveMs += Math.max(0, nowFn() - activeSegmentStartMs);
                activeSegmentStartMs = null;
            }
            mode = 'paused';
            sendIndicatorFalseIfActive();
            updatePausedBadgeUi(true);
            updateRecordingHud();
            notifyDockChanged();
        } else if (mode === 'paused') {
            if (typeof mediaRecorder.resume !== 'function' || mediaRecorder.state !== 'paused') {
                reportError('Resuming recordings is not supported in this browser.');
                return;
            }
            try {
                mediaRecorder.resume();
            } catch {
                reportError('Could not resume voice recording.');
                return;
            }
            activeSegmentStartMs = nowFn();
            mode = 'recording';
            if (activeConvId) {
                sendIndicatorTrueIfNeeded(activeConvId, true);
            }
            updatePausedBadgeUi(false);
            updateRecordingHud();
            notifyDockChanged();
        }
    }

    function discard() {
        ++opToken;
        isStarting = false;
        if (mediaRecorder) {
            pendingStopMode = 'abort';
            try {
                mediaRecorder.onstop = null;
                if (mediaRecorder.state !== 'inactive' && typeof mediaRecorder.stop === 'function') {
                    mediaRecorder.stop();
                }
            } catch {}
        }
        resetInternalState();
        showDockState('idle');

        const inputEl = getEl('message-content-input');
        if (inputEl && !inputEl.disabled && typeof inputEl.focus === 'function') {
            inputEl.focus();
        }
    }

    function sendFromPreview() {
        if (mode !== 'preview' || !previewBlob || previewDurationMs < minDurationMs) {
            return null;
        }
        if (previewBlob.size > MAX_VOICE_BYTES) {
            resetInternalState();
            showDockState('idle');
            reportError('Voice recording exceeds the 25 MB maximum size.');
            return null;
        }
        const payload = {
            conversationId: activeConvId,
            blob: previewBlob,
            fileName: `voice-message${previewExt}`,
            contentType: previewMime,
            durationMs: previewDurationMs,
            waveformPeaks: [...previewPeaks]
        };
        resetInternalState();
        showDockState('idle');
        if (payload.conversationId && typeof onStartSendVoice === 'function') {
            return onStartSendVoice(payload);
        }
        return null;
    }

    function forceAbortAndWipe() {
        ++opToken;
        isStarting = false;
        if (mediaRecorder) {
            try {
                mediaRecorder.onstop = null;
                mediaRecorder.ondataavailable = null;
                if (mediaRecorder.state !== 'inactive' && typeof mediaRecorder.stop === 'function') {
                    mediaRecorder.stop();
                }
            } catch {}
        }
        resetInternalState();
        showDockState('idle');
    }

    function hasBlockingModalOpen() {
        if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return false;
        const modals = document.querySelectorAll('[aria-modal="true"]');
        for (const modal of modals) {
            if (modal && modal.classList && !modal.classList.contains('hidden')) {
                return true;
            }
        }
        return false;
    }

    const onDocumentClick = (e) => {
        if (!e.target || typeof e.target.closest !== 'function') return;

        if (e.target.closest('#composer-voice-btn')) {
            e.preventDefault();
            startRecording();
            return;
        }
        if (e.target.closest('#voice-discard-btn') || e.target.closest('#voice-preview-discard-btn')) {
            e.preventDefault();
            discard();
            return;
        }
        if (e.target.closest('#voice-pause-btn')) {
            e.preventDefault();
            togglePause();
            return;
        }
        if (e.target.closest('#voice-stop-preview-btn')) {
            e.preventDefault();
            stopRecorderAndHandle('preview');
            return;
        }
        if (e.target.closest('#voice-send-now-btn')) {
            e.preventDefault();
            stopRecorderAndHandle('send');
            return;
        }
        if (e.target.closest('#voice-preview-rerecord-btn')) {
            e.preventDefault();
            startRecording();
            return;
        }
        if (e.target.closest('#voice-preview-send-btn')) {
            e.preventDefault();
            sendFromPreview();
            return;
        }
        if (e.target.closest('#voice-preview-play-btn')) {
            e.preventDefault();
            const audio = getEl('voice-preview-audio');
            if (!audio) return;
            if (!audio.paused && !audio.ended) {
                audio.pause();
            } else {
                if (typeof onBeforeStart === 'function') onBeforeStart();
                Promise.resolve(audio.play()).catch((err) => {
                    if (err && err.name !== 'AbortError') {
                        setPreviewPlayUi(false);
                        reportError(err.name === 'NotSupportedError'
                            ? 'Audio format not supported for preview in this browser.'
                            : 'Could not play voice preview.');
                    }
                });
            }
        }
    };

    const onPreviewWaveformClick = (e) => {
        const wf = getEl('voice-preview-waveform');
        const audio = getEl('voice-preview-audio');
        if (!wf || !audio || mode !== 'preview' || previewDurationMs <= 0) return;
        if (typeof wf.getBoundingClientRect !== 'function' || typeof e.clientX !== 'number') return;
        const rect = wf.getBoundingClientRect();
        if (!rect || rect.width <= 0) return;
        const fraction = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
        const durSec = Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : (previewDurationMs / 1000);
        const targetSec = fraction * durSec;
        try { audio.currentTime = targetSec; } catch {}
        updatePreviewProgressUi(targetSec);
    };

    const onPreviewWaveformKeyDown = (e) => {
        const audio = getEl('voice-preview-audio');
        if (!audio || mode !== 'preview' || previewDurationMs <= 0) return;
        const durSec = Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : (previewDurationMs / 1000);
        const cur = Number(audio.currentTime) || 0;
        let next = null;
        if (e.key === 'ArrowRight' || e.key === 'ArrowUp') next = Math.min(durSec, cur + 5);
        else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') next = Math.max(0, cur - 5);
        else if (e.key === 'Home') next = 0;
        else if (e.key === 'End') next = durSec;
        if (next !== null) {
            if (typeof e.preventDefault === 'function') e.preventDefault();
            try { audio.currentTime = next; } catch {}
            updatePreviewProgressUi(next);
        }
    };

    const onKeyDown = (e) => {
        if (e.key === 'Escape' && mode !== 'idle' && !hasBlockingModalOpen()) {
            if (typeof e.preventDefault === 'function') e.preventDefault();
            discard();
        }
    };

    let boundPreviewAudio = null;
    let boundPreviewWaveform = null;

    const onPreviewAudioPlay = () => {
        if (typeof onBeforeStart === 'function') onBeforeStart();
        setPreviewPlayUi(true);
    };
    const onPreviewAudioPause = () => {
        setPreviewPlayUi(false);
    };
    const onPreviewAudioTimeUpdate = () => {
        const audio = getEl('voice-preview-audio');
        updatePreviewProgressUi(audio ? Number(audio.currentTime) || 0 : 0);
    };
    const onPreviewAudioEnded = () => {
        const audio = getEl('voice-preview-audio');
        if (audio) {
            try { audio.currentTime = 0; } catch {}
        }
        setPreviewPlayUi(false);
        updatePreviewProgressUi(0);
    };
    const onPreviewAudioError = () => {
        const audio = getEl('voice-preview-audio');
        const hasSrc = Boolean(audio && (audio.getAttribute('src') || audio.src));
        if (!hasSrc) return;
        setPreviewPlayUi(false);
        reportError('Preview audio playback failed in this browser.');
    };

    function init() {
        if (initialized || typeof document === 'undefined') return;
        if (typeof document.addEventListener === 'function') {
            document.addEventListener('click', onDocumentClick);
            document.addEventListener('keydown', onKeyDown);
        }

        boundPreviewWaveform = getEl('voice-preview-waveform');
        if (boundPreviewWaveform && typeof boundPreviewWaveform.addEventListener === 'function') {
            boundPreviewWaveform.addEventListener('click', onPreviewWaveformClick);
            boundPreviewWaveform.addEventListener('keydown', onPreviewWaveformKeyDown);
        }

        boundPreviewAudio = getEl('voice-preview-audio');
        if (boundPreviewAudio && typeof boundPreviewAudio.addEventListener === 'function') {
            boundPreviewAudio.addEventListener('playing', onPreviewAudioPlay);
            boundPreviewAudio.addEventListener('pause', onPreviewAudioPause);
            boundPreviewAudio.addEventListener('timeupdate', onPreviewAudioTimeUpdate);
            boundPreviewAudio.addEventListener('ended', onPreviewAudioEnded);
            boundPreviewAudio.addEventListener('error', onPreviewAudioError);
        }

        initialized = true;
    }

    function destroy() {
        forceAbortAndWipe();
        if (!initialized) return;
        if (typeof document !== 'undefined' && typeof document.removeEventListener === 'function') {
            document.removeEventListener('click', onDocumentClick);
            document.removeEventListener('keydown', onKeyDown);
        }
        if (boundPreviewWaveform && typeof boundPreviewWaveform.removeEventListener === 'function') {
            boundPreviewWaveform.removeEventListener('click', onPreviewWaveformClick);
            boundPreviewWaveform.removeEventListener('keydown', onPreviewWaveformKeyDown);
        }
        if (boundPreviewAudio && typeof boundPreviewAudio.removeEventListener === 'function') {
            boundPreviewAudio.removeEventListener('playing', onPreviewAudioPlay);
            boundPreviewAudio.removeEventListener('pause', onPreviewAudioPause);
            boundPreviewAudio.removeEventListener('timeupdate', onPreviewAudioTimeUpdate);
            boundPreviewAudio.removeEventListener('ended', onPreviewAudioEnded);
            boundPreviewAudio.removeEventListener('error', onPreviewAudioError);
        }
        boundPreviewWaveform = null;
        boundPreviewAudio = null;
        initialized = false;
    }

    return {
        init,
        destroy,
        startRecording,
        togglePause,
        pausePreview,
        stopToPreview: () => stopRecorderAndHandle('preview'),
        sendNow: () => stopRecorderAndHandle('send'),
        sendFromPreview,
        discard,
        forceAbortAndWipe,
        isDirty: () => mode !== 'idle',
        getMode: () => mode
    };
}
