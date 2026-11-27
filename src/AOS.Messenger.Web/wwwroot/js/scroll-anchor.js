/**
 * AOS Messenger - Bottom Scroll Anchor / Pinner Module
 *
 * Keeps #message-stream pinned to the newest message across initial SSR loads,
 * browser tab refreshes (scroll restoration), zero-refresh conversation switches,
 * and asynchronous media (image/sticker/video) or webfont height expansion —
 * while immediately yielding control the instant the user scrolls upward or
 * triggers older-message pagination.
 */

const USER_UP_KEYS = new Set(['ArrowUp', 'PageUp', 'Home']);
const DEFAULT_SETTLE_MS = 4000;
const DEFAULT_MAX_WINDOW_MS = 8000;
const REFRESH_RESTORE_GRACE_MS = 180;

/**
 * Creates a user-cancellable bottom scroll pinner bound to #message-stream and #messages-container.
 * @param {{
 *   streamEl?: HTMLElement|null,
 *   containerEl?: HTMLElement|null,
 *   windowRef?: Window|object|null,
 *   documentRef?: Document|object|null,
 *   shouldBlockPin?: (() => boolean)|null,
 *   settleMs?: number,
 *   maxWindowMs?: number,
 *   nowFn?: () => number
 * }} [options]
 */
