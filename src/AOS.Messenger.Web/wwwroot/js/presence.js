/**
 * AOS Messenger - Presence & Last Seen Module
 *
 * Implements client-side presence state tracking, relative last seen formatting,
 * and live presence event synchronization.
 *
 * Invariants (AGENTS.md):
 * - Safe DOM tree construction exclusively - zero innerHTML/outerHTML/insertAdjacentHTML.
 * - All text content bound strictly via textContent or createTextNode.
 * - Handles hidden/blocked peer presence gracefully.
 */

import { getPresence } from './api-client.js';

/**
 * Formats an ISO-8601 UTC timestamp to a user-friendly relative time string.
 * Strictly returns a plain string, never HTML.
 *
 * @param {string|Date|null} lastSeenAtUtc
 * @param {Date} [now]
 * @returns {string} Formatted string, e.g. "Last seen just now", "Last seen today at 6:42 PM", "Offline"
 */
export function formatLastSeen(lastSeenAtUtc, now = new Date()) {
    if (!lastSeenAtUtc) return 'Offline';

    const d = new Date(lastSeenAtUtc);
    if (isNaN(d.getTime())) return 'Offline';

    const refNow = now instanceof Date && !isNaN(now.getTime()) ? now : new Date();
    const diffMs = refNow.getTime() - d.getTime();
    const diffSec = Math.floor(diffMs / 1000);

    if (diffSec >= -5 && diffSec < 60) {
        return 'Last seen just now';
    }

    const isToday = d.getFullYear() === refNow.getFullYear() &&
                    d.getMonth() === refNow.getMonth() &&
                    d.getDate() === refNow.getDate();

    const yesterday = new Date(refNow);
    yesterday.setDate(yesterday.getDate() - 1);
    const isYesterday = d.getFullYear() === yesterday.getFullYear() &&
                        d.getMonth() === yesterday.getMonth() &&
                        d.getDate() === yesterday.getDate();

    const timeStr = d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', hour12: true });

    if (isToday) {
        return `Last seen today at ${timeStr}`;
    }

    if (isYesterday) {
        return `Last seen yesterday at ${timeStr}`;
    }

    if (d.getFullYear() === refNow.getFullYear()) {
        const dateStr = d.toLocaleDateString([], { month: 'short', day: 'numeric' });
        return `Last seen ${dateStr} at ${timeStr}`;
    }

    const fullDateStr = d.toLocaleDateString([], { month: 'short', day: 'numeric', year: 'numeric' });
    return `Last seen ${fullDateStr}`;
}

/**
 * Computes milliseconds until the formatted relative last-seen text will next change
 * ("Last seen just now" -> "Last seen today at ...", or midnight day boundary rollover).
 * Returns null if no further relative transition is pending or timestamp is invalid.
 *
 * @param {string|Date|null} lastSeenAtUtc
 * @param {Date} [now]
 * @returns {number|null}
 */
export function computeNextLastSeenRefreshDelayMs(lastSeenAtUtc, now = new Date()) {
    if (!lastSeenAtUtc) return null;

    const d = new Date(lastSeenAtUtc);
    if (isNaN(d.getTime())) return null;

    const refNow = now instanceof Date && !isNaN(now.getTime()) ? now : new Date();
    const diffMs = refNow.getTime() - d.getTime();
    const diffSec = Math.floor(diffMs / 1000);

    if (diffSec < 60) {
        // Re-check right after the 60-second "just now" window expires
        const msUntilMinute = (60 * 1000) - diffMs;
        return Math.max(250, Math.min(65 * 1000, msUntilMinute + 250));
    }

    // Past-year timestamps format as "Last seen MMM D, YYYY" and never change again
    if (d.getFullYear() < refNow.getFullYear()) {
        return null;
    }

    // Re-check at the next local midnight (capped at 1 hour to stay resilient to sleep/DST)
    const nextMidnight = new Date(refNow.getFullYear(), refNow.getMonth(), refNow.getDate() + 1, 0, 0, 0, 250);
    const msUntilMidnight = nextMidnight.getTime() - refNow.getTime();
    return Math.max(1000, Math.min(60 * 60 * 1000, msUntilMidnight));
}

