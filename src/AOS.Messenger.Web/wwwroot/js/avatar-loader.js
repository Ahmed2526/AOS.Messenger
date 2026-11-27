/**
 * AOS Messenger - Thumbnail-First Profile Avatar Loader & Full-Resolution Lightbox
 *
 * Security & Architecture Invariants:
 * - Zero innerHTML / outerHTML / insertAdjacentHTML; pure DOM manipulation with textContent.
 * - Zero inline onerror= / onload= handlers (CSP 'self' safe; binds listeners via addEventListener).
 * - Zero crossorigin attributes on display <img> tags.
 * - Strictly loads lightweight 128x128 WebP thumbnails (?size=thumb) across all chat icons,
 *   headers, incoming bubbles, search cards, and profile hero; requests ?size=full ONLY when opened.
 * - Coalesces concurrent requests per (userId, version, size) and evicts on SignalR removal/block.
 */

import { getUserAvatarUrl } from './api-client.js';
import { isSafeMediaUrl } from './media-lightbox.js';
import {
    createCachedImageLoader,
    parkLoadedFallbackImage,
    setBoundedMapEntry,
    DEFAULT_AVATAR_FULL_MAX_ITEMS,
    DEFAULT_AVATAR_FULL_MAX_BYTES
} from './memory-image-cache.js';

const AVATAR_URL_CACHE_TTL_MS = 4 * 60 * 1000;
const AVATAR_URL_SAFETY_MARGIN_MS = 60 * 1000;

const avatarUrlCache = new Map();
const inFlightRequests = new Map();
const avatarUrlUserFullEvictEpochs = new Map();
const avatarUrlUserVersionEpochs = new Map();
let avatarUrlGlobalEpoch = 0;
const slotHydrationTokens = new WeakMap();
const avatarFullImageLoader = createCachedImageLoader({
    maxItems: DEFAULT_AVATAR_FULL_MAX_ITEMS,
    maxBytes: DEFAULT_AVATAR_FULL_MAX_BYTES,
    isSafeUrlFn: isSafeMediaUrl
});

let lightboxState = {
    openGeneration: 0,
    activeUserId: null,
    activeVersion: null,
    activeCacheKey: null,
    activeDisplayName: '',
    retriedCurrent: false,
    lastFocusedElement: null,
    docKeyDownBound: null,
    getAvatarUrlFn: null,
    nowFn: null,
    fetchFn: null,
    createObjectUrlFn: null,
    revokeObjectUrlFn: null,
    activeTransientBlob: null,
    activeFallbackElement: null,
    pendingFallback: null,
    loadedFallback: null,
    boundPrimaryImg: null
};

function normalizeId(value) {
    if (value === null || value === undefined) return '';
    if (typeof value === 'object' && value.value !== undefined) {
        return String(value.value).trim().toLowerCase();
    }
    return String(value).trim().toLowerCase();
}

function normalizeSize(size) {
    return String(size || '').trim().toLowerCase() === 'full' ? 'full' : 'thumb';
}

function makeCacheKey(userId, version, size) {
    return `${normalizeId(userId)}:${String(version || '').trim()}:${normalizeSize(size)}`;
}

function closeTransientPopovers() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
    const emojiPanel = document.getElementById('emoji-picker-panel');
    if (emojiPanel && emojiPanel.classList) {
        emojiPanel.classList.add('hidden');
    }
    const emojiBtn = document.getElementById('composer-emoji-btn');
    if (emojiBtn && typeof emojiBtn.setAttribute === 'function') {
        emojiBtn.setAttribute('aria-expanded', 'false');
    }
    const attachMenu = document.getElementById('composer-attach-menu');
    if (attachMenu && attachMenu.classList) {
        attachMenu.classList.add('hidden');
    }
    const attachBtn = document.getElementById('composer-attach-btn');
    if (attachBtn && typeof attachBtn.setAttribute === 'function') {
        attachBtn.setAttribute('aria-expanded', 'false');
    }
    const reactionPopover = document.getElementById('reaction-picker-popover');
    if (reactionPopover && reactionPopover.classList) {
        reactionPopover.classList.add('hidden');
    }
}

/**
 * Derives 1- or 2-character uppercase initials from displayName or handle.
 * @param {string|null|undefined} name
 * @param {string|null|undefined} [handle]
 * @returns {string}
 */
export function getAvatarInitials(name, handle = '') {
    const raw = (name && String(name).trim()) || (handle && String(handle).trim()) || 'U';
    const parts = raw.split(/\s+/).filter(Boolean);
    if (parts.length > 1 && parts[0].length > 0 && parts[1].length > 0) {
        return (parts[0][0] + parts[1][0]).toUpperCase();
    }
    if (parts.length === 1 && parts[0].length > 0) {
        return parts[0].substring(0, Math.min(2, parts[0].length)).toUpperCase();
    }
    return 'U';
}

/**
 * Clears all cached signed avatar URLs, cached full-size Blobs, in-flight promises, and closes the avatar lightbox.
 */
export function clearAvatarUrlCache() {
    avatarUrlGlobalEpoch++;
    avatarUrlUserFullEvictEpochs.clear();
    avatarUrlUserVersionEpochs.clear();
    avatarUrlCache.clear();
    inFlightRequests.clear();
    closeAvatarLightbox({ cacheCurrentFallback: false });
    avatarFullImageLoader.clear();
}

/**
 * Evicts all cached signed avatar URLs, full-size Blobs, and in-flight fetches for a specific userId.
 * @param {string|object|null|undefined} userId
 * @param {{ preserveVersion?: string|null }} [options]
 */
