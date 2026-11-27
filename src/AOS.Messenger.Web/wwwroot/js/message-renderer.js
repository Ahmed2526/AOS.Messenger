/**
 * AOS Messenger - Message Renderer Module
 *
 * Implements safe DOM construction for message bubbles and conversation stream items.
 *
 * Invariants:
 * - Safe DOM tree construction using document.createElement exclusively.
 * - Zero HTML string injection into DOM.
 * - All DOM elements built with document.createElement().
 * - All user text assigned strictly via .textContent or createTextNode().
 * - Strict escaping and deduplication.
 * - Outgoing message checkmark status rendering (sent -> delivered -> read).
 * - Phase 8: Message actions (React, Reply, Edit, Delete) and reaction pills.
 */

import { renderReactionPills } from './reactions.js';
import { formatLastSeen } from './presence.js';
import { closeChatActionsMenu } from './chat-actions.js';
import { updateSidebarLastMessage as updateSidebarLastMessageInList } from './conversation-list.js';
import { getMessageContentClassName, truncateGraphemes } from './emoji-utils.js';
import { buildMediaInlineUrl, getMediaDownloadUrl } from './api-client.js';
import { isSafeMediaUrl, triggerSafeDownloadAnchor } from './media-lightbox.js';
import { setBoundedMapEntry } from './memory-image-cache.js';
import {
    hydrateAvatarSlot,
    evictUserAvatarCache,
    closeAvatarLightboxIfUser,
    clearAvatarUrlCache
} from './avatar-loader.js';
import {
    clearStickerUrlCache,
    applyInitialStickerSrc,
    hydrateStickerImages
} from './sticker-picker.js';
import {
    createVoiceBubbleElement,
    formatVoiceDuration,
    teardownRowAudio
} from './voice-player.js';

const MEDIA_URL_CACHE_TTL_MS = 8 * 60 * 1000;
const MEDIA_URL_SAFETY_MARGIN_MS = 60 * 1000;
const VIDEO_RETRY_COOLDOWN_MS = 15 * 1000;
const mediaUrlCache = new Map();
const inFlightMediaUrls = new Map();
const mediaUrlMessageEpochs = new Map();
let mediaUrlCacheEpoch = 0;
const activeMediaObservers = new Set();
const videoLoadHandlers = new WeakMap();
let sharedVideoObserver = null;
let voicePlayerCoordinator = null;

export function setVoicePlayerCoordinator(coordinator) {
    voicePlayerCoordinator = coordinator || null;
}

export function getVoicePlayerCoordinator() {
    return voicePlayerCoordinator;
}

export function disconnectActiveMediaObservers() {
    for (const obs of activeMediaObservers) {
        try {
            if (obs && typeof obs.disconnect === 'function') {
                obs.disconnect();
            }
        } catch {}
    }
    activeMediaObservers.clear();
}

/**
 * Clears all cached signed media download URLs (called on logout / session expiry).
 */
export function clearMediaUrlCache() {
    mediaUrlCacheEpoch++;
    mediaUrlMessageEpochs.clear();
    mediaUrlCache.clear();
    inFlightMediaUrls.clear();
}

/**
 * Evicts cached signed media download URLs for a specific message ID (called when a message is deleted).
 * @param {string|number|null|undefined} msgId
 */
export function evictMediaUrlCacheForMessage(msgId) {
    if (msgId === null || msgId === undefined || msgId === '') return;
    const target = String(msgId);
    mediaUrlMessageEpochs.set(target, (mediaUrlMessageEpochs.get(target) || 0) + 1);
    for (const key of Array.from(mediaUrlCache.keys())) {
        const parts = String(key).split(':');
        if (parts[1] === target) {
            mediaUrlCache.delete(key);
        }
    }
    for (const key of Array.from(inFlightMediaUrls.keys())) {
        const parts = String(key).split(':');
        if (parts[1] === target) {
            inFlightMediaUrls.delete(key);
        }
    }
}

export function teardownRowVideos(rootEl) {
    if (!rootEl || typeof rootEl.querySelectorAll !== 'function') return;
    const videos = rootEl.querySelectorAll('video');
    for (const video of videos) {
        if (!video) continue;
        try {
            if (typeof video.pause === 'function') video.pause();
            if (typeof video.removeAttribute === 'function') video.removeAttribute('src');
            if (typeof video.load === 'function') video.load();
        } catch {}
    }
    teardownRowAudio(rootEl);
}

function hasVisibleMessageText(value) {
    if (value === null || value === undefined) return false;
    const stripped = String(value).replace(/[\s\p{Cf}\p{Cc}\p{Zs}\p{Zl}\p{Zp}]/gu, '');
    return stripped.length > 0;
}

function formatAttachmentBytes(bytes) {
    const n = Number(bytes) || 0;
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1).replace(/\.0$/, '')} KB`;
    if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1).replace(/\.0$/, '')} MB`;
    return `${(n / (1024 * 1024 * 1024)).toFixed(2).replace(/\.00$/, '')} GB`;
}

/**
 * Builds human-friendly preview text for text, media, and captioned media messages.
 * Mirrors server-side MediaPreviewFormatter.FormatPreview.
 * @param {object|null|undefined} message
 * @param {number} [maxGraphemes]
 * @returns {string}
 */
export function formatMessagePreview(message, maxGraphemes = 100) {
    if (!message || typeof message !== 'object') return '';
    if (message.isDeletedForEveryone ?? message.IsDeletedForEveryone) {
        return 'This message was deleted';
    }

    const rawSticker = message.sticker ?? message.Sticker ?? null;
    if (rawSticker && (rawSticker.id ?? rawSticker.Id)) {
        return '🏷️ Sticker';
    }

    const rawContent = message.content !== undefined ? message.content : message.Content;
    const normalizedCaption = hasVisibleMessageText(rawContent) ? String(rawContent).trim() : null;
    const rawAttachments = message.attachments ?? message.Attachments ?? [];
    if (!Array.isArray(rawAttachments) || rawAttachments.length === 0) {
        return normalizedCaption ? truncateGraphemes(normalizedCaption, maxGraphemes) : '';
    }

    const kinds = rawAttachments.map(a => String(a?.kind ?? a?.Kind ?? 'document').toLowerCase());
    const firstKind = kinds[0];
    const allSame = kinds.every(k => k === firstKind);

    const iconFor = (k) => {
        if (k === 'image') return '📷';
        if (k === 'video') return '🎬';
        if (k === 'voice') return '🎤';
        return '📎';
    };

    const icon = allSame ? iconFor(firstKind) : '📎';
    if (normalizedCaption) {
        return truncateGraphemes(`${icon} ${normalizedCaption}`, maxGraphemes);
    }

    if (rawAttachments.length === 1) {
        if (firstKind === 'image') return '📷 Photo';
        if (firstKind === 'video') return '🎬 Video';
        if (firstKind === 'voice') {
            const durMs = rawAttachments[0]?.durationMs ?? rawAttachments[0]?.DurationMs ?? null;
            return durMs && Number(durMs) > 0
                ? `🎤 Voice message (${formatVoiceDuration(durMs)})`
                : '🎤 Voice message';
        }
        const fileName = String(rawAttachments[0]?.fileName ?? rawAttachments[0]?.FileName ?? '').trim() || 'Attachment';
        return truncateGraphemes(`📎 ${fileName}`, maxGraphemes);
    }

    if (allSame) {
        if (firstKind === 'image') return `📷 ${rawAttachments.length} photos`;
        if (firstKind === 'video') return `🎬 ${rawAttachments.length} videos`;
        if (firstKind === 'voice') return `🎤 ${rawAttachments.length} voice messages`;
        return `📎 ${rawAttachments.length} files`;
    }

    return `📎 ${rawAttachments.length} attachments`;
}

/**
 * Resolves and caches a signed download URL JSON payload (~8-min in-memory TTL).
 */
export async function resolveCachedMediaUrl(
    conversationId,
    messageId,
    mediaId,
    { download = false, variant = null, bypassCache = false, getMediaUrlFn = getMediaDownloadUrl, nowFn = () => Date.now() } = {}
) {
    if (!conversationId || !messageId || !mediaId) return null;

    const variantKey = variant ? String(variant).toLowerCase() : 'default';
    const cacheKey = `${conversationId}:${messageId}:${mediaId}:${download ? 'dl' : 'inline'}:${variantKey}`;
    const now = nowFn();
    if (!bypassCache && mediaUrlCache.has(cacheKey)) {
        const entry = mediaUrlCache.get(cacheKey);
        if (entry && entry.expiresAtMs > now && isSafeMediaUrl(entry.url)) {
            return entry.url;
        }
        mediaUrlCache.delete(cacheKey);
    }

    if (!bypassCache && inFlightMediaUrls.has(cacheKey)) {
        return inFlightMediaUrls.get(cacheKey);
    }

    const msgKey = String(messageId);
    const startGlobalEpoch = mediaUrlCacheEpoch;
    const startMsgEpoch = mediaUrlMessageEpochs.get(msgKey) || 0;

    const promise = (async () => {
        try {
            const dto = await getMediaUrlFn(conversationId, messageId, mediaId, { download, variant });
            if (mediaUrlCacheEpoch !== startGlobalEpoch || (mediaUrlMessageEpochs.get(msgKey) || 0) !== startMsgEpoch) {
                return null;
            }
            const url = dto?.url ?? dto?.Url ?? '';
            if (!isSafeMediaUrl(url)) {
                return null;
            }

            const currentNow = nowFn();
            const rawExpiry = dto?.expiresAtUtc ?? dto?.ExpiresAtUtc ?? null;
            const parsedExpiryMs = rawExpiry ? Date.parse(rawExpiry) : NaN;
            const defaultExpiryMs = currentNow + MEDIA_URL_CACHE_TTL_MS;
            const expiresAtMs = Number.isFinite(parsedExpiryMs)
                ? Math.min(parsedExpiryMs - MEDIA_URL_SAFETY_MARGIN_MS, defaultExpiryMs)
                : defaultExpiryMs;

            if (expiresAtMs > currentNow) {
                setBoundedMapEntry(mediaUrlCache, cacheKey, {
                    url,
                    expiresAtMs
                });
            }
            return url;
        } finally {
            if (inFlightMediaUrls.get(cacheKey) === promise) {
                inFlightMediaUrls.delete(cacheKey);
            }
        }
    })();

    if (!bypassCache) {
        inFlightMediaUrls.set(cacheKey, promise);
    }
    return promise;
}

/**
 * Hydrates rendered <video> elements (lazy /url loading + recoverable error retry) and document download buttons.
 * @param {HTMLElement|Document|null} rootEl
 * @param {{ getMediaUrlFn?: Function, nowFn?: Function }} [options]
 */