function getHeaderPresenceTextElement(headerElement) {
    if (!headerElement || typeof headerElement.querySelector !== 'function') return null;
    return headerElement.querySelector('#header-presence-text') ||
           headerElement.querySelector('[data-presence-text]');
}

/**
 * Re-formats the active header's stored last-seen timestamp using the current clock.
 * Returns the active offline lastSeenAtUtc string if one is present, otherwise null.
 *
 * @param {HTMLElement|null} headerElement
 * @param {Date} [now]
 * @returns {string|null}
 */
export function refreshHeaderLastSeen(headerElement, now = new Date()) {
    const text = getHeaderPresenceTextElement(headerElement);
    if (!text || typeof text.getAttribute !== 'function') return null;

    if (text.getAttribute('data-presence-online') === 'true') {
        return null;
    }

    const lastSeenAtUtc = text.getAttribute('data-last-seen-at') || '';
    if (!lastSeenAtUtc) {
        return null;
    }

    const formatted = formatLastSeen(lastSeenAtUtc, now);
    if (text.textContent !== formatted) {
        text.textContent = formatted;
    }
    return lastSeenAtUtc;
}

/**
 * Updates the active conversation header presence indicator and subtitle.
 *
 * @param {HTMLElement} headerElement
 * @param {string} peerUserId
 * @param {boolean} isOnline
 * @param {string|null} lastSeenAtUtc
 * @param {Date} [now]
 */