export function createBottomScrollPinner({
    streamEl = null,
    containerEl = null,
    windowRef = typeof window !== 'undefined' ? window : null,
    documentRef = typeof document !== 'undefined' ? document : null,
    shouldBlockPin = null,
    settleMs = DEFAULT_SETTLE_MS,
    maxWindowMs = DEFAULT_MAX_WINDOW_MS,
    nowFn = () => Date.now()
} = {}) {
    let active = false;
    let destroyed = false;
    let pinStartedAtMs = 0;
    let activeSettleMs = settleMs;
    let activeMaxWindowMs = maxWindowMs;
    let lastObservedScrollTop = null;
    let isSmoothAnimating = false;
    let lastPinnedAtMs = 0;
    let lastPinnedScrollHeight = 0;
    let settleTimer = null;
    let rafId = null;
    let resizeObserver = null;
    let touchStartY = null;
    let pointerDownOnStream = false;
    let fontsHooked = false;

    function clearTimers() {
        if (settleTimer !== null) {
            clearTimeout(settleTimer);
            settleTimer = null;
        }
        if (rafId !== null && typeof cancelAnimationFrame === 'function') {
            cancelAnimationFrame(rafId);
        }
        rafId = null;
    }

    function disconnectResizeObserver() {
        if (resizeObserver && typeof resizeObserver.disconnect === 'function') {
            try {
                resizeObserver.disconnect();
            } catch {}
        }
        resizeObserver = null;
    }

    function disarm() {
        active = false;
        isSmoothAnimating = false;
        touchStartY = null;
        pointerDownOnStream = false;
        clearTimers();
        disconnectResizeObserver();
    }

    function isBlocked() {
        return typeof shouldBlockPin === 'function' && Boolean(shouldBlockPin());
    }

    function scrollNow({ smooth = false } = {}) {
        if (!active || destroyed || !streamEl) return false;
        if (isBlocked()) {
            disarm();
            return false;
        }

        const targetTop = Number(streamEl.scrollHeight || 0);
        lastPinnedScrollHeight = targetTop;
        if (smooth && typeof streamEl.scrollTo === 'function') {
            isSmoothAnimating = true;
            lastObservedScrollTop = Number(streamEl.scrollTop || 0);
            streamEl.scrollTo({ top: targetTop, behavior: 'smooth' });
        } else {
            isSmoothAnimating = false;
            streamEl.scrollTop = targetTop;
            lastObservedScrollTop = Number(streamEl.scrollTop || 0);
        }
        lastPinnedAtMs = nowFn();
        return true;
    }

    function refreshSettleTimer() {
        if (!active || destroyed) return;
        if (settleTimer !== null) {
            clearTimeout(settleTimer);
            settleTimer = null;
        }
        const elapsed = nowFn() - pinStartedAtMs;
        const remainingMax = activeMaxWindowMs - elapsed;
        if (remainingMax <= 0) {
            disarm();
            return;
        }
        const nextDelay = Math.min(activeSettleMs, remainingMax);
        settleTimer = setTimeout(() => {
            settleTimer = null;
            disarm();
        }, nextDelay);
        if (settleTimer && typeof settleTimer.unref === 'function') {
            settleTimer.unref();
        }
    }

    function onMediaOrLayoutGrowth() {
        if (!active || destroyed) return;
        if (scrollNow({ smooth: false })) {
            refreshSettleTimer();
        }
    }

    function attachResizeObserver() {
        disconnectResizeObserver();
        if (!containerEl || typeof ResizeObserver !== 'function') return;
        let sawInitialObservation = false;
        let lastObservedRectHeight = null;
        try {
            resizeObserver = new ResizeObserver((entries) => {
                const currentScrollHeight = Number(streamEl?.scrollHeight || 0);
                const lastEntry = Array.isArray(entries) && entries.length > 0 ? entries[entries.length - 1] : null;
                const rectHeight = typeof lastEntry?.contentRect?.height === 'number'
                    ? lastEntry.contentRect.height
                    : null;

                if (!sawInitialObservation) {
                    sawInitialObservation = true;
                    lastObservedRectHeight = rectHeight;
                    if (currentScrollHeight === lastPinnedScrollHeight) {
                        return;
                    }
                } else if (
                    currentScrollHeight === lastPinnedScrollHeight &&
                    (rectHeight === null || rectHeight === lastObservedRectHeight)
                ) {
                    return;
                }

                if (rectHeight !== null) {
                    lastObservedRectHeight = rectHeight;
                }
                onMediaOrLayoutGrowth();
            });
            resizeObserver.observe(containerEl);
        } catch {
            resizeObserver = null;
        }
    }

    function pin({
        smooth = false,
        settleMs: customSettleMs = settleMs,
        maxWindowMs: customMaxWindowMs = maxWindowMs
    } = {}) {
        if (destroyed || !streamEl) return false;
        if (isBlocked()) {
            disarm();
            return false;
        }

        clearTimers();
        active = true;
        pinStartedAtMs = nowFn();
        activeSettleMs = Number.isFinite(customSettleMs) && customSettleMs > 0 ? customSettleMs : settleMs;
        activeMaxWindowMs = Number.isFinite(customMaxWindowMs) && customMaxWindowMs > 0 ? customMaxWindowMs : maxWindowMs;
        touchStartY = null;
        pointerDownOnStream = false;

        scrollNow({ smooth });
        attachResizeObserver();
        refreshSettleTimer();

        if (!smooth && typeof requestAnimationFrame === 'function') {
            rafId = requestAnimationFrame(() => {
                rafId = null;
                if (!active || destroyed) return;
                scrollNow({ smooth: false });
            });
        }

        if (!fontsHooked && documentRef && documentRef.fonts && typeof documentRef.fonts.ready?.then === 'function') {
            fontsHooked = true;
            documentRef.fonts.ready.then(() => {
                if (active && !destroyed) {
                    onMediaOrLayoutGrowth();
                }
            }).catch(() => {});
        }

        return true;
    }

    // --- User intent listeners (immediately disarm so we never fight the user) ---

    const onWheel = (e) => {
        if (!active) return;
        if (typeof e?.deltaY === 'number' && e.deltaY < 0) {
            disarm();
        }
    };

    const onTouchStart = (e) => {
        if (!active) return;
        const touch = e?.touches && e.touches[0];
        touchStartY = touch && typeof touch.clientY === 'number' ? touch.clientY : null;
    };

    const onTouchMove = (e) => {
        if (!active) return;
        const touch = e?.touches && e.touches[0];
        if (!touch || typeof touch.clientY !== 'number') return;
        if (touchStartY !== null && touch.clientY > touchStartY + 6) {
            // Finger moving downward scrolls the viewport upward toward older messages
            disarm();
        }
    };

    const onTouchEnd = () => {
        touchStartY = null;
    };

    const onPointerDown = () => {
        if (!active) return;
        pointerDownOnStream = true;
    };

    const onPointerUp = () => {
        pointerDownOnStream = false;
    };

    const onKeyDown = (e) => {
        if (!active) return;
        const key = e?.key;
        if (!USER_UP_KEYS.has(key)) return;
        const target = e?.target;
        const tag = typeof target?.tagName === 'string' ? target.tagName.toLowerCase() : '';
        if (tag === 'textarea' || tag === 'input') return;
        disarm();
    };

    const onScroll = () => {
        if (!active || !streamEl) return;
        if (isBlocked()) {
            disarm();
            return;
        }
        const curTop = Number(streamEl.scrollTop || 0);
        if (lastObservedScrollTop === null) {
            lastObservedScrollTop = curTop;
            return;
        }

        // Advance watermark monotonically while scrolling downward (e.g. smooth scroll frames)
        if (curTop >= lastObservedScrollTop) {
            lastObservedScrollTop = curTop;
            const maxTop = Math.max(0, Number(streamEl.scrollHeight || 0) - Number(streamEl.clientHeight || 0));
            if (isSmoothAnimating && curTop >= maxTop - 4) {
                isSmoothAnimating = false;
            }
            return;
        }

        const movedUpward = curTop < lastObservedScrollTop - 24;
        if (!movedUpward) {
            return;
        }

        const sinceLastPin = nowFn() - lastPinnedAtMs;
        if (pointerDownOnStream || isSmoothAnimating || sinceLastPin > REFRESH_RESTORE_GRACE_MS) {
            disarm();
        } else {
            // Within the initial browser-refresh scroll-restoration window with no user input:
            // snap back to the latest message.
            scrollNow({ smooth: false });
        }
    };

    const onMediaLoadCapture = (e) => {
        if (!active) return;
        const target = e?.target;
        const tag = typeof target?.tagName === 'string' ? target.tagName.toLowerCase() : '';
        if (tag === 'img' || tag === 'video') {
            onMediaOrLayoutGrowth();
        }
    };

    const onWindowLoad = () => {
        if (!active) return;
        onMediaOrLayoutGrowth();
    };

    if (streamEl && typeof streamEl.addEventListener === 'function') {
        streamEl.addEventListener('wheel', onWheel, { passive: true });
        streamEl.addEventListener('touchstart', onTouchStart, { passive: true });
        streamEl.addEventListener('touchmove', onTouchMove, { passive: true });
        streamEl.addEventListener('touchend', onTouchEnd, { passive: true });
        streamEl.addEventListener('touchcancel', onTouchEnd, { passive: true });
        streamEl.addEventListener('pointerdown', onPointerDown, { passive: true });
        streamEl.addEventListener('scroll', onScroll, { passive: true });
    }
    if (windowRef && typeof windowRef.addEventListener === 'function') {
        windowRef.addEventListener('pointerup', onPointerUp, { passive: true });
        windowRef.addEventListener('pointercancel', onPointerUp, { passive: true });
        windowRef.addEventListener('blur', onPointerUp);
        windowRef.addEventListener('load', onWindowLoad);
    }
    if (documentRef && typeof documentRef.addEventListener === 'function') {
        documentRef.addEventListener('keydown', onKeyDown);
    }
    if (containerEl && typeof containerEl.addEventListener === 'function') {
        containerEl.addEventListener('load', onMediaLoadCapture, true);
        containerEl.addEventListener('error', onMediaLoadCapture, true);
        containerEl.addEventListener('loadedmetadata', onMediaLoadCapture, true);
    }

    function destroy() {
        if (destroyed) return;
        destroyed = true;
        disarm();
        if (streamEl && typeof streamEl.removeEventListener === 'function') {
            streamEl.removeEventListener('wheel', onWheel);
            streamEl.removeEventListener('touchstart', onTouchStart);
            streamEl.removeEventListener('touchmove', onTouchMove);
            streamEl.removeEventListener('touchend', onTouchEnd);
            streamEl.removeEventListener('touchcancel', onTouchEnd);
            streamEl.removeEventListener('pointerdown', onPointerDown);
            streamEl.removeEventListener('scroll', onScroll);
        }
        if (windowRef && typeof windowRef.removeEventListener === 'function') {
            windowRef.removeEventListener('pointerup', onPointerUp);
            windowRef.removeEventListener('pointercancel', onPointerUp);
            windowRef.removeEventListener('blur', onPointerUp);
            windowRef.removeEventListener('load', onWindowLoad);
        }
        if (documentRef && typeof documentRef.removeEventListener === 'function') {
            documentRef.removeEventListener('keydown', onKeyDown);
        }
        if (containerEl && typeof containerEl.removeEventListener === 'function') {
            containerEl.removeEventListener('load', onMediaLoadCapture, true);
            containerEl.removeEventListener('error', onMediaLoadCapture, true);
            containerEl.removeEventListener('loadedmetadata', onMediaLoadCapture, true);
        }
    }

    return {
        pin,
        disarm,
        isActive: () => active,
        destroy
    };
}