export function evictUserAvatarCache(userId, { preserveVersion = null } = {}) {
    const norm = normalizeId(userId);
    if (!norm) return;
    const trimmedPreserve = preserveVersion ? String(preserveVersion).trim() : '';
    const prefix = `${norm}:`;
    const preservePrefix = trimmedPreserve ? `${norm}:${trimmedPreserve}:` : '';
    for (const key of Array.from(avatarUrlCache.keys())) {
        const keyStr = String(key);
        if (keyStr.startsWith(prefix) && (!preservePrefix || !keyStr.startsWith(preservePrefix))) {
            avatarUrlCache.delete(key);
        }
    }
    for (const key of Array.from(inFlightRequests.keys())) {
        const keyStr = String(key);
        if (keyStr.startsWith(prefix) && (!preservePrefix || !keyStr.startsWith(preservePrefix))) {
            inFlightRequests.delete(key);
        }
    }
    if (!trimmedPreserve) {
        avatarUrlUserFullEvictEpochs.set(norm, (avatarUrlUserFullEvictEpochs.get(norm) || 0) + 1);
        if (lightboxState.activeUserId && normalizeId(lightboxState.activeUserId) === norm) {
            lightboxState.pendingFallback = null;
            lightboxState.loadedFallback = null;
        }
        avatarFullImageLoader.evictScope(norm);
    } else {
        const prevVerEpoch = avatarUrlUserVersionEpochs.get(norm)?.epoch || 0;
        avatarUrlUserVersionEpochs.set(norm, { epoch: prevVerEpoch + 1, preserveVersion: trimmedPreserve });
        if (lightboxState.activeUserId && normalizeId(lightboxState.activeUserId) === norm && lightboxState.activeVersion !== trimmedPreserve) {
            lightboxState.pendingFallback = null;
            lightboxState.loadedFallback = null;
        }
        avatarFullImageLoader.evictScopeExcept(norm, makeCacheKey(norm, trimmedPreserve, 'full'));
    }
}

/**
 * Evicts only cached ?size=thumb URL entries for userId (preserving ?size=full Blob/fallback caches
 * and keeping in-flight requests active so concurrent thumb retries coalesce).
 * @param {string|object|null|undefined} userId
 * @param {string|null|undefined} [version]
 * @param {string|null|undefined} [expectedFailedUrl]
 */
export function evictUserAvatarThumbUrlCache(userId, version = null, expectedFailedUrl = null) {
    const norm = normalizeId(userId);
    if (!norm) return;
    const trimmedVersion = version ? String(version).trim() : '';
    const exactKey = trimmedVersion ? makeCacheKey(norm, trimmedVersion, 'thumb') : '';
    const prefix = `${norm}:`;
    for (const [key, entry] of Array.from(avatarUrlCache.entries())) {
        const keyStr = String(key);
        const matches = exactKey
            ? keyStr === exactKey
            : (keyStr.startsWith(prefix) && keyStr.endsWith(':thumb'));
        if (!matches) continue;
        if (expectedFailedUrl && entry && entry.url !== expectedFailedUrl) continue;
        avatarUrlCache.delete(key);
    }
}

/**
 * Returns live telemetry for the full-size avatar RAM LRU cache.
 */
export function getAvatarFullBlobCacheStats() {
    return avatarFullImageLoader.stats();
}

/**
 * Resolves and caches a signed avatar URL for (userId, version, size) while coalescing
 * concurrent fetches so rendering many icons/bubbles for the same peer causes 1 request.
 * @param {string|object} userId
 * @param {string|null|undefined} version
 * @param {{ size?: 'thumb'|'full', bypassCache?: boolean, getAvatarUrlFn?: Function, nowFn?: Function }} [options]
 * @returns {Promise<string|null>}
 */
export async function resolveAvatarUrl(
    userId,
    version,
    { size = 'thumb', bypassCache = false, getAvatarUrlFn = getUserAvatarUrl, nowFn = () => Date.now() } = {}
) {
    const normId = normalizeId(userId);
    const trimmedVersion = version ? String(version).trim() : '';
    if (!normId || !trimmedVersion) return null;

    const variant = normalizeSize(size);
    const cacheKey = makeCacheKey(normId, trimmedVersion, variant);
    const now = nowFn();

    if (!bypassCache && avatarUrlCache.has(cacheKey)) {
        const entry = avatarUrlCache.get(cacheKey);
        if (entry && entry.expiresAtMs > now && isSafeMediaUrl(entry.url)) {
            return entry.url;
        }
        avatarUrlCache.delete(cacheKey);
    }

    if (!bypassCache && inFlightRequests.has(cacheKey)) {
        return inFlightRequests.get(cacheKey);
    }

    const startGlobalEpoch = avatarUrlGlobalEpoch;
    const startUserFullEpoch = avatarUrlUserFullEvictEpochs.get(normId) || 0;
    const startUserVersionEpoch = avatarUrlUserVersionEpochs.get(normId)?.epoch || 0;

    const promise = (async () => {
        try {
            const dto = await getAvatarUrlFn(normId, { size: variant });
            const resolvedUrl = dto?.url ?? dto?.Url ?? '';
            if (!isSafeMediaUrl(resolvedUrl)) {
                return null;
            }

            if (avatarUrlGlobalEpoch !== startGlobalEpoch) return null;
            if ((avatarUrlUserFullEvictEpochs.get(normId) || 0) !== startUserFullEpoch) return null;
            const latestVersionState = avatarUrlUserVersionEpochs.get(normId);
            if (latestVersionState
                && latestVersionState.epoch !== startUserVersionEpoch
                && latestVersionState.preserveVersion !== trimmedVersion) {
                return null;
            }

            const currentNow = nowFn();
            const rawExpiry = dto?.expiresAtUtc ?? dto?.ExpiresAtUtc ?? null;
            const parsedExpiryMs = rawExpiry ? Date.parse(rawExpiry) : NaN;
            const defaultExpiryMs = currentNow + AVATAR_URL_CACHE_TTL_MS;
            const expiresAtMs = Number.isFinite(parsedExpiryMs)
                ? Math.min(parsedExpiryMs - AVATAR_URL_SAFETY_MARGIN_MS, defaultExpiryMs)
                : defaultExpiryMs;

            if (expiresAtMs > currentNow) {
                setBoundedMapEntry(avatarUrlCache, cacheKey, {
                    url: resolvedUrl,
                    expiresAtMs
                });
            }

            return resolvedUrl;
        } catch {
            return null;
        }
    })().finally(() => {
        if (inFlightRequests.get(cacheKey) === promise) {
            inFlightRequests.delete(cacheKey);
        }
    });

    if (!bypassCache) {
        inFlightRequests.set(cacheKey, promise);
    }
    return promise;
}

