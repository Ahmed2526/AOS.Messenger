/**
 * AOS Messenger - WhatsApp-Style Sticker Picker, Signed URL Cache & Sticker Info Modal
 *
 * Security & Architecture Invariants:
 * - Pure DOM construction via document.createElement and .textContent only (zero innerHTML).
 * - Zero inline event handlers (onerror=/onclick= prohibited); programmatic listeners only.
 * - Zero crossorigin="anonymous" on display <img> tags (presigned R2 redirects).
 * - Stickers load via safe /api/stickers/{id}/image or signed /api/stickers/{id}/url lookups
 *   cached in memory (~8 min) and wiped on logout/switch.
 */

import {
    getStickerCatalog as apiGetStickerCatalog,
    addFavoriteSticker as apiAddFavoriteSticker,
    removeFavoriteSticker as apiRemoveFavoriteSticker,
    archiveCustomSticker as apiArchiveCustomSticker,
    deleteStickerPack as apiDeleteStickerPack,
    getStickerImageUrl as apiGetStickerImageUrl,
    buildStickerImageRedirectUrl
} from './api-client.js';
import { isSafeMediaUrl } from './media-lightbox.js';
import { setBoundedMapEntry } from './memory-image-cache.js';

const STICKER_URL_CACHE_TTL_MS = 8 * 60 * 1000;
const STICKER_URL_SAFETY_MARGIN_MS = 60 * 1000;
const MAX_LOCAL_RECENTS = 36;

const stickerUrlCache = new Map();
const inFlightStickerUrls = new Map();

function normalizeId(val) {
    if (val === null || val === undefined) return '';
    if (typeof val === 'object' && val.value !== undefined) return String(val.value);
    return String(val);
}

function clearChildren(el) {
    if (!el) return;
    while (el.firstChild) {
        el.removeChild(el.firstChild);
    }
}

/**
 * Clears all cached signed custom sticker URLs (called on logout / session expiry / identity change).
 */
export function clearStickerUrlCache() {
    stickerUrlCache.clear();
    inFlightStickerUrls.clear();
}

/**
 * Returns a non-expired cached signed URL for stickerId if available.
 * @param {string|object} stickerId
 * @param {{ nowFn?: Function }} [options]
 * @returns {string|null}
 */
export function getCachedStickerUrl(stickerId, { nowFn = () => Date.now() } = {}) {
    const id = normalizeId(stickerId);
    if (!id || !stickerUrlCache.has(id)) return null;
    const entry = stickerUrlCache.get(id);
    if (entry && entry.expiresAtMs > nowFn() && isSafeMediaUrl(entry.url)) {
        return entry.url;
    }
    stickerUrlCache.delete(id);
    return null;
}

/**
 * Resolves the immediate safe display src for a sticker (safe static asset, cached R2 URL, or redirect endpoint).
 * @param {object|null|undefined} sticker
 * @returns {string}
 */
export function getInitialStickerSrc(sticker) {
    if (!sticker) return '';
    const staticUrl = sticker.staticUrl ?? sticker.StaticUrl ?? '';
    if (staticUrl && isSafeMediaUrl(staticUrl)) {
        return staticUrl;
    }

    const id = normalizeId(sticker.id ?? sticker.Id);
    if (!id) return '';

    return getCachedStickerUrl(id) || buildStickerImageRedirectUrl(id);
}

/**
 * Assigns an initial safe display src synchronously ONLY when static or already cached;
 * leaves cold custom R2 stickers without an eager /image redirect so hydrateStickerImages
 * proactively resolves and coalesces /api/stickers/{id}/url.
 * @param {HTMLImageElement|null} img
 * @param {object|null|undefined} sticker
 */
export function applyInitialStickerSrc(img, sticker) {
    if (!img || !sticker) return;
    const staticUrl = sticker.staticUrl ?? sticker.StaticUrl ?? '';
    if (staticUrl && isSafeMediaUrl(staticUrl)) {
        img.src = staticUrl;
        return;
    }

    const id = normalizeId(sticker.id ?? sticker.Id);
    const cached = id ? getCachedStickerUrl(id) : null;
    if (cached) {
        img.src = cached;
    } else if (typeof img.removeAttribute === 'function') {
        img.removeAttribute('src');
    }
}

/**
 * Coalesces and caches /api/stickers/{id}/url lookups for custom R2 stickers.
 * @param {string} stickerId
 * @param {{ bypassCache?: boolean, getUrlFn?: Function, nowFn?: Function }} [options]
 * @returns {Promise<string|null>}
 */
export async function resolveCachedStickerUrl(
    stickerId,
    { bypassCache = false, getUrlFn = apiGetStickerImageUrl, nowFn = () => Date.now() } = {}
) {
    const id = normalizeId(stickerId);
    if (!id) return null;

    const now = nowFn();
    if (!bypassCache && stickerUrlCache.has(id)) {
        const entry = stickerUrlCache.get(id);
        if (entry && entry.expiresAtMs > now && isSafeMediaUrl(entry.url)) {
            return entry.url;
        }
        stickerUrlCache.delete(id);
    }

    if (!bypassCache && inFlightStickerUrls.has(id)) {
        return inFlightStickerUrls.get(id);
    }

    const promise = Promise.resolve()
        .then(() => getUrlFn(id))
        .then((dto) => {
            const url = dto?.url ?? dto?.Url ?? '';
            if (!isSafeMediaUrl(url)) {
                return null;
            }
            const rawExp = dto?.expiresAtUtc ?? dto?.ExpiresAtUtc ?? null;
            const parsedExp = rawExp ? Date.parse(rawExp) : NaN;
            const defaultExp = nowFn() + STICKER_URL_CACHE_TTL_MS;
            const expiresAtMs = Number.isFinite(parsedExp)
                ? Math.min(parsedExp - STICKER_URL_SAFETY_MARGIN_MS, defaultExp)
                : defaultExp;
            if (expiresAtMs > nowFn()) {
                setBoundedMapEntry(stickerUrlCache, id, { url, expiresAtMs });
            }
            return url;
        })
        .catch(() => null)
        .finally(() => {
            inFlightStickerUrls.delete(id);
        });

    if (!bypassCache) {
        inFlightStickerUrls.set(id, promise);
    }
    return promise;
}

function evictExpiredStickerUrl(stickerId, expectedFailedUrl = null) {
    const id = normalizeId(stickerId);
    if (!id || !stickerUrlCache.has(id)) return;
    const entry = stickerUrlCache.get(id);
    if (expectedFailedUrl && entry && entry.url !== expectedFailedUrl) return;
    stickerUrlCache.delete(id);
}

/**
 * Proactively hydrates cold custom sticker images via coalesced /api/stickers/{id}/url lookups
 * and attaches safe programmatic error-recovery listeners (zero inline onerror=).
 * @param {HTMLElement|Document|null} rootEl
 * @param {{ getUrlFn?: Function, nowFn?: Function }} [options]
 */
export function hydrateStickerImages(rootEl, { getUrlFn = apiGetStickerImageUrl, nowFn = () => Date.now() } = {}) {
    if (!rootEl || typeof rootEl.querySelectorAll !== 'function') return;
    const imgs = rootEl.querySelectorAll('img[data-sticker-img="true"]');
    for (let i = 0; i < imgs.length; i++) {
        const img = imgs[i];
        if (!img || img.getAttribute('data-sticker-bound') === 'true') continue;
        img.setAttribute('data-sticker-bound', 'true');

        const stickerId = img.getAttribute('data-sticker-id') || '';
        const isStatic = img.getAttribute('data-sticker-static') === 'true';
        if (!stickerId || isStatic) continue;

        const hasSrc = (typeof img.hasAttribute === 'function' && img.hasAttribute('src'))
            ? Boolean(img.getAttribute('src'))
            : Boolean(img.src);

        if (typeof img.addEventListener === 'function') {
            img.addEventListener('error', async () => {
                const curHasSrc = (typeof img.hasAttribute === 'function' && img.hasAttribute('src'))
                    ? Boolean(img.getAttribute('src'))
                    : Boolean(img.src);
                if (!curHasSrc || img.getAttribute('data-sticker-retried') === 'true') {
                    return;
                }
                const failedSrc = (typeof img.getAttribute === 'function' ? img.getAttribute('src') : null) || img.src || '';
                img.setAttribute('data-sticker-retried', 'true');
                evictExpiredStickerUrl(stickerId, failedSrc);
                const freshUrl = await resolveCachedStickerUrl(stickerId, { getUrlFn, nowFn });
                if (freshUrl && img.isConnected !== false) {
                    img.src = freshUrl;
                }
            });
        }

        if (!hasSrc) {
            void resolveCachedStickerUrl(stickerId, { getUrlFn, nowFn }).then((resolvedUrl) => {
                if (resolvedUrl && img.isConnected !== false) {
                    img.src = resolvedUrl;
                }
            });
        }
    }
}