export function hydrateMediaElements(rootEl, { getMediaUrlFn = getMediaDownloadUrl, nowFn = () => Date.now() } = {}) {
    if (!rootEl || typeof rootEl.querySelectorAll !== 'function') return;

    const videos = rootEl.querySelectorAll('video[data-media-video="true"]');
    for (const video of videos) {
        if (!video || video.getAttribute('data-video-bound') === 'true') continue;
        video.setAttribute('data-video-bound', 'true');

        const convId = video.getAttribute('data-conversation-id') || '';
        const msgId = video.getAttribute('data-message-id') || '';
        const mediaId = video.getAttribute('data-media-id') || '';
        if (!convId || !msgId || !mediaId) continue;

        const loadVideoSource = async (bypassCache = false) => {
            try {
                const url = await resolveCachedMediaUrl(convId, msgId, mediaId, {
                    download: false,
                    bypassCache,
                    getMediaUrlFn,
                    nowFn
                });
                if (video.isConnected === false) {
                    return false;
                }
                if (url) {
                    video.src = url;
                    return true;
                }
            } catch {}
            return false;
        };

        const showFallback = () => {
            video.classList.add('hidden');
            const wrapper = typeof video.closest === 'function' ? video.closest('[data-video-container="true"]') : null;
            const fallback = wrapper ? wrapper.querySelector('.media-video-fallback') : null;
            if (fallback) fallback.classList.remove('hidden');
        };

        if (typeof video.addEventListener === 'function') {
            const resetRetry = () => {
                if (typeof video.removeAttribute === 'function') {
                    video.removeAttribute('data-video-retried');
                }
            };
            video.addEventListener('loadeddata', resetRetry);
            video.addEventListener('playing', resetRetry);

            video.addEventListener('error', async () => {
                const hasSrc = (typeof video.hasAttribute === 'function' && video.hasAttribute('src'))
                    ? Boolean(video.getAttribute('src'))
                    : Boolean(video.src);
                if (!hasSrc) {
                    return;
                }
                const now = nowFn();
                const lastRetryMs = Number(video.getAttribute('data-video-last-retry-ms') || '0');
                if (video.getAttribute('data-video-retried') === 'true' || (lastRetryMs > 0 && now - lastRetryMs < VIDEO_RETRY_COOLDOWN_MS)) {
                    showFallback();
                    return;
                }
                video.setAttribute('data-video-retried', 'true');
                video.setAttribute('data-video-last-retry-ms', String(now));
                const savedTime = Number(video.currentTime) || 0;
                const ok = await loadVideoSource(true);
                if (video.isConnected === false) {
                    return;
                }
                if (!ok) {
                    showFallback();
                    return;
                }
                if (savedTime > 0) {
                    video.addEventListener('loadedmetadata', () => {
                        try {
                            video.currentTime = savedTime;
                        } catch {}
                    }, { once: true });
                }
            });
        }

        if (typeof IntersectionObserver === 'function') {
            if (!sharedVideoObserver || !activeMediaObservers.has(sharedVideoObserver)) {
                sharedVideoObserver = new IntersectionObserver((entries) => {
                    for (let j = 0; j < entries.length; j++) {
                        const entry = entries[j];
                        if (!entry || !entry.isIntersecting) continue;
                        const target = entry.target;
                        if (sharedVideoObserver && typeof sharedVideoObserver.unobserve === 'function' && target) {
                            sharedVideoObserver.unobserve(target);
                        }
                        if (target) {
                            const loadFn = videoLoadHandlers.get(target);
                            if (typeof loadFn === 'function') {
                                videoLoadHandlers.delete(target);
                                loadFn(false);
                            }
                        }
                    }
                });
                activeMediaObservers.add(sharedVideoObserver);
            }
            videoLoadHandlers.set(video, loadVideoSource);
            sharedVideoObserver.observe(video);
        } else {
            loadVideoSource(false);
        }
    }

    const docButtons = rootEl.querySelectorAll('button[data-media-download="true"]');
    for (const btn of docButtons) {
        if (!btn || btn.getAttribute('data-download-bound') === 'true') continue;
        btn.setAttribute('data-download-bound', 'true');

        if (typeof btn.addEventListener === 'function') {
            btn.addEventListener('click', async (e) => {
                if (e && typeof e.preventDefault === 'function') e.preventDefault();
                const convId = btn.getAttribute('data-conversation-id') || '';
                const msgId = btn.getAttribute('data-message-id') || '';
                const mediaId = btn.getAttribute('data-media-id') || '';
                const fileName = btn.getAttribute('data-file-name') || '';
                if (!convId || !msgId || !mediaId) return;

                try {
                    const url = await resolveCachedMediaUrl(convId, msgId, mediaId, {
                        download: true,
                        bypassCache: true,
                        getMediaUrlFn,
                        nowFn
                    });
                    if (url) {
                        triggerSafeDownloadAnchor(url, fileName);
                    }
                } catch {
                    // Refresh via /url when user retries
                }
            });
        }
    }

    if (voicePlayerCoordinator && typeof voicePlayerCoordinator.hydrateRoot === 'function') {
        voicePlayerCoordinator.hydrateRoot(rootEl);
    }
}

/**
 * Sanitizes user-supplied text to prevent Cross-Site Scripting (XSS).
 * @param {string} str - Raw input string
 * @returns {string} HTML-escaped string
 */
export function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

/**
 * Formats an ISO-8601 UTC timestamp to client local time (e.g., '10:42 AM').
 * @param {string} isoString - ISO date string
 * @returns {string} Formatted time string
 */
export function formatTime(isoString) {
    if (!isoString) return '';
    try {
        const d = new Date(isoString);
        return d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', hour12: true });
    } catch {
        return '';
    }
}

/**
 * Generates a 1- or 2-letter uppercase initials string from a name or handle.
 * @param {string} name
 * @param {string} handle
 * @returns {string} Initials
 */