function ensureSlotChildren(slotEl, name, handle, { createImg = true } = {}) {
    let initialsEl = typeof slotEl.querySelector === 'function'
        ? slotEl.querySelector('[data-avatar-initials]')
        : null;
    if (!initialsEl && typeof document !== 'undefined' && typeof document.createElement === 'function') {
        initialsEl = document.createElement('span');
        initialsEl.setAttribute('data-avatar-initials', 'true');
        initialsEl.textContent = getAvatarInitials(name, handle);
        slotEl.appendChild(initialsEl);
    } else if (initialsEl && (name || handle)) {
        initialsEl.textContent = getAvatarInitials(name, handle);
    }

    let imgEl = typeof slotEl.querySelector === 'function'
        ? slotEl.querySelector('[data-avatar-img]')
        : null;
    if (!imgEl && createImg && typeof document !== 'undefined' && typeof document.createElement === 'function') {
        imgEl = document.createElement('img');
        imgEl.setAttribute('data-avatar-img', 'true');
        imgEl.setAttribute('width', '128');
        imgEl.setAttribute('height', '128');
        imgEl.className = 'w-full h-full object-cover rounded-full hidden';
        imgEl.loading = 'lazy';
        imgEl.decoding = 'async';
        imgEl.draggable = false;
        imgEl.setAttribute('draggable', 'false');
        imgEl.alt = name ? `${name}'s profile photo` : 'Profile photo';
        slotEl.appendChild(imgEl);
    }

    return { initialsEl, imgEl };
}

function setSlotFallbackVisible(slotEl, initialsEl, imgEl) {
    if (imgEl) {
        // Strictly remove the src attribute instead of setting imgEl.src = ''.
        // Assigning imgEl.src = '' re-adds src="" in browsers, triggering an infinite
        // error-event loop, unbounded RAM growth, and a permanently stuck page load spinner.
        if (typeof imgEl.removeAttribute === 'function'
            && (typeof imgEl.hasAttribute !== 'function' || imgEl.hasAttribute('src'))) {
            imgEl.removeAttribute('src');
        }
        if (imgEl.classList) {
            imgEl.classList.add('hidden');
        }
    }
    if (initialsEl && initialsEl.classList) {
        initialsEl.classList.remove('hidden');
    }
    if (slotEl && slotEl.getAttribute('data-avatar-clickable') === 'true') {
        slotEl.setAttribute('data-avatar-lightbox-trigger', 'false');
        if (slotEl.classList) slotEl.classList.remove('cursor-pointer');
        if (typeof slotEl.removeAttribute === 'function') {
            slotEl.removeAttribute('role');
            slotEl.removeAttribute('tabindex');
            slotEl.removeAttribute('aria-label');
        }
    }
}

function hasNonEmptySrc(el) {
    if (!el) return false;
    if (typeof el.hasAttribute === 'function' && el.hasAttribute('src')) {
        return Boolean(el.getAttribute('src'));
    }
    return Boolean(el.src);
}

function bindSlotImageListeners(slotEl, initialsEl, imgEl, { getAvatarUrlFn = getUserAvatarUrl, nowFn = () => Date.now() } = {}) {
    if (!imgEl || imgEl.getAttribute('data-avatar-bound') === 'true' || typeof imgEl.addEventListener !== 'function') {
        return;
    }
    imgEl.setAttribute('data-avatar-bound', 'true');

    imgEl.addEventListener('load', () => {
        const currentVer = slotEl.getAttribute('data-avatar-version') || '';
        if (!currentVer || !hasNonEmptySrc(imgEl)) {
            setSlotFallbackVisible(slotEl, initialsEl, imgEl);
            return;
        }
        imgEl.removeAttribute('data-avatar-retried');
        if (imgEl.classList) imgEl.classList.remove('hidden');
        if (initialsEl && initialsEl.classList) initialsEl.classList.add('hidden');
    });

    imgEl.addEventListener('error', async () => {
        // Ignore error events when no src attribute is present to prevent feedback loops.
        if (!hasNonEmptySrc(imgEl)) {
            return;
        }
        const userId = slotEl.getAttribute('data-avatar-user-id') || '';
        const version = slotEl.getAttribute('data-avatar-version') || '';
        if (!userId || !version || imgEl.getAttribute('data-avatar-retried') === 'true') {
            setSlotFallbackVisible(slotEl, initialsEl, imgEl);
            return;
        }

        const failedSrc = (typeof imgEl.getAttribute === 'function' ? imgEl.getAttribute('src') : null) || imgEl.src || '';
        imgEl.setAttribute('data-avatar-retried', 'true');
        evictUserAvatarThumbUrlCache(userId, version, failedSrc);
        const freshUrl = await resolveAvatarUrl(userId, version, {
            size: 'thumb',
            getAvatarUrlFn,
            nowFn
        });

        if (slotEl.isConnected === false || imgEl.isConnected === false) {
            return;
        }
        const stillUserId = slotEl.getAttribute('data-avatar-user-id') || '';
        const stillVersion = slotEl.getAttribute('data-avatar-version') || '';
        if (freshUrl && normalizeId(stillUserId) === normalizeId(userId) && stillVersion === version) {
            imgEl.src = freshUrl;
        } else {
            setSlotFallbackVisible(slotEl, initialsEl, imgEl);
        }
    });
}

