/**
 * AOS Messenger - Sidebar Conversation List Module
 *
 * Manages active row highlight & accessibility (aria-current), unread badges,
 * cross-tab BroadcastChannel read sync, dynamic document.title counters,
 * pinned-aware reordering, and authoritative REST sidebar reconciliation.
 *
 * Security & DOM Invariants:
 * - Pure DOM construction via document.createElement and .textContent only.
 * - Dynamic conversation items carry data-conversation-link and canonical /chat/{id} hrefs.
 */

import { getConversations } from './api-client.js';

const ACTIVE_ROW_CLASS = 'conversation-item group flex items-center gap-3 p-3 rounded-xl transition-all bg-[#FFFFFF] dark:bg-[#45423E] shadow-xs border border-[#E3DCD7] dark:border-[#514D48]';
const INACTIVE_ROW_CLASS = 'conversation-item group flex items-center gap-3 p-3 rounded-xl transition-all hover:bg-[#E3DCD7]/50 dark:hover:bg-[#45423E]/50 border border-transparent';

let baseDocumentTitle = null;
let sidebarBroadcastChannel = null;
let isSyncingSidebar = false;
let pendingSidebarSync = false;
let pendingSidebarActiveGetter = null;
let pendingSidebarSignal = undefined;

function normalizeId(val) {
    if (val === null || val === undefined) return '';
    if (typeof val === 'object' && val.value !== undefined) {
        return String(val.value);
    }
    return String(val);
}

function getInitials(name, handle) {
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

function getSidebarItems() {
    if (typeof document === 'undefined') return [];
    const listEl = typeof document.getElementById === 'function' ? document.getElementById('conversationList') : null;
    if (listEl && typeof listEl.querySelectorAll === 'function') {
        const scoped = listEl.querySelectorAll('.conversation-item');
        if (scoped && scoped.length > 0) return scoped;
    }
    if (typeof document.querySelectorAll === 'function') {
        return document.querySelectorAll('.conversation-item') || [];
    }
    return [];
}

/**
 * Finds a conversation sidebar item element by conversation ID (case-insensitive).
 * @param {string|object} convId
 * @returns {HTMLElement|null}
 */
export function findSidebarItem(convId) {
    if (!convId || typeof document === 'undefined') return null;
    const normalizedId = normalizeId(convId).trim();
    const lowerId = normalizedId.toLowerCase();
    if (!lowerId) return null;

    if (typeof document.querySelector === 'function') {
        const direct = document.querySelector(`.conversation-item[data-conversation-id="${normalizedId}"]`) ||
                       document.querySelector(`.conversation-item[data-conversation-id="${lowerId}"]`) ||
                       document.querySelector(`.conversation-item[href*="${normalizedId}"]`) ||
                       document.querySelector(`.conversation-item[href*="${lowerId}"]`);
        if (direct) return direct;
    }

    const items = getSidebarItems();
    for (let i = 0; i < items.length; i++) {
        const candidate = items[i];
        const candidateId = candidate && typeof candidate.getAttribute === 'function'
            ? candidate.getAttribute('data-conversation-id')
            : null;
        if (candidateId && String(candidateId).toLowerCase() === lowerId) {
            return candidate;
        }
    }

    return null;
}

/**
 * Updates active row highlight and aria-current="page" across sidebar items.
 * Gracefully clears prior highlights when activeConvId is null or not listed (e.g., archived chat).
 * @param {string|null|undefined} activeConvId
 */
export function setActiveRow(activeConvId) {
    if (typeof document === 'undefined') return;
    const targetId = normalizeId(activeConvId).trim();
    const lowerTarget = targetId.toLowerCase();

    const listEl = typeof document.getElementById === 'function' ? document.getElementById('conversationList') : null;
    if (listEl && typeof listEl.setAttribute === 'function') {
        listEl.setAttribute('data-active-conversation-id', targetId);
    }

    const items = getSidebarItems();
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        if (!item) continue;
        const itemId = typeof item.getAttribute === 'function'
            ? (item.getAttribute('data-conversation-id') || '').trim().toLowerCase()
            : '';

        if (lowerTarget && itemId === lowerTarget) {
            if (typeof item.setAttribute === 'function') {
                item.setAttribute('aria-current', 'page');
            }
            item.className = ACTIVE_ROW_CLASS;
        } else {
            if (typeof item.removeAttribute === 'function') {
                item.removeAttribute('aria-current');
            }
            item.className = INACTIVE_ROW_CLASS;
        }
    }
}