export function getInitials(name, handle) {
    const raw = (name && name.trim()) || (handle && handle.trim()) || 'U';
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
 * Normalizes a message ID from string, GUID, or strongly-typed object.
 * @param {object|string} msg
 * @returns {string|null} Normalized ID string
 */
export function getMessageId(msg) {
    if (!msg) return null;
    if (typeof msg === 'string' || typeof msg === 'number' || typeof msg === 'bigint') return String(msg);
    if (msg.id && typeof msg.id === 'object' && msg.id.value !== undefined) return String(msg.id.value);
    if (msg.id !== undefined && msg.id !== null) return String(msg.id);
    if (msg.messageId !== undefined && msg.messageId !== null) return String(msg.messageId);
    return null;
}

/**
 * Normalizes a user ID from string, GUID, or strongly-typed object.
 * @param {object|string} u
 * @returns {string} Normalized user ID string
 */
export function getUserId(u) {
    if (!u) return '';
    if (typeof u === 'string') return u;
    if (typeof u === 'object' && u.value) return String(u.value);
    return String(u);
}

/**
 * Checks if a message ID is already rendered in the DOM.
 * @param {string|object} msgOrId
 * @returns {boolean} True if already rendered in message stream
 */
export function isMessageRendered(msgOrId) {
    const id = typeof msgOrId === 'object' ? getMessageId(msgOrId) : msgOrId;
    if (!id) return false;
    if (typeof document === 'undefined') return false;
    return !!(document.getElementById(`msg-${id}`) || document.querySelector(`[data-message-id="${id}"]`));
}

let bottomScrollHandler = null;

/**
 * Registers an optional scroll-to-bottom handler (e.g., bottom scroll pinner) owned by the chat shell.
 * Pass null to restore direct scrolling.
 * @param {((smooth: boolean) => void)|null} handler
 */
export function setBottomScrollHandler(handler) {
    bottomScrollHandler = typeof handler === 'function' ? handler : null;
}

/**
 * Scrolls the message stream smoothly to the bottom.
 * @param {boolean} smooth
 */
export function scrollToBottom(smooth = true) {
    if (bottomScrollHandler) {
        bottomScrollHandler(Boolean(smooth));
        return;
    }
    if (typeof document === 'undefined') return;
    const streamEl = document.getElementById('message-stream');
    if (streamEl) {
        if (smooth && typeof streamEl.scrollTo === 'function') {
            streamEl.scrollTo({ top: streamEl.scrollHeight, behavior: 'smooth' });
        } else {
            streamEl.scrollTop = streamEl.scrollHeight;
        }
    }
}

/**
 * Updates the last message snippet in the left conversation sidebar.
 * @param {string} convId
 * @param {string} previewText
 */
export function updateSidebarLastMessage(convId, previewText, messageId = null, onlyIfLatest = false, forceClearId = false, shouldReorder = false) {
    if (!convId || typeof document === 'undefined') return false;
    return updateSidebarLastMessageInList(convId, previewText, messageId, onlyIfLatest, forceClearId, shouldReorder);
}

/**
 * Checks whether msgId is currently the latest rendered row inside containerEl.
 * @param {HTMLElement|null} containerEl
 * @param {string|number} msgId
 * @returns {boolean}
 */
export function isLatestRenderedMessage(containerEl, msgId) {
    if (!containerEl || !msgId || typeof containerEl.querySelectorAll !== 'function') return false;
    const rows = containerEl.querySelectorAll('.message-bubble-row');
    if (!rows || rows.length === 0) return false;
    const lastRow = rows[rows.length - 1];
    return String(lastRow.getAttribute('data-message-id')) === String(msgId);
}

/**
 * Truncates a reply quote preview safely by grapheme clusters so complex emojis are never split.
 * @param {string} text
 * @param {number} [maxGraphemes]
 * @returns {string}
 */
export function truncateReplyPreview(text, maxGraphemes = 100) {
    return truncateGraphemes(text, maxGraphemes);
}

/**
 * Updates child reply quote banners referencing an edited parent message.
 * @param {string|number} parentMessageId
 * @param {string} newContent
 */
export function updateReplyParentBanners(parentMessageId, newContent) {
    if (!parentMessageId || typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;
    const preview = truncateReplyPreview(newContent);
    const quotes = document.querySelectorAll(`[data-reply-parent-id="${parentMessageId}"]`);
    quotes.forEach((quote) => {
        const previewEl = quote.querySelector('.reply-preview-text') ||
                          (quote.children && quote.children.length > 1 ? quote.children[1] : null);
        if (previewEl) {
            previewEl.textContent = preview;
        }
    });
}

/**
 * Replaces child reply quote banner text with a tombstoned placeholder when parent is deleted.
 * @param {string|number} parentMessageId
 */
export function scrubReplyParentBanners(parentMessageId) {
    if (!parentMessageId || typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;
    const quotes = document.querySelectorAll(`[data-reply-parent-id="${parentMessageId}"]`);
    quotes.forEach((quote) => {
        const previewEl = quote.querySelector('.reply-preview-text') ||
                          (quote.children && quote.children.length > 1 ? quote.children[1] : null);
        if (previewEl) {
            while (previewEl.firstChild) {
                previewEl.removeChild(previewEl.firstChild);
            }
            const deletedSpan = document.createElement('span');
            deletedSpan.className = 'italic';
            deletedSpan.textContent = 'This message was deleted';
            previewEl.appendChild(deletedSpan);
        }
    });
}

/**
 * Applies an authoritative edit update to a rendered message row and its child reply banners.
 * @param {string|number} msgId
 * @param {string|null} newContent
 * @param {string|null|undefined} editedAtUtc
 * @param {string|null} [previewText]
 */
export function applyMessageEditedToRow(msgId, newContent, editedAtUtc, previewText = null) {
    if (!msgId || typeof document === 'undefined') return;
    const normalized = newContent === null || newContent === undefined ? '' : String(newContent);
    const hasVisible = normalized.length > 0;
    const row = typeof document.getElementById === 'function' ? document.getElementById(`msg-${msgId}`) : null;
    const hasAttachments = row ? row.getAttribute('data-has-attachments') === 'true' : false;
    const resolvedPreview = previewText !== null && previewText !== undefined && previewText !== ''
        ? String(previewText)
        : (hasAttachments && row?.getAttribute('data-message-preview')
            ? row.getAttribute('data-message-preview')
            : normalized);

    if (row) {
        row.setAttribute('data-message-preview', resolvedPreview);
        const contentEl = row.querySelector('.message-text-content');
        if (contentEl && row.getAttribute('data-deleted') !== 'true') {
            contentEl.textContent = normalized;
            contentEl.className = `${getMessageContentClassName(normalized)}${hasVisible ? '' : ' hidden'}`;
        }
        const editedLabel = row.querySelector('.edited-label');
        if (editedLabel && editedAtUtc) {
            editedLabel.classList.remove('hidden');
        }
        const replyBtn = row.querySelector('.reply-action-btn');
        if (replyBtn) {
            replyBtn.setAttribute('data-preview', resolvedPreview);
        }
    }
    updateReplyParentBanners(msgId, resolvedPreview);
}

/**
 * Transforms a rendered message row into a tombstoned state and scrubs child reply banners.
 * @param {HTMLElement|null} row
 * @param {string|number} msgId
 */
export function applyMessageDeletedForEveryone(row, msgId) {
    if (row && typeof document !== 'undefined') {
        const wasSticker = row.getAttribute('data-is-sticker') === 'true';
        row.setAttribute('data-deleted', 'true');
        row.setAttribute('data-has-attachments', 'false');
        row.setAttribute('data-is-sticker', 'false');
        row.setAttribute('data-is-voice', 'false');
        row.setAttribute('data-message-preview', 'This message was deleted');

        if (typeof row.querySelectorAll === 'function') {
            teardownRowVideos(row);
            row.querySelectorAll('.message-media-container').forEach((mediaEl) => {
                if (typeof mediaEl.remove === 'function') mediaEl.remove();
            });
            row.querySelectorAll('.message-sticker-container').forEach((stickerEl) => {
                if (typeof stickerEl.remove === 'function') stickerEl.remove();
            });
        }

        const isOutgoing = row.getAttribute('data-is-outgoing') === 'true' || row.classList.contains('justify-end');
        if (wasSticker) {
            const bubbleEl = row.querySelector('.message-bubble');
            if (bubbleEl) {
                bubbleEl.className = isOutgoing
                    ? 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative'
                    : 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tl-xs px-4 py-2 bg-[#FFFFFF] dark:bg-[#3A3835] text-[#2B2725] dark:text-[#F0E9E2] border border-[#E3DCD7] dark:border-[#514D48] shadow-2xs relative';
            }
            const footerEl = row.querySelector('.message-meta-footer');
            if (footerEl) {
                footerEl.className = isOutgoing
                    ? 'message-meta-footer flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80'
                    : 'message-meta-footer flex items-center justify-end gap-1 mt-1 text-[10px] text-[#6E6560] dark:text-[#C9C0B9]';
            }
        }

        let contentEl = row.querySelector('.message-text-content');
        if (!contentEl) {
            const bubbleEl = row.querySelector('.message-bubble');
            if (bubbleEl) {
                contentEl = document.createElement('div');
                const footerEl = bubbleEl.querySelector('.message-meta-footer');
                if (footerEl && typeof bubbleEl.insertBefore === 'function') {
                    bubbleEl.insertBefore(contentEl, footerEl);
                } else {
                    bubbleEl.appendChild(contentEl);
                }
            }
        }
        if (contentEl) {
            while (contentEl.firstChild) {
                contentEl.removeChild(contentEl.firstChild);
            }
            contentEl.className = isOutgoing
                ? 'message-text-content italic text-xs text-white/80 flex items-center gap-1.5 py-0.5'
                : 'message-text-content italic text-xs text-[#6E6560] dark:text-[#C9C0B9] flex items-center gap-1.5 py-0.5';

            const blockIcon = document.createElement('span');
            blockIcon.className = 'material-symbols-outlined text-[15px]';
            blockIcon.textContent = 'block';
            contentEl.appendChild(blockIcon);

            const delText = document.createElement('span');
            delText.textContent = 'This message was deleted';
            contentEl.appendChild(delText);
        }

        for (const sel of ['.message-actions-bar', '[data-action="edit"]', '[data-action="delete"]', '[data-action="react"]', '.reply-action-btn', 'span[data-receipt-status]', '.reaction-pills-container', '.edited-label']) {
            const node = row.querySelector(sel);
            if (node && typeof node.remove === 'function') {
                node.remove();
            }
        }
    }

    if (msgId) {
        evictMediaUrlCacheForMessage(msgId);
        scrubReplyParentBanners(msgId);
    }
}

/**
 * Removes a caller-deleted message row and synchronizes sidebar preview safely.
 * @param {string|number} msgId
 * @param {string} convId
 * @param {string|number|null} newLastMessageId
 * @param {string|null} newLastMessagePreview
 * @param {boolean} isActiveConv
 */
export function applyMessageDeletedForMe(msgId, convId, newLastMessageId, newLastMessagePreview, isActiveConv, options = {}) {
    if (msgId) {
        evictMediaUrlCacheForMessage(msgId);
    }
    if (typeof document === 'undefined') return;
    if (isActiveConv && msgId) {
        const row = typeof document.getElementById === 'function' ? document.getElementById(`msg-${msgId}`) : null;
        if (row) {
            teardownRowVideos(row);
            if (typeof row.remove === 'function') {
                row.remove();
            }
        }

        if (typeof document.querySelectorAll === 'function') {
            document.querySelectorAll(`[data-reply-parent-id="${msgId}"]`).forEach((q) => {
                if (typeof q.remove === 'function') q.remove();
            });
        }

        const containerEl = typeof document.getElementById === 'function' ? document.getElementById('messages-container') : null;
        const emptyPlaceholderEl = typeof document.getElementById === 'function' ? document.getElementById('no-messages-placeholder') : null;
        const effectiveHasMore = typeof options?.hasMore === 'boolean'
            ? options.hasMore
            : Boolean(containerEl && typeof containerEl.getAttribute === 'function' && containerEl.getAttribute('data-has-more') === 'true');
        if (containerEl && emptyPlaceholderEl && !effectiveHasMore && containerEl.querySelectorAll('.message-bubble-row').length === 0) {
            emptyPlaceholderEl.classList.remove('hidden');
            emptyPlaceholderEl.classList.add('flex');
        }
    }

    if (convId) {
        const hasNew = newLastMessageId !== null && newLastMessageId !== undefined && newLastMessageId !== '';
        updateSidebarLastMessage(
            convId,
            hasNew ? (newLastMessagePreview || '') : 'No messages yet',
            hasNew ? newLastMessageId : null,
            false,
            !hasNew);
    }
}

/**
 * Resolves initial checkmark status and icons based on peer watermarks.
 * @param {string|null} messageId
 * @param {object|null} peerWatermarks
 * @returns {{ status: string, icon: string, colorClass: string }}
 */
export function resolveReceiptStatus(messageId, peerWatermarks = null) {
    if (!messageId) {
        return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
    }

    let lastRead = null;
    let lastDelivered = null;

    if (peerWatermarks) {
        lastRead = peerWatermarks.peerLastReadMessageId || peerWatermarks.PeerLastReadMessageId;
        lastDelivered = peerWatermarks.peerLastDeliveredMessageId || peerWatermarks.PeerLastDeliveredMessageId;
    } else if (typeof document !== 'undefined') {
        const c = document.getElementById('messages-container');
        if (c) {
            lastRead = c.getAttribute('data-peer-last-read-id');
            lastDelivered = c.getAttribute('data-peer-last-delivered-id');
        }
    }

    let msgIdVal = 0n;
    try {
        msgIdVal = BigInt(messageId);
    } catch {
        return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
    }

    let readVal = 0n;
    try {
        if (lastRead) readVal = BigInt(lastRead);
    } catch {}

    let delivVal = 0n;
    try {
        if (lastDelivered) delivVal = BigInt(lastDelivered);
    } catch {}

    if (readVal > 0n && msgIdVal <= readVal) {
        return { status: 'read', icon: 'done_all', colorClass: 'text-sky-300' };
    }
    if (delivVal > 0n && msgIdVal <= delivVal) {
        return { status: 'delivered', icon: 'done_all', colorClass: 'text-white/70' };
    }

    return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
}

/**
 * Creates DOM element tree for a single message bubble matching Stitch warm modern design tokens.
 * Pure DOM construction using document.createElement and .textContent - zero HTML string parsing.
 *
 * @param {object} message - MessageDto
 * @param {string|null} currentUserId - Authenticated user's GUID string
 * @param {object|null} peerWatermarks - Optional peer watermarks
 * @returns {HTMLElement} Message bubble row element
 */
export function createMessageBubbleElement(message, currentUserId, peerWatermarks = null, { eagerMedia = false } = {}) {
    if (typeof document === 'undefined') {
        return null;
    }

    const id = getMessageId(message);
    const senderId = getUserId(message.senderId);

    const isOutgoing = currentUserId
        ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
        : !!message.isOutgoing;

    const initials = getInitials(message.senderName, message.senderHandle);
    const senderName = message.senderName || 'Unknown';
    const timeStr = formatTime(message.createdAtUtc);
    const convId = message.conversationId ? (typeof message.conversationId === 'object' && message.conversationId.value ? message.conversationId.value : String(message.conversationId)) : '';
    const rawAttachments = message.attachments ?? message.Attachments ?? [];
    const attachments = !message.isDeletedForEveryone && Array.isArray(rawAttachments) ? rawAttachments : [];
    const hasAttachments = attachments.length > 0;
    const rawSticker = !message.isDeletedForEveryone ? (message.sticker ?? message.Sticker ?? null) : null;
    const stickerId = rawSticker ? normalizeIdValue(rawSticker.id ?? rawSticker.Id) : '';
    const isSticker = Boolean(rawSticker && stickerId);
    const isVoice = hasAttachments && attachments.some(a => String(a?.kind ?? a?.Kind ?? '').toLowerCase() === 'voice');
    const formattedPreview = formatMessagePreview(message);

    // Root row container
    const row = document.createElement('div');
    if (id) {
        row.id = `msg-${id}`;
        row.setAttribute('data-message-id', id);
    }
    row.setAttribute('data-deleted', message.isDeletedForEveryone ? 'true' : 'false');
    row.setAttribute('data-has-attachments', hasAttachments ? 'true' : 'false');
    row.setAttribute('data-is-sticker', isSticker ? 'true' : 'false');
    row.setAttribute('data-is-voice', isVoice ? 'true' : 'false');
    row.setAttribute('data-message-preview', formattedPreview);
    if (message.createdAtUtc) {
        row.setAttribute('data-created-at', message.createdAtUtc);
    }

    if (isOutgoing) {
        row.className = 'flex flex-wrap-reverse justify-end items-center gap-y-1 message-bubble-row group relative';
        row.setAttribute('data-is-outgoing', 'true');
    } else {
        row.className = 'flex flex-wrap justify-start items-center gap-2.5 message-bubble-row group relative';
        row.setAttribute('data-is-outgoing', 'false');

        // Avatar circle for incoming
        const senderAvatarVersion = message.senderAvatarVersion ?? message.SenderAvatarVersion ?? '';
        const avatar = document.createElement('div');
        avatar.className = 'w-8 h-8 rounded-full overflow-hidden bg-[#B3929D]/20 dark:bg-[#B3929D]/25 text-[#735761] dark:text-[#E1BDC9] border border-[#B3929D]/30 font-bold text-xs flex items-center justify-center flex-shrink-0 mt-1';
        avatar.setAttribute('data-avatar-slot', 'true');
        if (senderId) avatar.setAttribute('data-avatar-user-id', senderId);
        avatar.setAttribute('data-avatar-version', senderAvatarVersion ? String(senderAvatarVersion) : '');
        avatar.setAttribute('data-avatar-name', senderName);
        avatar.setAttribute('data-avatar-handle', message.senderHandle || '');
        const initialsSpan = document.createElement('span');
        initialsSpan.setAttribute('data-avatar-initials', 'true');
        initialsSpan.textContent = initials;
        avatar.appendChild(initialsSpan);
        hydrateAvatarSlot(avatar);
        row.appendChild(avatar);
    }

    if (senderId) {
        row.setAttribute('data-sender-id', senderId);
    }

    // Bubble container
    const bubble = document.createElement('div');
    if (isSticker) {
        bubble.className = isOutgoing
            ? 'message-bubble max-w-[75%] min-w-0 relative flex flex-col items-end'
            : 'message-bubble max-w-[75%] min-w-0 relative flex flex-col items-start';
        if (!isOutgoing) {
            const senderHeader = document.createElement('div');
            senderHeader.className = 'text-[11px] font-bold text-[#735761] dark:text-[#E1BDC9] px-1 mb-0.5';
            senderHeader.textContent = senderName;
            bubble.appendChild(senderHeader);
        }
    } else if (isOutgoing) {
        bubble.className = 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative';
    } else {
        bubble.className = 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tl-xs px-4 py-2 bg-[#FFFFFF] dark:bg-[#3A3835] text-[#2B2725] dark:text-[#F0E9E2] border border-[#E3DCD7] dark:border-[#514D48] shadow-2xs relative';

        const senderHeader = document.createElement('div');
        senderHeader.className = 'text-[11px] font-bold text-[#735761] dark:text-[#E1BDC9] mb-0.5';
        senderHeader.textContent = senderName;
        bubble.appendChild(senderHeader);
    }

    // Reply quote banner if replyTo exists
    if (message.replyTo) {
        const replyBanner = document.createElement('div');
        const rBgClass = (isOutgoing && !isSticker)
            ? 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-black/15 border-white/70 text-white/80 cursor-pointer hover:bg-black/25 transition-colors'
            : 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-[#FFFFFF] dark:bg-[#3A3835] border-[#B3929D] text-[#6E6560] dark:text-[#C9C0B9] cursor-pointer hover:bg-[#F0E9DF] dark:hover:bg-[#3E3C38] transition-colors shadow-2xs';
        replyBanner.className = rBgClass;

        const parentId = getMessageId(message.replyTo);
        if (parentId) {
            replyBanner.setAttribute('data-reply-parent-id', parentId);
        }

        const rSenderEl = document.createElement('div');
        rSenderEl.className = (isOutgoing && !isSticker) ? 'font-bold text-[10px] text-white/95' : 'font-bold text-[10px] text-[#2B2725] dark:text-[#F0E9E2]';
        rSenderEl.textContent = message.replyTo.senderName || message.replyTo.senderDisplayName || 'Unknown';
        replyBanner.appendChild(rSenderEl);

        const rContentEl = document.createElement('div');
        rContentEl.className = 'reply-preview-text truncate text-[10px]';
        if (message.replyTo.isDeletedForEveryone) {
            const deletedSpan = document.createElement('span');
            deletedSpan.className = 'italic';
            deletedSpan.textContent = 'This message was deleted';
            rContentEl.appendChild(deletedSpan);
        } else {
            rContentEl.textContent = message.replyTo.previewContent || message.replyTo.contentPreview || '';
        }
        replyBanner.appendChild(rContentEl);
        bubble.appendChild(replyBanner);
    }

    // Message content or deleted placeholder
    if (message.isDeletedForEveryone) {
        const delDiv = document.createElement('div');
        delDiv.className = isOutgoing
            ? 'message-text-content italic text-xs text-white/80 flex items-center gap-1.5 py-0.5'
            : 'message-text-content italic text-xs text-[#6E6560] dark:text-[#C9C0B9] flex items-center gap-1.5 py-0.5';

        const blockIcon = document.createElement('span');
        blockIcon.className = 'material-symbols-outlined text-[15px]';
        blockIcon.textContent = 'block';
        delDiv.appendChild(blockIcon);

        const delText = document.createElement('span');
        delText.textContent = 'This message was deleted';
        delDiv.appendChild(delText);

        bubble.appendChild(delDiv);
    } else if (isSticker) {
        const stickerWrap = document.createElement('div');
        stickerWrap.className = 'message-sticker-container';

        const stickerPackId = normalizeIdValue(rawSticker.packId ?? rawSticker.PackId);
        const stickerPackName = String(rawSticker.packName ?? rawSticker.PackName ?? 'Stickers');
        const stickerPackAuthor = String(rawSticker.packAuthor ?? rawSticker.PackAuthor ?? 'Community Sticker');
        const stickerAnimated = !!(rawSticker.isAnimated ?? rawSticker.IsAnimated);
        const stickerSystem = !!(rawSticker.isSystem ?? rawSticker.IsSystem);
        const stickerStaticUrl = rawSticker.staticUrl ?? rawSticker.StaticUrl ?? '';
        const stickerTags = String(rawSticker.emojiTags ?? rawSticker.EmojiTags ?? '');
        const stickerFavorited = !!(rawSticker.isFavoritedByCurrentUser ?? rawSticker.IsFavoritedByCurrentUser);

        const stickerBtn = document.createElement('button');
        stickerBtn.type = 'button';
        stickerBtn.setAttribute('data-sticker-info-trigger', 'true');
        stickerBtn.setAttribute('data-sticker-id', stickerId);
        stickerBtn.setAttribute('data-sticker-pack-id', stickerPackId);
        stickerBtn.setAttribute('data-sticker-pack-name', stickerPackName);
        stickerBtn.setAttribute('data-sticker-pack-author', stickerPackAuthor);
        stickerBtn.setAttribute('data-sticker-animated', stickerAnimated ? 'true' : 'false');
        stickerBtn.setAttribute('data-sticker-system', stickerSystem ? 'true' : 'false');
        stickerBtn.setAttribute('data-sticker-static-url', stickerStaticUrl || '');
        stickerBtn.setAttribute('data-sticker-tags', stickerTags);
        stickerBtn.setAttribute('data-sticker-favorited', stickerFavorited ? 'true' : 'false');
        stickerBtn.setAttribute('aria-label', stickerTags ? `Sticker: ${stickerTags}. View details` : 'View sticker details');
        stickerBtn.title = stickerPackName;
        stickerBtn.className = 'block rounded-2xl p-0.5 focus:outline-none focus:ring-2 focus:ring-[#B3929D] transition-transform hover:scale-[1.03] active:scale-[0.98] cursor-pointer';

        const img = document.createElement('img');
        applyInitialStickerSrc(img, rawSticker);
        img.alt = stickerTags ? `Sticker ${stickerTags}` : 'Sticker';
        img.setAttribute('width', String(Number(rawSticker.width ?? rawSticker.Width ?? 512) || 512));
        img.setAttribute('height', String(Number(rawSticker.height ?? rawSticker.Height ?? 512) || 512));
        img.loading = eagerMedia ? 'eager' : 'lazy';
        img.decoding = 'async';
        img.draggable = false;
        img.setAttribute('draggable', 'false');
        img.setAttribute('data-sticker-img', 'true');
        img.setAttribute('data-sticker-id', stickerId);
        img.setAttribute('data-sticker-static', stickerStaticUrl ? 'true' : 'false');
        img.className = 'w-36 h-36 sm:w-44 sm:h-44 object-contain drop-shadow-sm select-none';
        stickerBtn.appendChild(img);

        stickerWrap.appendChild(stickerBtn);
        bubble.appendChild(stickerWrap);

        const hiddenText = document.createElement('div');
        hiddenText.className = 'message-text-content hidden';
        bubble.appendChild(hiddenText);
    } else {
        if (hasAttachments) {
            const mediaContainer = document.createElement('div');
            mediaContainer.className = 'message-media-container mb-1.5 space-y-1.5';

            const images = attachments.filter(a => String(a?.kind ?? a?.Kind ?? '').toLowerCase() === 'image');
            const others = attachments.filter(a => String(a?.kind ?? a?.Kind ?? '').toLowerCase() !== 'image');

            const buildImageButton = (att, isGrid) => {
                const mediaId = normalizeIdValue(att.id ?? att.Id);
                const fileName = att.fileName ?? att.FileName ?? 'image';
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.setAttribute('data-lightbox-trigger', 'true');
                btn.setAttribute('data-conversation-id', convId);
                if (id) btn.setAttribute('data-message-id', id);
                btn.setAttribute('data-media-id', mediaId);
                btn.setAttribute('data-file-name', fileName);
                btn.className = isGrid
                    ? (isOutgoing
                        ? 'block aspect-square overflow-hidden rounded-lg bg-black/10 focus:outline-none focus:ring-2 focus:ring-white/80 cursor-pointer'
                        : 'block aspect-square overflow-hidden rounded-lg bg-black/5 dark:bg-black/20 focus:outline-none focus:ring-2 focus:ring-[#B3929D] cursor-pointer')
                    : (isOutgoing
                        ? 'block w-full overflow-hidden rounded-xl bg-black/10 focus:outline-none focus:ring-2 focus:ring-white/80 cursor-pointer'
                        : 'block w-full overflow-hidden rounded-xl bg-black/5 dark:bg-black/20 focus:outline-none focus:ring-2 focus:ring-[#B3929D] cursor-pointer');

                const img = document.createElement('img');
                img.src = buildMediaInlineUrl(convId, id || '', mediaId, { variant: 'thumb' });
                img.alt = fileName;
                const w = att.width ?? att.Width;
                const h = att.height ?? att.Height;
                if (w) img.setAttribute('width', String(w));
                if (h) img.setAttribute('height', String(h));
                img.loading = eagerMedia ? 'eager' : 'lazy';
                img.decoding = 'async';
                img.draggable = false;
                img.setAttribute('draggable', 'false');
                img.setAttribute('data-media-img', 'true');
                img.className = isGrid
                    ? 'w-full h-full aspect-square object-cover select-none'
                    : 'w-full h-auto max-h-80 object-contain rounded-xl select-none';
                btn.appendChild(img);
                return btn;
            };

            if (images.length === 1) {
                mediaContainer.appendChild(buildImageButton(images[0], false));
            } else if (images.length > 1) {
                const grid = document.createElement('div');
                grid.className = 'grid grid-cols-2 gap-1.5';
                for (const imgAtt of images) {
                    grid.appendChild(buildImageButton(imgAtt, true));
                }
                mediaContainer.appendChild(grid);
            }

            for (const att of others) {
                const kind = String(att?.kind ?? att?.Kind ?? 'document').toLowerCase();
                const mediaId = normalizeIdValue(att.id ?? att.Id);
                const fileName = att.fileName ?? att.FileName ?? 'Attachment';
                const sizeBytes = Number(att.sizeBytes ?? att.SizeBytes ?? 0);

                if (kind === 'video') {
                    const wrap = document.createElement('div');
                    wrap.className = isOutgoing
                        ? 'media-video-wrapper overflow-hidden rounded-xl bg-black/20'
                        : 'media-video-wrapper overflow-hidden rounded-xl bg-black/10 dark:bg-black/25';
                    wrap.setAttribute('data-video-container', 'true');

                    const video = document.createElement('video');
                    video.controls = true;
                    video.preload = 'metadata';
                    video.playsInline = true;
                    video.setAttribute('playsinline', '');
                    video.className = 'w-full max-h-80 rounded-xl bg-black';
                    video.setAttribute('data-media-video', 'true');
                    video.setAttribute('data-conversation-id', convId);
                    if (id) video.setAttribute('data-message-id', id);
                    video.setAttribute('data-media-id', mediaId);
                    video.setAttribute('data-file-name', fileName);
                    wrap.appendChild(video);

                    const fallback = document.createElement('div');
                    fallback.className = isOutgoing
                        ? 'media-video-fallback hidden px-3 py-2 text-xs opacity-80'
                        : 'media-video-fallback hidden px-3 py-2 text-xs text-[#6E6560] dark:text-[#C9C0B9]';
                    fallback.textContent = 'Media unavailable';
                    wrap.appendChild(fallback);

                    mediaContainer.appendChild(wrap);
                } else if (kind === 'voice') {
                    const voiceCard = createVoiceBubbleElement({
                        conversationId: convId,
                        messageId: id,
                        mediaId,
                        durationMs: att.durationMs ?? att.DurationMs ?? 0,
                        waveformPeaks: att.waveformPeaks ?? att.WaveformPeaks ?? null,
                        playedAtUtc: att.playedAtUtc ?? att.PlayedAtUtc ?? null,
                        isOutgoing
                    });
                    mediaContainer.appendChild(voiceCard);
                } else {
                    const docBtn = document.createElement('button');
                    docBtn.type = 'button';
                    docBtn.setAttribute('data-media-download', 'true');
                    docBtn.setAttribute('data-conversation-id', convId);
                    if (id) docBtn.setAttribute('data-message-id', id);
                    docBtn.setAttribute('data-media-id', mediaId);
                    docBtn.setAttribute('data-file-name', fileName);
                    docBtn.className = isOutgoing
                        ? 'w-full max-w-xs min-w-0 flex items-center gap-2.5 px-3 py-2 rounded-xl bg-black/15 hover:bg-black/25 text-left transition-colors cursor-pointer'
                        : 'w-full max-w-xs min-w-0 flex items-center gap-2.5 px-3 py-2 rounded-xl bg-[#F8F6F3] dark:bg-[#302F2C] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] border border-[#E3DCD7] dark:border-[#514D48] text-left transition-colors cursor-pointer';

                    const docIcon = document.createElement('span');
                    docIcon.className = isOutgoing
                        ? 'material-symbols-outlined text-[20px] shrink-0'
                        : 'material-symbols-outlined text-[20px] text-[#735761] dark:text-[#E1BDC9] shrink-0';
                    docIcon.textContent = 'description';
                    docBtn.appendChild(docIcon);

                    const textCol = document.createElement('span');
                    textCol.className = 'flex-1 min-w-0';
                    const nameSpan = document.createElement('span');
                    nameSpan.className = isOutgoing
                        ? 'block text-xs font-semibold truncate'
                        : 'block text-xs font-semibold truncate text-[#2B2725] dark:text-[#F0E9E2]';
                    nameSpan.textContent = fileName;
                    textCol.appendChild(nameSpan);

                    const sizeSpan = document.createElement('span');
                    sizeSpan.className = isOutgoing
                        ? 'block text-[10px] opacity-80'
                        : 'block text-[10px] text-[#6E6560] dark:text-[#C9C0B9]';
                    sizeSpan.textContent = formatAttachmentBytes(sizeBytes);
                    textCol.appendChild(sizeSpan);
                    docBtn.appendChild(textCol);

                    const dlIcon = document.createElement('span');
                    dlIcon.className = isOutgoing
                        ? 'material-symbols-outlined text-[18px] shrink-0 opacity-90'
                        : 'material-symbols-outlined text-[18px] text-[#6E6560] dark:text-[#C9C0B9] shrink-0';
                    dlIcon.textContent = 'download';
                    docBtn.appendChild(dlIcon);

                    mediaContainer.appendChild(docBtn);
                }
            }

            bubble.appendChild(mediaContainer);
        }

        const msgText = message.content || '';
        const hasCaption = msgText.length > 0;
        const contentDiv = document.createElement('div');
        contentDiv.className = `${getMessageContentClassName(msgText)}${hasCaption ? '' : ' hidden'}`;
        contentDiv.textContent = msgText;
        bubble.appendChild(contentDiv);
    }

    // Footer with time, edited flag, and receipt checkmarks
    const footer = document.createElement('div');
    if (isSticker) {
        footer.className = 'message-meta-footer inline-flex items-center justify-end gap-1 mt-1 px-2 py-0.5 rounded-full bg-black/55 text-white text-[10px] shadow-2xs';
    } else {
        footer.className = isOutgoing
            ? 'message-meta-footer flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80'
            : 'message-meta-footer flex items-center justify-end gap-1 mt-1 text-[10px] text-[#6E6560] dark:text-[#C9C0B9]';
    }

    const editedSpan = document.createElement('span');
    editedSpan.className = `text-[9px] italic edited-label ${message.editedAtUtc && !message.isDeletedForEveryone ? '' : 'hidden'}`;
    editedSpan.textContent = 'edited';
    footer.appendChild(editedSpan);

    const timeSpan = document.createElement('span');
    timeSpan.className = 'message-timestamp';
    if (message.createdAtUtc) {
        timeSpan.setAttribute('data-created-at', message.createdAtUtc);
    }
    timeSpan.textContent = timeStr;
    footer.appendChild(timeSpan);

    // Delivery & Read Receipt checkmark indicator (outgoing only)
    if (isOutgoing && !message.isDeletedForEveryone) {
        const receipt = resolveReceiptStatus(id, peerWatermarks);
        const statusSpan = document.createElement('span');
        statusSpan.className = `material-symbols-outlined text-[13px] select-none align-middle ${receipt.colorClass}`;
        statusSpan.textContent = receipt.icon;
        statusSpan.setAttribute('data-receipt-status', receipt.status);
        if (id) {
            statusSpan.setAttribute('data-message-id', id);
        }
        footer.appendChild(statusSpan);
    }

    bubble.appendChild(footer);

    // Reaction pills container below footer
    const pillsContainer = document.createElement('div');
    pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
    bubble.appendChild(pillsContainer);

    row.appendChild(bubble);

    // Render reaction pills AFTER bubble is attached to row so data-is-sticker contrast rules apply
    if (message.reactions && message.reactions.length > 0) {
        renderReactionPills(pillsContainer, message.reactions, id, convId, isOutgoing);
    } else {
        pillsContainer.classList.add('hidden');
    }

    // Message action controls bar (desktop hover/focus; mobile uses WhatsApp-style long-press action sheet)
    if (!message.isDeletedForEveryone) {
        const actionsBar = document.createElement('div');
        actionsBar.className = isOutgoing
            ? 'message-actions-bar hidden sm:flex sm:opacity-0 sm:group-hover:opacity-100 sm:group-focus-within:opacity-100 transition-opacity self-center shrink-0 mr-1 items-center gap-0.5'
            : 'message-actions-bar hidden sm:flex sm:opacity-0 sm:group-hover:opacity-100 sm:group-focus-within:opacity-100 transition-opacity self-center shrink-0 ml-1 items-center gap-0.5';

        // React button
        const reactBtn = document.createElement('button');
        reactBtn.type = 'button';
        reactBtn.setAttribute('data-action', 'react');
        reactBtn.className = 'w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
        reactBtn.title = 'Add reaction';
        reactBtn.setAttribute('aria-label', 'Add reaction');

        const reactIcon = document.createElement('span');
        reactIcon.className = 'material-symbols-outlined text-[16px]';
        reactIcon.textContent = 'add_reaction';
        reactBtn.appendChild(reactIcon);
        actionsBar.appendChild(reactBtn);

        // Reply button
        const replyBtn = document.createElement('button');
        replyBtn.type = 'button';
        replyBtn.className = 'reply-action-btn w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
        replyBtn.title = 'Reply to message';
        replyBtn.setAttribute('aria-label', 'Reply to message');
        if (id) replyBtn.setAttribute('data-id', id);
        replyBtn.setAttribute('data-sender', senderName);
        replyBtn.setAttribute('data-preview', formattedPreview);

        const replyIcon = document.createElement('span');
        replyIcon.className = 'material-symbols-outlined text-[16px]';
        replyIcon.textContent = 'reply';
        replyBtn.appendChild(replyIcon);
        actionsBar.appendChild(replyBtn);

        // Quick Star / Sticker Info button (sticker messages only)
        if (isSticker) {
            const stickerPackId = normalizeIdValue(rawSticker.packId ?? rawSticker.PackId);
            const stickerPackName = String(rawSticker.packName ?? rawSticker.PackName ?? 'Stickers');
            const stickerPackAuthor = String(rawSticker.packAuthor ?? rawSticker.PackAuthor ?? 'Community Sticker');
            const stickerAnimated = !!(rawSticker.isAnimated ?? rawSticker.IsAnimated);
            const stickerSystem = !!(rawSticker.isSystem ?? rawSticker.IsSystem);
            const stickerStaticUrl = rawSticker.staticUrl ?? rawSticker.StaticUrl ?? '';
            const stickerTags = String(rawSticker.emojiTags ?? rawSticker.EmojiTags ?? '');
            const stickerFavorited = !!(rawSticker.isFavoritedByCurrentUser ?? rawSticker.IsFavoritedByCurrentUser);

            const favBtn = document.createElement('button');
            favBtn.type = 'button';
            favBtn.setAttribute('data-sticker-info-trigger', 'true');
            favBtn.setAttribute('data-sticker-id', stickerId);
            favBtn.setAttribute('data-sticker-pack-id', stickerPackId);
            favBtn.setAttribute('data-sticker-pack-name', stickerPackName);
            favBtn.setAttribute('data-sticker-pack-author', stickerPackAuthor);
            favBtn.setAttribute('data-sticker-animated', stickerAnimated ? 'true' : 'false');
            favBtn.setAttribute('data-sticker-system', stickerSystem ? 'true' : 'false');
            favBtn.setAttribute('data-sticker-static-url', stickerStaticUrl || '');
            favBtn.setAttribute('data-sticker-tags', stickerTags);
            favBtn.setAttribute('data-sticker-favorited', stickerFavorited ? 'true' : 'false');
            favBtn.className = 'w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-amber-500 hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
            favBtn.title = 'Sticker info & favorites';
            favBtn.setAttribute('aria-label', 'Sticker info and favorites');

            const favIcon = document.createElement('span');
            favIcon.className = 'material-symbols-outlined text-[16px]';
            favIcon.textContent = stickerFavorited ? 'star' : 'star_outline';
            favBtn.appendChild(favIcon);
            actionsBar.appendChild(favBtn);
        }

        // Edit button (sender only, never on stickers or voice messages)
        if (isOutgoing && !isSticker && !isVoice) {
            const editBtn = document.createElement('button');
            editBtn.type = 'button';
            editBtn.setAttribute('data-action', 'edit');
            editBtn.className = 'w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
            editBtn.title = 'Edit message';
            editBtn.setAttribute('aria-label', 'Edit message');

            const editIcon = document.createElement('span');
            editIcon.className = 'material-symbols-outlined text-[16px]';
            editIcon.textContent = 'edit';
            editBtn.appendChild(editIcon);
            actionsBar.appendChild(editBtn);
        }

        // Delete button
        const delBtn = document.createElement('button');
        delBtn.type = 'button';
        delBtn.setAttribute('data-action', 'delete');
        delBtn.className = 'w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-red-500 hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
        delBtn.title = 'Delete message';
        delBtn.setAttribute('aria-label', 'Delete message');

        const delIcon = document.createElement('span');
        delIcon.className = 'material-symbols-outlined text-[16px]';
        delIcon.textContent = 'delete';
        delBtn.appendChild(delIcon);
        actionsBar.appendChild(delBtn);

        if (isOutgoing) {
            row.insertBefore(actionsBar, bubble);
        } else {
            row.appendChild(actionsBar);
        }
    }

    if (hasAttachments) {
        hydrateMediaElements(row);
    }
    if (isSticker) {
        hydrateStickerImages(row);
    }

    return row;
}

/**
 * Creates HTML string representation for a single message bubble (for testing parity).
 * Escapes all user content for XSS defense.
 *
 * @param {object} message - MessageDto
 * @param {string|null} currentUserId - Authenticated user's GUID string
 * @param {object|null} peerWatermarks - Optional peer watermarks
 * @returns {string} HTML string
 */
export function createMessageBubbleHtml(message, currentUserId, peerWatermarks = null) {
    const id = escapeHtml(getMessageId(message));
    const senderId = getUserId(message.senderId);

    const isOutgoing = currentUserId
        ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
        : !!message.isOutgoing;

    const initials = escapeHtml(getInitials(message.senderName, message.senderHandle));
    const senderName = escapeHtml(message.senderName);
    const timeStr = escapeHtml(formatTime(message.createdAtUtc));
    const editedStr = (message.editedAtUtc && !message.isDeletedForEveryone) ? '<span class="text-[9px] italic edited-label">edited</span>' : '';

    let replyBannerHtml = '';
    if (message.replyTo) {
        const rSender = escapeHtml(message.replyTo.senderName || message.replyTo.senderDisplayName || 'Unknown');
        const rContent = message.replyTo.isDeletedForEveryone
            ? '<span class="italic">This message was deleted</span>'
            : escapeHtml(message.replyTo.previewContent);
        const rBgClass = isOutgoing
            ? 'bg-black/15 border-white/70 text-white/80'
            : 'bg-[#F8F6F3] dark:bg-[#302F2C] border-[#B3929D] text-[#6E6560] dark:text-[#C9C0B9]';
        const rSenderColor = isOutgoing ? 'text-white/95' : 'text-[#2B2725] dark:text-[#F0E9E2]';
        const parentId = escapeHtml(getMessageId(message.replyTo));

        replyBannerHtml = `
            <div class="mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs ${rBgClass} cursor-pointer" data-reply-parent-id="${parentId}">
                <div class="font-bold text-[10px] ${rSenderColor}">${rSender}</div>
                <div class="truncate text-[10px]">${rContent}</div>
            </div>`;
    }

    let contentHtml = '';
    if (message.isDeletedForEveryone) {
        const delColor = isOutgoing ? 'text-white/80' : 'text-[#6E6560] dark:text-[#C9C0B9]';
        contentHtml = `
            <div class="message-text-content italic text-xs ${delColor} flex items-center gap-1.5 py-0.5">
                <span class="material-symbols-outlined text-[15px]">block</span>
                <span>This message was deleted</span>
            </div>`;
    } else {
        contentHtml = `<div class="${getMessageContentClassName(message.content || '')}">${escapeHtml(message.content)}</div>`;
    }

    const replyBtnHtml = !message.isDeletedForEveryone
        ? `<button type="button" class="reply-action-btn opacity-0 group-hover:opacity-100 transition-opacity self-center ${isOutgoing ? 'mr-2' : 'ml-2'} text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D]" title="Reply to message" data-id="${id}" data-sender="${senderName}" data-preview="${escapeHtml(message.content)}">
            <span class="material-symbols-outlined text-[18px]">reply</span>
           </button>`
        : '';

    let receiptHtml = '';
    if (isOutgoing && !message.isDeletedForEveryone) {
        const receipt = resolveReceiptStatus(id, peerWatermarks);
        receiptHtml = `<span class="material-symbols-outlined text-[13px] select-none align-middle ${receipt.colorClass}" data-message-id="${id}" data-receipt-status="${receipt.status}">${receipt.icon}</span>`;
    }

    const senderIdAttr = senderId ? ` data-sender-id="${escapeHtml(senderId)}"` : '';

    if (isOutgoing) {
        return `
        <div id="msg-${id}" class="flex flex-wrap-reverse justify-end items-center gap-y-1 message-bubble-row group relative" data-message-id="${id}" data-is-outgoing="true"${senderIdAttr} data-created-at="${escapeHtml(message.createdAtUtc)}">
            ${replyBtnHtml}
            <div class="max-w-[75%] rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative">
                ${replyBannerHtml}
                ${contentHtml}
                <div class="flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80">
                    ${editedStr}
                    <span>${timeStr}</span>
                    ${receiptHtml}
                </div>
            </div>
        </div>`;
    } else {
        return `
        <div id="msg-${id}" class="flex justify-start gap-2.5 message-bubble-row group relative" data-message-id="${id}" data-is-outgoing="false"${senderIdAttr} data-created-at="${escapeHtml(message.createdAtUtc)}">
            <div class="w-8 h-8 rounded-full bg-[#B3929D]/20 dark:bg-[#B3929D]/25 text-[#735761] dark:text-[#E1BDC9] border border-[#B3929D]/30 font-bold text-xs flex items-center justify-center flex-shrink-0 mt-1">
                ${initials}
            </div>
            <div class="max-w-[75%] rounded-2xl rounded-tl-xs px-4 py-2 bg-[#FFFFFF] dark:bg-[#3A3835] text-[#2B2725] dark:text-[#F0E9E2] border border-[#E3DCD7] dark:border-[#514D48] shadow-2xs relative">
                <div class="text-[11px] font-bold text-[#735761] dark:text-[#E1BDC9] mb-0.5">${senderName}</div>
                ${replyBannerHtml}
                ${contentHtml}
                <div class="flex items-center justify-end gap-1 mt-1 text-[10px] text-[#6E6560] dark:text-[#C9C0B9]">
                    ${editedStr}
                    <span>${timeStr}</span>
                </div>
            </div>
            ${replyBtnHtml}
        </div>`;
    }
}

function normalizeIdValue(value) {
    if (value === null || value === undefined) return '';
    if (typeof value === 'object' && value.value !== undefined) {
        return String(value.value);
    }
    return String(value);
}

function parseBigIntSafe(val) {
    if (val === null || val === undefined || val === '') return 0n;
    try {
        return BigInt(String(val));
    } catch {
        return 0n;
    }
}

/**
 * Deterministic total ordering key comparator: (CreatedAtUtc, Id).
 * @param {object} a
 * @param {object} b
 * @returns {number}
 */
export function compareMessages(a, b) {
    const rawTimeA = a?.createdAtUtc ?? a?.CreatedAtUtc ?? '';
    const rawTimeB = b?.createdAtUtc ?? b?.CreatedAtUtc ?? '';
    const timeA = rawTimeA ? Date.parse(rawTimeA) : NaN;
    const timeB = rawTimeB ? Date.parse(rawTimeB) : NaN;

    if (!Number.isNaN(timeA) && !Number.isNaN(timeB) && timeA !== timeB) {
        return timeA - timeB;
    }

    const idA = parseBigIntSafe(getMessageId(a));
    const idB = parseBigIntSafe(getMessageId(b));
    if (idA < idB) return -1;
    if (idA > idB) return 1;
    return 0;
}

function clearElementChildren(el) {
    if (!el) return;
    while (el.firstChild) {
        el.removeChild(el.firstChild);
    }
}

function syncMessagesContainerAttributes(containerEl, currentUserId, meta) {
    if (!containerEl) return;

    if (currentUserId !== undefined && currentUserId !== null) {
        containerEl.setAttribute('data-current-user-id', String(currentUserId));
    }

    if (!meta) return;

    const convId = normalizeIdValue(meta.id ?? meta.Id);
    const peerUserId = normalizeIdValue(meta.otherParticipantId ?? meta.OtherParticipantId);
    const isOnline = !!(meta.isOnline ?? meta.IsOnline);
    const lastSeen = meta.lastSeenAtUtc ?? meta.LastSeenAtUtc ?? '';
    const lastDelivered = normalizeIdValue(meta.peerLastDeliveredMessageId ?? meta.PeerLastDeliveredMessageId);
    const lastRead = normalizeIdValue(meta.peerLastReadMessageId ?? meta.PeerLastReadMessageId);

    if (convId) containerEl.setAttribute('data-conversation-id', convId);
    containerEl.setAttribute('data-peer-user-id', peerUserId);
    containerEl.setAttribute('data-peer-is-online', isOnline ? 'true' : 'false');
    containerEl.setAttribute('data-peer-last-seen', lastSeen || '');
    containerEl.setAttribute('data-peer-last-delivered-id', lastDelivered);
    containerEl.setAttribute('data-peer-last-read-id', lastRead);
}

/**
 * Inserts a message into containerEl in deterministic (CreatedAtUtc, Id) order with ID deduplication.
 * @param {HTMLElement|null} containerEl
 * @param {object} message
 * @param {string|null} currentUserId
 * @param {object|null} [peerWatermarks]
 * @returns {HTMLElement|null}
 */
export function insertMessageOrdered(containerEl, message, currentUserId, peerWatermarks = null) {
    if (!containerEl || !message || typeof document === 'undefined') return null;

    const msgId = getMessageId(message);
    if (!msgId) return null;

    if ((typeof containerEl.querySelector === 'function' && containerEl.querySelector(`[data-message-id="${msgId}"]`)) ||
        document.getElementById(`msg-${msgId}`)) {
        return null;
    }

    const el = createMessageBubbleElement(message, currentUserId, peerWatermarks, { eagerMedia: true });
    if (!el) return null;

    const rows = typeof containerEl.querySelectorAll === 'function'
        ? containerEl.querySelectorAll('.message-bubble-row')
        : [];

    let insertBeforeNode = null;
    for (let i = rows.length - 1; i >= 0; i--) {
        const row = rows[i];
        const rowModel = {
            id: row.getAttribute('data-message-id'),
            createdAtUtc: row.getAttribute('data-created-at')
        };
        if (compareMessages(message, rowModel) < 0) {
            insertBeforeNode = row;
        } else {
            break;
        }
    }

    if (insertBeforeNode && typeof containerEl.insertBefore === 'function') {
        containerEl.insertBefore(el, insertBeforeNode);
    } else if (typeof containerEl.appendChild === 'function') {
        containerEl.appendChild(el);
    }

    const emptyPlaceholder = document.getElementById('no-messages-placeholder');
    if (emptyPlaceholder) {
        emptyPlaceholder.classList.add('hidden');
    }

    return el;
}

/**
 * Renders a full message page sorted ascending by (CreatedAtUtc, Id) using a DocumentFragment.
 * @param {HTMLElement|null} containerEl
 * @param {Array<object>} messages
 * @param {string|null} currentUserId
 * @param {object|null} [peerWatermarks]
 * @param {{ scrollToEnd?: boolean }} [options]
 */
export function renderMessages(containerEl, messages, currentUserId, peerWatermarks = null, { scrollToEnd = true, hasMore = undefined } = {}) {
    if (!containerEl || typeof document === 'undefined') return;

    disconnectActiveMediaObservers();
    teardownRowVideos(containerEl);
    clearElementChildren(containerEl);
    syncMessagesContainerAttributes(containerEl, currentUserId, peerWatermarks);

    const skeletonEl = document.getElementById('chat-loading-skeleton');
    if (skeletonEl) skeletonEl.classList.add('hidden');

    hideSafeError();

    const list = Array.isArray(messages) ? [...messages] : [];
    const seenIds = new Set();
    const deduped = [];
    for (const msg of list) {
        const msgId = getMessageId(msg);
        if (!msgId || seenIds.has(msgId)) continue;
        seenIds.add(msgId);
        deduped.push(msg);
    }

    deduped.sort(compareMessages);

    const emptyPlaceholder = document.getElementById('no-messages-placeholder');
    const resolvedHasMore = typeof hasMore === 'boolean'
        ? hasMore
        : (typeof containerEl.getAttribute === 'function' && containerEl.getAttribute('data-has-more') === 'true');
    if (deduped.length === 0) {
        if (emptyPlaceholder) {
            if (resolvedHasMore) {
                emptyPlaceholder.classList.add('hidden');
                emptyPlaceholder.classList.remove('flex');
            } else {
                emptyPlaceholder.classList.remove('hidden');
                emptyPlaceholder.classList.add('flex');
            }
        }
        return;
    }

    if (emptyPlaceholder) {
        emptyPlaceholder.classList.add('hidden');
        emptyPlaceholder.classList.remove('flex');
    }

    const frag = typeof document.createDocumentFragment === 'function'
        ? document.createDocumentFragment()
        : containerEl;

    for (let i = 0; i < deduped.length; i++) {
        const msg = deduped[i];
        const eagerMedia = (deduped.length - i) <= 6;
        const el = createMessageBubbleElement(msg, currentUserId, peerWatermarks, { eagerMedia });
        if (el) {
            frag.appendChild(el);
        }
    }

    if (frag !== containerEl && typeof containerEl.appendChild === 'function') {
        containerEl.appendChild(frag);
    }

    if (scrollToEnd) {
        scrollToBottom(false);
    }
}

/**
 * Synchronizes keyset pagination attributes on #messages-container.
 * @param {HTMLElement|null} containerEl
 * @param {string|null|undefined} nextCursor
 * @param {boolean} hasMore
 */
export function syncPaginationAttributes(containerEl, nextCursor, hasMore) {
    if (!containerEl || typeof containerEl.setAttribute !== 'function') return;
    containerEl.setAttribute('data-next-cursor', nextCursor || '');
    containerEl.setAttribute('data-has-more', hasMore ? 'true' : 'false');
}

/**
 * Synchronizes top pagination UI elements (loading spinner, tall-viewport manual load button,
 * transient error retry bar, and beginning-of-conversation pill).
 */
export function syncPaginationUi({
    hasMore = false,
    hasMessages = false,
    hasPaginatedOlder = false,
    isLoading = false,
    errorMessage = null,
    streamEl = null
} = {}) {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;

    const loadingTopEl = document.getElementById('messages-loading-top');
    if (loadingTopEl && loadingTopEl.classList) {
        loadingTopEl.classList.toggle('hidden', !isLoading);
    }

    const errorWrapEl = document.getElementById('messages-load-older-error');
    const errorTextEl = document.getElementById('messages-load-older-error-text');
    const showError = Boolean(errorMessage) && !isLoading;
    if (errorTextEl) {
        errorTextEl.textContent = showError ? String(errorMessage) : 'Could not load older messages.';
    }
    if (errorWrapEl && errorWrapEl.classList) {
        errorWrapEl.classList.toggle('hidden', !showError);
    }

    const targetStreamEl = streamEl || document.getElementById('message-stream');
    const scrollHeight = Number(targetStreamEl?.scrollHeight || 0);
    const clientHeight = Number(targetStreamEl?.clientHeight || 0);
    const needsManualLoad = Boolean(
        hasMore &&
        !isLoading &&
        !showError &&
        targetStreamEl &&
        scrollHeight > 0 &&
        scrollHeight <= clientHeight + 24
    );

    const loadOlderWrapEl = document.getElementById('messages-load-older-wrap');
    if (loadOlderWrapEl && loadOlderWrapEl.classList) {
        loadOlderWrapEl.classList.toggle('hidden', !needsManualLoad);
    }

    const historyStartEl = document.getElementById('messages-history-start');
    const showHistoryStart = Boolean(!hasMore && hasMessages && hasPaginatedOlder && !isLoading && !showError);
    if (historyStartEl && historyStartEl.classList) {
        historyStartEl.classList.toggle('hidden', !showHistoryStart);
    }
}

/**
 * Prepends older keyset-paginated messages while preserving the caller's scroll offset.
 * @param {HTMLElement|null} containerEl
 * @param {HTMLElement|null} streamEl
 * @param {Array<object>} messages
 * @param {string|null} currentUserId
 * @param {object|null} [peerWatermarks]
 * @returns {number}
 */
export function prependOlderMessages(containerEl, streamEl, messages, currentUserId, peerWatermarks = null) {
    if (!containerEl || !Array.isArray(messages) || messages.length === 0 || typeof document === 'undefined') {
        return 0;
    }

    const prevScrollHeight = streamEl ? streamEl.scrollHeight : 0;
    const prevScrollTop = streamEl ? streamEl.scrollTop : 0;

    const sorted = [...messages].sort(compareMessages);
    const frag = typeof document.createDocumentFragment === 'function'
        ? document.createDocumentFragment()
        : null;

    let added = 0;
    const addedNodes = [];
    for (const msg of sorted) {
        const msgId = getMessageId(msg);
        if (!msgId || isMessageRendered(msgId)) continue;
        const el = createMessageBubbleElement(msg, currentUserId, peerWatermarks);
        if (el && frag) {
            frag.appendChild(el);
            addedNodes.push(el);
            added++;
        }
    }

    if (added > 0 && frag) {
        if (containerEl.firstChild && typeof containerEl.insertBefore === 'function') {
            containerEl.insertBefore(frag, containerEl.firstChild);
        } else if (typeof containerEl.appendChild === 'function') {
            containerEl.appendChild(frag);
        }

        for (const node of addedNodes) {
            hydrateMediaElements(node);
            hydrateStickerImages(node);
        }

        if (streamEl) {
            streamEl.scrollTop = prevScrollTop + (streamEl.scrollHeight - prevScrollHeight);
        }
    }

    return added;
}

/**
 * Clears peer identity, handle, initials, bio, and status badges from the active conversation header
 * so stale peer metadata never remains visible while loading or displaying an error for another chat.
 */
export function clearConversationHeader() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;

    const headerEl = document.getElementById('active-conversation-header');
    if (headerEl) {
        if (typeof headerEl.setAttribute === 'function') {
            headerEl.setAttribute('data-peer-user-id', '');
        }
        const avatarSlot = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-avatar-slot]') : null;
        if (avatarSlot) {
            avatarSlot.setAttribute('data-avatar-user-id', '');
            avatarSlot.setAttribute('data-avatar-version', '');
            avatarSlot.setAttribute('data-avatar-name', '');
            avatarSlot.setAttribute('data-avatar-handle', '');
            hydrateAvatarSlot(avatarSlot);
        }
        const initialsEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-initials]') : null;
        if (initialsEl) initialsEl.textContent = '';

        const nameEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-name]') : null;
        if (nameEl) nameEl.textContent = '';

        const handleEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-handle]') : null;
        if (handleEl) handleEl.textContent = '';

        const bioEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-bio]') : null;
        if (bioEl) {
            bioEl.textContent = '';
            if (bioEl.classList) bioEl.classList.add('hidden');
        }

        const bioSep = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-bio-sep]') : null;
        if (bioSep && bioSep.classList) bioSep.classList.add('hidden');

        if (typeof headerEl.querySelector === 'function') {
            for (const selector of ['[data-peer-pinned-badge]', '[data-peer-muted-badge]', '[data-peer-blocked-badge]']) {
                const badge = headerEl.querySelector(selector);
                if (badge && badge.classList) badge.classList.add('hidden');
            }
        }

        const typingEl = document.getElementById('typing-indicator');
        if (typingEl) {
            typingEl.textContent = '';
            if (typingEl.classList) typingEl.classList.add('hidden');
        }
    }

    const emptyPromptEl = typeof document.querySelector === 'function'
        ? document.querySelector('[data-empty-state-peer-prompt]')
        : null;
    if (emptyPromptEl) {
        emptyPromptEl.textContent = 'Send a message below to start the conversation.';
    }
}

