/**
 * AOS Messenger - Accessible Image Lightbox & Safe URL Validator
 *
 * Security Invariants:
 * - Strictly renders kind === 'image' items triggered via [data-lightbox-trigger="true"].
 * - Enforces safe URL validation (https:// or single-slash /dev-media/download paths; rejects //, javascript:, data:, http:).
 * - Pure DOM updates via textContent / element properties (zero innerHTML, zero inline style="...").
 */

import { buildMediaInlineUrl, getMediaDownloadUrl } from './api-client.js';

/**
 * Validates that a resolved media URL is either strict HTTPS or a single-slash /dev-media/ endpoint.
 * Rejects protocol-relative //, javascript:, data:, blob:, and plaintext http:// URLs.
 * @param {string|null|undefined} rawUrl
 * @returns {boolean}
 */
export function isSafeMediaUrl(rawUrl) {
    if (!rawUrl || typeof rawUrl !== 'string') return false;
    const trimmed = rawUrl.trim();
    if (!trimmed) return false;

    if (trimmed.startsWith('/')) {
        if (trimmed.startsWith('//')) return false;
        return /^\/dev-media\/download(?:\?|$)/i.test(trimmed) ||
               /^\/api\/conversations\/[0-9a-f-]+\/messages\/\d+\/media\/[0-9a-f-]+(?:\?|$)/i.test(trimmed);
    }

    try {
        const parsed = new URL(trimmed);
        return parsed.protocol === 'https:';
    } catch {
        return false;
    }
}

/**
 * Opens a safe external/download URL using an anchor with target="_blank" and rel="noopener noreferrer".
 * @param {string} url
 * @param {string} [fileName]
 */
export function triggerSafeDownloadAnchor(url, fileName = '') {
    if (!isSafeMediaUrl(url) || typeof document === 'undefined' || typeof document.createElement !== 'function') {
        return false;
    }

    const a = document.createElement('a');
    a.href = url;
    a.target = '_blank';
    a.rel = 'noopener noreferrer';
    if (fileName) {
        a.download = fileName;
    }
    a.className = 'hidden';

    if (document.body && typeof document.body.appendChild === 'function') {
        document.body.appendChild(a);
        if (typeof a.click === 'function') {
            a.click();
        }
        if (typeof a.remove === 'function') {
            a.remove();
        }
    } else if (typeof a.click === 'function') {
        a.click();
    }

    return true;
}

/**
 * Creates the accessible image Lightbox controller bound to #media-lightbox-modal.
 * @param {{ getMediaUrlFn?: Function }} [options]
 * @returns {object}
 */
