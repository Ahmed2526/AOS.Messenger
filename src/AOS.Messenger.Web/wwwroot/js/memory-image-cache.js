/**
 * AOS Messenger - Bounded Tab-Only In-Memory Image Cache (RAM LRU)
 *
 * Security & Privacy Invariants:
 * - Lives strictly in JavaScript heap memory for the active tab session.
 * - Never writes to localStorage, sessionStorage, IndexedDB, or Service Worker Cache Storage.
 * - Server endpoints and R2 continue emitting Cache-Control: no-store.
 * - Enforces both item-count and byte-size LRU caps and revokes blob: ObjectURLs on eviction,
 *   message deletion, block, avatar update/removal, conversation switch, or session wipe.
 * - Rejects untrusted URLs via caller-supplied isSafeUrlFn and rejects SVG/scriptable blobs.
 */

export const DEFAULT_CHAT_PHOTO_MAX_ITEMS = 25;
export const DEFAULT_CHAT_PHOTO_MAX_BYTES = 80 * 1024 * 1024;
export const DEFAULT_AVATAR_FULL_MAX_ITEMS = 20;
export const DEFAULT_AVATAR_FULL_MAX_BYTES = 30 * 1024 * 1024;

function defaultRevokeObjectUrl(url) {
    if (!url || typeof URL === 'undefined' || typeof URL.revokeObjectURL !== 'function') return;
    try {
        URL.revokeObjectURL(url);
    } catch {}
}