/**
 * Hydrates a single [data-avatar-slot="true"] container cleanly.
 * Always resolves ?size=thumb exclusively (never ?size=full).
 * @param {HTMLElement|null} slotEl
 * @param {{ getAvatarUrlFn?: Function, nowFn?: Function }} [options]
 */
export async function hydrateAvatarSlot(slotEl, { getAvatarUrlFn = getUserAvatarUrl, nowFn = () => Date.now() } = {}) {
    if (!slotEl || typeof slotEl.getAttribute !== 'function') return;

    const slotToken = (slotHydrationTokens.get(slotEl) || 0) + 1;
    slotHydrationTokens.set(slotEl, slotToken);

    const userId = slotEl.getAttribute('data-avatar-user-id') || '';
    const version = slotEl.getAttribute('data-avatar-version') || '';
    const name = slotEl.getAttribute('data-avatar-name') || '';
    const handle = slotEl.getAttribute('data-avatar-handle') || '';

    const hasAvatar = Boolean(userId && version);
    const { initialsEl, imgEl } = ensureSlotChildren(slotEl, name, handle, { createImg: hasAvatar });

    if (!hasAvatar) {
        if (imgEl && typeof imgEl.removeAttribute === 'function') {
            imgEl.removeAttribute('data-avatar-loaded-key');
            imgEl.removeAttribute('data-avatar-retried');
        }
        setSlotFallbackVisible(slotEl, initialsEl, imgEl);
        return;
    }

    bindSlotImageListeners(slotEl, initialsEl, imgEl, { getAvatarUrlFn, nowFn });

    if (imgEl) {
        imgEl.alt = name ? `${name}'s profile photo` : 'Profile photo';
    }

    if (slotEl.getAttribute('data-avatar-clickable') === 'true') {
        slotEl.setAttribute('data-avatar-lightbox-trigger', 'true');
        if (slotEl.classList) slotEl.classList.add('cursor-pointer');
        const tag = String(slotEl.tagName || '').toLowerCase();
        if (tag !== 'button' && tag !== 'a') {
            slotEl.setAttribute('role', 'button');
            slotEl.setAttribute('tabindex', '0');
        }
        slotEl.setAttribute('aria-label', name ? `View ${name}'s profile photo` : 'View profile photo');
    }

    const targetKey = `${normalizeId(userId)}:${version}`;
    if (imgEl && imgEl.getAttribute('data-avatar-loaded-key') === targetKey && imgEl.getAttribute('src')) {
        if (imgEl.classList) imgEl.classList.remove('hidden');
        if (initialsEl && initialsEl.classList) initialsEl.classList.add('hidden');
        return;
    }

    const url = await resolveAvatarUrl(userId, version, {
        size: 'thumb',
        getAvatarUrlFn,
        nowFn
    });

    // Guard against detached DOM nodes, superseded hydrations, or concurrent profile change/clear while resolveAvatarUrl was awaiting
    if (slotHydrationTokens.get(slotEl) !== slotToken || slotEl.isConnected === false || (imgEl && imgEl.isConnected === false)) {
        return;
    }
    const currentUserId = slotEl.getAttribute('data-avatar-user-id') || '';
    const currentVersion = slotEl.getAttribute('data-avatar-version') || '';
    if (normalizeId(currentUserId) !== normalizeId(userId) || currentVersion !== version) {
        return;
    }

    if (!url || !imgEl) {
        setSlotFallbackVisible(slotEl, initialsEl, imgEl);
        return;
    }

    imgEl.removeAttribute('data-avatar-retried');
    imgEl.setAttribute('data-avatar-loaded-key', targetKey);
    imgEl.src = url;
    if (imgEl.classList) imgEl.classList.remove('hidden');
    if (initialsEl && initialsEl.classList) initialsEl.classList.add('hidden');
}

/**
 * Hydrates all [data-avatar-slot="true"] elements under rootEl with thumbnail URLs.
 * Uses single-selector querySelectorAll for MockDomPrelude compatibility.
 * @param {HTMLElement|Document|null} [rootEl]
 * @param {{ getAvatarUrlFn?: Function, nowFn?: Function }} [options]
 */
export function hydrateAllAvatars(rootEl = typeof document !== 'undefined' ? document : null, options = {}) {
    if (!rootEl || typeof rootEl.querySelectorAll !== 'function') return [];
    const slots = rootEl.querySelectorAll('[data-avatar-slot="true"]');
    const promises = [];
    for (const slot of slots) {
        promises.push(hydrateAvatarSlot(slot, options));
    }
    return Promise.all(promises);
}

/**
 * Evicts cache for userId and updates every matching DOM avatar slot & display-name label live.
 * Reverts to initials and auto-closes the avatar lightbox if avatarVersion becomes null/empty.
 * @param {string|object} userId
 * @param {string|null|undefined} avatarVersion
 * @param {string|null} [displayName]
 * @param {{ getAvatarUrlFn?: Function, nowFn?: Function }} [options]
 */
