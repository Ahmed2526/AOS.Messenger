/**
 * AOS Messenger - Authenticated Fetch API Client
 *
 * Centralizes cookie-authenticated JSON requests with cache: 'no-store',
 * CSRF RequestVerificationToken headers on mutations, distinguishable CSRF 400 recovery,
 * 204 No Content support, and safe 401/login-redirect session-expiry detection.
 */

export const ANTIFORGERY_PROBLEM_TYPE = 'https://aos.messenger/errors/antiforgery';

export class ApiError extends Error {
    /**
     * @param {string} message
     * @param {{ status?: number, code?: string, problem?: object|null }} [details]
     */
    constructor(message, { status = 0, code = 'HTTP_ERROR', problem = null } = {}) {
        super(message);
        this.name = 'ApiError';
        this.status = status;
        this.code = code;
        this.problem = problem;
    }
}

let config = {
    onSessionExpired: null,
    onCsrfMismatch: null,
    getCsrfToken: null,
    fetchImpl: null
};

/**
 * Configures global callbacks or test doubles for the API client.
 * @param {object} [options]
 */
export function configureApiClient(options = {}) {
    if ('onSessionExpired' in options) {
        config.onSessionExpired = options.onSessionExpired;
    }
    if ('onCsrfMismatch' in options) {
        config.onCsrfMismatch = options.onCsrfMismatch;
    }
    if ('getCsrfToken' in options) {
        config.getCsrfToken = options.getCsrfToken;
    }
    if ('fetchImpl' in options) {
        config.fetchImpl = options.fetchImpl;
    }
}

/**
 * Reads the antiforgery token emitted by @Html.AntiForgeryToken().
 * @returns {string}
 */
export function getVerificationToken() {
    if (typeof config.getCsrfToken === 'function') {
        return config.getCsrfToken() || '';
    }
    if (typeof document === 'undefined' || typeof document.querySelector !== 'function') {
        return '';
    }
    const input = document.querySelector('input[name="__RequestVerificationToken"]');
    return input ? (input.value || '') : '';
}

function isJsonContentType(contentType) {
    if (!contentType) return false;
    const normalized = String(contentType).toLowerCase();
    return normalized.includes('application/json') ||
           normalized.includes('application/problem+json') ||
           normalized.includes('+json');
}

function isMutatingMethod(method) {
    return method === 'POST' || method === 'PUT' || method === 'DELETE' || method === 'PATCH';
}

function isPlainJsonBody(body) {
    if (body === null || body === undefined) return false;
    if (typeof body === 'string') return false;
    if (typeof FormData !== 'undefined' && body instanceof FormData) return false;
    if (typeof Blob !== 'undefined' && body instanceof Blob) return false;
    if (typeof ArrayBuffer !== 'undefined' && body instanceof ArrayBuffer) return false;
    if (typeof URLSearchParams !== 'undefined' && body instanceof URLSearchParams) return false;
    return typeof body === 'object';
}

function mapStatusToErrorCode(status) {
    switch (status) {
        case 401: return 'UNAUTHORIZED';
        case 403: return 'FORBIDDEN';
        case 404: return 'NOT_FOUND';
        case 409: return 'CONFLICT';
        case 429: return 'RATE_LIMITED';
        default: return 'HTTP_ERROR';
    }
}

/**
 * Performs an authenticated, no-store same-origin JSON API request.
 * @param {string} url
 * @param {RequestInit & { body?: any }} [options]
 * @returns {Promise<any>}
 */