function updateHeaderPresence(headerElement, peerUserId, isOnline, lastSeenAtUtc, now = new Date()) {
    if (!headerElement || !peerUserId) return;

    const currentPeerId = headerElement.getAttribute('data-peer-user-id');
    const normalizedCurrentPeerId = currentPeerId ? String(currentPeerId).trim().toLowerCase() : '';
    const normalizedPeerId = String(peerUserId).trim().toLowerCase();
    if (!normalizedCurrentPeerId || normalizedCurrentPeerId !== normalizedPeerId) {
        return;
    }

    const dot = headerElement.querySelector('#header-presence-dot') ||
                headerElement.querySelector('[data-presence-dot]');
    const text = getHeaderPresenceTextElement(headerElement);

    if (dot) {
        if (isOnline) {
            dot.className = 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-[#6F9473] border-2 border-[#FFFFFF] dark:border-[#3A3835]';
            dot.classList.remove('hidden');
        } else {
            dot.className = 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-stone-400 dark:bg-stone-500 border-2 border-[#FFFFFF] dark:border-[#3A3835]';
        }
    }

    if (text) {
        if (typeof text.setAttribute === 'function') {
            text.setAttribute('data-presence-online', isOnline ? 'true' : 'false');
            text.setAttribute('data-last-seen-at', !isOnline && lastSeenAtUtc ? String(lastSeenAtUtc) : '');
        }
        text.textContent = isOnline ? 'Online' : formatLastSeen(lastSeenAtUtc, now);
    }
}

/**
 * Updates a conversation list item's presence dot in the sidebar.
 *
 * @param {HTMLElement} listElement
 * @param {string} peerUserId
 * @param {boolean} isOnline
 */
function updateSidebarPresence(listElement, peerUserId, isOnline) {
    if (!listElement || !peerUserId) return;
    const normalizedUserId = String(peerUserId).toLowerCase();

    const items = listElement.querySelectorAll('.conversation-item');
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        const itemPeerId = item.getAttribute('data-peer-user-id');
        if (itemPeerId && String(itemPeerId).toLowerCase() === normalizedUserId) {
            let dot = item.querySelector('.presence-indicator-dot') || item.querySelector('[data-presence-dot]');
            if (!dot) {
                const avatarContainer = item.querySelector('.relative');
                if (avatarContainer) {
                    dot = document.createElement('span');
                    dot.className = 'presence-indicator-dot absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full border-2 border-[#F0E9DF] dark:border-[#3A3835]';
                    avatarContainer.appendChild(dot);
                }
            }

            if (dot) {
                if (isOnline) {
                    dot.className = 'presence-indicator-dot absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-[#6F9473] border-2 border-[#F0E9DF] dark:border-[#3A3835]';
                } else {
                    dot.className = 'presence-indicator-dot absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-stone-400 dark:bg-stone-500 border-2 border-[#F0E9DF] dark:border-[#3A3835]';
                }
            }
        }
    }
}

/**
 * Fetches presence from the REST API endpoint and applies it to sidebar and header.
 *
 * @param {HTMLElement} conversationListElement
 * @param {HTMLElement} activeHeaderElement
 * @returns {Promise<void>}
 */
export async function fetchPresence(conversationListElement, activeHeaderElement) {
    try {
        const data = await getPresence();
        if (!data) return;

        if (Array.isArray(data)) {
            for (const item of data) {
                const uId = item.userId || item.UserId;
                const online = (item.isOnline !== undefined) ? item.isOnline : item.IsOnline;
                const lastSeen = item.lastSeenAtUtc || item.LastSeenAtUtc;
                if (uId) {
                    updateHeaderPresence(activeHeaderElement, uId, !!online, lastSeen);
                    updateSidebarPresence(conversationListElement, uId, !!online);
                }
            }
        } else if (typeof data === 'object') {
            for (const [convId, item] of Object.entries(data)) {
                if (!item) continue;
                const uId = item.userId || item.UserId;
                const online = (item.isOnline !== undefined) ? item.isOnline : item.IsOnline;
                const lastSeen = item.lastSeenAtUtc || item.LastSeenAtUtc;
                if (uId) {
                    updateHeaderPresence(activeHeaderElement, uId, !!online, lastSeen);
                    if (conversationListElement) {
                        const itemEl = conversationListElement.querySelector(`.conversation-item[data-conversation-id="${String(convId).toLowerCase()}"]`);
                        if (itemEl) {
                            itemEl.setAttribute('data-peer-user-id', uId);
                        }
                    }
                    updateSidebarPresence(conversationListElement, uId, !!online);
                }
            }
        }
    } catch (err) {
        console.warn('[Presence] Failed to fetch presence state:', err);
    }
}

/**
 * Initializes presence tracking on the SignalR connection and elements.
 *
 * @param {object} connection - SignalR HubConnection
 * @param {HTMLElement} conversationListElement
 * @param {HTMLElement} activeHeaderElement
 * @returns {object} Presence controller
 */
export function resetHeaderPresence(headerElement) {
    if (!headerElement) return;
    headerElement.setAttribute('data-peer-user-id', '');

    const dot = headerElement.querySelector('#header-presence-dot') ||
                headerElement.querySelector('[data-presence-dot]');
    if (dot) {
        dot.className = 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-stone-400 dark:bg-stone-500 border-2 border-[#FFFFFF] dark:border-[#3A3835]';
    }

    const text = getHeaderPresenceTextElement(headerElement);
    if (text) {
        if (typeof text.setAttribute === 'function') {
            text.setAttribute('data-presence-online', 'false');
            text.setAttribute('data-last-seen-at', '');
        }
        text.textContent = 'Offline';
    }
}

/**
 * Initializes presence tracking on the SignalR connection and elements.
 *
 * @param {object} connection - SignalR HubConnection
 * @param {HTMLElement} conversationListElement
 * @param {HTMLElement} activeHeaderElement
 * @param {{ skipInitialFetch?: boolean, nowFn?: () => Date }} [options]
 * @returns {object} Presence controller
 */
export function setupPresence(connection, conversationListElement, activeHeaderElement, options = {}) {
    const getNow = typeof options?.nowFn === 'function' ? options.nowFn : () => new Date();
    let refreshTimer = null;
    let destroyed = false;

    function clearRefreshTimer() {
        if (refreshTimer !== null) {
            clearTimeout(refreshTimer);
            refreshTimer = null;
        }
    }

    function syncAndScheduleHeaderLastSeen() {
        clearRefreshTimer();
        if (destroyed || !activeHeaderElement) return;

        const now = getNow();
        const activeLastSeen = refreshHeaderLastSeen(activeHeaderElement, now);
        if (!activeLastSeen) return;

        const delayMs = computeNextLastSeenRefreshDelayMs(activeLastSeen, now);
        if (delayMs === null) return;

        refreshTimer = setTimeout(() => {
            refreshTimer = null;
            syncAndScheduleHeaderLastSeen();
        }, delayMs);
        if (refreshTimer && typeof refreshTimer.unref === 'function') {
            refreshTimer.unref();
        }
    }

    function handlePresenceChanged(presence) {
        if (!presence || destroyed) return;
        const uId = presence.userId || presence.UserId;
        if (!uId) return;

        const isOnline = (presence.isOnline !== undefined) ? presence.isOnline : presence.IsOnline;
        const lastSeen = presence.lastSeenAtUtc || presence.LastSeenAtUtc;

        updateHeaderPresence(activeHeaderElement, uId, !!isOnline, lastSeen, getNow());
        updateSidebarPresence(conversationListElement, uId, !!isOnline);
        syncAndScheduleHeaderLastSeen();
    }

    const onVisibilityChange = () => {
        if (destroyed || typeof document === 'undefined') return;
        if (document.visibilityState === 'visible') {
            syncAndScheduleHeaderLastSeen();
        }
    };

    if (connection && typeof connection.on === 'function') {
        connection.on('ReceivePresenceChanged', handlePresenceChanged);
    }

    if (activeHeaderElement && typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
        document.addEventListener('visibilitychange', onVisibilityChange);
    }

    // Initial presence fetch from REST API (skipped when caller defers initial fetch to SignalR startup)
    if (!options?.skipInitialFetch) {
        Promise.resolve(fetchPresence(conversationListElement, activeHeaderElement))
            .then(() => syncAndScheduleHeaderLastSeen());
    } else if (activeHeaderElement) {
        syncAndScheduleHeaderLastSeen();
    }

    return {
        handlePresenceChanged,
        updateHeaderPresence: (uId, isOnline, lastSeen) => {
            updateHeaderPresence(activeHeaderElement, uId, isOnline, lastSeen, getNow());
            syncAndScheduleHeaderLastSeen();
        },
        updateSidebarPresence: (uId, isOnline) => updateSidebarPresence(conversationListElement, uId, isOnline),
        fetchPresence: async () => {
            await fetchPresence(conversationListElement, activeHeaderElement);
            syncAndScheduleHeaderLastSeen();
        },
        resetHeaderPresence: () => {
            clearRefreshTimer();
            resetHeaderPresence(activeHeaderElement);
        },
        refreshHeaderLastSeen: () => syncAndScheduleHeaderLastSeen(),
        formatLastSeen,
        destroy: () => {
            destroyed = true;
            clearRefreshTimer();
            if (activeHeaderElement && typeof document !== 'undefined' && typeof document.removeEventListener === 'function') {
                document.removeEventListener('visibilitychange', onVisibilityChange);
            }
            if (connection && typeof connection.off === 'function') {
                connection.off('ReceivePresenceChanged', handlePresenceChanged);
            }
        }
    };
}

const presenceApi = {
    setupPresence,
    formatLastSeen,
    computeNextLastSeenRefreshDelayMs,
    refreshHeaderLastSeen,
    fetchPresence,
    resetHeaderPresence
};

if (typeof window !== 'undefined') {
    window.Presence = presenceApi;
}
if (typeof globalThis !== 'undefined') {
    globalThis.Presence = presenceApi;
}