export function updateUserAvatarAcrossDom(userId, avatarVersion, displayName = null, options = {}) {
    const normId = normalizeId(userId);
    if (!normId || typeof document === 'undefined') return Promise.resolve();

    const nextVersion = avatarVersion ? String(avatarVersion) : '';
    const isLightboxUser = Boolean(lightboxState.activeUserId && normalizeId(lightboxState.activeUserId) === normId);
    const prevVersion = isLightboxUser ? lightboxState.activeVersion : null;
    const preserveOpenLightbox = Boolean(nextVersion && isLightboxUser && isAvatarLightboxOpen() && prevVersion === nextVersion);

    if (!preserveOpenLightbox) {
        evictUserAvatarCache(normId, { preserveVersion: nextVersion || null });
    }
    if (!nextVersion) {
        closeAvatarLightboxIfUser(normId);
    } else if (isLightboxUser) {
        lightboxState.activeVersion = nextVersion;
        if (displayName) {
            lightboxState.activeDisplayName = displayName;
            const titleEl = document.getElementById('avatar-lightbox-title');
            if (titleEl) titleEl.textContent = displayName;
        }
        if (isAvatarLightboxOpen() && prevVersion !== nextVersion) {
            void openAvatarLightbox(normId, nextVersion, lightboxState.activeDisplayName, {
                getAvatarUrlFn: options.getAvatarUrlFn || lightboxState.getAvatarUrlFn || getUserAvatarUrl,
                nowFn: options.nowFn || lightboxState.nowFn || (() => Date.now()),
                fetchFn: options.fetchFn ?? lightboxState.fetchFn,
                createObjectUrlFn: options.createObjectUrlFn ?? lightboxState.createObjectUrlFn,
                revokeObjectUrlFn: options.revokeObjectUrlFn ?? lightboxState.revokeObjectUrlFn
            });
        }
    }

    const promises = [];
    if (typeof document.querySelectorAll === 'function') {
        const slots = document.querySelectorAll('[data-avatar-slot="true"]');
        for (const slot of slots) {
            if (!slot || typeof slot.getAttribute !== 'function') continue;
            const slotUserId = normalizeId(slot.getAttribute('data-avatar-user-id'));
            if (slotUserId !== normId) continue;

            if (displayName) {
                slot.setAttribute('data-avatar-name', displayName);
            }
            slot.setAttribute('data-avatar-version', avatarVersion ? String(avatarVersion) : '');
            promises.push(hydrateAvatarSlot(slot, options));
        }

        if (displayName) {
            const nameNodes = document.querySelectorAll('[data-avatar-user-display-name]');
            for (const node of nameNodes) {
                if (!node || typeof node.getAttribute !== 'function') continue;
                if (normalizeId(node.getAttribute('data-avatar-user-display-name')) === normId) {
                    node.textContent = displayName;
                }
            }
        }
    }

    return Promise.all(promises);
}

function getAvatarLightboxElements() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return {};
    }
    return {
        modal: document.getElementById('avatar-lightbox-modal'),
        title: document.getElementById('avatar-lightbox-title'),
        img: document.getElementById('avatar-lightbox-img'),
        loading: document.getElementById('avatar-lightbox-loading'),
        error: document.getElementById('avatar-lightbox-error'),
        closeBtn: document.getElementById('avatar-lightbox-close-btn')
    };
}

function ensureAvatarLightboxModal() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return null;
    }

    const existing = document.getElementById('avatar-lightbox-modal');
    if (existing) {
        return existing;
    }

    if (typeof document.createElement !== 'function' || !document.body) {
        return null;
    }

    const modal = document.createElement('div');
    modal.id = 'avatar-lightbox-modal';
    modal.setAttribute('role', 'dialog');
    modal.setAttribute('aria-modal', 'true');
    modal.setAttribute('aria-labelledby', 'avatar-lightbox-title');
    modal.className = 'hidden fixed inset-0 z-[70] flex flex-col items-center justify-center bg-black/85 p-4 backdrop-blur-xs';

    const topBar = document.createElement('div');
    topBar.className = 'w-full max-w-2xl flex items-center justify-between gap-3 text-white mb-3';

    const title = document.createElement('h3');
    title.id = 'avatar-lightbox-title';
    title.className = 'text-xs sm:text-sm font-semibold truncate';
    title.textContent = 'Profile photo';
    topBar.appendChild(title);

    const closeBtn = document.createElement('button');
    closeBtn.id = 'avatar-lightbox-close-btn';
    closeBtn.type = 'button';
    closeBtn.className = 'w-8 h-8 rounded-lg flex items-center justify-center text-white/90 hover:text-white hover:bg-white/15 transition-colors cursor-pointer';
    closeBtn.title = 'Close (Esc)';
    closeBtn.setAttribute('aria-label', 'Close profile photo viewer');
    const closeIcon = document.createElement('span');
    closeIcon.className = 'material-symbols-outlined text-[20px]';
    closeIcon.textContent = 'close';
    closeBtn.appendChild(closeIcon);
    topBar.appendChild(closeBtn);
    modal.appendChild(topBar);

    const body = document.createElement('div');
    body.className = 'relative flex items-center justify-center w-full max-w-2xl flex-1 min-h-0';

    const loading = document.createElement('div');
    loading.id = 'avatar-lightbox-loading';
    loading.className = 'hidden flex items-center gap-2 text-xs text-white/80';
    const spinIcon = document.createElement('span');
    spinIcon.className = 'material-symbols-outlined animate-spin text-[20px]';
    spinIcon.textContent = 'progress_activity';
    const loadTxt = document.createElement('span');
    loadTxt.textContent = 'Loading full photo...';
    loading.appendChild(spinIcon);
    loading.appendChild(loadTxt);
    body.appendChild(loading);

    const img = document.createElement('img');
    img.id = 'avatar-lightbox-img';
    img.alt = 'Profile photo';
    img.draggable = false;
    img.setAttribute('draggable', 'false');
    img.className = 'hidden max-h-[78vh] max-w-[90vw] w-auto h-auto object-contain rounded-2xl shadow-2xl select-none';
    body.appendChild(img);

    const errorEl = document.createElement('div');
    errorEl.id = 'avatar-lightbox-error';
    errorEl.className = 'hidden px-4 py-3 rounded-xl bg-white/10 text-white text-xs font-medium';
    errorEl.textContent = 'Photo unavailable';
    body.appendChild(errorEl);

    modal.appendChild(body);
    document.body.appendChild(modal);

    if (typeof closeBtn.addEventListener === 'function') {
        closeBtn.addEventListener('click', (e) => {
            if (e && typeof e.preventDefault === 'function') e.preventDefault();
            closeAvatarLightbox();
        });
    }

    if (typeof modal.addEventListener === 'function') {
        modal.addEventListener('click', (e) => {
            if (e && e.target === modal) {
                closeAvatarLightbox();
            }
        });
    }

    bindPrimaryAvatarImgListeners(img);

    return modal;
}

