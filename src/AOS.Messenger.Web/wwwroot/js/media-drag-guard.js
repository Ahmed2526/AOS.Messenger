/**
 * AOS Messenger - Media Drag & Drop Guard Module
 *
 * Defense-in-depth layer that:
 * 1. Prevents HTML5 dragstart on in-app media, stickers, avatars, lightboxes, and UI navigation links
 *    while preserving desktop text-selection drags.
 * 2. Prevents dropping internal media/sticker/avatar/dev-media/blob or currently rendered signed R2 URLs
 *    into text inputs, while passing through real OS file drops and normal external text/URL drops.
 */

const MEDIA_DRAG_SELECTORS = [
    'img',
    'video',
    '[data-media-img]',
    '[data-sticker-img]',
    '[data-avatar-img]',
    '[data-lightbox-trigger]',
    '[data-sticker-info-trigger]',
    '[data-media-download]',
    '[data-conversation-link]',
    'a[draggable="false"]'
];

const TEXT_INPUT_TYPES = new Set([
    '',
    'text',
    'search',
    'url',
    'email',
    'tel',
    'password'
]);

const INTERNAL_MEDIA_PATH_PATTERNS = [
    /^\/api\/conversations\/[0-9a-f-]+\/messages\/\d+\/media\/[0-9a-f-]+(?:\/url)?$/i,
    /^\/api\/stickers\/[0-9a-f-]+\/(?:image|url)$/i,
    /^\/api\/users\/[0-9a-f-]+\/avatar(?:\/url)?$/i,
    /^\/stickers\/packs\/[a-z0-9-]+\/[a-z0-9-]+\.webp$/i,
    /^\/dev-media\/download$/i
];

function normalizeEventElement(target) {
    if (!target) return null;
    if (target.nodeType === 1) return target;
    if (target.parentElement && target.parentElement.nodeType === 1) return target.parentElement;
    if (target.parentNode && target.parentNode.nodeType === 1) return target.parentNode;
    return null;
}

function matchesAnySingleSelector(el, selectors) {
    if (!el || typeof el.closest !== 'function') return false;
    for (let i = 0; i < selectors.length; i++) {
        if (el.closest(selectors[i])) {
            return true;
        }
    }
    return false;
}

function isTextInputTarget(el) {
    if (!el || el.nodeType !== 1) return false;
    if (el.isContentEditable === true) return true;
    const tag = typeof el.tagName === 'string' ? el.tagName.toLowerCase() : '';
    if (tag === 'textarea') return true;
    if (tag === 'input') {
        const attrType = typeof el.getAttribute === 'function' ? el.getAttribute('type') : null;
        const rawType = attrType !== null && attrType !== undefined ? attrType : (el.type ?? '');
        const normalizedType = String(rawType).trim().toLowerCase();
        return TEXT_INPUT_TYPES.has(normalizedType);
    }
    return false;
}

function looksLikeUrlCandidate(str) {
    if (typeof str !== 'string') return false;
    const trimmed = str.trim();
    if (!trimmed) return false;
    if (trimmed.startsWith('/')) return true;
    const lower = trimmed.toLowerCase();
    return lower.startsWith('http://') || lower.startsWith('https://') || lower.startsWith('blob:');
}

function canonicalizeUrl(raw, origin) {
    if (typeof raw !== 'string') return null;
    const trimmed = raw.trim();
    if (!trimmed) return null;
    try {
        return (origin ? new URL(trimmed, origin) : new URL(trimmed)).href;
    } catch {
        return null;
    }
}

/**
 * Extracts unique non-comment URL/text candidates from a Drop DataTransfer.
 * Handles RFC 2483 multi-line text/uri-list payloads (ignoring '#' comments).
 * @param {DataTransfer|object|null|undefined} dataTransfer
 * @returns {string[]}
 */
export function extractDroppedUrlCandidates(dataTransfer) {
    if (!dataTransfer || typeof dataTransfer.getData !== 'function') return [];
    const seen = new Set();
    const out = [];

    const addCandidate = (value) => {
        if (typeof value !== 'string') return;
        const trimmed = value.trim();
        if (!trimmed || seen.has(trimmed)) return;
        seen.add(trimmed);
        out.push(trimmed);
    };

    try {
        const uriList = dataTransfer.getData('text/uri-list');
        if (typeof uriList === 'string' && uriList.length > 0) {
            const lines = uriList.split(/\r?\n/);
            for (let i = 0; i < lines.length; i++) {
                const line = lines[i].trim();
                if (!line || line.startsWith('#')) continue;
                addCandidate(line);
            }
        }
    } catch {}

    try {
        const plain = dataTransfer.getData('text/plain');
        if (typeof plain === 'string' && plain.length > 0) {
            addCandidate(plain);
        }
    } catch {}

    return out;
}

/**
 * Collects canonicalized URLs from currently rendered <img> and <video> elements.
 * Uses separate single-tag queries for compatibility with lightweight mock DOMs.
 * @param {Document|object|null|undefined} documentRef
 * @param {string} [origin]
 * @returns {Set<string>}
 */