export function createMediaLightbox({ getMediaUrlFn = getMediaDownloadUrl } = {}) {
    if (typeof document === 'undefined') {
        return {
            open: () => {},
            close: () => {},
            closeIfMessage: () => {},
            resetForSwitch: () => {},
            wipe: () => {},
            destroy: () => {},
            isOpen: () => false
        };
    }

    let items = [];
    let currentIndex = -1;
    let activeMessageId = null;
    let retriedCurrent = false;
    let openGeneration = 0;
    let lastFocusedElement = null;

    function els() {
        return {
            modal: document.getElementById('media-lightbox-modal'),
            title: document.getElementById('media-lightbox-title'),
            counter: document.getElementById('media-lightbox-counter'),
            img: document.getElementById('media-lightbox-img'),
            error: document.getElementById('media-lightbox-error'),
            prevBtn: document.getElementById('media-lightbox-prev-btn'),
            nextBtn: document.getElementById('media-lightbox-next-btn'),
            downloadBtn: document.getElementById('media-lightbox-download-btn'),
            closeBtn: document.getElementById('media-lightbox-close-btn')
        };
    }

    function isOpen() {
        const { modal } = els();
        return !!(modal && modal.classList && !modal.classList.contains('hidden'));
    }

    function renderSlide() {
        const { title, counter, img, error, prevBtn, nextBtn } = els();
        const item = items[currentIndex];
        if (!item) return;

        retriedCurrent = false;
        if (title) title.textContent = item.fileName || 'Image preview';
        if (counter) {
            if (items.length > 1) {
                counter.textContent = `${currentIndex + 1} / ${items.length}`;
                counter.classList.remove('hidden');
            } else {
                counter.textContent = '';
                counter.classList.add('hidden');
            }
        }
        if (prevBtn) prevBtn.classList.toggle('hidden', items.length <= 1);
        if (nextBtn) nextBtn.classList.toggle('hidden', items.length <= 1);
        if (error) error.classList.add('hidden');
        if (img) {
            img.classList.remove('hidden');
            img.alt = item.fileName || 'Image attachment';
            img.src = buildMediaInlineUrl(item.conversationId, item.messageId, item.mediaId);
        }
    }

    function getFocusableElements() {
        const { downloadBtn, closeBtn, prevBtn, nextBtn } = els();
        return [downloadBtn, closeBtn, prevBtn, nextBtn].filter(
            el => el && !el.disabled && (!el.classList || !el.classList.contains('hidden'))
        );
    }

    function open(nextItems, index = 0) {
        if (!Array.isArray(nextItems) || nextItems.length === 0) return;
        const { modal, closeBtn } = els();
        if (!modal) return;

        if (!isOpen()) {
            lastFocusedElement = document.activeElement || null;
        }

        openGeneration++;
        items = nextItems;
        currentIndex = Math.max(0, Math.min(index, nextItems.length - 1));
        activeMessageId = nextItems[currentIndex]?.messageId ? String(nextItems[currentIndex].messageId) : null;

        renderSlide();
        modal.classList.remove('hidden');

        if (closeBtn && typeof closeBtn.focus === 'function') {
            closeBtn.focus();
        }
    }

    function close() {
        const { modal, img, title, counter, error, prevBtn, nextBtn } = els();
        const restoreTarget = lastFocusedElement;
        lastFocusedElement = null;

        openGeneration++;
        items = [];
        currentIndex = -1;
        activeMessageId = null;
        retriedCurrent = false;

        if (img) {
            img.removeAttribute('src');
            img.alt = '';
            img.classList.remove('hidden');
        }
        if (error) error.classList.add('hidden');
        if (title) title.textContent = '';
        if (counter) {
            counter.textContent = '';
            counter.classList.add('hidden');
        }
        if (prevBtn) prevBtn.classList.add('hidden');
        if (nextBtn) nextBtn.classList.add('hidden');
        if (modal) modal.classList.add('hidden');

        if (restoreTarget && typeof restoreTarget.focus === 'function' && restoreTarget.isConnected !== false) {
            restoreTarget.focus();
        }
    }

    function closeIfMessage(msgId) {
        if (!msgId || !activeMessageId) return;
        if (String(msgId) === String(activeMessageId)) {
            close();
        }
    }

    function navigate(delta) {
        if (items.length <= 1 || currentIndex < 0) return;
        currentIndex = (currentIndex + delta + items.length) % items.length;
        renderSlide();
    }

    async function handleImgError() {
        const { img, error } = els();
        const item = items[currentIndex];
        if (!item || !img) return;

        if (retriedCurrent) {
            img.classList.add('hidden');
            if (error) {
                error.textContent = 'Media unavailable';
                error.classList.remove('hidden');
            }
            return;
        }

        retriedCurrent = true;
        const gen = openGeneration;
        try {
            const payload = await getMediaUrlFn(item.conversationId, item.messageId, item.mediaId, { download: false });
            if (gen !== openGeneration) return;
            const resolvedUrl = payload?.url ?? payload?.Url ?? '';
            if (isSafeMediaUrl(resolvedUrl)) {
                img.classList.remove('hidden');
                if (error) error.classList.add('hidden');
                img.src = resolvedUrl;
                return;
            }
        } catch {
            // Fall through to unavailable fallback
        }

        if (gen === openGeneration) {
            img.classList.add('hidden');
            if (error) {
                error.textContent = 'Media unavailable';
                error.classList.remove('hidden');
            }
        }
    }

    async function handleDownloadClick() {
        const item = items[currentIndex];
        if (!item) return;

        const gen = openGeneration;
        try {
            const payload = await getMediaUrlFn(item.conversationId, item.messageId, item.mediaId, { download: true });
            if (gen !== openGeneration) return;
            const resolvedUrl = payload?.url ?? payload?.Url ?? '';
            if (isSafeMediaUrl(resolvedUrl)) {
                triggerSafeDownloadAnchor(resolvedUrl, item.fileName || '');
            }
        } catch {
            // Download URL errors are surfaced on next view or ignored gracefully
        }
    }

    const onDocumentClick = (e) => {
        if (!e.target) return;

        const trigger = typeof e.target.closest === 'function' ? e.target.closest('[data-lightbox-trigger="true"]') : null;
        if (trigger) {
            e.preventDefault();
            const row = typeof trigger.closest === 'function' ? trigger.closest('.message-bubble-row') : null;
            const triggers = row && typeof row.querySelectorAll === 'function'
                ? Array.from(row.querySelectorAll('[data-lightbox-trigger="true"]'))
                : [trigger];

            const galleryItems = triggers
                .map(btn => ({
                    conversationId: btn.getAttribute('data-conversation-id') || '',
                    messageId: btn.getAttribute('data-message-id') || '',
                    mediaId: btn.getAttribute('data-media-id') || '',
                    fileName: btn.getAttribute('data-file-name') || 'Image'
                }))
                .filter(it => it.conversationId && it.messageId && it.mediaId);

            if (galleryItems.length > 0) {
                const clickedIdx = Math.max(0, triggers.indexOf(trigger));
                open(galleryItems, clickedIdx);
            }
            return;
        }

        const closeBtn = typeof e.target.closest === 'function' ? e.target.closest('#media-lightbox-close-btn') : null;
        if (closeBtn) {
            e.preventDefault();
            close();
            return;
        }

        const prevBtn = typeof e.target.closest === 'function' ? e.target.closest('#media-lightbox-prev-btn') : null;
        if (prevBtn) {
            e.preventDefault();
            navigate(-1);
            return;
        }

        const nextBtn = typeof e.target.closest === 'function' ? e.target.closest('#media-lightbox-next-btn') : null;
        if (nextBtn) {
            e.preventDefault();
            navigate(1);
            return;
        }

        const dlBtn = typeof e.target.closest === 'function' ? e.target.closest('#media-lightbox-download-btn') : null;
        if (dlBtn) {
            e.preventDefault();
            handleDownloadClick();
            return;
        }

        const { modal } = els();
        if (modal && e.target === modal) {
            close();
        }
    };

    const onDocumentKeyDown = (e) => {
        if (!isOpen()) return;
        if (e.key === 'Escape') {
            e.preventDefault();
            close();
        } else if (e.key === 'ArrowLeft') {
            e.preventDefault();
            navigate(-1);
        } else if (e.key === 'ArrowRight') {
            e.preventDefault();
            navigate(1);
        } else if (e.key === 'Tab') {
            const focusables = getFocusableElements();
            if (focusables.length === 0) {
                e.preventDefault();
                return;
            }
            const activeEl = document.activeElement || null;
            const idx = focusables.indexOf(activeEl);
            if (e.shiftKey) {
                if (idx <= 0) {
                    e.preventDefault();
                    const target = focusables[focusables.length - 1];
                    if (typeof target.focus === 'function') target.focus();
                }
            } else if (idx === -1 || idx >= focusables.length - 1) {
                e.preventDefault();
                const target = focusables[0];
                if (typeof target.focus === 'function') target.focus();
            }
        } else if (e.key === 'Enter' || e.key === ' ') {
            const { modal, closeBtn } = els();
            const focusables = getFocusableElements();
            const activeEl = document.activeElement || null;
            const isInside = focusables.includes(activeEl) ||
                (modal && typeof modal.contains === 'function' && activeEl && modal.contains(activeEl));
            if (activeEl && !isInside) {
                e.preventDefault();
                if (closeBtn && typeof closeBtn.focus === 'function') closeBtn.focus();
            }
        }
    };

    const onImgErrorEvent = () => {
        handleImgError();
    };

    if (typeof document.addEventListener === 'function') {
        document.addEventListener('click', onDocumentClick);
        document.addEventListener('keydown', onDocumentKeyDown);
    }

    const { img: initialImg } = els();
    if (initialImg && typeof initialImg.addEventListener === 'function') {
        initialImg.addEventListener('error', onImgErrorEvent);
    }

    return {
        open,
        close,
        closeIfMessage,
        resetForSwitch: close,
        wipe: close,
        isOpen,
        destroy: () => {
            close();
            if (typeof document.removeEventListener === 'function') {
                document.removeEventListener('click', onDocumentClick);
                document.removeEventListener('keydown', onDocumentKeyDown);
            }
            const { img } = els();
            if (img && typeof img.removeEventListener === 'function') {
                img.removeEventListener('error', onImgErrorEvent);
            }
        }
    };
}