/**
 * Disables all 10 desktop & mobile header action forms, clears their hidden conversationId,
 * and scrubs stale peer identity from the header before awaiting a switch.
 */
export function lockAndClearHeaderActions() {
    closeChatActionsMenu();
    clearConversationHeader();
    if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;

    const forms = document.querySelectorAll('form[data-header-action-form]');
    for (const form of forms) {
        const convInput = form.querySelector('input[name="conversationId"]');
        if (convInput) convInput.value = '';
        const submitBtn = form.querySelector('button[type="submit"]');
        if (submitBtn) submitBtn.disabled = true;
    }
}

function updateHeaderActionForms(convId, isPinned, isMuted, isArchived = false) {
    if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;

    const forms = document.querySelectorAll('form[data-header-action-form]');
    for (const form of forms) {
        const actionType = form.getAttribute('data-header-action-form');
        const convInput = form.querySelector('input[name="conversationId"]');
        if (convInput) convInput.value = convId;

        const submitBtn = form.querySelector('button[type="submit"]');
        if (submitBtn) {
            submitBtn.disabled = !convId;
        }

        if (actionType === 'pin') {
            const pinInput = form.querySelector('input[name="isPinned"]');
            if (pinInput) pinInput.value = isPinned ? 'false' : 'true';
            const icon = form.querySelector('.material-symbols-outlined');
            if (icon) icon.textContent = isPinned ? 'keep_off' : 'push_pin';
            const label = isPinned ? 'Unpin conversation' : 'Pin conversation';
            if (submitBtn && submitBtn.getAttribute('title')) {
                submitBtn.setAttribute('title', label);
            }
            const spans = form.querySelectorAll('span');
            if (spans.length > 1) {
                spans[spans.length - 1].textContent = label;
            }
        } else if (actionType === 'mute') {
            const muteInput = form.querySelector('input[name="isMuted"]');
            if (muteInput) muteInput.value = isMuted ? 'false' : 'true';
            const icon = form.querySelector('.material-symbols-outlined');
            if (icon) icon.textContent = isMuted ? 'volume_up' : 'volume_off';
            const label = isMuted ? 'Unmute conversation' : 'Mute conversation';
            if (submitBtn && submitBtn.getAttribute('title')) {
                submitBtn.setAttribute('title', label);
            }
            const spans = form.querySelectorAll('span');
            if (spans.length > 1) {
                spans[spans.length - 1].textContent = label;
            }
        } else if (actionType === 'archive') {
            const archiveInput = form.querySelector('input[name="isArchived"]');
            if (archiveInput) archiveInput.value = isArchived ? 'false' : 'true';
            const icon = form.querySelector('.material-symbols-outlined');
            if (icon) icon.textContent = isArchived ? 'unarchive' : 'archive';
            const label = isArchived ? 'Unarchive conversation' : 'Archive conversation';
            if (submitBtn && submitBtn.getAttribute('title')) {
                submitBtn.setAttribute('title', label);
            }
            const spans = form.querySelectorAll('span');
            if (spans.length > 1) {
                spans[spans.length - 1].textContent = label;
            }
        }
    }
}

