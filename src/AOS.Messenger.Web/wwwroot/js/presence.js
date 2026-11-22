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

/**
 * Formats an ISO-8601 UTC timestamp to a user-friendly relative time string.
 * Strictly returns a plain string, never HTML.
 *
 * @param {string|Date|null} lastSeenAtUtc
 * @returns {string} Formatted string, e.g. "Last seen just now", "Last seen today at 6:42 PM", "Offline"
 */
export function formatLastSeen(lastSeenAtUtc) {
    if (!lastSeenAtUtc) return 'Offline';

    const d = new Date(lastSeenAtUtc);
    if (isNaN(d.getTime())) return 'Offline';

    const now = new Date();
    const diffMs = now.getTime() - d.getTime();
    const diffSec = Math.floor(diffMs / 1000);

    if (diffSec >= -5 && diffSec < 60) {
        return 'Last seen just now';
    }

    const isToday = d.getFullYear() === now.getFullYear() &&
                    d.getMonth() === now.getMonth() &&
                    d.getDate() === now.getDate();

    const yesterday = new Date(now);
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

    if (d.getFullYear() === now.getFullYear()) {
        const dateStr = d.toLocaleDateString([], { month: 'short', day: 'numeric' });
        return `Last seen ${dateStr} at ${timeStr}`;
    }

    const fullDateStr = d.toLocaleDateString([], { month: 'short', day: 'numeric', year: 'numeric' });
    return `Last seen ${fullDateStr}`;
}

/**
 * Updates the active conversation header presence indicator and subtitle.
 *
 * @param {HTMLElement} headerElement
 * @param {string} peerUserId
 * @param {boolean} isOnline
 * @param {string|null} lastSeenAtUtc
 */
function updateHeaderPresence(headerElement, peerUserId, isOnline, lastSeenAtUtc) {
    if (!headerElement || !peerUserId) return;

    const currentPeerId = headerElement.getAttribute('data-peer-user-id');
    if (currentPeerId && String(currentPeerId).toLowerCase() !== String(peerUserId).toLowerCase()) {
        return;
    }

    const dot = headerElement.querySelector('#header-presence-dot') ||
                headerElement.querySelector('[data-presence-dot]');
    const text = headerElement.querySelector('#header-presence-text') ||
                 headerElement.querySelector('[data-presence-text]');

    if (dot) {
        if (isOnline) {
            dot.className = 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-[#6F9473] border-2 border-[#FFFFFF] dark:border-[#3A3835]';
            dot.classList.remove('hidden');
        } else {
            dot.className = 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-stone-400 dark:bg-stone-500 border-2 border-[#FFFFFF] dark:border-[#3A3835]';
        }
    }

    if (text) {
        text.textContent = isOnline ? 'Online' : formatLastSeen(lastSeenAtUtc);
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
        const token = typeof document !== 'undefined'
            ? document.querySelector('input[name="__RequestVerificationToken"]')?.value || ''
            : '';
        const headers = { 'Accept': 'application/json' };
        if (token) {
            headers['RequestVerificationToken'] = token;
        }

        const resp = await fetch('/api/presence', {
            headers: headers
        });

        if (!resp.ok) return;

        const data = await resp.json();
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
export function setupPresence(connection, conversationListElement, activeHeaderElement) {
    function handlePresenceChanged(presence) {
        if (!presence) return;
        const uId = presence.userId || presence.UserId;
        if (!uId) return;

        const isOnline = (presence.isOnline !== undefined) ? presence.isOnline : presence.IsOnline;
        const lastSeen = presence.lastSeenAtUtc || presence.LastSeenAtUtc;

        updateHeaderPresence(activeHeaderElement, uId, !!isOnline, lastSeen);
        updateSidebarPresence(conversationListElement, uId, !!isOnline);
    }

    if (connection && typeof connection.on === 'function') {
        connection.on('ReceivePresenceChanged', handlePresenceChanged);
    }

    // Initial presence fetch from REST API
    fetchPresence(conversationListElement, activeHeaderElement);

    return {
        updateHeaderPresence: (uId, isOnline, lastSeen) => updateHeaderPresence(activeHeaderElement, uId, isOnline, lastSeen),
        updateSidebarPresence: (uId, isOnline) => updateSidebarPresence(conversationListElement, uId, isOnline),
        fetchPresence: () => fetchPresence(conversationListElement, activeHeaderElement),
        formatLastSeen,
        destroy: () => {
            if (connection && typeof connection.off === 'function') {
                connection.off('ReceivePresenceChanged', handlePresenceChanged);
            }
        }
    };
}

const presenceApi = {
    setupPresence,
    formatLastSeen,
    fetchPresence
};

if (typeof window !== 'undefined') {
    window.Presence = presenceApi;
}
if (typeof globalThis !== 'undefined') {
    globalThis.Presence = presenceApi;
}