/**
 * Updates the base page title (without unread badge prefix) and re-applies the unread badge count.
 * @param {string} title
 */
export function setDocumentBaseTitle(title) {
    const clean = String(title || 'A♡S Messenger').replace(/^\(\d+\)\s+/, '').trim();
    baseDocumentTitle = clean || 'A♡S Messenger';
    updateDocumentTitleUnreadBadge();
}

/**
 * Clears cached baseDocumentTitle state (used on session wipe or reset).
 */
export function resetDocumentTitleState() {
    baseDocumentTitle = null;
}

/**
 * Synchronizes document.title prefix with total unread count across sidebar items.
 */
export function updateDocumentTitleUnreadBadge() {
    if (typeof document === 'undefined' || typeof document.title !== 'string') return;
    if (baseDocumentTitle === null) {
        baseDocumentTitle = document.title.replace(/^\(\d+\)\s+/, '');
    }

    const items = (typeof document.querySelectorAll === 'function'
        ? document.querySelectorAll('#conversationList .conversation-item')
        : null) || getSidebarItems();

    let total = 0;
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        const raw = item && typeof item.getAttribute === 'function'
            ? item.getAttribute('data-unread-count')
            : null;
        const parsed = parseInt(raw || '0', 10);
        if (!isNaN(parsed) && parsed > 0) {
            total += parsed;
        }
    }

    document.title = total > 0 ? `(${total}) ${baseDocumentTitle}` : baseDocumentTitle;
}

/**
 * Re-applies the active sidebar search query to all conversation items.
 */
export function applySidebarSearchFilter() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return;
    }
    const searchInput = document.getElementById('conversationSearchInput');
    if (!searchInput) return;

    const query = (searchInput.value || '').toLowerCase().trim();
    const items = getSidebarItems();
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        const text = typeof item.getAttribute === 'function'
            ? (item.getAttribute('data-search') || '').toLowerCase()
            : '';
        if (item.style) {
            item.style.display = text.includes(query) ? '' : 'none';
        }
    }
}

function updateSidebarItemSearchAttr(item, previewText) {
    if (!item || typeof item.getAttribute !== 'function' || typeof item.setAttribute !== 'function') return;
    const peerName = item.getAttribute('data-peer-name');
    const peerHandle = item.getAttribute('data-peer-handle');
    if (peerName !== null || peerHandle !== null) {
        item.setAttribute('data-search', `${peerName || ''} ${peerHandle || ''} ${previewText || ''}`.trim());
    }
}

function reorderSidebarItem(item) {
    const parent = item && (item.parentElement || item.parentNode);
    if (!item || !parent || typeof parent.insertBefore !== 'function') return;

    const isPinned = typeof item.getAttribute === 'function' && item.getAttribute('data-is-pinned') === 'true';
    const firstChild = parent.firstElementChild || (parent.children && parent.children[0]) || (parent.childNodes && parent.childNodes[0]) || null;

    if (isPinned) {
        if (firstChild && firstChild !== item) {
            parent.insertBefore(item, firstChild);
        }
        return;
    }

    const children = parent.children || parent.childNodes;
    if (!children || children.length <= 1) return;

    let firstUnpinned = null;
    for (let i = 0; i < children.length; i++) {
        const child = children[i];
        const childPinned = child && typeof child.getAttribute === 'function' && child.getAttribute('data-is-pinned') === 'true';
        if (!childPinned) {
            firstUnpinned = child;
            break;
        }
    }

    if (firstUnpinned && firstUnpinned !== item) {
        parent.insertBefore(item, firstUnpinned);
    } else if (!firstUnpinned && typeof parent.appendChild === 'function') {
        parent.appendChild(item);
    }
}

/**
 * Sets the unread message count badge for a conversation in the sidebar.
 * @param {string|object} convId
 * @param {number} count
 * @returns {boolean} True if item was found
 */