export async function apiFetch(url, options = {}) {
    const fetchFn = config.fetchImpl || (typeof fetch === 'function' ? fetch : null);
    if (!fetchFn) {
        throw new ApiError('Fetch API is unavailable.', { status: 0, code: 'FETCH_UNAVAILABLE' });
    }

    const method = (options.method || 'GET').toUpperCase();
    const headers = {
        'Accept': 'application/json',
        ...(options.headers || {})
    };

    let body = options.body;
    if (isPlainJsonBody(body)) {
        if (!headers['Content-Type'] && !headers['content-type']) {
            headers['Content-Type'] = 'application/json';
        }
        body = JSON.stringify(body);
    }

    if (isMutatingMethod(method)) {
        const token = getVerificationToken();
        if (token && !headers['RequestVerificationToken']) {
            headers['RequestVerificationToken'] = token;
        }
    }

    const fetchOptions = {
        ...options,
        method,
        credentials: 'same-origin',
        cache: 'no-store',
        headers
    };

    if (body !== undefined) {
        fetchOptions.body = body;
    }

    const resp = await fetchFn(url, fetchOptions);
    const contentType = resp.headers && typeof resp.headers.get === 'function'
        ? (resp.headers.get('content-type') || '')
        : '';

    const redirectedToLogin = !!(resp.redirected && resp.url && String(resp.url).toLowerCase().includes('/account/login'));
    if (resp.status === 401 || redirectedToLogin) {
        if (typeof config.onSessionExpired === 'function') {
            config.onSessionExpired();
        }
        throw new ApiError('Session expired.', { status: resp.status || 401, code: 'UNAUTHORIZED' });
    }

    if (resp.status === 204) {
        return null;
    }

    let rawText = '';
    if (typeof resp.text === 'function') {
        rawText = await resp.text();
    }

    let parsed = null;
    if (rawText && isJsonContentType(contentType)) {
        try {
            parsed = JSON.parse(rawText);
        } catch {
            parsed = null;
        }
    } else if (!rawText && !contentType && typeof resp.json === 'function' && resp.ok) {
        // Support minimal test Response mocks that only implement json() without text()/headers
        try {
            parsed = await resp.json();
            return parsed;
        } catch {
            parsed = null;
        }
    }

    if (resp.status === 400) {
        const isCsrfMismatch = (parsed && parsed.type === ANTIFORGERY_PROBLEM_TYPE) ||
            (isMutatingMethod(method) && !isJsonContentType(contentType) && !rawText.trim());

        if (isCsrfMismatch) {
            if (typeof config.onCsrfMismatch === 'function') {
                config.onCsrfMismatch();
            }
            throw new ApiError(
                (parsed && (parsed.detail || parsed.title)) || 'Request verification failed. Please reload.',
                { status: 400, code: 'CSRF_MISMATCH', problem: parsed }
            );
        }

        throw new ApiError(
            (parsed && (parsed.detail || parsed.title || parsed.message)) || 'Invalid request.',
            { status: 400, code: 'BAD_REQUEST', problem: parsed }
        );
    }

    if (!resp.ok) {
        const code = mapStatusToErrorCode(resp.status);
        const message = (parsed && (parsed.detail || parsed.title || parsed.message)) ||
            `Request failed (${resp.status}).`;
        throw new ApiError(message, { status: resp.status, code, problem: parsed });
    }

    if (!isJsonContentType(contentType)) {
        throw new ApiError('Unexpected non-JSON response.', { status: resp.status, code: 'INVALID_RESPONSE' });
    }

    return parsed;
}

export const DEFAULT_MESSAGE_PAGE_SIZE = 20;

/**
 * Fetches initial metadata, policy flags, and ascending messages for a conversation in one request.
 */
