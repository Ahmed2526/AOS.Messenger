/**
 * AOS Messenger - Accessible Image Lightbox & Safe URL Validator
 *
 * Security Invariants:
 * - Strictly renders kind === 'image' items triggered via [data-lightbox-trigger="true"].
 * - Enforces safe URL validation (https:// or single-slash /dev-media/download paths; rejects //, javascript:, data:, http:).
 * - Pure DOM updates via textContent / element properties (zero innerHTML, zero inline style="...").
 */

import { getMediaDownloadUrl } from './api-client.js';
import {
    createCachedImageLoader,
    parkLoadedFallbackImage,
    DEFAULT_CHAT_PHOTO_MAX_ITEMS,
    DEFAULT_CHAT_PHOTO_MAX_BYTES
} from './memory-image-cache.js';

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
        if (trimmed.startsWith('//') || trimmed.includes('..') || trimmed.includes('\\')) {
            return false;
        }
        return /^\/dev-media\/download(?:\?|$)/i.test(trimmed) ||
               /^\/api\/conversations\/[0-9a-f-]+\/messages\/\d+\/media\/[0-9a-f-]+(?:\?|$)/i.test(trimmed) ||
               /^\/api\/users\/[0-9a-f-]+\/avatar(?:\?|$)/i.test(trimmed) ||
               /^\/api\/stickers\/[0-9a-f-]+\/image(?:\?|$)/i.test(trimmed);
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
 * Caches loaded full-resolution photos in a bounded tab-only RAM LRU cache for the active
 * conversation and revokes ObjectURLs cleanly on delete, switch, or logout.
 * @param {{
 *   getMediaUrlFn?: Function,
 *   fetchFn?: Function|null,
 *   createObjectUrlFn?: Function|null,
 *   revokeObjectUrlFn?: Function|null,
 *   maxCachedItems?: number,
 *   maxCachedBytes?: number
 * }} [options]
 * @returns {object}
 */