function defaultCreateObjectUrl(blob) {
    if (!blob || typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function') return '';
    return URL.createObjectURL(blob);
}

function defaultFetchImpl(url, options) {
    if (typeof fetch !== 'function') {
        return Promise.reject(new TypeError('fetch unavailable'));
    }
    return fetch(url, options);
}

function defaultIsSafeUrl(rawUrl) {
    if (!rawUrl || typeof rawUrl !== 'string') return false;
    const trimmed = rawUrl.trim();
    if (!trimmed) return false;
    if (trimmed.startsWith('/')) {
        return !trimmed.startsWith('//') && !trimmed.includes('..') && !trimmed.includes('\\');
    }
    try {
        return new URL(trimmed).protocol === 'https:';
    } catch {
        return false;
    }
}

/**
 * Returns true if the URL resolves to the same origin as window.location (or relative /).
 * @param {string} rawUrl
 * @returns {boolean}
 */
export function isSameOriginUrl(rawUrl) {
    if (!rawUrl || typeof rawUrl !== 'string') return false;
    const trimmed = rawUrl.trim();
    if (trimmed.startsWith('/') && !trimmed.startsWith('//')) return true;
    try {
        if (typeof window === 'undefined' || !window.location || !window.location.origin) {
            return false;
        }
        return new URL(trimmed).origin === window.location.origin;
    } catch {
        return false;
    }
}

function isAllowedRasterMime(mimeType) {
    if (!mimeType || typeof mimeType !== 'string') return true;
    const normalized = mimeType.trim().toLowerCase().split(';')[0];
    if (!normalized || normalized === 'application/octet-stream') return true;
    if (normalized.includes('svg') || normalized.includes('html') || normalized.includes('javascript') || normalized.includes('xml')) {
        return false;
    }
    return normalized.startsWith('image/');
}

function clearFallbackElement(el) {
    if (!el) return;
    try {
        if (typeof el.removeAttribute === 'function') {
            el.removeAttribute('src');
        } else {
            el.src = '';
        }
        if (el.classList && typeof el.classList.add === 'function') {
            el.classList.add('hidden');
        }
        if (typeof el.remove === 'function') {
            el.remove();
        }
    } catch {}
}

/**
 * Creates a bounded in-memory LRU cache supporting both Blob ObjectURLs and fallback <img> nodes.
 * @param {{
 *   maxItems?: number,
 *   maxBytes?: number,
 *   revokeObjectUrlFn?: Function|null
 * }} [options]
 */
export function createMemoryImageCache({
    maxItems = DEFAULT_CHAT_PHOTO_MAX_ITEMS,
    maxBytes = DEFAULT_CHAT_PHOTO_MAX_BYTES,
    revokeObjectUrlFn = null
} = {}) {
    const capItems = Math.max(0, Math.floor(Number(maxItems) || 0));
    const capBytes = Math.max(0, Math.floor(Number(maxBytes) || 0));
    const entries = new Map();
    let totalBytes = 0;

    function resolveRevoker(entryRevokeFn) {
        return typeof entryRevokeFn === 'function'
            ? entryRevokeFn
            : (typeof revokeObjectUrlFn === 'function' ? revokeObjectUrlFn : defaultRevokeObjectUrl);
    }

    function teardownEntry(entry, { skipBlobUrl = null } = {}) {
        if (!entry) return;
        if (entry.kind === 'blob') {
            totalBytes = Math.max(0, totalBytes - (entry.byteSize || 0));
            if (entry.objectUrl && entry.objectUrl !== skipBlobUrl) {
                resolveRevoker(entry.revokeFn)(entry.objectUrl);
            }
        } else if (entry.kind === 'element') {
            clearFallbackElement(entry.element);
        }
    }

    function get(key) {
        if (!key || !entries.has(key)) return null;
        const entry = entries.get(key);
        entries.delete(key);
        entries.set(key, entry);
        return entry;
    }

    function has(key) {
        return !!(key && entries.has(key));
    }

    function evictOldest({ targetAdditionalBytes = 0 } = {}) {
        while (entries.size > 0 && (entries.size >= capItems || (totalBytes + targetAdditionalBytes > capBytes))) {
            const oldestKey = entries.keys().next().value;
            if (oldestKey === undefined) break;
            const oldestEntry = entries.get(oldestKey);
            entries.delete(oldestKey);
            teardownEntry(oldestEntry);
        }
    }

    function setBlob(key, objectUrl, byteSize, meta = {}, entryRevokeFn = null) {
        if (!key || typeof objectUrl !== 'string' || !objectUrl.startsWith('blob:')) {
            return false;
        }
        const size = Math.max(0, Math.floor(Number(byteSize) || 0));
        if (capItems <= 0 || size > capBytes) {
            return false;
        }

        if (entries.has(key)) {
            const existing = entries.get(key);
            entries.delete(key);
            teardownEntry(existing, { skipBlobUrl: objectUrl });
        }

        evictOldest({ targetAdditionalBytes: size });

        entries.set(key, {
            kind: 'blob',
            objectUrl,
            byteSize: size,
            meta: meta || {},
            revokeFn: typeof entryRevokeFn === 'function' ? entryRevokeFn : null
        });
        totalBytes += size;
        return true;
    }

    function setElement(key, element, meta = {}) {
        if (!key || !element || capItems <= 0) {
            return false;
        }

        if (entries.has(key)) {
            const existing = entries.get(key);
            if (existing?.kind === 'element' && existing.element === element) {
                entries.delete(key);
                entries.set(key, { ...existing, meta: meta || existing.meta });
                return true;
            }
            entries.delete(key);
            teardownEntry(existing);
        }

        evictOldest({ targetAdditionalBytes: 0 });

        entries.set(key, {
            kind: 'element',
            element,
            byteSize: 0,
            meta: meta || {}
        });
        return true;
    }

    function deleteKey(key) {
        if (!key || !entries.has(key)) return false;
        const entry = entries.get(key);
        entries.delete(key);
        teardownEntry(entry);
        return true;
    }

    function deleteWhere(predicateFn) {
        if (typeof predicateFn !== 'function') return 0;
        let removed = 0;
        for (const [key, entry] of Array.from(entries.entries())) {
            if (predicateFn(entry.meta, key, entry)) {
                entries.delete(key);
                teardownEntry(entry);
                removed++;
            }
        }
        return removed;
    }

    function clear() {
        for (const entry of entries.values()) {
            teardownEntry(entry);
        }
        entries.clear();
        totalBytes = 0;
    }

    function stats() {
        return {
            count: entries.size,
            totalBytes,
            maxItems: capItems,
            maxBytes: capBytes
        };
    }

    return {
        get,
        has,
        setBlob,
        setElement,
        delete: deleteKey,
        deleteWhere,
        clear,
        stats
    };
}

/**
 * Downloads an image URL into a local in-memory blob: ObjectURL with strict MIME & safety checks.
 */
async function fetchImageAsBlobUrl({
    resolvedUrl,
    signal = undefined,
    fetchFn = null,
    createObjectUrlFn = null,
    revokeObjectUrlFn = null,
    isSafeUrlFn = defaultIsSafeUrl
}) {
    if (!isSafeUrlFn(resolvedUrl)) {
        const err = new Error('Unsafe image URL rejected');
        err.isUnsafeUrl = true;
        throw err;
    }

    const doCreateUrl = typeof createObjectUrlFn === 'function' ? createObjectUrlFn : defaultCreateObjectUrl;
    const doRevokeUrl = typeof revokeObjectUrlFn === 'function' ? revokeObjectUrlFn : defaultRevokeObjectUrl;
    if (typeof createObjectUrlFn !== 'function' && (typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function')) {
        throw new TypeError('URL.createObjectURL unavailable');
    }

    const doFetch = typeof fetchFn === 'function' ? fetchFn : defaultFetchImpl;
    const sameOrigin = isSameOriginUrl(resolvedUrl);
    const response = await doFetch(resolvedUrl, {
        method: 'GET',
        mode: sameOrigin ? 'same-origin' : 'cors',
        credentials: sameOrigin ? 'same-origin' : 'omit',
        cache: 'no-store',
        signal
    });

    if (!response || !response.ok) {
        const err = new Error(`HTTP ${response?.status || 0}`);
        err.isHttpFailure = true;
        err.httpStatus = response?.status || 0;
        throw err;
    }

    const headerType = typeof response.headers?.get === 'function' ? response.headers.get('content-type') : '';
    const blob = await response.blob();
    const byteSize = Number(blob?.size) || 0;
    const mimeType = (blob && blob.type) || headerType || '';

    if (!blob || byteSize <= 0) {
        const err = new Error('Empty image blob');
        err.isHttpFailure = true;
        err.httpStatus = response.status || 0;
        throw err;
    }

    if (!isAllowedRasterMime(mimeType)) {
        const err = new Error('Rejected non-raster image MIME type');
        err.isUnsafeMime = true;
        throw err;
    }

    const objectUrl = doCreateUrl(blob);
    if (typeof objectUrl !== 'string' || !objectUrl.startsWith('blob:')) {
        if (objectUrl) doRevokeUrl(objectUrl);
        throw new TypeError('Failed to allocate blob ObjectURL');
    }

    return {
        objectUrl,
        byteSize
    };
}

/**
 * Parks a live loaded CORS-fallback <img> element inside its parent container while swapping in
 * a clean primary <img> node with id intact and no src attribute.
 * @param {HTMLImageElement} primaryImg
 * @returns {{ freshPrimary: HTMLImageElement, parkedElement: HTMLImageElement } | null}
 */
export function parkLoadedFallbackImage(primaryImg) {
    if (!primaryImg || !primaryImg.parentNode || typeof document === 'undefined' || typeof document.createElement !== 'function') {
        return null;
    }
    const parent = primaryImg.parentNode;
    if (typeof parent.insertBefore !== 'function') {
        return null;
    }

    const freshPrimary = document.createElement('img');
    if (primaryImg.id) {
        freshPrimary.id = primaryImg.id;
    }
    freshPrimary.alt = '';
    freshPrimary.className = primaryImg.className || '';
    if (freshPrimary.classList && typeof freshPrimary.classList.add === 'function') {
        freshPrimary.classList.add('hidden');
    }

    parent.insertBefore(freshPrimary, primaryImg);

    if (typeof primaryImg.removeAttribute === 'function') {
        primaryImg.removeAttribute('id');
    } else {
        primaryImg.id = '';
    }
    if (primaryImg.classList && typeof primaryImg.classList.add === 'function') {
        primaryImg.classList.add('hidden');
    }

    return {
        freshPrimary,
        parkedElement: primaryImg
    };
}

/**
 * Creates a coordinated image loader combining bounded LRU caching, in-flight request coalescing,
 * scope/global epoch race invalidation, AbortController cancellation, and automatic CORS fallback.
 */
export const DEFAULT_SIGNED_URL_CACHE_MAX_ENTRIES = 500;

/**
 * Stores an entry in a bounded Map, refreshing LRU insertion order and evicting the oldest entry
 * once the map exceeds maxEntries.
 * @param {Map<string, any>} map
 * @param {string} key
 * @param {any} value
 * @param {number} [maxEntries]
 */
export function setBoundedMapEntry(map, key, value, maxEntries = DEFAULT_SIGNED_URL_CACHE_MAX_ENTRIES) {
    if (!map || typeof map.set !== 'function') return;
    if (map.has(key)) {
        map.delete(key);
    }
    map.set(key, value);
    const cap = Math.max(1, Number(maxEntries) || DEFAULT_SIGNED_URL_CACHE_MAX_ENTRIES);
    while (map.size > cap) {
        const oldestKey = map.keys().next().value;
        if (oldestKey === undefined) break;
        map.delete(oldestKey);
    }
}

export function createCachedImageLoader({
    maxItems = DEFAULT_CHAT_PHOTO_MAX_ITEMS,
    maxBytes = DEFAULT_CHAT_PHOTO_MAX_BYTES,
    isSafeUrlFn = defaultIsSafeUrl,
    fetchFn = null,
    createObjectUrlFn = null,
    revokeObjectUrlFn = null
} = {}) {
    const cache = createMemoryImageCache({ maxItems, maxBytes, revokeObjectUrlFn });
    const inFlight = new Map();
    const scopeEpochs = new Map();
    let globalEpoch = 0;

    function normScopeKey(scopeKey) {
        if (scopeKey === null || scopeKey === undefined) return '';
        return String(scopeKey).trim().toLowerCase();
    }

    function getScopeEpoch(normScope) {
        return normScope ? (scopeEpochs.get(normScope) || 0) : 0;
    }

    function getEpochs(scopeKey = '') {
        const normScope = normScopeKey(scopeKey);
        return {
            globalEpoch,
            scopeEpoch: getScopeEpoch(normScope)
        };
    }

    function isEpochValid(capturedGlobalEpoch, scopeKey, capturedScopeEpoch) {
        if (capturedGlobalEpoch !== globalEpoch) return false;
        const normScope = normScopeKey(scopeKey);
        if (normScope && capturedScopeEpoch !== getScopeEpoch(normScope)) return false;
        return true;
    }

    function abortEntry(entry) {
        try {
            if (entry && entry.controller && typeof entry.controller.abort === 'function') {
                entry.controller.abort();
            }
        } catch {}
    }

    function evictKey(cacheKey) {
        if (!cacheKey) return false;
        const active = inFlight.get(cacheKey);
        if (active) {
            abortEntry(active);
            inFlight.delete(cacheKey);
        }
        return cache.delete(cacheKey);
    }

    function evictScope(scopeKey, predicateFn = null) {
        const normScope = normScopeKey(scopeKey);
        if (!normScope) return 0;

        scopeEpochs.set(normScope, getScopeEpoch(normScope) + 1);
        for (const [key, entry] of Array.from(inFlight.entries())) {
            if (entry.scopeKey === normScope) {
                abortEntry(entry);
                inFlight.delete(key);
            }
        }

        const matcher = typeof predicateFn === 'function'
            ? predicateFn
            : (meta) => normScopeKey(meta?.scopeKey) === normScope;
        return cache.deleteWhere(matcher);
    }

    function clear() {
        globalEpoch++;
        scopeEpochs.clear();
        for (const entry of inFlight.values()) {
            abortEntry(entry);
        }
        inFlight.clear();
        cache.clear();
    }

    function setElement(cacheKey, element, meta = {}) {
        const expectedGlobal = meta?.cacheEpoch;
        const expectedScope = meta?.scopeEpoch;
        const normScope = normScopeKey(meta?.scopeKey);
        if (expectedGlobal !== undefined && !isEpochValid(expectedGlobal, normScope, expectedScope)) {
            clearFallbackElement(element);
            return false;
        }
        return cache.setElement(cacheKey, element, {
            ...(meta || {}),
            scopeKey: normScope
        });
    }

    async function load({
        cacheKey,
        scopeKey = '',
        meta = {},
        getSignedUrlFn,
        refreshSignedUrlFn = null,
        fetchFn: callFetchFn = null,
        createObjectUrlFn: callCreateUrlFn = null,
        revokeObjectUrlFn: callRevokeUrlFn = null
    }) {
        if (!cacheKey) {
            return { status: 'unavailable', reason: 'missing-key' };
        }

        const hit = cache.get(cacheKey);
        if (hit) {
            return { status: 'hit', entry: hit };
        }

        if (inFlight.has(cacheKey)) {
            return inFlight.get(cacheKey).promise;
        }

        const normScope = normScopeKey(scopeKey);
        const startGlobalEpoch = globalEpoch;
        const startScopeEpoch = getScopeEpoch(normScope);
        const controller = typeof AbortController === 'function' ? new AbortController() : null;
        const signal = controller ? controller.signal : undefined;
        const activeFetch = callFetchFn || fetchFn;
        const activeCreateUrl = callCreateUrlFn || createObjectUrlFn;
        const activeRevokeUrl = callRevokeUrlFn || revokeObjectUrlFn;

        const isStale = () => Boolean(signal?.aborted) || !isEpochValid(startGlobalEpoch, normScope, startScopeEpoch);

        const promise = (async () => {
            let resolvedUrl = '';
            try {
                if (typeof getSignedUrlFn === 'function') {
                    resolvedUrl = await getSignedUrlFn(signal);
                }
            } catch (err) {
                if (err?.name === 'AbortError' || isStale()) {
                    return { status: 'aborted' };
                }
                return { status: 'unavailable', reason: 'url-error', error: err };
            }

            if (isStale()) {
                return { status: 'aborted' };
            }
            if (!resolvedUrl) {
                return { status: 'unavailable', reason: 'missing-url' };
            }
            if (!isSafeUrlFn(resolvedUrl)) {
                return { status: 'unavailable', reason: 'unsafe-url' };
            }

            const attemptBlobFetch = (targetUrl) => fetchImageAsBlobUrl({
                resolvedUrl: targetUrl,
                signal,
                fetchFn: activeFetch,
                createObjectUrlFn: activeCreateUrl,
                revokeObjectUrlFn: activeRevokeUrl,
                isSafeUrlFn
            });

            try {
                let blobResult;
                try {
                    blobResult = await attemptBlobFetch(resolvedUrl);
                } catch (firstErr) {
                    if (firstErr?.name === 'AbortError' || isStale()) {
                        return { status: 'aborted' };
                    }
                    if (firstErr?.isHttpFailure && firstErr.httpStatus === 403 && typeof refreshSignedUrlFn === 'function') {
                        let refreshedUrl = '';
                        try {
                            refreshedUrl = await refreshSignedUrlFn(signal);
                        } catch (refreshErr) {
                            if (refreshErr?.name === 'AbortError' || isStale()) {
                                return { status: 'aborted' };
                            }
                            throw firstErr;
                        }
                        if (isStale()) return { status: 'aborted' };
                        if (refreshedUrl && isSafeUrlFn(refreshedUrl)) {
                            resolvedUrl = refreshedUrl;
                            blobResult = await attemptBlobFetch(refreshedUrl);
                        } else {
                            throw firstErr;
                        }
                    } else {
                        throw firstErr;
                    }
                }

                const doRevoke = typeof activeRevokeUrl === 'function' ? activeRevokeUrl : defaultRevokeObjectUrl;
                if (isStale()) {
                    doRevoke(blobResult.objectUrl);
                    return { status: 'aborted' };
                }

                const stored = cache.setBlob(
                    cacheKey,
                    blobResult.objectUrl,
                    blobResult.byteSize,
                    { ...(meta || {}), scopeKey: normScope },
                    activeRevokeUrl
                );

                return {
                    status: 'blob',
                    objectUrl: blobResult.objectUrl,
                    byteSize: blobResult.byteSize,
                    stored,
                    revokeFn: doRevoke
                };
            } catch (err) {
                if (err?.name === 'AbortError' || isStale()) {
                    return { status: 'aborted' };
                }
                if (err?.isUnsafeUrl || err?.isUnsafeMime || err?.isHttpFailure) {
                    return {
                        status: 'unavailable',
                        reason: err?.isUnsafeMime ? 'unsafe-mime' : 'http-error',
                        httpStatus: err?.httpStatus || 0
                    };
                }
                return {
                    status: 'fallback-url',
                    url: resolvedUrl,
                    cacheEpoch: startGlobalEpoch,
                    scopeEpoch: startScopeEpoch,
                    scopeKey: normScope
                };
            } finally {
                const current = inFlight.get(cacheKey);
                if (current && current.promise === promise) {
                    inFlight.delete(cacheKey);
                }
            }
        })();

        inFlight.set(cacheKey, {
            promise,
            controller,
            scopeKey: normScope
        });

        return promise;
    }

    return {
        get: (cacheKey) => cache.get(cacheKey),
        has: (cacheKey) => cache.has(cacheKey),
        load,
        setElement,
        evictKey,
        evictScope,
        clear,
        getEpochs,
        stats: () => ({
            ...cache.stats(),
            inFlightCount: inFlight.size
        })
    };
}