function revokeLightboxTransientBlob() {
    if (!lightboxState.activeTransientBlob) return;
    try {
        lightboxState.activeTransientBlob.revokeFn(lightboxState.activeTransientBlob.url);
    } catch {}
    lightboxState.activeTransientBlob = null;
}

function hideLightboxFallbackElement() {
    if (!lightboxState.activeFallbackElement) return;
    if (lightboxState.activeFallbackElement.classList) {
        lightboxState.activeFallbackElement.classList.add('hidden');
    }
    lightboxState.activeFallbackElement = null;
}

function onAvatarLightboxImgLoad() {
    const { loading: lEl, error: eEl, img } = getAvatarLightboxElements();
    if (lEl && lEl.classList) lEl.classList.add('hidden');
    if (eEl && eEl.classList) eEl.classList.add('hidden');
    if (img && img.classList) img.classList.remove('hidden');
    if (lightboxState.pendingFallback && img && hasNonEmptySrc(img)) {
        lightboxState.loadedFallback = lightboxState.pendingFallback;
    }
}

async function onAvatarLightboxImgError({ fromElement = null } = {}) {
    const { img: curImg, loading: lEl, error: eEl } = getAvatarLightboxElements();
    if (!curImg || (!fromElement && !hasNonEmptySrc(curImg))) {
        return;
    }
    const gen = lightboxState.openGeneration;
    lightboxState.pendingFallback = null;
    lightboxState.loadedFallback = null;
    revokeLightboxTransientBlob();

    if (lightboxState.activeUserId && lightboxState.activeVersion) {
        avatarFullImageLoader.evictKey(makeCacheKey(lightboxState.activeUserId, lightboxState.activeVersion, 'full'));
    }

    if (!lightboxState.activeUserId || !lightboxState.activeVersion || lightboxState.retriedCurrent) {
        if (curImg.classList) curImg.classList.add('hidden');
        if (lEl && lEl.classList) lEl.classList.add('hidden');
        if (eEl) {
            eEl.textContent = 'Photo unavailable';
            if (eEl.classList) eEl.classList.remove('hidden');
        }
        return;
    }

    lightboxState.retriedCurrent = true;
    evictUserAvatarCache(lightboxState.activeUserId);
    const freshUrl = await resolveAvatarUrl(
        lightboxState.activeUserId,
        lightboxState.activeVersion,
        {
            size: 'full',
            bypassCache: true,
            getAvatarUrlFn: lightboxState.getAvatarUrlFn || getUserAvatarUrl,
            nowFn: lightboxState.nowFn || (() => Date.now())
        }
    );
    if (gen !== lightboxState.openGeneration || !isAvatarLightboxOpen()) return;

    if (freshUrl && curImg) {
        bindPrimaryAvatarImgListeners(curImg);
        if (curImg.classList) curImg.classList.remove('hidden');
        if (lEl && lEl.classList) lEl.classList.add('hidden');
        if (eEl && eEl.classList) eEl.classList.add('hidden');
        curImg.src = freshUrl;
        return;
    }
    if (curImg && curImg.classList) curImg.classList.add('hidden');
    if (lEl && lEl.classList) lEl.classList.add('hidden');
    if (eEl) {
        eEl.textContent = 'Photo unavailable';
        if (eEl.classList) eEl.classList.remove('hidden');
    }
}

function bindPrimaryAvatarImgListeners(imgEl) {
    if (!imgEl || lightboxState.boundPrimaryImg === imgEl) return;
    if (lightboxState.boundPrimaryImg && typeof lightboxState.boundPrimaryImg.removeEventListener === 'function') {
        lightboxState.boundPrimaryImg.removeEventListener('load', onAvatarLightboxImgLoad);
        lightboxState.boundPrimaryImg.removeEventListener('error', onAvatarLightboxImgError);
    }
    lightboxState.boundPrimaryImg = imgEl;
    if (typeof imgEl.addEventListener === 'function') {
        imgEl.addEventListener('load', onAvatarLightboxImgLoad);
        imgEl.addEventListener('error', onAvatarLightboxImgError);
    }
}

function parkCurrentAvatarFallbackIfLoaded() {
    const currentLoaded = lightboxState.loadedFallback;
    lightboxState.loadedFallback = null;
    lightboxState.pendingFallback = null;
    if (!currentLoaded) return;

    const epochs = avatarFullImageLoader.getEpochs(currentLoaded.scopeKey);
    if (epochs.globalEpoch !== currentLoaded.cacheEpoch || epochs.scopeEpoch !== currentLoaded.scopeEpoch) {
        return;
    }

    const { img } = getAvatarLightboxElements();
    if (!img || !hasNonEmptySrc(img)) return;
    const parked = parkLoadedFallbackImage(img);
    if (!parked) return;

    const parkedEl = parked.parkedElement;
    const parkedCacheKey = currentLoaded.cacheKey;
    if (typeof parkedEl.removeEventListener === 'function') {
        parkedEl.removeEventListener('load', onAvatarLightboxImgLoad);
        parkedEl.removeEventListener('error', onAvatarLightboxImgError);
    }
    parkedEl.addEventListener?.('error', () => {
        const wasActive = isAvatarLightboxOpen() && lightboxState.activeFallbackElement === parkedEl;
        avatarFullImageLoader.evictKey(parkedCacheKey);
        if (wasActive) {
            hideLightboxFallbackElement();
            onAvatarLightboxImgError({ fromElement: parkedEl });
        }
    });

    lightboxState.boundPrimaryImg = null;
    bindPrimaryAvatarImgListeners(parked.freshPrimary);
    avatarFullImageLoader.setElement(currentLoaded.cacheKey, parked.parkedElement, currentLoaded.meta);
}