export function collectRenderedMediaUrls(documentRef, origin = '') {
    const urls = new Set();
    if (!documentRef || typeof documentRef.querySelectorAll !== 'function') return urls;

    const addFromElements = (elements) => {
        if (!elements) return;
        for (let i = 0; i < elements.length; i++) {
            const el = elements[i];
            if (!el) continue;
            const attrSrc = typeof el.getAttribute === 'function' ? el.getAttribute('src') : '';
            const rawSrc = el.currentSrc || el.src || attrSrc || '';
            const canonical = canonicalizeUrl(rawSrc, origin);
            if (canonical) {
                urls.add(canonical);
            }
        }
    };

    addFromElements(documentRef.querySelectorAll('img'));
    addFromElements(documentRef.querySelectorAll('video'));
    return urls;
}

/**
 * Checks whether a dropped URL/text candidate points to an internal media/sticker/avatar/dev-media
 * endpoint, same-origin blob URL, or a currently rendered signed media URL.
 * @param {string} rawCandidate
 * @param {{ origin?: string, renderedMediaUrls?: Set<string>|string[]|null }} [options]
 * @returns {boolean}
 */
export function isInternalMediaUrl(rawCandidate, { origin = '', renderedMediaUrls = null } = {}) {
    if (typeof rawCandidate !== 'string') return false;
    const trimmed = rawCandidate.trim();
    if (!looksLikeUrlCandidate(trimmed)) return false;

    const normalizedOrigin = typeof origin === 'string' ? origin.trim() : '';
    const isSingleSlashRelative = trimmed.startsWith('/') && !trimmed.startsWith('//');

    let parsed = null;
    try {
        if (normalizedOrigin) {
            parsed = new URL(trimmed, normalizedOrigin);
        } else if (isSingleSlashRelative) {
            parsed = new URL(trimmed, 'https://local.invalid');
        } else {
            parsed = new URL(trimmed);
        }
    } catch {
        return false;
    }

    if (parsed.protocol === 'blob:') {
        if (!normalizedOrigin) return false;
        if (parsed.origin === normalizedOrigin) return true;
        return trimmed.toLowerCase().startsWith(`blob:${normalizedOrigin.toLowerCase()}/`);
    }

    if (renderedMediaUrls) {
        if (typeof renderedMediaUrls.has === 'function' && renderedMediaUrls.has(parsed.href)) {
            return true;
        }
        if (Array.isArray(renderedMediaUrls) && renderedMediaUrls.includes(parsed.href)) {
            return true;
        }
    }

    const matchesSameOrigin = normalizedOrigin ? parsed.origin === normalizedOrigin : isSingleSlashRelative;
    if (matchesSameOrigin) {
        for (let i = 0; i < INTERNAL_MEDIA_PATH_PATTERNS.length; i++) {
            if (INTERNAL_MEDIA_PATH_PATTERNS[i].test(parsed.pathname)) {
                return true;
            }
        }
    }

    return false;
}

/**
 * Attaches delegated dragstart and drop guards to the given document.
 * @param {{ documentRef?: Document|null, getOrigin?: Function|null }} [options]
 * @returns {{ destroy: () => void }}
 */
export function setupMediaDragGuard({
    documentRef = typeof document !== 'undefined' ? document : null,
    getOrigin = null
} = {}) {
    if (!documentRef || typeof documentRef.addEventListener !== 'function') {
        return { destroy: () => {} };
    }

    const resolveOrigin = () => {
        if (typeof getOrigin === 'function') {
            return String(getOrigin() || '');
        }
        if (typeof window !== 'undefined' && window.location && typeof window.location.origin === 'string') {
            return window.location.origin;
        }
        return '';
    };

    const onDragStart = (e) => {
        const targetEl = normalizeEventElement(e?.target);
        if (!targetEl) return;
        if (matchesAnySingleSelector(targetEl, MEDIA_DRAG_SELECTORS)) {
            if (typeof e.preventDefault === 'function') {
                e.preventDefault();
            }
        }
    };

    const onDrop = (e) => {
        const targetEl = normalizeEventElement(e?.target);
        if (!isTextInputTarget(targetEl)) return;

        const dt = e?.dataTransfer;
        if (!dt) return;

        // Always allow real OS file drops to pass through to media-uploader / sticker-creator
        if (dt.files && dt.files.length > 0) {
            return;
        }

        const candidates = extractDroppedUrlCandidates(dt);
        if (candidates.length === 0) return;

        let hasUrlLikeCandidate = false;
        for (let i = 0; i < candidates.length; i++) {
            if (looksLikeUrlCandidate(candidates[i])) {
                hasUrlLikeCandidate = true;
                break;
            }
        }
        if (!hasUrlLikeCandidate) return;

        const origin = resolveOrigin();
        const renderedMediaUrls = collectRenderedMediaUrls(documentRef, origin);

        for (let i = 0; i < candidates.length; i++) {
            if (isInternalMediaUrl(candidates[i], { origin, renderedMediaUrls })) {
                if (typeof e.preventDefault === 'function') {
                    e.preventDefault();
                }
                return;
            }
        }
    };

    documentRef.addEventListener('dragstart', onDragStart);
    documentRef.addEventListener('drop', onDrop);

    let destroyed = false;
    return {
        destroy() {
            if (destroyed) return;
            destroyed = true;
            if (typeof documentRef.removeEventListener === 'function') {
                documentRef.removeEventListener('dragstart', onDragStart);
                documentRef.removeEventListener('drop', onDrop);
            }
        }
    };
}