/**
 * Updates the active conversation header, presence state, badges, and all 10 desktop/mobile action forms.
 * @param {object} meta
 */
export function renderConversationHeader(meta) {
    if (!meta || typeof document === 'undefined') return;

    const convId = normalizeIdValue(meta.id ?? meta.Id);
    const peerId = normalizeIdValue(meta.otherParticipantId ?? meta.OtherParticipantId);
    const peerName = meta.otherParticipantName ?? meta.OtherParticipantName ?? 'Unknown';
    const peerHandle = meta.otherParticipantHandle ?? meta.OtherParticipantHandle ?? '';
    const peerBio = meta.otherParticipantBio ?? meta.OtherParticipantBio ?? '';
    const isBlocked = !!(meta.isBlocked ?? meta.IsBlocked);
    const isPeerDeleted = !!(meta.isPeerDeleted ?? meta.IsPeerDeleted);
    const peerAvatarVersion = (isBlocked || isPeerDeleted)
        ? ''
        : (meta.otherParticipantAvatarVersion ?? meta.OtherParticipantAvatarVersion ?? '');
    const isPinned = !!(meta.isPinned ?? meta.IsPinned);
    const isMuted = !!(meta.isMuted ?? meta.IsMuted);
    const isArchived = !!(meta.isArchived ?? meta.IsArchived);
    const isOnline = !!(meta.isOnline ?? meta.IsOnline);
    const lastSeen = meta.lastSeenAtUtc ?? meta.LastSeenAtUtc ?? null;

    if (isBlocked || isPeerDeleted || !peerAvatarVersion) {
        evictUserAvatarCache(peerId);
        closeAvatarLightboxIfUser(peerId);
    }

    const headerEl = document.getElementById('active-conversation-header');
    if (headerEl) {
        headerEl.setAttribute('data-peer-user-id', peerId);

        const avatarSlot = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-avatar-slot]') : null;
        if (avatarSlot) {
            avatarSlot.setAttribute('data-avatar-user-id', peerId);
            avatarSlot.setAttribute('data-avatar-version', peerAvatarVersion ? String(peerAvatarVersion) : '');
            avatarSlot.setAttribute('data-avatar-name', peerName);
            avatarSlot.setAttribute('data-avatar-handle', peerHandle);
            hydrateAvatarSlot(avatarSlot);
        }

        const initialsEl = headerEl.querySelector('[data-peer-initials]');
        if (initialsEl) initialsEl.textContent = getInitials(peerName, peerHandle);

        const nameEl = headerEl.querySelector('[data-peer-name]');
        if (nameEl) nameEl.textContent = peerName;

        const handleEl = headerEl.querySelector('[data-peer-handle]');
        if (handleEl) handleEl.textContent = peerHandle ? `@${peerHandle}` : '';

        const pinnedBadge = headerEl.querySelector('[data-peer-pinned-badge]');
        if (pinnedBadge) pinnedBadge.classList.toggle('hidden', !isPinned);

        const mutedBadge = headerEl.querySelector('[data-peer-muted-badge]');
        if (mutedBadge) mutedBadge.classList.toggle('hidden', !isMuted);

        const blockedBadge = headerEl.querySelector('[data-peer-blocked-badge]');
        if (blockedBadge) blockedBadge.classList.toggle('hidden', !isBlocked);

        const bioSep = headerEl.querySelector('[data-peer-bio-sep]');
        const bioEl = headerEl.querySelector('[data-peer-bio]');
        const hasBio = !!(peerBio && String(peerBio).trim());
        if (bioSep) bioSep.classList.toggle('hidden', !hasBio);
        if (bioEl) {
            bioEl.textContent = hasBio ? String(peerBio) : '';
            bioEl.classList.toggle('hidden', !hasBio);
        }

        const dotEl = document.getElementById('header-presence-dot');
        if (dotEl) {
            dotEl.className = isOnline
                ? 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-[#6F9473] border-2 border-[#FFFFFF] dark:border-[#3A3835]'
                : 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-stone-400 dark:bg-stone-500 border-2 border-[#FFFFFF] dark:border-[#3A3835]';
        }

        const presenceTextEl = document.getElementById('header-presence-text');
        if (presenceTextEl) {
            if (typeof presenceTextEl.setAttribute === 'function') {
                presenceTextEl.setAttribute('data-presence-online', isOnline ? 'true' : 'false');
                presenceTextEl.setAttribute('data-last-seen-at', !isOnline && lastSeen ? String(lastSeen) : '');
            }
            presenceTextEl.textContent = isOnline ? 'Online' : formatLastSeen(lastSeen);
        }

        const typingEl = document.getElementById('typing-indicator');
        if (typingEl) {
            typingEl.textContent = '';
            typingEl.classList.add('hidden');
        }
    }

    const emptyPromptEl = document.querySelector('[data-empty-state-peer-prompt]');
    if (emptyPromptEl) {
        emptyPromptEl.textContent = `Send a message below to start the conversation with ${peerName}.`;
    }

    updateHeaderActionForms(convId, isPinned, isMuted, isArchived);

    const announcerEl = document.getElementById('chat-live-announcer');
    if (announcerEl) {
        announcerEl.textContent = `Switched to conversation with ${peerName}`;
    }
}

/**
 * Clears active message pane DOM elements without destroying shell structure.
 */
export function clearPane() {
    disconnectActiveMediaObservers();
    if (typeof document === 'undefined') return;
    const containerEl = document.getElementById('messages-container');
    teardownRowVideos(containerEl);
    clearElementChildren(containerEl);
    if (containerEl) {
        containerEl.setAttribute('data-conversation-id', '');
        containerEl.setAttribute('data-peer-user-id', '');
        containerEl.setAttribute('data-peer-is-online', 'false');
        containerEl.setAttribute('data-peer-last-seen', '');
        containerEl.setAttribute('data-peer-last-delivered-id', '');
        containerEl.setAttribute('data-peer-last-read-id', '');
        containerEl.setAttribute('data-next-cursor', '');
        containerEl.setAttribute('data-has-more', 'false');
    }
    const emptyPlaceholder = document.getElementById('no-messages-placeholder');
    if (emptyPlaceholder) emptyPlaceholder.classList.add('hidden');
    const loadingTop = document.getElementById('messages-loading-top');
    if (loadingTop) loadingTop.classList.add('hidden');
    const historyStart = document.getElementById('messages-history-start');
    if (historyStart) historyStart.classList.add('hidden');
    const loadOlderWrap = document.getElementById('messages-load-older-wrap');
    if (loadOlderWrap) loadOlderWrap.classList.add('hidden');
    const loadOlderError = document.getElementById('messages-load-older-error');
    if (loadOlderError) loadOlderError.classList.add('hidden');
    const loadOlderErrorText = document.getElementById('messages-load-older-error-text');
    if (loadOlderErrorText) loadOlderErrorText.textContent = 'Could not load older messages.';
    const skeletonEl = document.getElementById('chat-loading-skeleton');
    if (skeletonEl) skeletonEl.classList.add('hidden');
    hideSafeError();
}

export function showEmptyState() {
    if (typeof document === 'undefined') return;
    const emptyEl = document.getElementById('chat-empty-state');
    const activeEl = document.getElementById('chat-active-pane');
    if (emptyEl) emptyEl.classList.remove('hidden');
    if (activeEl) activeEl.classList.add('hidden');
}

export function showActivePane() {
    if (typeof document === 'undefined') return;
    const emptyEl = document.getElementById('chat-empty-state');
    const activeEl = document.getElementById('chat-active-pane');
    if (emptyEl) emptyEl.classList.add('hidden');
    if (activeEl) activeEl.classList.remove('hidden');
}

export function showLoadingSkeleton() {
    if (typeof document === 'undefined') return;
    clearPane();
    const skeletonEl = document.getElementById('chat-loading-skeleton');
    if (skeletonEl) skeletonEl.classList.remove('hidden');
}

export function hideSafeError() {
    if (typeof document === 'undefined') return;
    const errEl = document.getElementById('chat-error-banner');
    if (errEl) {
        errEl.textContent = '';
        errEl.classList.add('hidden');
    }
}

export function showSafeError(message) {
    if (typeof document === 'undefined') return;
    clearPane();
    const errEl = document.getElementById('chat-error-banner');
    if (errEl) {
        errEl.textContent = message || 'Could not load conversation. Try again.';
        errEl.classList.remove('hidden');
    }
}

/**
 * Immediately purges all sensitive private data from the DOM before session-expiry redirect.
 */
export function wipeSensitiveDom() {
    if (typeof document === 'undefined') return;

    if (voicePlayerCoordinator && typeof voicePlayerCoordinator.stopAllPlayback === 'function') {
        voicePlayerCoordinator.stopAllPlayback();
    }
    clearMediaUrlCache();
    clearAvatarUrlCache();
    clearStickerUrlCache();
    clearPane();
    lockAndClearHeaderActions();

    const headerEl = document.getElementById('active-conversation-header');
    if (headerEl) {
        headerEl.setAttribute('data-peer-user-id', '');
        const initialsEl = headerEl.querySelector('[data-peer-initials]');
        if (initialsEl) initialsEl.textContent = '';
        const nameEl = headerEl.querySelector('[data-peer-name]');
        if (nameEl) nameEl.textContent = '';
        const handleEl = headerEl.querySelector('[data-peer-handle]');
        if (handleEl) handleEl.textContent = '';
        const bioEl = headerEl.querySelector('[data-peer-bio]');
        if (bioEl) {
            bioEl.textContent = '';
            bioEl.classList.add('hidden');
        }
        const bioSep = headerEl.querySelector('[data-peer-bio-sep]');
        if (bioSep) bioSep.classList.add('hidden');
        for (const selector of ['[data-peer-pinned-badge]', '[data-peer-muted-badge]', '[data-peer-blocked-badge]']) {
            const badge = headerEl.querySelector(selector);
            if (badge) badge.classList.add('hidden');
        }
        const presenceText = document.getElementById('header-presence-text');
        if (presenceText) presenceText.textContent = '';
        const typingEl = document.getElementById('typing-indicator');
        if (typingEl) {
            typingEl.textContent = '';
            typingEl.classList.add('hidden');
        }
    }

    const inputEl = document.getElementById('message-content-input');
    if (inputEl) {
        inputEl.value = '';
        inputEl.disabled = true;
    }
    const sendBtn = document.getElementById('send-message-btn');
    if (sendBtn) sendBtn.disabled = true;
    const attachBtn = document.getElementById('composer-attach-btn');
    if (attachBtn) attachBtn.disabled = true;
    const voiceBtn = document.getElementById('composer-voice-btn');
    if (voiceBtn) voiceBtn.disabled = true;
    const voiceDock = document.getElementById('composer-voice-dock');
    if (voiceDock && voiceDock.classList) voiceDock.classList.add('hidden');
    const previewAudio = document.getElementById('voice-preview-audio');
    if (previewAudio) {
        try {
            if (typeof previewAudio.pause === 'function') previewAudio.pause();
            if (typeof previewAudio.removeAttribute === 'function') previewAudio.removeAttribute('src');
        } catch {}
    }

    const replyBanner = document.getElementById('composer-reply-banner');
    if (replyBanner) replyBanner.classList.add('hidden');
    const replySender = document.getElementById('reply-banner-sender');
    if (replySender) replySender.textContent = '';
    const replyPreview = document.getElementById('reply-banner-preview');
    if (replyPreview) replyPreview.textContent = '';

    for (const id of ['chat-policy-notice', 'chat-action-error', 'edit-message-modal', 'delete-message-modal', 'mobile-message-action-sheet', 'reaction-picker-popover', 'composer-attachments-tray', 'composer-attach-menu', 'chat-dropzone-overlay', 'media-lightbox-modal', 'sticker-info-modal', 'sticker-creator-modal', 'sticker-confirm-modal']) {
        const el = document.getElementById(id);
        if (el) {
            el.classList.add('hidden');
            if (id === 'chat-policy-notice' || id === 'chat-action-error') {
                el.textContent = '';
            }
            if (id === 'composer-attachments-tray') {
                clearElementChildren(el);
            }
        }
    }

    const pendingContainer = document.getElementById('pending-uploads-container');
    if (pendingContainer) {
        clearElementChildren(pendingContainer);
    }

    const historyStartEl = document.getElementById('messages-history-start');
    if (historyStartEl) historyStartEl.classList.add('hidden');
    const loadOlderWrapEl = document.getElementById('messages-load-older-wrap');
    if (loadOlderWrapEl) loadOlderWrapEl.classList.add('hidden');
    const loadOlderErrEl = document.getElementById('messages-load-older-error');
    if (loadOlderErrEl) loadOlderErrEl.classList.add('hidden');
    const loadOlderErrTextEl = document.getElementById('messages-load-older-error-text');
    if (loadOlderErrTextEl) loadOlderErrTextEl.textContent = '';

    const stickerInfoImg = document.getElementById('sticker-info-img');
    if (stickerInfoImg && typeof stickerInfoImg.removeAttribute === 'function') {
        stickerInfoImg.removeAttribute('src');
    }

    const lightboxImg = document.getElementById('media-lightbox-img');
    if (lightboxImg) {
        if (typeof lightboxImg.removeAttribute === 'function') lightboxImg.removeAttribute('src');
        lightboxImg.alt = '';
    }
    const lightboxTitle = document.getElementById('media-lightbox-title');
    if (lightboxTitle) lightboxTitle.textContent = '';
    const lightboxCounter = document.getElementById('media-lightbox-counter');
    if (lightboxCounter) {
        lightboxCounter.textContent = '';
        lightboxCounter.classList.add('hidden');
    }
    const lightboxErr = document.getElementById('media-lightbox-error');
    if (lightboxErr) lightboxErr.classList.add('hidden');

    const editInput = document.getElementById('edit-message-input');
    if (editInput) editInput.value = '';

    const sidebarList = document.getElementById('conversationList');
    if (sidebarList) {
        clearElementChildren(sidebarList);
    }

    const announcerEl = document.getElementById('chat-live-announcer');
    if (announcerEl) announcerEl.textContent = '';

    if (typeof document.title === 'string') {
        document.title = 'A♡S Messenger';
    }
}