export function setSidebarUnreadCount(convId, count) {
    const item = findSidebarItem(convId);
    if (!item) {
        updateDocumentTitleUnreadBadge();
        return false;
    }

    const safeCount = Math.max(0, parseInt(count, 10) || 0);
    if (typeof item.setAttribute === 'function') {
        item.setAttribute('data-unread-count', String(safeCount));
    }

    if (typeof item.querySelector !== 'function') {
        updateDocumentTitleUnreadBadge();
        return true;
    }

    let badge = item.querySelector('.conversation-unread-badge') || item.querySelector('[data-unread-badge]');

    if (safeCount > 0) {
        if (!badge && typeof document.createElement === 'function') {
            const badgesContainer = item.querySelector('.conversation-badges') ||
                                    item.querySelector('.flex.items-center.gap-1.flex-shrink-0');
            if (badgesContainer && typeof badgesContainer.appendChild === 'function') {
                badge = document.createElement('span');
                badge.className = 'conversation-unread-badge px-1.5 py-0.5 text-[10px] font-bold rounded-full bg-[#B3929D] text-white';
                badge.setAttribute('data-unread-badge', 'true');
                badgesContainer.appendChild(badge);
            }
        }
        if (badge) {
            badge.textContent = String(safeCount);
            if (typeof badge.setAttribute === 'function') {
                badge.setAttribute('aria-label', safeCount === 1 ? '1 unread message' : `${safeCount} unread messages`);
            }
        }
    } else if (badge && typeof badge.remove === 'function') {
        badge.remove();
    } else if (badge && badge.parentNode && typeof badge.parentNode.removeChild === 'function') {
        badge.parentNode.removeChild(badge);
    }

    updateDocumentTitleUnreadBadge();
    return true;
}

/**
 * Increments the unread message count badge for a conversation in the sidebar.
 * @param {string|object} convId
 * @param {number} [delta]
 * @returns {boolean} True if item was found
 */
export function incrementSidebarUnreadCount(convId, delta = 1) {
    const item = findSidebarItem(convId);
    if (!item) return false;

    let current = 0;
    if (typeof item.getAttribute === 'function') {
        const attrVal = item.getAttribute('data-unread-count');
        if (attrVal !== null && attrVal !== '') {
            current = parseInt(attrVal, 10) || 0;
        } else if (typeof item.querySelector === 'function') {
            const existingBadge = item.querySelector('.conversation-unread-badge') || item.querySelector('[data-unread-badge]');
            if (existingBadge) {
                current = parseInt(existingBadge.textContent || '0', 10) || 0;
            }
        }
    }

    return setSidebarUnreadCount(convId, current + delta);
}

/**
 * Initializes same-origin cross-tab sidebar synchronization via BroadcastChannel.
 */
export function ensureSidebarBroadcastChannel() {
    if (sidebarBroadcastChannel || typeof BroadcastChannel === 'undefined') return sidebarBroadcastChannel;
    try {
        sidebarBroadcastChannel = new BroadcastChannel('aos-messenger-sidebar');
        if (typeof sidebarBroadcastChannel.unref === 'function') {
            sidebarBroadcastChannel.unref();
        }
        sidebarBroadcastChannel.addEventListener('message', (event) => {
            const data = event && event.data;
            if (!data || typeof data !== 'object') return;
            if (data.type === 'conversation-read' && data.conversationId) {
                setSidebarUnreadCount(data.conversationId, 0);
            }
        });
    } catch {
        sidebarBroadcastChannel = null;
    }
    return sidebarBroadcastChannel;
}

/**
 * Closes the BroadcastChannel cleanly (primarily for test teardown).
 */
export function closeSidebarBroadcastChannel() {
    if (sidebarBroadcastChannel && typeof sidebarBroadcastChannel.close === 'function') {
        try {
            sidebarBroadcastChannel.close();
        } catch {}
    }
    sidebarBroadcastChannel = null;
}

/**
 * Notifies other open tabs in the same browser that a conversation was marked as read.
 * @param {string|object} convId
 */
export function broadcastConversationRead(convId) {
    if (!convId) return;
    const normalizedId = normalizeId(convId);
    const channel = ensureSidebarBroadcastChannel();
    if (channel && typeof channel.postMessage === 'function') {
        try {
            channel.postMessage({ type: 'conversation-read', conversationId: normalizedId });
        } catch {}
    }
}

/**
 * Updates the last message snippet in the left conversation sidebar.
 * @param {string|object} convId
 * @param {string} previewText
 * @param {string|number|null} [messageId]
 * @param {boolean} [onlyIfLatest]
 * @param {boolean} [forceClearId]
 * @param {boolean} [shouldReorder]
 * @returns {boolean} True if sidebar item was found
 */