function normalizeSticker(raw) {
    if (!raw || typeof raw !== 'object') return null;
    const id = normalizeId(raw.id ?? raw.Id);
    if (!id) return null;
    return {
        id,
        packId: normalizeId(raw.packId ?? raw.PackId),
        packName: String(raw.packName ?? raw.PackName ?? 'Stickers'),
        packAuthor: String(raw.packAuthor ?? raw.PackAuthor ?? 'Community Sticker'),
        isAnimated: !!(raw.isAnimated ?? raw.IsAnimated),
        isSystem: !!(raw.isSystem ?? raw.IsSystem),
        isOwnedByCurrentUser: !!(raw.isOwnedByCurrentUser ?? raw.IsOwnedByCurrentUser),
        staticUrl: raw.staticUrl ?? raw.StaticUrl ?? null,
        width: Number(raw.width ?? raw.Width ?? 512) || 512,
        height: Number(raw.height ?? raw.Height ?? 512) || 512,
        emojiTags: String(raw.emojiTags ?? raw.EmojiTags ?? ''),
        isFavorited: !!(raw.isFavorited ?? raw.IsFavorited ?? raw.isFavoritedByCurrentUser ?? raw.IsFavoritedByCurrentUser)
    };
}

function normalizePack(raw) {
    if (!raw || typeof raw !== 'object') return null;
    const id = normalizeId(raw.id ?? raw.Id);
    if (!id) return null;
    const rawStickers = raw.stickers ?? raw.Stickers ?? [];
    const stickers = Array.isArray(rawStickers)
        ? rawStickers.map(normalizeSticker).filter(Boolean)
        : [];
    return {
        id,
        slug: raw.slug ?? raw.Slug ?? null,
        name: String(raw.name ?? raw.Name ?? 'Stickers'),
        authorName: String(raw.authorName ?? raw.AuthorName ?? ''),
        isSystemPack: !!(raw.isSystemPack ?? raw.IsSystemPack),
        sortOrder: Number(raw.sortOrder ?? raw.SortOrder ?? 0) || 0,
        stickers
    };
}

function normalizeCatalog(raw) {
    const recents = Array.isArray(raw?.recents ?? raw?.Recents)
        ? (raw.recents ?? raw.Recents).map(normalizeSticker).filter(Boolean)
        : [];
    const favorites = Array.isArray(raw?.favorites ?? raw?.Favorites)
        ? (raw.favorites ?? raw.Favorites).map(normalizeSticker).filter(Boolean)
        : [];
    const systemPacks = Array.isArray(raw?.systemPacks ?? raw?.SystemPacks)
        ? (raw.systemPacks ?? raw.SystemPacks).map(normalizePack).filter(Boolean)
        : [];
    const customPacks = Array.isArray(raw?.customPacks ?? raw?.CustomPacks)
        ? (raw.customPacks ?? raw.CustomPacks).map(normalizePack).filter(Boolean)
        : [];

    return { recents, favorites, systemPacks, customPacks };
}

/**
 * Filters a flat list of unique stickers matching query against emojiTags and packName.
 * @param {object} catalog
 * @param {string} rawQuery
 * @returns {Array<object>}
 */
export function searchCatalogStickers(catalog, rawQuery) {
    if (!catalog) return [];
    const query = String(rawQuery || '').trim().toLowerCase();
    if (!query) return [];

    const tokens = query.split(/\s+/).filter(Boolean);
    const seen = new Set();
    const matches = [];

    const visit = (list) => {
        if (!Array.isArray(list)) return;
        for (let i = 0; i < list.length; i++) {
            const st = list[i];
            if (!st || !st.id || seen.has(st.id)) continue;
            const hay = `${st.emojiTags || ''} ${st.packName || ''}`.toLowerCase();
            let allOk = true;
            for (let t = 0; t < tokens.length; t++) {
                if (!hay.includes(tokens[t])) {
                    allOk = false;
                    break;
                }
            }
            if (allOk) {
                seen.add(st.id);
                matches.push(st);
            }
        }
    };

    visit(catalog.favorites);
    for (const pack of catalog.customPacks || []) visit(pack.stickers);
    for (const pack of catalog.systemPacks || []) visit(pack.stickers);
    visit(catalog.recents);
    return matches;
}

/**
 * Creates the sticker picker + Sticker Info modal controller for the unified chat shell.
 * @param {{
 *   getActiveConversationId?: Function,
 *   getCurrentUserId?: Function,
 *   onSendSticker?: Function,
 *   onOpenCreator?: Function,
 *   onBeforeOpenModal?: Function,
 *   onNotice?: Function,
 *   getCatalogFn?: Function,
 *   addFavoriteFn?: Function,
 *   removeFavoriteFn?: Function,
 *   archiveStickerFn?: Function,
 *   deletePackFn?: Function,
 *   getUrlFn?: Function
 * }} [options]
 */