function clearAvatarLightboxMediaState({ cacheCurrentFallback = true } = {}) {
    if (cacheCurrentFallback) {
        parkCurrentAvatarFallbackIfLoaded();
    } else {
        lightboxState.loadedFallback = null;
        lightboxState.pendingFallback = null;
    }
    revokeLightboxTransientBlob();
    hideLightboxFallbackElement();

    const { img } = getAvatarLightboxElements();
    if (img) {
        if (typeof img.removeAttribute === 'function') {
            img.removeAttribute('src');
        } else {
            img.src = '';
        }
        if (img.classList) img.classList.add('hidden');
    }
}

/**
 * Returns true if the full-resolution avatar lightbox is currently visible.
 */
export function isAvatarLightboxOpen() {
    const { modal } = getAvatarLightboxElements();
    return !!(modal && modal.classList && !modal.classList.contains('hidden'));
}

/**
 * Closes #avatar-lightbox-modal and restores focus cleanly without evicting cached Blob ObjectURLs.
 * @param {{ cacheCurrentFallback?: boolean }} [options]
 */
export function closeAvatarLightbox({ cacheCurrentFallback = true } = {}) {
    const { modal, loading, error, title } = getAvatarLightboxElements();
    const restoreTarget = lightboxState.lastFocusedElement;
    lightboxState.openGeneration++;
    clearAvatarLightboxMediaState({ cacheCurrentFallback });
    lightboxState.activeUserId = null;
    lightboxState.activeVersion = null;
    lightboxState.activeCacheKey = null;
    lightboxState.activeDisplayName = '';
    lightboxState.retriedCurrent = false;
    lightboxState.lastFocusedElement = null;

    if (typeof document !== 'undefined' && lightboxState.docKeyDownBound && typeof document.removeEventListener === 'function') {
        document.removeEventListener('keydown', lightboxState.docKeyDownBound);
        lightboxState.docKeyDownBound = null;
    }

    if (loading && loading.classList) loading.classList.add('hidden');
    if (error && error.classList) error.classList.add('hidden');
    if (title) title.textContent = '';
    if (modal && modal.classList) modal.classList.add('hidden');

    if (restoreTarget && typeof restoreTarget.focus === 'function' && restoreTarget.isConnected !== false) {
        restoreTarget.focus();
    }
}

/**
 * Closes the avatar lightbox immediately if currently open for userId.
 * @param {string|object} userId
 */
export function closeAvatarLightboxIfUser(userId) {
    const norm = normalizeId(userId);
    if (!norm || !lightboxState.activeUserId) return;
    if (normalizeId(lightboxState.activeUserId) === norm) {
        closeAvatarLightbox({ cacheCurrentFallback: false });
    }
}

/**
 * Opens the full-size profile photo lightbox and loads ?size=full strictly on demand,
 * serving repeat views cleanly from the bounded tab RAM Blob LRU cache.
 * @param {string|object} userId
 * @param {string} avatarVersion
 * @param {string} [displayName]
 * @param {{
 *   getAvatarUrlFn?: Function,
 *   nowFn?: Function,
 *   fetchFn?: Function|null,
 *   createObjectUrlFn?: Function|null,
 *   revokeObjectUrlFn?: Function|null
 * }} [options]
 * @returns {Promise<boolean>}
 */