export function updateSidebarLastMessage(convId, previewText, messageId = null, onlyIfLatest = false, forceClearId = false, shouldReorder = false) {
    if (!convId || typeof document === 'undefined') return false;
    const item = findSidebarItem(convId);
    if (!item) return false;

    if (onlyIfLatest && messageId !== null && messageId !== undefined) {
        const currentLastId = typeof item.getAttribute === 'function' ? item.getAttribute('data-last-message-id') : null;
        if (currentLastId && String(currentLastId) !== String(messageId)) {
            return true;
        }
    }

    if (typeof item.querySelector === 'function') {
        const previewEl = item.querySelector('.conversation-last-message') ||
                          item.querySelector('.text-\\[11px\\].truncate');
        if (previewEl) {
            previewEl.textContent = previewText || 'No messages yet';
        }
    }

    if (forceClearId && typeof item.removeAttribute === 'function') {
        item.removeAttribute('data-last-message-id');
    } else if (!onlyIfLatest && messageId !== null && messageId !== undefined && typeof item.setAttribute === 'function') {
        item.setAttribute('data-last-message-id', String(messageId));
    }

    updateSidebarItemSearchAttr(item, previewText);

    if (shouldReorder) {
        reorderSidebarItem(item);
    }

    applySidebarSearchFilter();
    return true;
}

/**
 * Safely builds a conversation sidebar anchor element from a ConversationSummaryDto.
 * Pure DOM creation with textContent only.
 * @param {object} summary
 * @param {string|null} [activeConvId]
 * @returns {HTMLElement|null}
 */
export function createSidebarConversationItemElement(summary, activeConvId = null) {
    if (!summary || typeof document === 'undefined' || typeof document.createElement !== 'function') return null;

    const convId = normalizeId(summary.id !== undefined ? summary.id : summary.Id).trim();
    if (!convId) return null;

    const peerId = normalizeId(summary.otherParticipantId !== undefined ? summary.otherParticipantId : summary.OtherParticipantId);
    const peerName = summary.otherParticipantName || summary.OtherParticipantName || 'Unknown';
    const peerHandle = summary.otherParticipantHandle || summary.OtherParticipantHandle || '';
    const previewRaw = summary.lastMessagePreview !== undefined ? summary.lastMessagePreview : summary.LastMessagePreview;
    const previewText = previewRaw && String(previewRaw).trim() ? String(previewRaw) : 'No messages yet';
    const unreadCount = Math.max(0, parseInt(summary.unreadCount !== undefined ? summary.unreadCount : (summary.UnreadCount || 0), 10) || 0);
    const isPinned = !!(summary.isPinned !== undefined ? summary.isPinned : summary.IsPinned);
    const isMuted = !!(summary.isMuted !== undefined ? summary.isMuted : summary.IsMuted);
    const isOnline = !!(summary.isOnline !== undefined ? summary.isOnline : summary.IsOnline);
    const lastMsgRaw = summary.lastMessageId !== undefined ? summary.lastMessageId : summary.LastMessageId;
    const lastMessageId = normalizeId(lastMsgRaw);

    const isActive = !!(activeConvId && String(activeConvId).trim().toLowerCase() === convId.toLowerCase());
    const initials = getInitials(peerName, peerHandle);

    const link = document.createElement('a');
    link.href = `/chat/${encodeURIComponent(convId)}`;
    link.setAttribute('data-conversation-link', 'true');
    if (isActive) {
        link.setAttribute('aria-current', 'page');
    }
    link.setAttribute('data-conversation-id', convId);
    if (peerId) link.setAttribute('data-peer-user-id', peerId);
    link.setAttribute('data-peer-name', peerName);
    link.setAttribute('data-peer-handle', peerHandle);
    link.setAttribute('data-last-message-id', lastMessageId);
    link.setAttribute('data-is-pinned', isPinned ? 'true' : 'false');
    link.setAttribute('data-is-muted', isMuted ? 'true' : 'false');
    link.setAttribute('data-unread-count', String(unreadCount));
    link.setAttribute('data-search', `${peerName} ${peerHandle} ${previewRaw || ''}`.trim());
    link.className = isActive ? ACTIVE_ROW_CLASS : INACTIVE_ROW_CLASS;

    const avatarWrap = document.createElement('div');
    avatarWrap.className = 'relative flex-shrink-0';

    const avatarCircle = document.createElement('div');
    avatarCircle.className = 'w-11 h-11 rounded-full bg-[#B3929D] text-white font-bold text-xs flex items-center justify-center shadow-xs';
    avatarCircle.textContent = initials;
    avatarWrap.appendChild(avatarCircle);

    const presenceDot = document.createElement('span');
    presenceDot.className = `presence-indicator-dot absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full ${isOnline ? 'bg-[#6F9473]' : 'bg-stone-400 dark:bg-stone-500'} border-2 border-[#F0E9DF] dark:border-[#3A3835]`;
    avatarWrap.appendChild(presenceDot);
    link.appendChild(avatarWrap);

    const body = document.createElement('div');
    body.className = 'flex-1 min-w-0';

    const topRow = document.createElement('div');
    topRow.className = 'flex items-center justify-between gap-1 mb-0.5';

    const nameSpan = document.createElement('span');
    nameSpan.className = 'text-xs font-bold text-[#2B2725] dark:text-[#F0E9E2] truncate';
    nameSpan.textContent = peerName;
    topRow.appendChild(nameSpan);

    const badgesDiv = document.createElement('div');
    badgesDiv.className = 'conversation-badges flex items-center gap-1 flex-shrink-0';

    if (isPinned) {
        const pinIcon = document.createElement('span');
        pinIcon.className = 'material-symbols-outlined text-[14px] text-[#A07860] dark:text-[#D8C080]';
        pinIcon.title = 'Pinned';
        pinIcon.textContent = 'push_pin';
        badgesDiv.appendChild(pinIcon);
    }

    if (isMuted) {
        const muteIcon = document.createElement('span');
        muteIcon.className = 'material-symbols-outlined text-[14px] text-[#6E6560] dark:text-[#C9C0B9]';
        muteIcon.title = 'Muted';
        muteIcon.textContent = 'volume_off';
        badgesDiv.appendChild(muteIcon);
    }

    if (unreadCount > 0) {
        const unreadBadge = document.createElement('span');
        unreadBadge.className = 'conversation-unread-badge px-1.5 py-0.5 text-[10px] font-bold rounded-full bg-[#B3929D] text-white';
        unreadBadge.setAttribute('data-unread-badge', 'true');
        unreadBadge.setAttribute('aria-label', unreadCount === 1 ? '1 unread message' : `${unreadCount} unread messages`);
        unreadBadge.textContent = String(unreadCount);
        badgesDiv.appendChild(unreadBadge);
    }

    topRow.appendChild(badgesDiv);
    body.appendChild(topRow);

    const bottomRow = document.createElement('div');
    bottomRow.className = 'flex items-center justify-between gap-1';

    const previewSpan = document.createElement('span');
    previewSpan.className = 'text-[11px] text-[#6E6560] dark:text-[#C9C0B9] truncate conversation-last-message';
    previewSpan.textContent = previewText;
    bottomRow.appendChild(previewSpan);

    const handleSpan = document.createElement('span');
    handleSpan.className = 'text-[10px] text-[#A07860] dark:text-[#C9C0B9] flex-shrink-0';
    handleSpan.textContent = peerHandle ? `@${peerHandle}` : '';
    bottomRow.appendChild(handleSpan);

    body.appendChild(bottomRow);
    link.appendChild(body);

    return link;
}