export function getConversationBundle(conversationId, { pageSize = DEFAULT_MESSAGE_PAGE_SIZE, signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/bundle?pageSize=${encodeURIComponent(pageSize)}`;
    return apiFetch(url, { method: 'GET', signal });
}

/**
 * Fetches a descending keyset page of messages for scroll-up pagination or reconciliation.
 */
export function getMessagesPage(conversationId, { cursor = null, pageSize = DEFAULT_MESSAGE_PAGE_SIZE, signal } = {}) {
    const params = new URLSearchParams();
    params.set('pageSize', String(pageSize));
    if (cursor) {
        params.set('cursor', cursor);
    }
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/messages?${params.toString()}`;
    return apiFetch(url, { method: 'GET', signal });
}

/**
 * Fetches the authoritative conversation list summaries for the sidebar.
 */
export function getConversations({ includeArchived = false, signal } = {}) {
    const url = `/api/conversations?includeArchived=${includeArchived ? 'true' : 'false'}`;
    return apiFetch(url, { method: 'GET', signal });
}

/**
 * Sends a message via HTTP POST with a client-generated IdempotencyKey.
 */
export function sendMessage(conversationId, payload, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/messages`;
    return apiFetch(url, { method: 'POST', body: payload, signal });
}

/**
 * Edits an existing message via HTTP PUT.
 */
export function editMessage(conversationId, messageId, content, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/messages/${encodeURIComponent(messageId)}`;
    return apiFetch(url, { method: 'PUT', body: { content }, signal });
}

/**
 * Soft-deletes a message for the current caller only (returns null on 204 No Content).
 */
export function deleteForMe(conversationId, messageId, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/messages/${encodeURIComponent(messageId)}/delete-for-me`;
    return apiFetch(url, { method: 'POST', signal });
}

/**
 * Tombstones a message for everyone within the server-enforced window.
 */
export function deleteForEveryone(conversationId, messageId, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/messages/${encodeURIComponent(messageId)}/delete-for-everyone`;
    return apiFetch(url, { method: 'POST', signal });
}

/**
 * Toggles a reaction emoji on a message.
 */
export function toggleReaction(conversationId, messageId, emoji, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/messages/${encodeURIComponent(messageId)}/reactions`;
    return apiFetch(url, { method: 'POST', body: { emoji }, signal });
}

/**
 * Removes the caller's reaction from a message.
 */
export function removeReaction(conversationId, messageId, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/messages/${encodeURIComponent(messageId)}/reactions`;
    return apiFetch(url, { method: 'DELETE', signal });
}

/**
 * Marks messages in a conversation as delivered up to upToMessageId.
 */
export function markDelivered(conversationId, upToMessageId, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/delivered`;
    return apiFetch(url, { method: 'POST', body: { upToMessageId: Number(upToMessageId) }, signal });
}

/**
 * Marks messages in a conversation as read up to upToMessageId.
 */
export function markRead(conversationId, upToMessageId, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/read`;
    return apiFetch(url, { method: 'POST', body: { upToMessageId: Number(upToMessageId) }, signal });
}

/**
 * Fetches online presence and last-seen state for the caller's conversation peers.
 */
export function getPresence({ signal } = {}) {
    return apiFetch('/api/presence', { method: 'GET', signal });
}

/**
 * Initiates a presigned upload for a pending media attachment.
 */
export function initiateMediaUpload(conversationId, payload, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/media/initiate`;
    return apiFetch(url, { method: 'POST', body: payload, signal });
}

/**
 * Verifies, sanitizes, and promotes a staged upload to permanent media storage.
 */
export function completeMediaUpload(conversationId, mediaId, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/media/${encodeURIComponent(mediaId)}/complete`;
    return apiFetch(url, { method: 'POST', signal });
}

/**
 * Cancels a pending or unattached uploaded media item and triggers cleanup.
 */
export function cancelMediaUpload(conversationId, mediaId, { signal } = {}) {
    const url = `/api/conversations/${encodeURIComponent(conversationId)}/media/${encodeURIComponent(mediaId)}`;
    return apiFetch(url, { method: 'DELETE', signal });
}

/**
 * Resolves a short-lived signed download URL JSON payload for a message attachment.
 */
export function getMediaDownloadUrl(conversationId, messageId, mediaId, { download = false, variant = null, signal } = {}) {
    const base = `/api/conversations/${encodeURIComponent(conversationId)}/messages/${encodeURIComponent(messageId)}/media/${encodeURIComponent(mediaId)}/url`;
    const params = new URLSearchParams();
    if (download) params.set('download', 'true');
    if (variant) params.set('variant', String(variant));
    const qs = params.toString();
    const url = qs ? `${base}?${qs}` : base;
    return apiFetch(url, { method: 'GET', signal });
}

/**
 * Builds the cookie-authenticated inline/download redirect endpoint URL for images.
 */
export function buildMediaInlineUrl(conversationId, messageId, mediaId, { download = false, variant = null } = {}) {
    const base = `/api/conversations/${encodeURIComponent(conversationId)}/messages/${encodeURIComponent(messageId)}/media/${encodeURIComponent(mediaId)}`;
    const params = new URLSearchParams();
    if (download) params.set('download', 'true');
    if (variant) params.set('variant', String(variant));
    const qs = params.toString();
    return qs ? `${base}?${qs}` : base;
}

/**
 * Initiates a presigned profile photo upload in private R2 storage.
 */
export function initiateAvatarUpload(payload, { signal } = {}) {
    return apiFetch('/api/profile/avatar/initiate', { method: 'POST', body: payload, signal });
}

/**
 * Completes, verifies, and promotes a staged profile photo upload (1024px full + 128px thumb).
 */
export function completeAvatarUpload(uploadId, { signal } = {}) {
    const url = `/api/profile/avatar/${encodeURIComponent(uploadId)}/complete`;
    return apiFetch(url, { method: 'POST', signal });
}

export function getAvatarUploadStatus(uploadId, { signal } = {}) {
    const url = `/api/profile/avatar/${encodeURIComponent(uploadId)}/status`;
    return apiFetch(url, { signal });
}

/**
 * Removes the current user's profile photo from R2 storage and SQL Server.
 */
export function removeAvatar({ signal } = {}) {
    return apiFetch('/api/profile/avatar', { method: 'DELETE', signal });
}

/**
 * Resolves a short-lived signed GET URL JSON payload for a user's avatar ('thumb' or 'full').
 */
export function getUserAvatarUrl(userId, { size = 'thumb', signal } = {}) {
    const url = `/api/users/${encodeURIComponent(userId)}/avatar/url?size=${encodeURIComponent(size || 'thumb')}`;
    return apiFetch(url, { method: 'GET', signal });
}

/**
 * Builds the cookie-authenticated avatar redirect endpoint URL ('thumb' or 'full').
 */
export function buildUserAvatarRedirectUrl(userId, { size = 'thumb', version = null } = {}) {
    const base = `/api/users/${encodeURIComponent(userId)}/avatar?size=${encodeURIComponent(size || 'thumb')}`;
    return version ? `${base}&v=${encodeURIComponent(version)}` : base;
}

/**
 * Fetches the authenticated caller's sticker catalog (recents, favorites, built-in packs, custom packs).
 */
export function getStickerCatalog({ signal } = {}) {
    return apiFetch('/api/stickers/catalog', { method: 'GET', signal });
}

/**
 * Creates a new custom sticker pack owned by the current user.
 */
export function createStickerPack(name, { signal } = {}) {
    return apiFetch('/api/stickers/packs', { method: 'POST', body: { name }, signal });
}

/**
 * Renames a custom sticker pack owned by the current user.
 */
export function renameStickerPack(packId, name, { signal } = {}) {
    const url = `/api/stickers/packs/${encodeURIComponent(packId)}`;
    return apiFetch(url, { method: 'PUT', body: { name }, signal });
}

/**
 * Soft-deletes a custom sticker pack and archives its stickers safely.
 */
export function deleteStickerPack(packId, { signal } = {}) {
    const url = `/api/stickers/packs/${encodeURIComponent(packId)}`;
    return apiFetch(url, { method: 'DELETE', signal });
}

/**
 * Initiates a presigned R2 staging upload for a custom static or animated sticker.
 */
export function initiateStickerUpload(payload, { signal } = {}) {
    return apiFetch('/api/stickers/uploads/initiate', { method: 'POST', body: payload, signal });
}

/**
 * Verifies, normalizes (512x512 WebP), and promotes a staged custom sticker upload.
 */
export function completeStickerUpload(stickerId, { signal } = {}) {
    const url = `/api/stickers/uploads/${encodeURIComponent(stickerId)}/complete`;
    return apiFetch(url, { method: 'POST', signal });
}

/**
 * Cancels an incomplete custom sticker staging upload.
 */
export function cancelStickerUpload(stickerId, { signal } = {}) {
    const url = `/api/stickers/uploads/${encodeURIComponent(stickerId)}`;
    return apiFetch(url, { method: 'DELETE', signal });
}

/**
 * Archives/removes a ready custom sticker from the owner's pack without breaking sent messages.
 */
export function archiveCustomSticker(stickerId, { signal } = {}) {
    const url = `/api/stickers/${encodeURIComponent(stickerId)}`;
    return apiFetch(url, { method: 'DELETE', signal });
}

/**
 * Stars a built-in, custom, or chat-received sticker into the caller's Favorites.
 */
export function addFavoriteSticker(payload, { signal } = {}) {
    return apiFetch('/api/stickers/favorites', { method: 'POST', body: payload, signal });
}

/**
 * Removes a sticker from the caller's Favorites.
 */
export function removeFavoriteSticker(stickerId, { signal } = {}) {
    const url = `/api/stickers/favorites/${encodeURIComponent(stickerId)}`;
    return apiFetch(url, { method: 'DELETE', signal });
}

/**
 * Resolves a short-lived signed GET URL JSON payload for a sticker.
 */
export function getStickerImageUrl(stickerId, { signal } = {}) {
    const url = `/api/stickers/${encodeURIComponent(stickerId)}/url`;
    return apiFetch(url, { method: 'GET', signal });
}

/**
 * Builds the cookie-authenticated redirect endpoint URL for a sticker image.
 */
export function buildStickerImageRedirectUrl(stickerId) {
    return `/api/stickers/${encodeURIComponent(stickerId)}/image`;
}