export async function openAvatarLightbox(
    userId,
    avatarVersion,
    displayName = '',
    {
        getAvatarUrlFn = getUserAvatarUrl,
        nowFn = () => Date.now(),
        fetchFn = null,
        createObjectUrlFn = null,
        revokeObjectUrlFn = null
    } = {}
) {
    const normId = normalizeId(userId);
    const trimmedVer = avatarVersion ? String(avatarVersion).trim() : '';
    if (!normId || !trimmedVer || typeof document === 'undefined') return false;

    const modal = ensureAvatarLightboxModal();
    if (!modal) return false;

    closeTransientPopovers();

    const wasOpen = isAvatarLightboxOpen();
    if (!wasOpen) {
        lightboxState.lastFocusedElement = document.activeElement || null;
    }

    lightboxState.openGeneration++;
    const gen = lightboxState.openGeneration;
    clearAvatarLightboxMediaState({ cacheCurrentFallback: wasOpen });

    lightboxState.activeUserId = normId;
    lightboxState.activeVersion = trimmedVer;
    lightboxState.activeDisplayName = displayName || 'Profile photo';
    lightboxState.retriedCurrent = false;
    lightboxState.getAvatarUrlFn = getAvatarUrlFn;
    lightboxState.nowFn = nowFn;
    lightboxState.fetchFn = fetchFn;
    lightboxState.createObjectUrlFn = createObjectUrlFn;
    lightboxState.revokeObjectUrlFn = revokeObjectUrlFn;

    const { title, img, loading, error, closeBtn } = getAvatarLightboxElements();
    if (img) {
        bindPrimaryAvatarImgListeners(img);
        img.alt = displayName ? `${displayName}'s profile photo` : 'Profile photo';
    }
    if (title) title.textContent = lightboxState.activeDisplayName;
    if (error && error.classList) error.classList.add('hidden');
    if (modal.classList) modal.classList.remove('hidden');

    if (!lightboxState.docKeyDownBound && typeof document.addEventListener === 'function') {
        lightboxState.docKeyDownBound = (e) => {
            if (!isAvatarLightboxOpen()) return;
            if (e.key === 'Escape') {
                if (typeof e.preventDefault === 'function') e.preventDefault();
                closeAvatarLightbox();
            } else if (e.key === 'Tab') {
                const { closeBtn: cBtn } = getAvatarLightboxElements();
                if (cBtn && typeof cBtn.focus === 'function') {
                    if (typeof e.preventDefault === 'function') e.preventDefault();
                    cBtn.focus();
                }
            }
        };
        document.addEventListener('keydown', lightboxState.docKeyDownBound);
    }

    if (closeBtn && typeof closeBtn.focus === 'function') {
        closeBtn.focus();
    }

    const cacheKey = makeCacheKey(normId, trimmedVer, 'full');
    lightboxState.activeCacheKey = cacheKey;
    const cached = avatarFullImageLoader.get(cacheKey);
    if (cached?.kind === 'blob' && img) {
        img.src = cached.objectUrl;
        if (loading && loading.classList) loading.classList.add('hidden');
        if (img.classList) img.classList.remove('hidden');
        return true;
    }
    if (cached?.kind === 'element' && cached.element && img) {
        const fallbackEl = cached.element;
        if (!fallbackEl.parentNode && img.parentNode && typeof img.parentNode.insertBefore === 'function') {
            img.parentNode.insertBefore(fallbackEl, img);
        }
        fallbackEl.alt = img.alt;
        if (fallbackEl.classList) fallbackEl.classList.remove('hidden');
        lightboxState.activeFallbackElement = fallbackEl;
        if (loading && loading.classList) loading.classList.add('hidden');
        return true;
    }

    if (loading && loading.classList) loading.classList.remove('hidden');

    const result = await avatarFullImageLoader.load({
        cacheKey,
        scopeKey: normId,
        meta: {
            userId: normId,
            version: trimmedVer
        },
        fetchFn,
        createObjectUrlFn,
        revokeObjectUrlFn,
        getSignedUrlFn: () => resolveAvatarUrl(normId, trimmedVer, {
            size: 'full',
            getAvatarUrlFn,
            nowFn
        }),
        refreshSignedUrlFn: () => resolveAvatarUrl(normId, trimmedVer, {
            size: 'full',
            bypassCache: true,
            getAvatarUrlFn,
            nowFn
        })
    });

    if (gen !== lightboxState.openGeneration || !isAvatarLightboxOpen()) {
        if (result?.status === 'blob' && !result.stored && result.revokeFn && (!isAvatarLightboxOpen() || lightboxState.activeCacheKey !== cacheKey)) {
            try { result.revokeFn(result.objectUrl); } catch {}
        }
        return false;
    }

    const { img: curImg, loading: curLoading, error: curError } = getAvatarLightboxElements();
    if (!curImg) return false;
    bindPrimaryAvatarImgListeners(curImg);

    if (result?.status === 'blob') {
        if (!result.stored && result.revokeFn) {
            lightboxState.activeTransientBlob = { url: result.objectUrl, revokeFn: result.revokeFn };
        }
        curImg.src = result.objectUrl;
        if (curLoading && curLoading.classList) curLoading.classList.add('hidden');
        if (curImg.classList) curImg.classList.remove('hidden');
        return true;
    }

    if (result?.status === 'fallback-url') {
        lightboxState.pendingFallback = {
            cacheKey,
            url: result.url,
            cacheEpoch: result.cacheEpoch,
            scopeEpoch: result.scopeEpoch,
            scopeKey: result.scopeKey,
            meta: {
                userId: normId,
                version: trimmedVer,
                cacheEpoch: result.cacheEpoch,
                scopeEpoch: result.scopeEpoch,
                scopeKey: result.scopeKey
            }
        };
        curImg.src = result.url;
        if (curLoading && curLoading.classList) curLoading.classList.add('hidden');
        if (curImg.classList) curImg.classList.remove('hidden');
        return true;
    }

    if (curLoading && curLoading.classList) curLoading.classList.add('hidden');
    if (result?.status !== 'aborted' && curError) {
        curError.textContent = 'Photo unavailable';
        if (curError.classList) curError.classList.remove('hidden');
    }
    return false;
}

/**
 * Binds delegated click & keyboard listeners for [data-avatar-lightbox-trigger="true"].
 * @param {HTMLElement|Document|null} [rootEl]
 * @param {{ getAvatarUrlFn?: Function, nowFn?: Function }} [options]
 * @returns {Function} Unbind cleanup function
 */
export function bindAvatarLightboxDelegation(rootEl = typeof document !== 'undefined' ? document : null, options = {}) {
    if (!rootEl || typeof rootEl.addEventListener !== 'function') {
        return () => {};
    }

    const handleTrigger = (trigger, e) => {
        if (!trigger) return;
        const userId = trigger.getAttribute('data-avatar-user-id') || '';
        const version = trigger.getAttribute('data-avatar-version') || '';
        const name = trigger.getAttribute('data-avatar-name') || '';
        if (!userId || !version) return;
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        openAvatarLightbox(userId, version, name, options);
    };

    const onClick = (e) => {
        const target = e?.target;
        const trigger = target && typeof target.closest === 'function'
            ? target.closest('[data-avatar-lightbox-trigger="true"]')
            : null;
        if (trigger) {
            handleTrigger(trigger, e);
        }
    };

    const onKeyDown = (e) => {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        const target = e?.target;
        const trigger = target && typeof target.closest === 'function'
            ? target.closest('[data-avatar-lightbox-trigger="true"]')
            : null;
        if (trigger) {
            const tag = String(trigger.tagName || '').toLowerCase();
            if (tag !== 'button' && tag !== 'a') {
                handleTrigger(trigger, e);
            }
        }
    };

    rootEl.addEventListener('click', onClick);
    rootEl.addEventListener('keydown', onKeyDown);

    return () => {
        if (typeof rootEl.removeEventListener === 'function') {
            rootEl.removeEventListener('click', onClick);
            rootEl.removeEventListener('keydown', onKeyDown);
        }
    };
}