/**
 * Reconciles the sidebar conversation list DOM from authoritative server summaries.
 * @param {Array<object>} conversations
 * @param {string|null} [activeConvId]
 */
export function renderSidebarConversations(conversations, activeConvId = null) {
    if (!Array.isArray(conversations) || typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
    const listEl = document.getElementById('conversationList');
    if (!listEl) return;

    const effectiveActiveId = activeConvId !== null && activeConvId !== undefined
        ? String(activeConvId)
        : ((typeof listEl.getAttribute === 'function' ? listEl.getAttribute('data-active-conversation-id') : '') || '');

    if (typeof listEl.setAttribute === 'function') {
        listEl.setAttribute('data-active-conversation-id', effectiveActiveId);
    }

    if (conversations.length === 0) {
        while (listEl.firstChild) {
            listEl.removeChild(listEl.firstChild);
        }

        const emptyWrap = document.createElement('div');
        emptyWrap.className = 'h-full p-6 flex flex-col items-center justify-center text-center';
        emptyWrap.setAttribute('data-sidebar-empty-state', 'true');

        const iconBox = document.createElement('div');
        iconBox.className = 'w-14 h-14 rounded-2xl bg-[#FFFFFF] dark:bg-[#45423E] border border-[#E3DCD7] dark:border-[#514D48] flex items-center justify-center text-[#B3929D] shadow-xs mb-3.5';
        const iconSpan = document.createElement('span');
        iconSpan.className = 'material-symbols-outlined text-[28px]';
        iconSpan.textContent = 'chat_bubble_outline';
        iconBox.appendChild(iconSpan);
        emptyWrap.appendChild(iconBox);

        const heading = document.createElement('h3');
        heading.className = 'text-sm font-bold text-[#2B2725] dark:text-[#F0E9E2] mb-1';
        heading.textContent = 'No conversations yet';
        emptyWrap.appendChild(heading);

        const desc = document.createElement('p');
        desc.className = 'text-xs text-[#6E6560] dark:text-[#C9C0B9] leading-relaxed max-w-[220px] mb-4';
        desc.textContent = 'Find a contact in the directory to start chatting';
        emptyWrap.appendChild(desc);

        const cta = document.createElement('a');
        cta.href = '/Chat/NewConversation';
        cta.className = 'inline-flex items-center gap-1.5 px-3.5 py-2 rounded-xl bg-[#B3929D] hover:bg-[#A07860] text-white text-xs font-semibold shadow-xs transition-colors';
        const addIcon = document.createElement('span');
        addIcon.className = 'material-symbols-outlined text-[16px]';
        addIcon.textContent = 'add';
        const ctaText = document.createElement('span');
        ctaText.textContent = 'Start a chat';
        cta.appendChild(addIcon);
        cta.appendChild(ctaText);
        emptyWrap.appendChild(cta);

        listEl.appendChild(emptyWrap);
        updateDocumentTitleUnreadBadge();
        return;
    }

    const emptyState = typeof listEl.querySelector === 'function'
        ? listEl.querySelector('[data-sidebar-empty-state]')
        : null;
    if (emptyState && typeof emptyState.remove === 'function') {
        emptyState.remove();
    }

    let itemsContainer = typeof listEl.querySelector === 'function'
        ? listEl.querySelector('.conversation-items-container')
        : null;
    if (!itemsContainer) {
        while (listEl.firstChild) {
            listEl.removeChild(listEl.firstChild);
        }
        itemsContainer = document.createElement('div');
        itemsContainer.className = 'p-2 space-y-1 conversation-items-container';
        listEl.appendChild(itemsContainer);
    } else {
        while (itemsContainer.firstChild) {
            itemsContainer.removeChild(itemsContainer.firstChild);
        }
    }

    const frag = typeof document.createDocumentFragment === 'function'
        ? document.createDocumentFragment()
        : itemsContainer;

    for (let i = 0; i < conversations.length; i++) {
        const el = createSidebarConversationItemElement(conversations[i], effectiveActiveId);
        if (el) {
            frag.appendChild(el);
        }
    }

    if (frag !== itemsContainer) {
        itemsContainer.appendChild(frag);
    }

    applySidebarSearchFilter();
    updateDocumentTitleUnreadBadge();
}

/**
 * Fetches authoritative conversation summaries from GET /api/conversations via api-client
 * and reconciles the sidebar list, coalescing concurrent refreshes cleanly.
 * @param {string|Function|null} [activeConvIdOrGetter]
 * @param {{ signal?: AbortSignal }} [options]
 * @returns {Promise<Array<object>|null>}
 */
export async function syncSidebarConversations(activeConvIdOrGetter = null, { signal } = {}) {
    if (typeof document === 'undefined') return null;
    if (typeof document.getElementById === 'function' && !document.getElementById('conversationList')) {
        return null;
    }

    if (isSyncingSidebar) {
        pendingSidebarSync = true;
        pendingSidebarActiveGetter = activeConvIdOrGetter;
        pendingSidebarSignal = signal;
        return null;
    }

    isSyncingSidebar = true;
    try {
        const data = await getConversations({ includeArchived: false, signal });
        if (Array.isArray(data)) {
            const activeConvId = typeof activeConvIdOrGetter === 'function'
                ? activeConvIdOrGetter()
                : activeConvIdOrGetter;
            renderSidebarConversations(data, activeConvId);
            return data;
        }
        return null;
    } catch (err) {
        if (err?.name !== 'AbortError' && err?.code !== 'UNAUTHORIZED') {
            console.warn('[Sidebar] Error syncing conversations:', err?.status || err);
        }
        return null;
    } finally {
        isSyncingSidebar = false;
        if (pendingSidebarSync) {
            pendingSidebarSync = false;
            const nextActiveGetter = pendingSidebarActiveGetter;
            const nextSignal = pendingSidebarSignal;
            pendingSidebarActiveGetter = null;
            pendingSidebarSignal = undefined;
            await syncSidebarConversations(nextActiveGetter, { signal: nextSignal });
        }
    }
}