export function createMediaLightbox({
    getMediaUrlFn = getMediaDownloadUrl,
    fetchFn = null,
    createObjectUrlFn = null,
    revokeObjectUrlFn = null,
    maxCachedItems = DEFAULT_CHAT_PHOTO_MAX_ITEMS,
    maxCachedBytes = DEFAULT_CHAT_PHOTO_MAX_BYTES
} = {}) {
    if (typeof document === 'undefined') {
        return {
            open: () => {},
            close: () => {},
            closeIfMessage: () => {},
            resetForSwitch: () => {},
            wipe: () => {},
            destroy: () => {},
            isOpen: () => false,
            getCacheStats: () => ({ count: 0, totalBytes: 0, maxItems: maxCachedItems, maxBytes: maxCachedBytes, inFlightCount: 0 })
        };
    }

    const photoLoader = createCachedImageLoader({
        maxItems: maxCachedItems,
        maxBytes: maxCachedBytes,
        isSafeUrlFn: isSafeMediaUrl,
        fetchFn,
        createObjectUrlFn,
        revokeObjectUrlFn
    });

    let items = [];
    let currentIndex = -1;
    let activeMessageId = null;
    let activeCacheKey = null;
    let retriedCurrent = false;
    let openGeneration = 0;
    let lastFocusedElement = null;
    let activeTransientBlob = null;
    let activeFallbackElement = null;
    let pendingFallback = null;
    let loadedFallback = null;
    let boundPrimaryImg = null;

    function makeCacheKey(item) {
        const conv = String(item?.conversationId || '').trim().toLowerCase();
        const msg = String(item?.messageId || '').trim();
        const media = String(item?.mediaId || '').trim().toLowerCase();
        return `${conv}:${msg}:${media}:original`;
    }

    function els() {
        return {
            modal: document.getElementById('media-lightbox-modal'),
            title: document.getElementById('media-lightbox-title'),
            counter: document.getElementById('media-lightbox-counter'),
            img: document.getElementById('media-lightbox-img'),
            loading: document.getElementById('media-lightbox-loading'),
            error: document.getElementById('media-lightbox-error'),
            prevBtn: document.getElementById('media-lightbox-prev-btn'),
            nextBtn: document.getElementById('media-lightbox-next-btn'),
            downloadBtn: document.getElementById('media-lightbox-download-btn'),
            closeBtn: document.getElementById('media-lightbox-close-btn')
        };
    }

    function setLoadingVisible(visible) {
        const { img, loading } = els();
        let loadingEl = loading;
        if (visible && !loadingEl && img && img.parentNode && typeof document.createElement === 'function' && typeof img.parentNode.insertBefore === 'function') {
            loadingEl = document.createElement('div');
            loadingEl.id = 'media-lightbox-loading';
            loadingEl.className = 'flex items-center gap-2 text-xs text-white/80';
            const spin = document.createElement('span');
            spin.className = 'material-symbols-outlined animate-spin text-[20px]';
            spin.textContent = 'progress_activity';
            const txt = document.createElement('span');
            txt.textContent = 'Loading photo...';
            loadingEl.appendChild(spin);
            loadingEl.appendChild(txt);
            img.parentNode.insertBefore(loadingEl, img);
        }
        if (loadingEl && loadingEl.classList) {
            loadingEl.classList.toggle('hidden', !visible);
        }
    }

    function revokeTransientBlob() {
        if (!activeTransientBlob) return;
        try {
            activeTransientBlob.revokeFn(activeTransientBlob.url);
        } catch {}
        activeTransientBlob = null;
    }

    function hideActiveFallbackElement() {
        if (!activeFallbackElement) return;
        if (activeFallbackElement.classList) {
            activeFallbackElement.classList.add('hidden');
        }
        activeFallbackElement = null;
    }

    function hasActiveImgSrc(imgEl) {
        if (!imgEl) return false;
        return typeof imgEl.hasAttribute === 'function' && imgEl.hasAttribute('src')
            ? Boolean(imgEl.getAttribute('src'))
            : Boolean(imgEl.src);
    }

    const onImgLoadEvent = () => {
        setLoadingVisible(false);
        if (pendingFallback && boundPrimaryImg && hasActiveImgSrc(boundPrimaryImg)) {
            loadedFallback = pendingFallback;
        }
    };

    const onImgErrorEvent = () => {
        handleImgError();
    };

    function bindPrimaryImgListeners(imgEl) {
        if (!imgEl || boundPrimaryImg === imgEl) return;
        if (boundPrimaryImg && typeof boundPrimaryImg.removeEventListener === 'function') {
            boundPrimaryImg.removeEventListener('load', onImgLoadEvent);
            boundPrimaryImg.removeEventListener('error', onImgErrorEvent);
        }
        boundPrimaryImg = imgEl;
        if (typeof imgEl.addEventListener === 'function') {
            imgEl.addEventListener('load', onImgLoadEvent);
            imgEl.addEventListener('error', onImgErrorEvent);
        }
    }

    function parkCurrentFallbackIfLoaded() {
        const currentLoaded = loadedFallback;
        loadedFallback = null;
        pendingFallback = null;
        if (!currentLoaded) return;

        const epochs = photoLoader.getEpochs(currentLoaded.scopeKey);
        if (epochs.globalEpoch !== currentLoaded.cacheEpoch || epochs.scopeEpoch !== currentLoaded.scopeEpoch) {
            return;
        }

        const { img } = els();
        if (!img || !hasActiveImgSrc(img)) return;
        const parked = parkLoadedFallbackImage(img);
        if (!parked) return;

        const parkedEl = parked.parkedElement;
        const parkedCacheKey = currentLoaded.cacheKey;
        if (typeof parkedEl.removeEventListener === 'function') {
            parkedEl.removeEventListener('load', onImgLoadEvent);
            parkedEl.removeEventListener('error', onImgErrorEvent);
        }
        parkedEl.addEventListener?.('error', () => {
            const wasActive = isOpen() && activeFallbackElement === parkedEl;
            photoLoader.evictKey(parkedCacheKey);
            if (wasActive) {
                hideActiveFallbackElement();
                handleImgError({ fromElement: parkedEl });
            }
        });

        boundPrimaryImg = null;
        bindPrimaryImgListeners(parked.freshPrimary);
        photoLoader.setElement(currentLoaded.cacheKey, parked.parkedElement, currentLoaded.meta);
    }

    function clearSlideMedia({ cacheCurrentFallback = true } = {}) {
        setLoadingVisible(false);
        if (cacheCurrentFallback) {
            parkCurrentFallbackIfLoaded();
        } else {
            loadedFallback = null;
            pendingFallback = null;
        }
        revokeTransientBlob();
        hideActiveFallbackElement();

        const { img } = els();
        if (img) {
            if (typeof img.removeAttribute === 'function') {
                img.removeAttribute('src');
            } else {
                img.src = '';
            }
            img.alt = '';
            if (img.classList) {
                img.classList.add('hidden');
            }
        }
    }

    function isOpen() {
        const { modal } = els();
        return !!(modal && modal.classList && !modal.classList.contains('hidden'));
    }

    function renderSlide() {
        openGeneration++;
        const gen = openGeneration;
        clearSlideMedia({ cacheCurrentFallback: true });

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
        if (!img) return;

        bindPrimaryImgListeners(img);
        const cacheKey = makeCacheKey(item);
        activeCacheKey = cacheKey;
        const cached = photoLoader.get(cacheKey);
        if (cached?.kind === 'blob') {
            img.alt = item.fileName || 'Image attachment';
            if (img.classList) img.classList.remove('hidden');
            img.src = cached.objectUrl;
            return;
        }
        if (cached?.kind === 'element' && cached.element) {
            const fallbackEl = cached.element;
            if (!fallbackEl.parentNode && img.parentNode && typeof img.parentNode.insertBefore === 'function') {
                img.parentNode.insertBefore(fallbackEl, img);
            }
            fallbackEl.alt = item.fileName || 'Image attachment';
            if (fallbackEl.classList) fallbackEl.classList.remove('hidden');
            activeFallbackElement = fallbackEl;
            return;
        }

        img.alt = item.fileName || 'Image attachment';
        setLoadingVisible(true);

        const resolveSignedUrl = async (signal) => {
            const payload = await getMediaUrlFn(item.conversationId, item.messageId, item.mediaId, {
                download: false,
                variant: 'original',
                signal
            });
            return payload?.url ?? payload?.Url ?? '';
        };

        Promise.resolve(photoLoader.load({
            cacheKey,
            scopeKey: String(item.messageId || ''),
            meta: {
                conversationId: String(item.conversationId || ''),
                messageId: String(item.messageId || ''),
                mediaId: String(item.mediaId || '')
            },
            getSignedUrlFn: resolveSignedUrl,
            refreshSignedUrlFn: resolveSignedUrl
        })).then((result) => {
            if (gen !== openGeneration || !isOpen()) {
                if (result?.status === 'blob' && !result.stored && result.revokeFn && (!isOpen() || activeCacheKey !== cacheKey)) {
                    try { result.revokeFn(result.objectUrl); } catch {}
                }
                return;
            }
            setLoadingVisible(false);

            const { img: curImg, error: curErr } = els();
            if (!curImg) return;
            bindPrimaryImgListeners(curImg);

            if (result?.status === 'blob') {
                if (!result.stored && result.revokeFn) {
                    activeTransientBlob = { url: result.objectUrl, revokeFn: result.revokeFn };
                }
                if (curErr) curErr.classList.add('hidden');
                curImg.alt = item.fileName || 'Image attachment';
                if (curImg.classList) curImg.classList.remove('hidden');
                curImg.src = result.objectUrl;
                return;
            }

            if (result?.status === 'fallback-url') {
                pendingFallback = {
                    cacheKey,
                    url: result.url,
                    cacheEpoch: result.cacheEpoch,
                    scopeEpoch: result.scopeEpoch,
                    scopeKey: result.scopeKey,
                    meta: {
                        conversationId: String(item.conversationId || ''),
                        messageId: String(item.messageId || ''),
                        mediaId: String(item.mediaId || ''),
                        cacheEpoch: result.cacheEpoch,
                        scopeEpoch: result.scopeEpoch,
                        scopeKey: result.scopeKey
                    }
                };
                if (curErr) curErr.classList.add('hidden');
                curImg.alt = item.fileName || 'Image attachment';
                if (curImg.classList) curImg.classList.remove('hidden');
                curImg.src = result.url;
                return;
            }

            if (result?.status === 'aborted') {
                return;
            }

            if (curImg.classList) curImg.classList.add('hidden');
            if (curErr) {
                curErr.textContent = 'Media unavailable';
                curErr.classList.remove('hidden');
            }
        });
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

    function close({ cacheCurrentFallback = true } = {}) {
        const { modal, title, counter, error, prevBtn, nextBtn } = els();
        const restoreTarget = lastFocusedElement;
        lastFocusedElement = null;

        openGeneration++;
        clearSlideMedia({ cacheCurrentFallback });
        items = [];
        currentIndex = -1;
        activeMessageId = null;
        activeCacheKey = null;
        retriedCurrent = false;

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
        if (msgId === null || msgId === undefined || msgId === '') return;
        const targetId = String(msgId);
        if (activeMessageId && targetId === String(activeMessageId)) {
            close({ cacheCurrentFallback: false });
        }
        photoLoader.evictScope(targetId);
    }

    function resetAndWipe() {
        close({ cacheCurrentFallback: false });
        photoLoader.clear();
    }

    function navigate(delta) {
        if (items.length <= 1 || currentIndex < 0) return;
        currentIndex = (currentIndex + delta + items.length) % items.length;
        activeMessageId = items[currentIndex]?.messageId ? String(items[currentIndex].messageId) : activeMessageId;
        renderSlide();
    }

    async function handleImgError({ fromElement = null } = {}) {
        const { img, error } = els();
        const item = items[currentIndex];
        if (!item || !img || (!fromElement && !hasActiveImgSrc(img))) return;

        pendingFallback = null;
        loadedFallback = null;
        revokeTransientBlob();
        photoLoader.evictKey(makeCacheKey(item));

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
            const payload = await getMediaUrlFn(item.conversationId, item.messageId, item.mediaId, { download: false, variant: 'original' });
            if (gen !== openGeneration || !isOpen()) return;
            const resolvedUrl = payload?.url ?? payload?.Url ?? '';
            if (isSafeMediaUrl(resolvedUrl)) {
                bindPrimaryImgListeners(img);
                img.alt = item.fileName || 'Image attachment';
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
            const payload = await getMediaUrlFn(item.conversationId, item.messageId, item.mediaId, { download: true, variant: 'original' });
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

    if (typeof document.addEventListener === 'function') {
        document.addEventListener('click', onDocumentClick);
        document.addEventListener('keydown', onDocumentKeyDown);
    }

    const { img: initialImg } = els();
    if (initialImg) {
        bindPrimaryImgListeners(initialImg);
    }

    return {
        open,
        close,
        closeIfMessage,
        resetForSwitch: resetAndWipe,
        wipe: resetAndWipe,
        isOpen,
        getCacheStats: () => photoLoader.stats(),
        destroy: () => {
            resetAndWipe();
            if (typeof document.removeEventListener === 'function') {
                document.removeEventListener('click', onDocumentClick);
                document.removeEventListener('keydown', onDocumentKeyDown);
            }
            if (boundPrimaryImg && typeof boundPrimaryImg.removeEventListener === 'function') {
                boundPrimaryImg.removeEventListener('load', onImgLoadEvent);
                boundPrimaryImg.removeEventListener('error', onImgErrorEvent);
            }
            boundPrimaryImg = null;
        }
    };
}