export function createStickerPicker(options = {}) {
    const getActiveConversationId = typeof options.getActiveConversationId === 'function'
        ? options.getActiveConversationId
        : () => null;
    const getCurrentUserId = typeof options.getCurrentUserId === 'function'
        ? options.getCurrentUserId
        : () => null;
    const onSendSticker = typeof options.onSendSticker === 'function' ? options.onSendSticker : null;
    const onOpenCreator = typeof options.onOpenCreator === 'function' ? options.onOpenCreator : null;
    const onBeforeOpenModal = typeof options.onBeforeOpenModal === 'function' ? options.onBeforeOpenModal : null;
    const onNotice = typeof options.onNotice === 'function' ? options.onNotice : null;

    const getCatalogFn = typeof options.getCatalogFn === 'function' ? options.getCatalogFn : apiGetStickerCatalog;
    const addFavoriteFn = typeof options.addFavoriteFn === 'function' ? options.addFavoriteFn : apiAddFavoriteSticker;
    const removeFavoriteFn = typeof options.removeFavoriteFn === 'function' ? options.removeFavoriteFn : apiRemoveFavoriteSticker;
    const archiveStickerFn = typeof options.archiveStickerFn === 'function' ? options.archiveStickerFn : apiArchiveCustomSticker;
    const deletePackFn = typeof options.deletePackFn === 'function' ? options.deletePackFn : apiDeleteStickerPack;
    const getUrlFn = typeof options.getUrlFn === 'function' ? options.getUrlFn : apiGetStickerImageUrl;

    let catalog = null;
    let catalogUserId = null;
    let catalogLoadPromise = null;
    let loadRequestToken = 0;
    let isLoading = false;
    let loadError = '';
    let activeTabKey = 'recents';
    let searchQuery = '';
    const favoriteIds = new Set();
    const favoriteInFlightIds = new Set();

    // Mounted picker DOM refs
    let hostEl = null;
    let searchInputEl = null;
    let clearSearchBtn = null;
    let tabsBarEl = null;
    let headingRowEl = null;
    let headingTitleEl = null;
    let headingMetaEl = null;
    let deletePackBtn = null;
    let activeCustomPackForDelete = null;
    let gridEl = null;

    // Sticker Info Modal state
    let activeInfoSticker = null;
    let activeInfoContext = null;
    let infoModalListenersBound = false;

    // Sticker / Pack Delete Confirmation Modal state
    let pendingConfirmAction = null;
    let confirmInFlight = false;
    let confirmGeneration = 0;
    let lastConfirmFocusedEl = null;
    let createdFallbackConfirmModal = null;
    let confirmModalListenersBound = false;

    function rebuildFavoriteSet() {
        favoriteIds.clear();
        if (!catalog) return;
        for (const st of catalog.favorites) {
            if (st && st.id) favoriteIds.add(st.id);
        }
        const syncList = (list) => {
            if (!Array.isArray(list)) return;
            for (const st of list) {
                if (st && st.id) st.isFavorited = favoriteIds.has(st.id);
            }
        };
        syncList(catalog.recents);
        syncList(catalog.favorites);
        for (const p of catalog.systemPacks) syncList(p.stickers);
        for (const p of catalog.customPacks) syncList(p.stickers);
    }

    function syncMessageRowFavoriteAttributes(stickerId, isFav) {
        if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function' || !stickerId) return;
        const buttons = document.querySelectorAll(`[data-sticker-info-trigger="true"]`);
        for (let i = 0; i < buttons.length; i++) {
            const btn = buttons[i];
            if (btn.getAttribute('data-sticker-id') === stickerId) {
                btn.setAttribute('data-sticker-favorited', isFav ? 'true' : 'false');
            }
        }
    }

    function pickInitialDefaultTab() {
        if (!catalog) return 'recents';
        if (catalog.recents.length > 0) return 'recents';
        if (catalog.favorites.length > 0) return 'favorites';
        if (catalog.systemPacks.length > 0) return `pack:${catalog.systemPacks[0].id}`;
        if (catalog.customPacks.length > 0) return `pack:${catalog.customPacks[0].id}`;
        return 'recents';
    }

    function ensureValidActiveTab() {
        if (!catalog) return;
        if (activeTabKey === 'recents' || activeTabKey === 'favorites') return;
        if (activeTabKey.startsWith('pack:')) {
            const targetId = activeTabKey.slice(5);
            const exists = catalog.systemPacks.some((p) => p.id === targetId)
                || catalog.customPacks.some((p) => p.id === targetId);
            if (exists) return;
        }
        activeTabKey = pickInitialDefaultTab();
    }

    async function ensureLoaded({ force = false, selectPackId = null } = {}) {
        const uid = normalizeId(getCurrentUserId());
        if (catalog && !force && catalogUserId === uid) {
            if (selectPackId) {
                activeTabKey = `pack:${selectPackId}`;
                searchQuery = '';
                if (searchInputEl) searchInputEl.value = '';
                if (clearSearchBtn && clearSearchBtn.classList) clearSearchBtn.classList.add('hidden');
            }
            ensureValidActiveTab();
            render();
            return catalog;
        }

        if (catalogLoadPromise && !force && catalogUserId === uid) {
            return catalogLoadPromise;
        }

        const myRequestToken = ++loadRequestToken;
        isLoading = true;
        loadError = '';
        catalogUserId = uid;
        render();

        const req = Promise.resolve()
            .then(() => getCatalogFn())
            .then((raw) => {
                if (myRequestToken !== loadRequestToken) {
                    return null;
                }
                if (normalizeId(getCurrentUserId()) !== uid) {
                    isLoading = false;
                    loadError = '';
                    render();
                    return null;
                }
                const hadCatalog = catalog !== null;
                catalog = normalizeCatalog(raw);
                rebuildFavoriteSet();
                if (selectPackId) {
                    activeTabKey = `pack:${selectPackId}`;
                    searchQuery = '';
                    if (searchInputEl) searchInputEl.value = '';
                    if (clearSearchBtn && clearSearchBtn.classList) clearSearchBtn.classList.add('hidden');
                } else if (!hadCatalog) {
                    activeTabKey = pickInitialDefaultTab();
                } else {
                    ensureValidActiveTab();
                }
                isLoading = false;
                loadError = '';
                render();
                return catalog;
            })
            .catch((err) => {
                if (myRequestToken !== loadRequestToken) {
                    return null;
                }
                isLoading = false;
                loadError = err?.message || 'Could not load stickers.';
                render();
                return null;
            })
            .finally(() => {
                if (catalogLoadPromise === req) {
                    catalogLoadPromise = null;
                }
            });

        catalogLoadPromise = req;
        return req;
    }

    function setStarButtonsDisabled(stickerId, disabled) {
        if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function' || !stickerId) return;
        const buttons = document.querySelectorAll('[data-sticker-star]');
        for (let i = 0; i < buttons.length; i++) {
            const btn = buttons[i];
            if (btn.getAttribute('data-sticker-star') === stickerId) {
                btn.disabled = Boolean(disabled);
            }
        }
    }

    function recordLocalRecent(sticker) {
        if (!catalog || !sticker) return;
        const normalized = normalizeSticker(sticker);
        if (!normalized) return;
        normalized.isFavorited = favoriteIds.has(normalized.id);
        catalog.recents = [
            normalized,
            ...catalog.recents.filter((s) => s.id !== normalized.id)
        ].slice(0, MAX_LOCAL_RECENTS);
        render();
    }

    async function toggleFavorite(sticker, { sourceConversationId = null, sourceMessageId = null } = {}) {
        const normalized = normalizeSticker(sticker);
        if (!normalized || !normalized.id) return false;
        if (favoriteInFlightIds.has(normalized.id)) {
            return favoriteIds.has(normalized.id);
        }

        const reqUserId = normalizeId(getCurrentUserId());
        const currentlyFav = favoriteIds.has(normalized.id) || normalized.isFavorited;
        favoriteInFlightIds.add(normalized.id);
        setStarButtonsDisabled(normalized.id, true);

        try {
            if (currentlyFav) {
                await removeFavoriteFn(normalized.id);
                if (normalizeId(getCurrentUserId()) !== reqUserId) return false;
                favoriteIds.delete(normalized.id);
                if (catalog) {
                    catalog.favorites = catalog.favorites.filter((s) => s.id !== normalized.id);
                    rebuildFavoriteSet();
                }
                syncMessageRowFavoriteAttributes(normalized.id, false);
                render();
                return false;
            } else {
                const payload = { stickerId: normalized.id };
                if (sourceConversationId && sourceMessageId) {
                    payload.sourceConversationId = sourceConversationId;
                    payload.sourceMessageId = Number(sourceMessageId);
                }
                const saved = await addFavoriteFn(payload);
                if (normalizeId(getCurrentUserId()) !== reqUserId) return false;
                const savedNorm = normalizeSticker(saved) || { ...normalized, isFavorited: true };
                savedNorm.isFavorited = true;
                favoriteIds.add(savedNorm.id);
                if (catalog) {
                    catalog.favorites = [
                        savedNorm,
                        ...catalog.favorites.filter((s) => s.id !== savedNorm.id)
                    ];
                    rebuildFavoriteSet();
                }
                syncMessageRowFavoriteAttributes(savedNorm.id, true);
                render();
                return true;
            }
        } catch (err) {
            if (normalizeId(getCurrentUserId()) === reqUserId && onNotice) {
                onNotice(err?.message || 'Could not update favorite sticker.');
            }
            return favoriteIds.has(normalized.id);
        } finally {
            favoriteInFlightIds.delete(normalized.id);
            setStarButtonsDisabled(normalized.id, false);
        }
    }

    function getConfirmModalEls() {
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return {};
        return {
            modal: document.getElementById('sticker-confirm-modal'),
            titleEl: document.getElementById('sticker-confirm-title'),
            messageEl: document.getElementById('sticker-confirm-message'),
            errorEl: document.getElementById('sticker-confirm-error'),
            cancelBtn: document.getElementById('sticker-confirm-cancel-btn'),
            acceptBtn: document.getElementById('sticker-confirm-accept-btn')
        };
    }

    function ensureConfirmModal() {
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return null;
        const existing = document.getElementById('sticker-confirm-modal');
        if (existing) return existing;
        if (typeof document.createElement !== 'function' || !document.body) return null;

        const modal = document.createElement('div');
        modal.id = 'sticker-confirm-modal';
        modal.setAttribute('role', 'dialog');
        modal.setAttribute('aria-modal', 'true');
        modal.setAttribute('aria-labelledby', 'sticker-confirm-title');
        modal.setAttribute('aria-describedby', 'sticker-confirm-message');
        modal.className = 'hidden fixed inset-0 z-[75] flex items-center justify-center bg-black/55 p-4 backdrop-blur-xs';

        const card = document.createElement('div');
        card.className = 'bg-white dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] rounded-2xl shadow-xl w-full max-w-sm p-5 flex flex-col gap-3.5';

        const headerRow = document.createElement('div');
        headerRow.className = 'flex items-start gap-3';

        const badge = document.createElement('div');
        badge.className = 'w-10 h-10 rounded-full bg-red-50 dark:bg-red-950/40 text-red-600 dark:text-red-400 flex items-center justify-center shrink-0';
        const badgeIcon = document.createElement('span');
        badgeIcon.className = 'material-symbols-outlined text-[20px]';
        badgeIcon.textContent = 'delete_forever';
        badge.appendChild(badgeIcon);
        headerRow.appendChild(badge);

        const textWrap = document.createElement('div');
        textWrap.className = 'min-w-0 flex-1';
        const titleEl = document.createElement('h3');
        titleEl.id = 'sticker-confirm-title';
        titleEl.className = 'text-sm font-bold text-[#2B2725] dark:text-[#F0E9E2]';
        titleEl.textContent = 'Delete sticker pack?';
        const messageEl = document.createElement('p');
        messageEl.id = 'sticker-confirm-message';
        messageEl.className = 'text-xs text-[#6E6560] dark:text-[#C9C0B9] mt-1 leading-relaxed break-words';
        textWrap.appendChild(titleEl);
        textWrap.appendChild(messageEl);
        headerRow.appendChild(textWrap);
        card.appendChild(headerRow);

        const errorEl = document.createElement('p');
        errorEl.id = 'sticker-confirm-error';
        errorEl.setAttribute('role', 'alert');
        errorEl.className = 'hidden text-xs text-red-600 dark:text-red-400 font-medium bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900/50 rounded-xl px-3 py-2';
        card.appendChild(errorEl);

        const actionsRow = document.createElement('div');
        actionsRow.className = 'flex items-center justify-end gap-2 pt-1';

        const cancelBtn = document.createElement('button');
        cancelBtn.type = 'button';
        cancelBtn.id = 'sticker-confirm-cancel-btn';
        cancelBtn.className = 'px-3.5 py-2 rounded-xl border border-[#E3DCD7] dark:border-[#514D48] text-xs font-semibold text-[#6E6560] dark:text-[#C9C0B9] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] transition-colors cursor-pointer disabled:opacity-50';
        cancelBtn.textContent = 'Cancel';
        actionsRow.appendChild(cancelBtn);

        const acceptBtn = document.createElement('button');
        acceptBtn.type = 'button';
        acceptBtn.id = 'sticker-confirm-accept-btn';
        acceptBtn.className = 'px-4 py-2 rounded-xl bg-red-600 hover:bg-red-700 text-white text-xs font-bold active:scale-[0.98] transition-all shadow-2xs cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed';
        acceptBtn.textContent = 'Delete';
        actionsRow.appendChild(acceptBtn);

        card.appendChild(actionsRow);
        modal.appendChild(card);
        document.body.appendChild(modal);
        createdFallbackConfirmModal = modal;
        return modal;
    }

    function setConfirmError(message) {
        const { errorEl } = getConfirmModalEls();
        if (!errorEl) return;
        if (message) {
            errorEl.textContent = message;
            if (errorEl.classList) errorEl.classList.remove('hidden');
        } else {
            errorEl.textContent = '';
            if (errorEl.classList) errorEl.classList.add('hidden');
        }
    }

    function isConfirmModalOpen() {
        const { modal } = getConfirmModalEls();
        return !!(modal && modal.classList && !modal.classList.contains('hidden'));
    }

    function closeConfirmModal({ restoreFocus = true } = {}) {
        confirmGeneration++;
        confirmInFlight = false;
        const restoreTarget = restoreFocus ? lastConfirmFocusedEl : null;
        pendingConfirmAction = null;
        lastConfirmFocusedEl = null;

        const { modal, cancelBtn, acceptBtn } = getConfirmModalEls();
        if (cancelBtn) cancelBtn.disabled = false;
        if (acceptBtn) {
            acceptBtn.disabled = false;
            acceptBtn.textContent = 'Delete';
        }
        setConfirmError('');
        if (modal && modal.classList) {
            modal.classList.add('hidden');
        }
        if (restoreTarget && typeof restoreTarget.focus === 'function' && restoreTarget.isConnected !== false) {
            restoreTarget.focus();
        }
    }

    async function executePendingConfirm() {
        if (!pendingConfirmAction || confirmInFlight) return;
        const action = pendingConfirmAction;
        confirmInFlight = true;
        setConfirmError('');
        const { cancelBtn, acceptBtn } = getConfirmModalEls();
        if (cancelBtn) cancelBtn.disabled = true;
        if (acceptBtn) {
            acceptBtn.disabled = true;
            acceptBtn.textContent = 'Deleting...';
        }

        try {
            await action.onConfirm();
            if (action.generation === confirmGeneration) {
                confirmInFlight = false;
                closeConfirmModal({ restoreFocus: false });
            }
        } catch (err) {
            if (action.generation === confirmGeneration) {
                confirmInFlight = false;
                if (cancelBtn) cancelBtn.disabled = false;
                if (acceptBtn) {
                    acceptBtn.disabled = false;
                    acceptBtn.textContent = action.confirmLabel || 'Delete';
                }
                setConfirmError(err?.message || 'Could not complete action.');
            }
        }
    }

    const onConfirmModalDocumentClick = (e) => {
        if (!isConfirmModalOpen() || !e.target) return;
        const { modal } = getConfirmModalEls();
        if (e.target === modal) {
            if (typeof e.stopPropagation === 'function') e.stopPropagation();
            if (!confirmInFlight) closeConfirmModal();
            return;
        }
        if (typeof e.target.closest !== 'function') return;
        if (e.target.closest('#sticker-confirm-cancel-btn')) {
            e.preventDefault();
            e.stopPropagation();
            if (!confirmInFlight) closeConfirmModal();
            return;
        }
        if (e.target.closest('#sticker-confirm-accept-btn')) {
            e.preventDefault();
            e.stopPropagation();
            void executePendingConfirm();
        }
    };

    const onConfirmModalKeyDown = (e) => {
        if (!isConfirmModalOpen()) return;
        if (e.key === 'Escape') {
            e.preventDefault();
            if (typeof e.stopPropagation === 'function') e.stopPropagation();
            if (!confirmInFlight) closeConfirmModal();
            return;
        }
        if (e.key === 'Tab') {
            const { cancelBtn, acceptBtn } = getConfirmModalEls();
            const focusables = [cancelBtn, acceptBtn].filter((el) => el && !el.disabled);
            if (focusables.length === 0) {
                e.preventDefault();
                return;
            }
            const activeEl = typeof document !== 'undefined' ? document.activeElement : null;
            const idx = focusables.indexOf(activeEl);
            if (e.shiftKey) {
                if (idx <= 0) {
                    e.preventDefault();
                    focusables[focusables.length - 1]?.focus?.();
                }
            } else if (idx === -1 || idx >= focusables.length - 1) {
                e.preventDefault();
                focusables[0]?.focus?.();
            }
        }
    };

    function bindConfirmModalListeners() {
        if (confirmModalListenersBound || typeof document === 'undefined') return;
        document.addEventListener('click', onConfirmModalDocumentClick);
        document.addEventListener('keydown', onConfirmModalKeyDown);
        confirmModalListenersBound = true;
    }

    function unbindConfirmModalListeners() {
        if (!confirmModalListenersBound || typeof document === 'undefined') return;
        document.removeEventListener('click', onConfirmModalDocumentClick);
        document.removeEventListener('keydown', onConfirmModalKeyDown);
        confirmModalListenersBound = false;
    }

    function openConfirmModal({ title, message, confirmLabel = 'Delete', onConfirm, triggerEl = null }) {
        const modal = ensureConfirmModal();
        if (!modal) return;
        bindConfirmModalListeners();

        confirmGeneration++;
        confirmInFlight = false;
        pendingConfirmAction = {
            generation: confirmGeneration,
            confirmLabel,
            onConfirm
        };
        lastConfirmFocusedEl = triggerEl || (typeof document !== 'undefined' ? document.activeElement : null);

        const { titleEl, messageEl, cancelBtn, acceptBtn } = getConfirmModalEls();
        if (titleEl) titleEl.textContent = title || 'Confirm deletion';
        if (messageEl) messageEl.textContent = message || '';
        if (cancelBtn) cancelBtn.disabled = false;
        if (acceptBtn) {
            acceptBtn.disabled = false;
            acceptBtn.textContent = confirmLabel;
        }
        setConfirmError('');
        if (modal.classList) modal.classList.remove('hidden');

        if (cancelBtn && typeof cancelBtn.focus === 'function') {
            cancelBtn.focus();
        } else if (acceptBtn && typeof acceptBtn.focus === 'function') {
            acceptBtn.focus();
        }
    }

    function handleDeleteCustomSticker(sticker, triggerEl = null) {
        if (!sticker || !sticker.id) return;
        const reqUserId = normalizeId(getCurrentUserId());
        openConfirmModal({
            title: 'Remove custom sticker?',
            message: 'Remove this custom sticker from your pack? Messages already sent will stay intact.',
            confirmLabel: 'Remove sticker',
            triggerEl,
            onConfirm: async () => {
                await archiveStickerFn(sticker.id);
                if (normalizeId(getCurrentUserId()) !== reqUserId) return;
                if (catalog) {
                    for (const pack of catalog.customPacks) {
                        pack.stickers = pack.stickers.filter((s) => s.id !== sticker.id);
                    }
                    catalog.recents = catalog.recents.filter((s) => s.id !== sticker.id);
                    catalog.favorites = catalog.favorites.filter((s) => s.id !== sticker.id);
                    rebuildFavoriteSet();
                }
                syncMessageRowFavoriteAttributes(sticker.id, false);
                render();
            }
        });
    }

    function handleDeleteActiveCustomPack(pack, triggerEl = null) {
        if (!pack || !pack.id || pack.isSystemPack) return;
        const reqUserId = normalizeId(getCurrentUserId());
        openConfirmModal({
            title: 'Delete sticker pack?',
            message: `Delete pack "${pack.name}"? Sent messages and starred stickers remain intact.`,
            confirmLabel: 'Delete pack',
            triggerEl,
            onConfirm: async () => {
                await deletePackFn(pack.id);
                if (normalizeId(getCurrentUserId()) !== reqUserId) return;
                if (catalog) {
                    const removedIds = new Set();
                    if (Array.isArray(pack.stickers)) {
                        for (const s of pack.stickers) {
                            if (s && s.id) removedIds.add(s.id);
                        }
                    }
                    const targetPack = catalog.customPacks.find((p) => p.id === pack.id);
                    if (targetPack && Array.isArray(targetPack.stickers)) {
                        for (const s of targetPack.stickers) {
                            if (s && s.id) removedIds.add(s.id);
                        }
                    }
                    catalog.customPacks = catalog.customPacks.filter((p) => p.id !== pack.id);
                    catalog.recents = catalog.recents.filter((s) => s.packId !== pack.id && !removedIds.has(s.id));
                    catalog.favorites = catalog.favorites.filter((s) => s.packId !== pack.id && !removedIds.has(s.id));
                    rebuildFavoriteSet();
                    for (const removedId of removedIds) {
                        syncMessageRowFavoriteAttributes(removedId, false);
                    }
                    ensureValidActiveTab();
                }
                render();
            }
        });
    }

    function createStickerImg(sticker, sizeClass) {
        const img = document.createElement('img');
        applyInitialStickerSrc(img, sticker);
        img.alt = sticker.emojiTags ? `Sticker ${sticker.emojiTags}` : 'Sticker';
        img.setAttribute('width', String(sticker.width || 512));
        img.setAttribute('height', String(sticker.height || 512));
        img.loading = 'lazy';
        img.decoding = 'async';
        img.setAttribute('data-sticker-img', 'true');
        img.setAttribute('data-sticker-id', sticker.id);
        img.setAttribute('data-sticker-static', sticker.staticUrl ? 'true' : 'false');
        img.className = sizeClass;
        return img;
    }

    function renderTabs() {
        if (!tabsBarEl) return;
        clearChildren(tabsBarEl);

        const makeIconTab = (key, label, iconName) => {
            const isActive = !searchQuery && activeTabKey === key;
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.setAttribute('role', 'tab');
            btn.setAttribute('aria-selected', isActive ? 'true' : 'false');
            btn.setAttribute('aria-label', label);
            btn.setAttribute('data-sticker-tab', key);
            btn.title = label;
            btn.className = isActive
                ? 'w-9 h-9 shrink-0 flex items-center justify-center border-b-2 border-[#B3929D] text-[#B3929D] cursor-pointer transition-colors'
                : 'w-9 h-9 shrink-0 flex items-center justify-center border-b-2 border-transparent text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#2B2725] dark:hover:text-[#F0E9E2] hover:bg-[#F0E9DF]/50 dark:hover:bg-[#45423E]/50 cursor-pointer transition-colors';

            const icon = document.createElement('span');
            icon.className = 'material-symbols-outlined text-[18px]';
            icon.textContent = iconName;
            btn.appendChild(icon);

            btn.addEventListener('mousedown', (e) => e.preventDefault());
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                activeTabKey = key;
                searchQuery = '';
                if (searchInputEl) searchInputEl.value = '';
                if (clearSearchBtn && clearSearchBtn.classList) clearSearchBtn.classList.add('hidden');
                render();
                if (gridEl) gridEl.scrollTop = 0;
            });
            return btn;
        };

        const makePackTab = (pack) => {
            const key = `pack:${pack.id}`;
            const isActive = !searchQuery && activeTabKey === key;
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.setAttribute('role', 'tab');
            btn.setAttribute('aria-selected', isActive ? 'true' : 'false');
            btn.setAttribute('aria-label', pack.name);
            btn.setAttribute('data-sticker-tab', key);
            btn.title = pack.name;
            btn.className = isActive
                ? 'w-9 h-9 shrink-0 flex items-center justify-center border-b-2 border-[#B3929D] bg-[#F0E9DF]/40 dark:bg-[#45423E]/40 cursor-pointer transition-colors'
                : 'w-9 h-9 shrink-0 flex items-center justify-center border-b-2 border-transparent hover:bg-[#F0E9DF]/50 dark:hover:bg-[#45423E]/50 cursor-pointer transition-colors';

            if (pack.stickers && pack.stickers.length > 0) {
                btn.appendChild(createStickerImg(pack.stickers[0], 'w-6 h-6 object-contain'));
            } else {
                const icon = document.createElement('span');
                icon.className = 'material-symbols-outlined text-[18px] text-[#6E6560] dark:text-[#C9C0B9]';
                icon.textContent = pack.isSystemPack ? 'sentiment_satisfied' : 'folder_special';
                btn.appendChild(icon);
            }

            btn.addEventListener('mousedown', (e) => e.preventDefault());
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                activeTabKey = key;
                searchQuery = '';
                if (searchInputEl) searchInputEl.value = '';
                if (clearSearchBtn && clearSearchBtn.classList) clearSearchBtn.classList.add('hidden');
                render();
                if (gridEl) gridEl.scrollTop = 0;
            });
            return btn;
        };

        tabsBarEl.appendChild(makeIconTab('recents', 'Recent stickers', 'schedule'));
        tabsBarEl.appendChild(makeIconTab('favorites', 'Favorite stickers', 'star'));

        if (catalog) {
            for (const pack of catalog.systemPacks) {
                tabsBarEl.appendChild(makePackTab(pack));
            }
            for (const pack of catalog.customPacks) {
                tabsBarEl.appendChild(makePackTab(pack));
            }
        }

        // + Create Sticker button pinned at end of pack strip
        const createBtn = document.createElement('button');
        createBtn.type = 'button';
        createBtn.id = 'sticker-picker-create-btn';
        createBtn.setAttribute('data-sticker-create', 'true');
        createBtn.setAttribute('aria-label', 'Create custom sticker');
        createBtn.title = 'Create custom sticker';
        createBtn.className = 'ml-auto w-9 h-9 shrink-0 flex items-center justify-center text-[#735761] dark:text-[#E1BDC9] hover:text-[#2B2725] dark:hover:text-[#F0E9E2] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 cursor-pointer transition-colors';
        const addIcon = document.createElement('span');
        addIcon.className = 'material-symbols-outlined text-[19px]';
        addIcon.textContent = 'add_photo_alternate';
        createBtn.appendChild(addIcon);
        createBtn.addEventListener('mousedown', (e) => e.preventDefault());
        createBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (onOpenCreator) {
                onOpenCreator({
                    customPacks: catalog ? catalog.customPacks : [],
                    onStickerCreated: (created) => {
                        const packId = normalizeId(created?.packId ?? created?.PackId);
                        ensureLoaded({ force: true, selectPackId: packId || null });
                    }
                });
            }
        });
        tabsBarEl.appendChild(createBtn);
        hydrateStickerImages(tabsBarEl, { getUrlFn });
    }

    function createStickerTile(sticker, { allowDelete = false } = {}) {
        const cell = document.createElement('div');
        cell.className = 'relative group flex items-center justify-center rounded-xl p-1.5 hover:bg-[#F0E9DF]/70 dark:hover:bg-[#45423E]/70 transition-colors';

        const pickBtn = document.createElement('button');
        pickBtn.type = 'button';
        pickBtn.setAttribute('data-sticker-pick', sticker.id);
        pickBtn.setAttribute('aria-label', sticker.emojiTags ? `Send sticker ${sticker.emojiTags}` : 'Send sticker');
        pickBtn.title = sticker.emojiTags || sticker.packName || 'Send sticker';
        pickBtn.className = 'w-full aspect-square flex items-center justify-center cursor-pointer focus:outline-none focus:ring-2 focus:ring-[#B3929D] rounded-lg';
        pickBtn.appendChild(createStickerImg(sticker, 'w-14 h-14 object-contain drop-shadow-xs transition-transform group-hover:scale-105'));

        pickBtn.addEventListener('mousedown', (e) => e.preventDefault());
        pickBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (onSendSticker) {
                onSendSticker(sticker);
            }
        });
        cell.appendChild(pickBtn);

        if (sticker.isAnimated) {
            const animBadge = document.createElement('span');
            animBadge.className = 'pointer-events-none absolute bottom-1 left-1 px-1 py-0.2 rounded bg-black/60 text-white text-[9px] font-bold tracking-tight';
            animBadge.textContent = 'ANIM';
            cell.appendChild(animBadge);
        }

        const isFav = favoriteIds.has(sticker.id) || sticker.isFavorited;
        const starBtn = document.createElement('button');
        starBtn.type = 'button';
        starBtn.setAttribute('data-sticker-star', sticker.id);
        starBtn.setAttribute('aria-label', isFav ? 'Remove from favorites' : 'Add to favorites');
        starBtn.title = isFav ? 'Unstar sticker' : 'Star sticker';
        starBtn.className = isFav
            ? 'absolute top-0.5 right-0.5 w-5 h-5 rounded-full bg-white/90 dark:bg-[#302F2C]/90 text-amber-500 shadow-xs flex items-center justify-center opacity-100 transition-opacity cursor-pointer'
            : 'absolute top-0.5 right-0.5 w-5 h-5 rounded-full bg-white/90 dark:bg-[#302F2C]/90 text-[#6E6560] dark:text-[#C9C0B9] hover:text-amber-500 shadow-xs flex items-center justify-center opacity-0 group-hover:opacity-100 focus:opacity-100 transition-opacity cursor-pointer';
        const starIcon = document.createElement('span');
        starIcon.className = 'material-symbols-outlined text-[13px]';
        starIcon.textContent = isFav ? 'star' : 'star_outline';
        starBtn.appendChild(starIcon);
        starBtn.disabled = favoriteInFlightIds.has(sticker.id);
        starBtn.addEventListener('mousedown', (e) => e.preventDefault());
        starBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (starBtn.disabled || favoriteInFlightIds.has(sticker.id)) return;
            toggleFavorite(sticker);
        });
        cell.appendChild(starBtn);

        if (allowDelete && sticker.isOwnedByCurrentUser) {
            const delBtn = document.createElement('button');
            delBtn.type = 'button';
            delBtn.setAttribute('data-sticker-delete', sticker.id);
            delBtn.setAttribute('aria-label', 'Delete custom sticker');
            delBtn.title = 'Remove from pack';
            delBtn.className = 'absolute top-0.5 left-0.5 w-5 h-5 rounded-full bg-white/90 dark:bg-[#302F2C]/90 text-red-500 hover:bg-red-50 dark:hover:bg-red-950/60 shadow-xs flex items-center justify-center opacity-0 group-hover:opacity-100 focus:opacity-100 transition-opacity cursor-pointer';
            const delIcon = document.createElement('span');
            delIcon.className = 'material-symbols-outlined text-[13px]';
            delIcon.textContent = 'delete';
            delBtn.appendChild(delIcon);
            delBtn.addEventListener('mousedown', (e) => e.preventDefault());
            delBtn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                handleDeleteCustomSticker(sticker, delBtn);
            });
            cell.appendChild(delBtn);
        }

        return cell;
    }

    function renderGrid() {
        if (!gridEl || !headingTitleEl) return;
        clearChildren(gridEl);
        activeCustomPackForDelete = null;
        if (deletePackBtn && deletePackBtn.classList) {
            deletePackBtn.classList.add('hidden');
        }
        if (headingMetaEl) headingMetaEl.textContent = '';

        if (isLoading && !catalog) {
            headingTitleEl.textContent = 'Loading stickers...';
            const loading = document.createElement('div');
            loading.className = 'col-span-4 py-10 text-center text-xs text-[#6E6560] dark:text-[#C9C0B9]';
            loading.textContent = 'Loading your sticker packs...';
            gridEl.appendChild(loading);
            return;
        }

        if (loadError && !catalog) {
            headingTitleEl.textContent = 'Stickers unavailable';
            const errWrap = document.createElement('div');
            errWrap.className = 'col-span-4 py-8 flex flex-col items-center gap-2 text-center text-xs text-[#6E6560] dark:text-[#C9C0B9]';
            const p = document.createElement('p');
            p.textContent = loadError;
            errWrap.appendChild(p);
            const retryBtn = document.createElement('button');
            retryBtn.type = 'button';
            retryBtn.className = 'px-3 py-1 rounded-lg bg-[#B3929D] text-white text-xs font-semibold cursor-pointer';
            retryBtn.textContent = 'Retry';
            retryBtn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                ensureLoaded({ force: true });
            });
            errWrap.appendChild(retryBtn);
            gridEl.appendChild(errWrap);
            return;
        }

        if (!catalog) return;

        const trimmed = searchQuery.trim();
        let items = [];
        let emptyMessage = 'No stickers yet.';
        let allowDelete = false;

        if (trimmed) {
            items = searchCatalogStickers(catalog, trimmed);
            headingTitleEl.textContent = items.length > 0 ? `Search results (${items.length})` : 'Search results';
            emptyMessage = 'No stickers match your search.';
        } else if (activeTabKey === 'recents') {
            items = catalog.recents;
            headingTitleEl.textContent = 'Recently used';
            emptyMessage = 'Stickers you send will appear here.';
        } else if (activeTabKey === 'favorites') {
            items = catalog.favorites;
            headingTitleEl.textContent = 'Favorite stickers';
            emptyMessage = 'Tap the star on any sticker to save it here.';
        } else if (activeTabKey.startsWith('pack:')) {
            const packId = activeTabKey.slice(5);
            const pack = catalog.systemPacks.find((p) => p.id === packId)
                || catalog.customPacks.find((p) => p.id === packId);
            if (pack) {
                items = pack.stickers || [];
                headingTitleEl.textContent = pack.name;
                if (headingMetaEl && pack.authorName) {
                    headingMetaEl.textContent = pack.authorName;
                }
                if (!pack.isSystemPack) {
                    allowDelete = true;
                    emptyMessage = 'No stickers in this pack yet. Click + to create one!';
                    activeCustomPackForDelete = pack;
                    if (deletePackBtn && deletePackBtn.classList) {
                        deletePackBtn.classList.remove('hidden');
                    }
                }
            }
        }

        const frag = document.createDocumentFragment();
        if (items.length === 0) {
            const hasNoPacksAtAll = !trimmed && catalog.systemPacks.length === 0 && catalog.customPacks.length === 0;
            const showCreateCta = hasNoPacksAtAll || (!trimmed && activeTabKey.startsWith('pack:') && allowDelete);
            const empty = document.createElement('div');
            empty.className = 'col-span-4 py-8 px-4 flex flex-col items-center justify-center gap-2.5 text-center text-xs text-[#6E6560] dark:text-[#C9C0B9]';
            const msg = document.createElement('p');
            msg.textContent = hasNoPacksAtAll
                ? 'No stickers yet. Create your own static or animated stickers!'
                : emptyMessage;
            empty.appendChild(msg);
            if (showCreateCta && onOpenCreator) {
                const ctaBtn = document.createElement('button');
                ctaBtn.type = 'button';
                ctaBtn.id = 'sticker-picker-empty-create-btn';
                ctaBtn.className = 'inline-flex items-center gap-1.5 px-3.5 py-1.5 rounded-xl bg-[#B3929D] text-white text-xs font-bold hover:opacity-95 active:scale-95 transition-all shadow-2xs cursor-pointer';
                const ctaIcon = document.createElement('span');
                ctaIcon.className = 'material-symbols-outlined text-[16px]';
                ctaIcon.textContent = 'add_photo_alternate';
                const ctaLabel = document.createElement('span');
                ctaLabel.textContent = 'Create Sticker';
                ctaBtn.appendChild(ctaIcon);
                ctaBtn.appendChild(ctaLabel);
                ctaBtn.addEventListener('mousedown', (e) => e.preventDefault());
                ctaBtn.addEventListener('click', (e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    onOpenCreator({
                        customPacks: catalog ? catalog.customPacks : [],
                        onStickerCreated: (created) => {
                            const packId = normalizeId(created?.packId ?? created?.PackId);
                            ensureLoaded({ force: true, selectPackId: packId || null });
                        }
                    });
                });
                empty.appendChild(ctaBtn);
            }
            frag.appendChild(empty);
        } else {
            for (let i = 0; i < items.length; i++) {
                frag.appendChild(createStickerTile(items[i], { allowDelete }));
            }
        }
        gridEl.appendChild(frag);
        hydrateStickerImages(gridEl, { getUrlFn });
    }

    function render() {
        if (!hostEl) return;
        renderTabs();
        renderGrid();
    }

    function mount(containerEl) {
        if (!containerEl || typeof document === 'undefined') return;
        hostEl = containerEl;
        clearChildren(hostEl);

        // Search row
        const headerEl = document.createElement('div');
        headerEl.className = 'p-2.5 pb-2 border-b border-[#E3DCD7] dark:border-[#514D48] flex items-center gap-2 shrink-0';

        const searchWrap = document.createElement('div');
        searchWrap.className = 'flex-1 flex items-center gap-1.5 bg-[#F8F6F3] dark:bg-[#302F2C] border border-[#E3DCD7] dark:border-[#514D48] rounded-xl px-2.5 py-1.5 focus-within:border-[#B3929D] focus-within:ring-1 focus-within:ring-[#B3929D]';

        const searchIcon = document.createElement('span');
        searchIcon.className = 'material-symbols-outlined text-[16px] text-[#6E6560] dark:text-[#C9C0B9] shrink-0';
        searchIcon.textContent = 'search';
        searchWrap.appendChild(searchIcon);

        searchInputEl = document.createElement('input');
        searchInputEl.id = 'sticker-picker-search';
        searchInputEl.type = 'search';
        searchInputEl.placeholder = 'Search stickers...';
        searchInputEl.setAttribute('aria-label', 'Search stickers');
        searchInputEl.autocomplete = 'off';
        searchInputEl.className = 'w-full bg-transparent text-xs text-[#2B2725] dark:text-[#F0E9E2] placeholder-[#6E6560] dark:placeholder-[#C9C0B9] focus:outline-none min-w-0';
        searchInputEl.addEventListener('input', () => {
            searchQuery = searchInputEl.value || '';
            if (clearSearchBtn && clearSearchBtn.classList) {
                if (searchQuery) clearSearchBtn.classList.remove('hidden');
                else clearSearchBtn.classList.add('hidden');
            }
            render();
            if (gridEl) gridEl.scrollTop = 0;
        });
        searchWrap.appendChild(searchInputEl);

        clearSearchBtn = document.createElement('button');
        clearSearchBtn.type = 'button';
        clearSearchBtn.setAttribute('aria-label', 'Clear sticker search');
        clearSearchBtn.className = 'hidden w-5 h-5 rounded-full flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#2B2725] dark:hover:text-[#F0E9E2] cursor-pointer shrink-0';
        const clearIcon = document.createElement('span');
        clearIcon.className = 'material-symbols-outlined text-[14px]';
        clearIcon.textContent = 'close';
        clearSearchBtn.appendChild(clearIcon);
        clearSearchBtn.addEventListener('mousedown', (e) => e.preventDefault());
        clearSearchBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            searchQuery = '';
            if (searchInputEl) searchInputEl.value = '';
            clearSearchBtn.classList.add('hidden');
            render();
        });
        searchWrap.appendChild(clearSearchBtn);
        headerEl.appendChild(searchWrap);
        hostEl.appendChild(headerEl);

        // Pack tabs bar
        tabsBarEl = document.createElement('div');
        tabsBarEl.setAttribute('role', 'tablist');
        tabsBarEl.setAttribute('aria-label', 'Sticker packs');
        tabsBarEl.setAttribute('data-sticker-tabs', 'true');
        tabsBarEl.className = 'flex items-center overflow-x-auto border-b border-[#E3DCD7] dark:border-[#514D48] bg-[#F8F6F3]/60 dark:bg-[#302F2C]/60 px-1 shrink-0';
        hostEl.appendChild(tabsBarEl);

        // Section heading
        headingRowEl = document.createElement('div');
        headingRowEl.className = 'px-3 pt-2 pb-1 flex items-center justify-between gap-2 shrink-0';
        const titleWrap = document.createElement('div');
        titleWrap.className = 'flex items-baseline gap-2 min-w-0';
        headingTitleEl = document.createElement('span');
        headingTitleEl.className = 'text-[11px] font-semibold uppercase tracking-wider text-[#6E6560] dark:text-[#C9C0B9] truncate';
        headingTitleEl.textContent = 'Stickers';
        headingMetaEl = document.createElement('span');
        headingMetaEl.className = 'text-[10px] text-[#6E6560]/80 dark:text-[#C9C0B9]/80 truncate';
        titleWrap.appendChild(headingTitleEl);
        titleWrap.appendChild(headingMetaEl);
        headingRowEl.appendChild(titleWrap);

        deletePackBtn = document.createElement('button');
        deletePackBtn.type = 'button';
        deletePackBtn.setAttribute('aria-label', 'Delete custom pack');
        deletePackBtn.title = 'Delete custom pack';
        deletePackBtn.className = 'hidden px-2 py-0.5 rounded text-[10px] font-semibold text-red-600 dark:text-red-400 hover:bg-red-50 dark:hover:bg-red-950/30 transition-colors cursor-pointer shrink-0';
        deletePackBtn.textContent = 'Delete pack';
        deletePackBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (activeCustomPackForDelete) {
                handleDeleteActiveCustomPack(activeCustomPackForDelete, deletePackBtn);
            }
        });
        headingRowEl.appendChild(deletePackBtn);
        hostEl.appendChild(headingRowEl);

        // 4-column sticker grid
        gridEl = document.createElement('div');
        gridEl.setAttribute('data-sticker-grid', 'true');
        gridEl.className = 'grid grid-cols-4 gap-1.5 px-2.5 pb-2.5 flex-1 min-h-0 overflow-y-auto custom-scroll content-start';
        hostEl.appendChild(gridEl);

        render();
    }

    // --- STICKER INFO MODAL (#sticker-info-modal) ---
    function syncInfoModalStarButton(isFav) {
        if (typeof document === 'undefined') return;
        const btn = document.getElementById('sticker-info-fav-btn');
        const icon = document.getElementById('sticker-info-fav-icon');
        const label = document.getElementById('sticker-info-fav-label');
        if (!btn) return;
        btn.setAttribute('aria-pressed', isFav ? 'true' : 'false');
        if (icon) icon.textContent = isFav ? 'star' : 'star_outline';
        if (label) label.textContent = isFav ? 'Remove from Favorites' : 'Add to Favorites';
    }

    function openStickerInfoModal(stickerInfo, context = {}) {
        if (typeof document === 'undefined' || !stickerInfo) return;
        const modal = document.getElementById('sticker-info-modal');
        if (!modal) return;

        if (onBeforeOpenModal) {
            onBeforeOpenModal();
        }

        const normalized = normalizeSticker(stickerInfo);
        if (!normalized) return;

        const isFav = catalog ? favoriteIds.has(normalized.id) : normalized.isFavorited;
        normalized.isFavorited = isFav;
        activeInfoSticker = normalized;
        activeInfoContext = {
            conversationId: context.conversationId || getActiveConversationId() || null,
            messageId: context.messageId || null
        };

        const img = document.getElementById('sticker-info-img');
        const packNameEl = document.getElementById('sticker-info-pack-name');
        const authorEl = document.getElementById('sticker-info-pack-author');
        const animBadge = document.getElementById('sticker-info-anim-badge');

        if (img) {
            applyInitialStickerSrc(img, normalized);
            img.alt = normalized.emojiTags || normalized.packName || 'Sticker';
            img.setAttribute('data-sticker-img', 'true');
            img.setAttribute('data-sticker-id', normalized.id);
            img.setAttribute('data-sticker-static', normalized.staticUrl ? 'true' : 'false');
            img.removeAttribute('data-sticker-bound');
            img.removeAttribute('data-sticker-retried');
        }
        if (packNameEl) packNameEl.textContent = normalized.packName || 'Stickers';
        if (authorEl) authorEl.textContent = normalized.packAuthor || 'Community Sticker';
        if (animBadge && animBadge.classList) {
            if (normalized.isAnimated) animBadge.classList.remove('hidden');
            else animBadge.classList.add('hidden');
        }

        syncInfoModalStarButton(isFav);
        modal.classList.remove('hidden');
        hydrateStickerImages(modal, { getUrlFn });
    }

    function closeStickerInfoModal() {
        activeInfoSticker = null;
        activeInfoContext = null;
        if (typeof document === 'undefined') return;
        const modal = document.getElementById('sticker-info-modal');
        const img = document.getElementById('sticker-info-img');
        if (img && typeof img.removeAttribute === 'function') {
            img.removeAttribute('src');
        }
        if (modal && modal.classList) {
            modal.classList.add('hidden');
        }
    }

    const onInfoModalDocumentClick = async (e) => {
        if (!e.target || typeof e.target.closest !== 'function') return;

        const infoTrigger = e.target.closest('[data-sticker-info-trigger="true"]');
        if (infoTrigger) {
            e.preventDefault();
            e.stopPropagation();
            const row = infoTrigger.closest('.message-bubble-row');
            const stickerData = {
                id: infoTrigger.getAttribute('data-sticker-id') || '',
                packId: infoTrigger.getAttribute('data-sticker-pack-id') || '',
                packName: infoTrigger.getAttribute('data-sticker-pack-name') || 'Stickers',
                packAuthor: infoTrigger.getAttribute('data-sticker-pack-author') || 'Community Sticker',
                isAnimated: infoTrigger.getAttribute('data-sticker-animated') === 'true',
                isSystem: infoTrigger.getAttribute('data-sticker-system') === 'true',
                staticUrl: infoTrigger.getAttribute('data-sticker-static-url') || null,
                emojiTags: infoTrigger.getAttribute('data-sticker-tags') || '',
                isFavorited: infoTrigger.getAttribute('data-sticker-favorited') === 'true'
            };
            openStickerInfoModal(stickerData, {
                conversationId: getActiveConversationId(),
                messageId: row ? row.getAttribute('data-message-id') : null
            });
            return;
        }

        const closeBtn = e.target.closest('#sticker-info-close-btn');
        if (closeBtn) {
            e.preventDefault();
            closeStickerInfoModal();
            return;
        }

        const favBtn = e.target.closest('#sticker-info-fav-btn');
        if (favBtn && activeInfoSticker) {
            e.preventDefault();
            favBtn.disabled = true;
            try {
                const nowFav = await toggleFavorite(activeInfoSticker, {
                    sourceConversationId: activeInfoContext?.conversationId || null,
                    sourceMessageId: activeInfoContext?.messageId || null
                });
                if (activeInfoSticker) {
                    activeInfoSticker.isFavorited = nowFav;
                    syncInfoModalStarButton(nowFav);
                }
            } finally {
                favBtn.disabled = false;
            }
            return;
        }

        const modal = document.getElementById('sticker-info-modal');
        if (modal && e.target === modal) {
            closeStickerInfoModal();
        }
    };

    const onInfoModalKeyDown = (e) => {
        if (e.key !== 'Escape') return;
        const modal = typeof document !== 'undefined' ? document.getElementById('sticker-info-modal') : null;
        if (modal && modal.classList && !modal.classList.contains('hidden')) {
            e.preventDefault();
            closeStickerInfoModal();
        }
    };

    function bindInfoModalListeners() {
        if (infoModalListenersBound || typeof document === 'undefined') return;
        document.addEventListener('click', onInfoModalDocumentClick);
        document.addEventListener('keydown', onInfoModalKeyDown);
        infoModalListenersBound = true;
    }

    function unbindInfoModalListeners() {
        if (!infoModalListenersBound || typeof document === 'undefined') return;
        document.removeEventListener('click', onInfoModalDocumentClick);
        document.removeEventListener('keydown', onInfoModalKeyDown);
        infoModalListenersBound = false;
    }

    function invalidateCache() {
        loadRequestToken++;
        activeCustomPackForDelete = null;
        catalog = null;
        catalogUserId = null;
        catalogLoadPromise = null;
        isLoading = false;
        loadError = '';
        searchQuery = '';
        favoriteIds.clear();
        favoriteInFlightIds.clear();
        clearStickerUrlCache();
        closeStickerInfoModal();
        closeConfirmModal({ restoreFocus: false });
        if (searchInputEl) searchInputEl.value = '';
        if (clearSearchBtn && clearSearchBtn.classList) clearSearchBtn.classList.add('hidden');
        render();
    }

    bindInfoModalListeners();

    return {
        mount,
        ensureLoaded,
        refreshCatalog: (selectPackId = null) => ensureLoaded({ force: true, selectPackId }),
        recordLocalRecent,
        toggleFavorite,
        isFavorited: (id) => favoriteIds.has(normalizeId(id)),
        getCustomPacks: () => (catalog ? catalog.customPacks : []),
        openStickerInfoModal,
        closeStickerInfoModal,
        closeConfirmModal: () => closeConfirmModal({ restoreFocus: false }),
        closeIfMessage: (msgId) => {
            if (activeInfoContext && msgId && String(activeInfoContext.messageId) === String(msgId)) {
                closeStickerInfoModal();
            }
        },
        invalidateCache,
        destroy: () => {
            unbindInfoModalListeners();
            unbindConfirmModalListeners();
            invalidateCache();
            if (createdFallbackConfirmModal && typeof createdFallbackConfirmModal.remove === 'function') {
                createdFallbackConfirmModal.remove();
            }
            createdFallbackConfirmModal = null;
            hostEl = null;
            searchInputEl = null;
            clearSearchBtn = null;
            tabsBarEl = null;
            headingRowEl = null;
            headingTitleEl = null;
            headingMetaEl = null;
            deletePackBtn = null;
            gridEl = null;
        }
    };
}
