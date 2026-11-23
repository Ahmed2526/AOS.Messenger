/**
 * AOS Messenger - Phase 6 & Phase 7 Realtime Chat & State Reconciliation
 *
 * Implements client-side SignalR connection lifecycle, live message streaming,
 * keyset state reconciliation on reconnect, ID deduplication, XSS sanitization,
 * delivery/read receipts, typing indicators, and presence synchronization.
 *
 * Architectural Invariants (AGENTS.md):
 * - SQL Server is the authoritative persistent store. SignalR is transport only.
 * - State Reconciliation queries GET /api/conversations/{id}/messages on reconnected.
 * - Deduplication by message ID prevents dual rendering from SignalR and REST.
 * - Safe DOM tree construction using document.createElement exclusively.
 * - Zero HTML string injection into live DOM.
 * - All text content bound strictly via textContent or createTextNode.
 * - UI classes align with Stitch warm modern token system.
 */
(function (global) {
    'use strict';

    /**
     * Sanitizes user-supplied text to prevent Cross-Site Scripting (XSS).
     * @param {string} str - Raw input string
     * @returns {string} HTML-escaped string
     */
    function escapeHtml(str) {
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
    function formatTime(isoString) {
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
    function getInitials(name, handle) {
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
     * @returns {string|null} Normalized GUID string
     */
    function getMessageId(msg) {
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
    function getUserId(u) {
        if (!u) return '';
        if (typeof u === 'string') return u;
        if (typeof u === 'object' && u.value) return String(u.value);
        return String(u);
    }

    /**
     * Normalizes a conversation ID from string, GUID, or strongly-typed object.
     * @param {object|string} obj
     * @returns {string} Normalized conversation ID string
     */
    function getConversationId(obj) {
        if (!obj) return '';
        if (typeof obj === 'string') return obj;
        const val = obj.conversationId !== undefined ? obj.conversationId : obj.ConversationId;
        if (!val) return '';
        if (typeof val === 'object' && val.value) return String(val.value);
        return String(val);
    }

    /**
     * Checks if a message ID is already rendered in the DOM.
     * @param {string|object} msgOrId
     * @returns {boolean} True if already rendered in message stream
     */
    function isMessageRendered(msgOrId) {
        const id = typeof msgOrId === 'object' ? getMessageId(msgOrId) : msgOrId;
        if (!id) return false;
        if (typeof document === 'undefined') return false;
        return !!(document.getElementById(`msg-${id}`) || document.querySelector(`[data-message-id="${id}"]`));
    }

    /**
     * Scrolls the message stream smoothly to the bottom.
     * @param {boolean} smooth
     */
    function scrollToBottom(smooth = true) {
        if (typeof document === 'undefined') return;
        const streamEl = document.getElementById('message-stream');
        if (streamEl) {
            if (smooth) {
                streamEl.scrollTo({ top: streamEl.scrollHeight, behavior: 'smooth' });
            } else {
                streamEl.scrollTop = streamEl.scrollHeight;
            }
        }
    }

    /**
     * Checks whether the given message ID is the latest rendered row inside containerEl.
     * @param {HTMLElement|null} containerEl
     * @param {string|number} msgId
     * @returns {boolean}
     */
    function isLatestRenderedMessage(containerEl, msgId) {
        if (!containerEl || !msgId || typeof containerEl.querySelectorAll !== 'function') return false;
        const rows = containerEl.querySelectorAll('.message-bubble-row');
        if (!rows || rows.length === 0) return false;
        const lastRow = rows[rows.length - 1];
        return String(lastRow.getAttribute('data-message-id')) === String(msgId);
    }

    let baseDocumentTitle = null;
    let sidebarBroadcastChannel = null;
    let isSyncingSidebar = false;
    let pendingSidebarSync = false;
    let pendingSidebarActiveConvId = null;
    let pendingSidebarOnSessionExpired = null;

    /**
     * Resolves the authenticated user ID from an explicit option or the sidebar container.
     * @param {string|null|undefined} explicitUserId
     * @returns {string|null}
     */
    function getEffectiveCurrentUserId(explicitUserId) {
        if (explicitUserId) return String(explicitUserId);
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return null;
        const listEl = document.getElementById('conversationList');
        const attr = listEl && typeof listEl.getAttribute === 'function'
            ? listEl.getAttribute('data-current-user-id')
            : null;
        return attr ? String(attr) : null;
    }

    /**
     * Checks whether the browser document is currently visible and focused.
     * @returns {boolean}
     */
    function isDocumentVisibleAndFocused() {
        if (typeof document === 'undefined') return true;
        const visible = document.visibilityState === undefined || document.visibilityState === 'visible';
        const focused = typeof document.hasFocus === 'function' ? document.hasFocus() : true;
        return visible && focused;
    }

    /**
     * Finds a conversation sidebar item element by conversation ID (case-insensitive).
     * @param {string|object} convId
     * @returns {HTMLElement|null}
     */
    function findSidebarItem(convId) {
        if (!convId || typeof document === 'undefined') return null;
        const normalizedId = typeof convId === 'object' && convId.value ? String(convId.value) : String(convId);
        const lowerId = normalizedId.toLowerCase();

        if (typeof document.querySelector === 'function') {
            const direct = document.querySelector(`.conversation-item[data-conversation-id="${normalizedId}"]`) ||
                           document.querySelector(`.conversation-item[data-conversation-id="${lowerId}"]`) ||
                           document.querySelector(`.conversation-item[href*="${normalizedId}"]`) ||
                           document.querySelector(`.conversation-item[href*="${lowerId}"]`);
            if (direct) return direct;
        }

        if (typeof document.querySelectorAll === 'function') {
            const items = document.querySelectorAll('.conversation-item');
            for (let i = 0; i < items.length; i++) {
                const candidate = items[i];
                const candidateId = candidate && typeof candidate.getAttribute === 'function'
                    ? candidate.getAttribute('data-conversation-id')
                    : null;
                if (candidateId && String(candidateId).toLowerCase() === lowerId) {
                    return candidate;
                }
            }
        }

        return null;
    }

    /**
     * Re-applies the active sidebar search query to all conversation items.
     */
    function applySidebarSearchFilter() {
        if (typeof document === 'undefined' ||
            typeof document.getElementById !== 'function' ||
            typeof document.querySelectorAll !== 'function') {
            return;
        }
        const searchInput = document.getElementById('conversationSearchInput');
        if (!searchInput) return;
        const query = (searchInput.value || '').toLowerCase().trim();
        const items = document.querySelectorAll('.conversation-item');
        items.forEach((item) => {
            const text = (item.getAttribute('data-search') || '').toLowerCase();
            if (item.style) {
                item.style.display = text.includes(query) ? '' : 'none';
            }
        });
    }

    /**
     * Synchronizes the document title prefix with total unread count across sidebar items.
     */
    function updateDocumentTitleUnreadBadge() {
        if (typeof document === 'undefined' || typeof document.title !== 'string') return;
        if (baseDocumentTitle === null) {
            baseDocumentTitle = document.title.replace(/^\(\d+\)\s+/, '');
        }
        if (typeof document.querySelectorAll !== 'function') return;

        const items = document.querySelectorAll('#conversationList .conversation-item');
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
     * Updates the data-search attribute on a sidebar item when its preview changes.
     * @param {HTMLElement} item
     * @param {string} previewText
     */
    function updateSidebarItemSearchAttr(item, previewText) {
        if (!item || typeof item.getAttribute !== 'function' || typeof item.setAttribute !== 'function') return;
        const peerName = item.getAttribute('data-peer-name');
        const peerHandle = item.getAttribute('data-peer-handle');
        if (peerName !== null || peerHandle !== null) {
            item.setAttribute('data-search', `${peerName || ''} ${peerHandle || ''} ${previewText || ''}`.trim());
        }
    }

    /**
     * Reorders a sidebar item to the top of its pinned or unpinned section.
     * Matches server sorting: IsPinned DESC, LastActivityAtUtc DESC.
     * @param {HTMLElement} item
     */
    function reorderSidebarItem(item) {
        if (!item || !item.parentElement || typeof item.parentElement.insertBefore !== 'function') return;
        const parent = item.parentElement;
        const isPinned = typeof item.getAttribute === 'function' && item.getAttribute('data-is-pinned') === 'true';

        if (isPinned) {
            if (parent.firstElementChild && parent.firstElementChild !== item) {
                parent.insertBefore(item, parent.firstElementChild);
            }
            return;
        }

        const children = parent.children;
        if (!children || children.length <= 1) return;

        let firstUnpinned = null;
        for (let i = 0; i < children.length; i++) {
            const child = children[i];
            const childPinned = typeof child.getAttribute === 'function' && child.getAttribute('data-is-pinned') === 'true';
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
     * Creates, updates, or removes the badge using safe DOM APIs exclusively.
     * @param {string|object} convId
     * @param {number} count
     * @returns {boolean} True if item was found
     */
    function setSidebarUnreadCount(convId, count) {
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
    function incrementSidebarUnreadCount(convId, delta = 1) {
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
    function ensureSidebarBroadcastChannel() {
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
     * Notifies other open tabs in the same browser that a conversation was marked as read.
     * @param {string|object} convId
     */
    function broadcastConversationRead(convId) {
        if (!convId) return;
        const normalizedId = typeof convId === 'object' && convId.value ? String(convId.value) : String(convId);
        const channel = ensureSidebarBroadcastChannel();
        if (channel && typeof channel.postMessage === 'function') {
            try {
                channel.postMessage({ type: 'conversation-read', conversationId: normalizedId });
            } catch {}
        }
    }

    /**
     * Safely builds a conversation sidebar anchor element from a ConversationSummaryDto.
     * Pure DOM creation with textContent only — zero HTML string injection.
     * @param {object} summary
     * @param {string|null} activeConvId
     * @returns {HTMLElement|null}
     */
    function createSidebarConversationItemElement(summary, activeConvId) {
        if (!summary || typeof document === 'undefined' || typeof document.createElement !== 'function') return null;

        const idRaw = summary.id !== undefined ? summary.id : summary.Id;
        const convId = typeof idRaw === 'object' && idRaw && idRaw.value ? String(idRaw.value) : String(idRaw || '');
        if (!convId) return null;

        const peerIdRaw = summary.otherParticipantId !== undefined ? summary.otherParticipantId : summary.OtherParticipantId;
        const peerId = getUserId(peerIdRaw);
        const peerName = summary.otherParticipantName || summary.OtherParticipantName || 'Unknown';
        const peerHandle = summary.otherParticipantHandle || summary.OtherParticipantHandle || '';
        const previewRaw = summary.lastMessagePreview !== undefined ? summary.lastMessagePreview : summary.LastMessagePreview;
        const previewText = previewRaw && String(previewRaw).trim() ? String(previewRaw) : 'No messages yet';
        const unreadCount = Math.max(0, parseInt(summary.unreadCount !== undefined ? summary.unreadCount : (summary.UnreadCount || 0), 10) || 0);
        const isPinned = !!(summary.isPinned !== undefined ? summary.isPinned : summary.IsPinned);
        const isMuted = !!(summary.isMuted !== undefined ? summary.isMuted : summary.IsMuted);
        const isOnline = !!(summary.isOnline !== undefined ? summary.isOnline : summary.IsOnline);
        const lastMsgRaw = summary.lastMessageId !== undefined ? summary.lastMessageId : summary.LastMessageId;
        const lastMessageId = lastMsgRaw !== null && lastMsgRaw !== undefined
            ? (typeof lastMsgRaw === 'object' && lastMsgRaw.value !== undefined ? String(lastMsgRaw.value) : String(lastMsgRaw))
            : '';

        const isActive = !!(activeConvId && String(activeConvId).toLowerCase() === convId.toLowerCase());
        const initials = getInitials(peerName, peerHandle);

        const link = document.createElement('a');
        link.href = `/Chat/Conversation/${encodeURIComponent(convId)}`;
        link.setAttribute('data-conversation-id', convId);
        if (peerId) link.setAttribute('data-peer-user-id', peerId);
        link.setAttribute('data-peer-name', peerName);
        link.setAttribute('data-peer-handle', peerHandle);
        link.setAttribute('data-last-message-id', lastMessageId);
        link.setAttribute('data-is-pinned', isPinned ? 'true' : 'false');
        link.setAttribute('data-is-muted', isMuted ? 'true' : 'false');
        link.setAttribute('data-unread-count', String(unreadCount));
        link.setAttribute('data-search', `${peerName} ${peerHandle} ${previewRaw || ''}`.trim());
        link.className = isActive
            ? 'conversation-item group flex items-center gap-3 p-3 rounded-xl transition-all bg-[#FFFFFF] dark:bg-[#45423E] shadow-xs border border-[#E3DCD7] dark:border-[#514D48]'
            : 'conversation-item group flex items-center gap-3 p-3 rounded-xl transition-all hover:bg-[#E3DCD7]/50 dark:hover:bg-[#45423E]/50 border border-transparent';

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
    function renderSidebarConversations(conversations, activeConvId = null) {
        if (!Array.isArray(conversations) || typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
        const listEl = document.getElementById('conversationList');
        if (!listEl) return;

        const effectiveActiveId = activeConvId || (typeof listEl.getAttribute === 'function' ? listEl.getAttribute('data-active-conversation-id') : '') || '';

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
     * Fetches the authoritative conversation list from GET /api/conversations
     * and reconciles sidebar previews, unread counts, new conversations, and ordering.
     * Coalesces concurrent requests cleanly.
     * @param {string|null} [activeConvId]
     * @param {Function|null} [onSessionExpired]
     * @returns {Promise<void>}
     */
    async function syncSidebarConversations(activeConvId = null, onSessionExpired = null) {
        if (typeof fetch !== 'function' || typeof document === 'undefined') return;
        if (typeof document.getElementById === 'function' && !document.getElementById('conversationList')) return;

        if (isSyncingSidebar) {
            pendingSidebarSync = true;
            pendingSidebarActiveConvId = activeConvId;
            pendingSidebarOnSessionExpired = onSessionExpired;
            return;
        }

        isSyncingSidebar = true;
        try {
            const resp = await fetch('/api/conversations?includeArchived=false', {
                headers: { 'Accept': 'application/json' }
            });

            if (resp.status === 401 || resp.status === 403 ||
                (resp.redirected && resp.url && resp.url.includes('/Account/Login'))) {
                if (typeof onSessionExpired === 'function') {
                    onSessionExpired();
                }
                return;
            }

            if (!resp.ok) {
                console.warn('[Sidebar] Failed to sync conversations:', resp.status);
                return;
            }

            const data = await resp.json();
            if (Array.isArray(data)) {
                renderSidebarConversations(data, activeConvId);
            }
        } catch (err) {
            console.warn('[Sidebar] Error syncing conversations:', err);
        } finally {
            isSyncingSidebar = false;
            if (pendingSidebarSync) {
                pendingSidebarSync = false;
                const nextActive = pendingSidebarActiveConvId;
                const nextExpired = pendingSidebarOnSessionExpired;
                pendingSidebarActiveConvId = null;
                pendingSidebarOnSessionExpired = null;
                await syncSidebarConversations(nextActive, nextExpired);
            }
        }
    }

    /**
     * Updates the last message snippet in the left conversation sidebar.
     * When onlyIfLatest is true, avoids overwriting newer previews when older messages are edited/deleted.
     * @param {string} convId
     * @param {string} previewText
     * @param {string|number|null} [messageId]
     * @param {boolean} [onlyIfLatest]
     * @param {boolean} [forceClearId]
     * @param {boolean} [shouldReorder]
     * @returns {boolean} True if sidebar item was found
     */
    function updateSidebarLastMessage(convId, previewText, messageId = null, onlyIfLatest = false, forceClearId = false, shouldReorder = false) {
        if (!convId || typeof document === 'undefined') return false;
        const item = findSidebarItem(convId);
        if (!item) return false;

        if (onlyIfLatest && messageId !== null && messageId !== undefined) {
            const currentLastId = item.getAttribute('data-last-message-id');
            if (currentLastId && String(currentLastId) !== String(messageId)) {
                return true;
            }
        }

        const previewEl = item.querySelector('.conversation-last-message') ||
                          item.querySelector('.text-\\[11px\\].truncate');
        if (previewEl) {
            previewEl.textContent = previewText || 'No messages yet';
        }

        if (forceClearId) {
            item.removeAttribute('data-last-message-id');
        } else if (!onlyIfLatest && messageId !== null && messageId !== undefined) {
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
     * Truncates a reply preview string safely by Unicode code points.
     * @param {string} text
     * @param {number} [maxCodePoints]
     * @returns {string}
     */
    function truncateReplyPreview(text, maxCodePoints = 100) {
        const chars = Array.from(String(text || ''));
        return chars.length > maxCodePoints ? `${chars.slice(0, maxCodePoints).join('')}...` : chars.join('');
    }

    /**
     * Updates child reply quote banners when a parent message is edited.
     * @param {string|number} parentMessageId
     * @param {string} newContent
     */
    function updateReplyParentBanners(parentMessageId, newContent) {
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
     * Applies caller-side DeleteForMe removal and authoritative sidebar preview sync.
     * @param {string|number} msgId
     * @param {string} convId
     * @param {string|number|null} newLastMessageId
     * @param {string|null} newLastMessagePreview
     * @param {boolean} isActiveConv
     */
    function applyMessageDeletedForMe(msgId, convId, newLastMessageId, newLastMessagePreview, isActiveConv) {
        if (typeof document === 'undefined') return;
        if (isActiveConv && msgId) {
            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                row.remove();
            }

            if (typeof document.querySelectorAll === 'function') {
                document.querySelectorAll(`[data-reply-parent-id="${msgId}"]`).forEach((q) => q.remove());
            }

            const containerEl = document.getElementById('messages-container');
            const emptyPlaceholderEl = document.getElementById('no-messages-placeholder');
            if (containerEl && emptyPlaceholderEl && containerEl.querySelectorAll('.message-bubble-row').length === 0) {
                emptyPlaceholderEl.classList.remove('hidden');
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
     * Scrubs reply quote banners referencing a tombstoned parent message anywhere in the DOM.
     * @param {string|number} parentMessageId
     */
    function scrubReplyParentBanners(parentMessageId) {
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
     * Transforms a message row into a tombstoned state and scrubs child reply banners.
     * @param {HTMLElement|null} row
     * @param {string|number} msgId
     */
    function applyMessageDeletedForEveryone(row, msgId) {
        if (row) {
            row.setAttribute('data-deleted', 'true');
            const isOutgoing = row.getAttribute('data-is-outgoing') === 'true' || row.classList.contains('justify-end');
            const contentEl = row.querySelector('.message-text-content');
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

            const actionsBar = row.querySelector('.message-actions-bar');
            if (actionsBar) actionsBar.remove();
            const editBtn = row.querySelector('[data-action="edit"]');
            if (editBtn) editBtn.remove();
            const delBtn = row.querySelector('[data-action="delete"]');
            if (delBtn) delBtn.remove();
            const reactBtn = row.querySelector('[data-action="react"]');
            if (reactBtn) reactBtn.remove();
            const replyBtn = row.querySelector('.reply-action-btn');
            if (replyBtn) replyBtn.remove();
            const receiptIcon = row.querySelector('span[data-receipt-status]');
            if (receiptIcon) receiptIcon.remove();
            const pillsContainer = row.querySelector('.reaction-pills-container');
            if (pillsContainer) pillsContainer.remove();
            const editedLabel = row.querySelector('.edited-label');
            if (editedLabel) editedLabel.remove();
        }

        if (msgId) {
            scrubReplyParentBanners(msgId);
        }
    }

    /**
     * Resolves initial checkmark status and icons based on peer watermarks.
     * @param {string|null} messageId
     * @returns {{ status: string, icon: string, colorClass: string }}
     */
    function resolveReceiptStatus(messageId) {
        if (!messageId) {
            return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
        }

        let lastRead = null;
        let lastDelivered = null;

        if (typeof document !== 'undefined') {
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
     * Creates a DOM element tree for a single message bubble using safe DOM manipulation APIs.
     * Guaranteed zero string-to-DOM parsing.
     *
     * @param {object} message
     * @param {string|null} currentUserId
     * @returns {HTMLElement|null}
     */
    function createMessageBubbleElement(message, currentUserId) {
        if (typeof document === 'undefined') {
            return null;
        }

        const id = getMessageId(message);
        const senderId = getUserId(message.senderId);
        const convId = getConversationId(message);

        const isOutgoing = currentUserId
            ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
            : !!message.isOutgoing;

        const initials = getInitials(message.senderName, message.senderHandle);
        const senderName = message.senderName || 'Unknown';
        const timeStr = formatTime(message.createdAtUtc);

        const row = document.createElement('div');
        if (id) {
            row.id = `msg-${id}`;
            row.setAttribute('data-message-id', id);
        }
        row.setAttribute('data-deleted', message.isDeletedForEveryone ? 'true' : 'false');
        if (message.createdAtUtc) {
            row.setAttribute('data-created-at', message.createdAtUtc);
        }

        if (isOutgoing) {
            row.className = 'flex flex-wrap justify-end items-center message-bubble-row group relative';
            row.setAttribute('data-is-outgoing', 'true');
        } else {
            row.className = 'flex flex-wrap justify-start items-center gap-2.5 message-bubble-row group relative';
            row.setAttribute('data-is-outgoing', 'false');

            const avatar = document.createElement('div');
            avatar.className = 'w-8 h-8 rounded-full bg-[#B3929D]/20 dark:bg-[#B3929D]/25 text-[#735761] dark:text-[#E1BDC9] border border-[#B3929D]/30 font-bold text-xs flex items-center justify-center flex-shrink-0 mt-1';
            avatar.textContent = initials;
            row.appendChild(avatar);
        }

        if (senderId) {
            row.setAttribute('data-sender-id', senderId);
        }

        const bubble = document.createElement('div');
        if (isOutgoing) {
            bubble.className = 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative';
        } else {
            bubble.className = 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tl-xs px-4 py-2 bg-[#FFFFFF] dark:bg-[#3A3835] text-[#2B2725] dark:text-[#F0E9E2] border border-[#E3DCD7] dark:border-[#514D48] shadow-2xs relative';

            const senderHeader = document.createElement('div');
            senderHeader.className = 'text-[11px] font-bold text-[#735761] dark:text-[#E1BDC9] mb-0.5';
            senderHeader.textContent = senderName;
            bubble.appendChild(senderHeader);
        }

        if (message.replyTo) {
            const replyBanner = document.createElement('div');
            replyBanner.className = isOutgoing
                ? 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-black/15 border-white/70 text-white/80 cursor-pointer hover:bg-black/25 transition-colors'
                : 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-[#F8F6F3] dark:bg-[#302F2C] border-[#B3929D] text-[#6E6560] dark:text-[#C9C0B9] cursor-pointer hover:bg-[#F0E9DF] dark:hover:bg-[#3E3C38] transition-colors';

            const parentId = getMessageId(message.replyTo);
            if (parentId) {
                replyBanner.setAttribute('data-reply-parent-id', parentId);
            }

            const rSenderEl = document.createElement('div');
            rSenderEl.className = isOutgoing ? 'font-bold text-[10px] text-white/95' : 'font-bold text-[10px] text-[#2B2725] dark:text-[#F0E9E2]';
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
        } else {
            const contentDiv = document.createElement('div');
            contentDiv.className = 'message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words';
            contentDiv.textContent = message.content || '';
            bubble.appendChild(contentDiv);
        }

        const footer = document.createElement('div');
        footer.className = isOutgoing
            ? 'flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80'
            : 'flex items-center justify-end gap-1 mt-1 text-[10px] text-[#6E6560] dark:text-[#C9C0B9]';

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

        // Checkmark for outgoing message
        if (isOutgoing && !message.isDeletedForEveryone) {
            const receipt = resolveReceiptStatus(id);
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

        const pillsContainer = document.createElement('div');
        pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
        if (message.reactions && message.reactions.length > 0 &&
            typeof window !== 'undefined' && window.Reactions && typeof window.Reactions.renderReactionPills === 'function') {
            window.Reactions.renderReactionPills(pillsContainer, message.reactions, id, convId, isOutgoing);
        } else {
            pillsContainer.classList.add('hidden');
        }
        bubble.appendChild(pillsContainer);

        row.appendChild(bubble);

        if (!message.isDeletedForEveryone) {
            const actionsBar = document.createElement('div');
            actionsBar.className = isOutgoing
                ? 'message-actions-bar opacity-100 sm:opacity-0 sm:group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity self-center shrink-0 mr-1 flex items-center gap-0.5'
                : 'message-actions-bar opacity-100 sm:opacity-0 sm:group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity self-center shrink-0 ml-1 flex items-center gap-0.5';

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

            const replyBtn = document.createElement('button');
            replyBtn.type = 'button';
            replyBtn.className = 'reply-action-btn w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
            replyBtn.title = 'Reply to message';
            replyBtn.setAttribute('aria-label', 'Reply to message');
            if (id) replyBtn.setAttribute('data-id', id);
            replyBtn.setAttribute('data-sender', senderName);
            replyBtn.setAttribute('data-preview', message.content || '');

            const replyIcon = document.createElement('span');
            replyIcon.className = 'material-symbols-outlined text-[16px]';
            replyIcon.textContent = 'reply';
            replyBtn.appendChild(replyIcon);
            actionsBar.appendChild(replyBtn);

            if (isOutgoing) {
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

            row.appendChild(actionsBar);
        }

        return row;
    }

    /**
     * Creates HTML string for a single message bubble matching Stitch warm modern design tokens.
     * Fully escapes all user content for XSS defense.
     * Provided for string serialization and unit testing parity.
     *
     * @param {object} message - MessageDto
     * @param {string|null} currentUserId - Authenticated user's GUID string
     * @returns {string} HTML string
     */
    function createMessageBubbleHtml(message, currentUserId) {
        const id = escapeHtml(getMessageId(message));
        const senderId = getUserId(message.senderId);

        const isOutgoing = currentUserId
            ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
            : !!message.isOutgoing;

        const initials = escapeHtml(getInitials(message.senderName, message.senderHandle));
        const senderName = escapeHtml(message.senderName);
        const timeStr = escapeHtml(formatTime(message.createdAtUtc));
        const editedStr = `<span class="text-[9px] italic edited-label ${message.editedAtUtc && !message.isDeletedForEveryone ? '' : 'hidden'}">edited</span>`;

        let replyBannerHtml = '';
        if (message.replyTo) {
            const rSender = escapeHtml(message.replyTo.senderName || 'Unknown');
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
                    <div class="reply-preview-text truncate text-[10px]">${rContent}</div>
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
            contentHtml = `<div class="message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words">${escapeHtml(message.content)}</div>`;
        }

        const replyBtnHtml = !message.isDeletedForEveryone
            ? `<button type="button" class="reply-action-btn opacity-0 group-hover:opacity-100 transition-opacity self-center ${isOutgoing ? 'mr-2' : 'ml-2'} text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D]" title="Reply to message" data-id="${id}" data-sender="${senderName}" data-preview="${escapeHtml(message.content)}">
                <span class="material-symbols-outlined text-[18px]">reply</span>
               </button>`
            : '';

        let receiptHtml = '';
        if (isOutgoing && !message.isDeletedForEveryone) {
            const receipt = resolveReceiptStatus(id);
            receiptHtml = `<span class="material-symbols-outlined text-[13px] select-none align-middle ${receipt.colorClass}" data-message-id="${id}" data-receipt-status="${receipt.status}">${receipt.icon}</span>`;
        }

        const senderIdAttr = senderId ? ` data-sender-id="${escapeHtml(senderId)}"` : '';

        if (isOutgoing) {
            return `
            <div id="msg-${id}" class="flex justify-end message-bubble-row group relative" data-message-id="${id}" data-is-outgoing="true"${senderIdAttr} data-created-at="${message.createdAtUtc}">
                <div class="max-w-[75%] rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative">
                    ${replyBannerHtml}
                    ${contentHtml}
                    <div class="flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80">
                        ${editedStr}
                        <span>${timeStr}</span>
                        ${receiptHtml}
                    </div>
                </div>
                ${replyBtnHtml}
            </div>`;
        } else {
            return `
            <div id="msg-${id}" class="flex justify-start gap-2.5 message-bubble-row group relative" data-message-id="${id}" data-is-outgoing="false"${senderIdAttr} data-created-at="${message.createdAtUtc}">
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

    /**
     * Initializes the SignalR Hub connection and state reconciliation for an active conversation.
     *
     * @param {object} options
     * @param {string} options.conversationId - Conversation GUID
     * @param {string} options.currentUserId - Authenticated User GUID
     * @param {string|null} [options.initialCursor] - Initial cursor for keyset pagination
     * @returns {object} Realtime controller instance
     */
    function init(options) {
        if (!options || !options.conversationId) {
            console.error('[SignalR] init requires a valid conversationId.');
            return null;
        }

        const conversationId = String(options.conversationId);
        const currentUserId = getEffectiveCurrentUserId(options.currentUserId);
        let latestReceivedCursor = options.initialCursor || null;
        const seenIncomingIds = new Set();

        ensureSidebarBroadcastChannel();
        updateDocumentTitleUnreadBadge();

        const bannerEl = document.getElementById('connection-banner');
        const bannerTextEl = document.getElementById('connection-banner-text');
        const statusDotEl = document.getElementById('connectionStatusDot');
        const statusTextEl = document.getElementById('connectionStatusText');
        const containerEl = document.getElementById('messages-container') || document.getElementById('message-stream');
        const emptyPlaceholderEl = document.getElementById('no-messages-placeholder');

        let isTerminated = false;

        /**
         * Permanently halts SignalR and redirects to login when session is revoked or expired.
         * Prevents infinite reconnect loops on 401/403 (RT-012).
         */
        function handleSessionExpired() {
            if (isTerminated) return;
            isTerminated = true;
            console.warn('[SignalR] Session revoked or expired (401/403). Stopping connection and redirecting to login.');
            try {
                if (connection) {
                    connection.stop();
                }
            } catch (e) {
                // Ignore errors during stop
            }
            if (typeof window !== 'undefined' && window.location) {
                window.location.href = '/Account/Login?sessionExpired=true';
            }
        }

        /**
         * Checks whether an error signifies an unauthorized / forbidden / revoked session.
         */
        function isUnauthorizedError(err) {
            if (!err) return false;
            if (err.statusCode === 401 || err.statusCode === 403) return true;
            const str = String(err.message || err);
            return str.includes('401') || str.includes('403') ||
                   str.toLowerCase().includes('unauthorized') ||
                   str.toLowerCase().includes('forbidden') ||
                   str.toLowerCase().includes('session expired') ||
                   str.toLowerCase().includes('login');
        }

        /**
         * Updates connection status banners and global titlebar indicators.
         */
        function setStatus(state, message) {
            if (isTerminated) return;
            if (state === 'reconnecting') {
                if (bannerEl) {
                    bannerEl.classList.remove('hidden');
                    if (bannerTextEl) bannerTextEl.textContent = message || 'Reconnecting to chat...';
                }
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-amber-500 animate-pulse';
                if (statusTextEl) statusTextEl.textContent = 'Reconnecting...';
            } else if (state === 'connected') {
                if (bannerEl) bannerEl.classList.add('hidden');
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-[#6F9473]';
                if (statusTextEl) statusTextEl.textContent = 'Connected';
            } else if (state === 'disconnected') {
                if (bannerEl) {
                    bannerEl.classList.remove('hidden');
                    if (bannerTextEl) bannerTextEl.textContent = message || 'Disconnected from chat. Reconnecting...';
                }
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-red-400';
                if (statusTextEl) statusTextEl.textContent = 'Disconnected';
            }
        }

        if (typeof signalR === 'undefined') {
            console.error('[SignalR] signalR client library is not loaded. Ensure signalr.min.js is included.');
            return null;
        }

        // Build SignalR HubConnection with automated exponential backoff reconnect policy
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/chat")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        // 1. Connection lifecycle: onreconnecting
        connection.onreconnecting((error) => {
            console.warn('[SignalR] Connection lost, attempting reconnection...', error);
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            setStatus('reconnecting', 'Reconnecting to chat...');
        });

        // 2. Connection lifecycle: onreconnected (with state reconciliation)
        connection.onreconnected(async (connectionId) => {
            if (isTerminated) return;
            console.log('[SignalR] Reconnected successfully with connectionId:', connectionId);
            setStatus('connected');

            try {
                // Re-join conversation group room
                await connection.invoke("JoinConversation", conversationId);
                console.log('[SignalR] Re-joined conversation room:', conversationId);

                // Re-fetch presence via /api/presence
                if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                    const listEl = document.getElementById('conversationList');
                    const headerEl = document.getElementById('active-conversation-header');
                    window.Presence.fetchPresence(listEl, headerEl);
                }

                // Reconcile state: query authoritative REST API for missed messages and sidebar unread counts
                await reconcileState();
                await syncSidebarConversations(conversationId, handleSessionExpired);

                // Trigger markRead/markDelivered for incoming messages
                if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.checkAndMarkIncoming === 'function') {
                    window.Receipts.checkAndMarkIncoming(containerEl || document, conversationId, currentUserId);
                }
            } catch (err) {
                console.error('[SignalR] Error during post-reconnect initialization:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                }
            }
        });

        // 3. Connection lifecycle: onclose
        connection.onclose((error) => {
            console.warn('[SignalR] Connection closed:', error);
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            if (!isTerminated) {
                setStatus('disconnected', 'Disconnected from chat.');
            }
        });

        /**
         * Inspects existing message bubbles in the DOM to find the newest rendered message
         * by (createdAtUtc, id). Used during state reconciliation to ensure ancient
         * messages from history are never appended to the live stream (RT-005).
         *
         * @returns {{ time: number, id: bigint } | null}
         */
        function getNewestRenderedMessageInfo() {
            if (!containerEl || typeof document === 'undefined') return null;
            const rows = containerEl.querySelectorAll('.message-bubble-row');
            if (!rows || rows.length === 0) return null;

            let maxTime = -Infinity;
            let maxId = 0n;
            let found = false;

            for (const row of rows) {
                const timeAttr = row.getAttribute('data-created-at');
                const idAttr = row.getAttribute('data-message-id');
                if (timeAttr) {
                    const t = new Date(timeAttr).getTime();
                    if (!isNaN(t)) {
                        found = true;
                        let idVal = 0n;
                        try {
                            if (idAttr) idVal = BigInt(idAttr);
                        } catch {
                            idVal = 0n;
                        }
                        if (t > maxTime || (t === maxTime && idVal > maxId)) {
                            maxTime = t;
                            maxId = idVal;
                        }
                    }
                }
            }

            return found ? { time: maxTime, id: maxId } : null;
        }

        /**
         * Inserts a list of messages into the stream, deduplicating against existing DOM elements
         * and filtering to messages newer than the newest rendered message (RT-005).
         *
         * @param {Array<object>} messages
         */
        function insertMissingMessages(messages) {
            if (!messages || messages.length === 0) return;

            const newestRendered = getNewestRenderedMessageInfo();

            // 1. Deduplicate against DOM and filter to messages strictly newer than newest rendered message
            const missingNewer = [];
            for (const msg of messages) {
                const msgId = getMessageId(msg);
                if (!msgId || isMessageRendered(msgId)) {
                    continue; // Deduplication: skip if already rendered
                }

                if (newestRendered && msg.createdAtUtc) {
                    const msgTime = new Date(msg.createdAtUtc).getTime();
                    let msgIdVal = 0n;
                    try {
                        msgIdVal = BigInt(msgId);
                    } catch {
                        msgIdVal = 0n;
                    }

                    if (msgTime < newestRendered.time || (msgTime === newestRendered.time && msgIdVal <= newestRendered.id)) {
                        // Older or equal to newest rendered message; skip to avoid appending ancient history
                        continue;
                    }
                }

                missingNewer.push(msg);
            }

            if (missingNewer.length === 0) return;

            // 2. Sort chronological (oldest to newest) to preserve conversational sequence
            missingNewer.sort((a, b) => {
                const ta = new Date(a.createdAtUtc).getTime();
                const tb = new Date(b.createdAtUtc).getTime();
                if (ta !== tb) return ta - tb;
                let idA = 0n;
                let idB = 0n;
                try { idA = BigInt(getMessageId(a) || '0'); } catch { idA = 0n; }
                try { idB = BigInt(getMessageId(b) || '0'); } catch { idB = 0n; }
                return idA < idB ? -1 : idA > idB ? 1 : 0;
            });

            let addedCount = 0;
            let lastAddedMessage = null;

            for (const msg of missingNewer) {
                const msgId = getMessageId(msg);
                if (!msgId || isMessageRendered(msgId)) {
                    continue;
                }

                if (containerEl) {
                    const el = createMessageBubbleElement(msg, currentUserId);
                    if (el && typeof containerEl.appendChild === 'function') {
                        containerEl.appendChild(el);
                        addedCount++;
                        lastAddedMessage = msg;
                    }
                }
            }

            if (addedCount > 0) {
                if (emptyPlaceholderEl) {
                    emptyPlaceholderEl.classList.add('hidden');
                }
                scrollToBottom(true);
                if (lastAddedMessage) {
                    const lastPreview = lastAddedMessage.isDeletedForEveryone ? 'This message was deleted' : lastAddedMessage.content;
                    updateSidebarLastMessage(conversationId, lastPreview, getMessageId(lastAddedMessage), false, false, true);
                }
            }
        }

        /**
         * State Reconciliation: Fetches missed messages from the authoritative REST API
         * and seamlessly appends any missing items to the live stream.
         */
        async function reconcileState() {
            if (isTerminated) return;
            try {
                console.log('[SignalR] Reconciling state for conversation:', conversationId);

                // NOTE [RT-005]: Query `/api/conversations/${conversationId}/messages?pageSize=30` directly
                const latestUrl = `/api/conversations/${conversationId}/messages?pageSize=30`;
                const latestResp = await fetch(latestUrl, {
                    headers: { 'Accept': 'application/json' }
                });

                if (latestResp.status === 401 || latestResp.status === 403 ||
                    (latestResp.redirected && latestResp.url.includes('/Account/Login'))) {
                    handleSessionExpired();
                    return;
                }

                if (!latestResp.ok) {
                    console.warn('[SignalR] Failed to fetch latest messages for reconciliation:', latestResp.status);
                    return;
                }

                const contentType = latestResp.headers.get('content-type') || '';
                if (!contentType.includes('application/json')) {
                    if (latestResp.url && latestResp.url.includes('/Account/Login')) {
                        handleSessionExpired();
                        return;
                    }
                }

                const latestData = await latestResp.json();
                if (latestData && latestData.messages && latestData.messages.length > 0) {
                    insertMissingMessages(latestData.messages);
                }
            } catch (err) {
                console.error('[SignalR] State reconciliation error:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                }
            }
        }

        // 4. Register live incoming message handler
        connection.on("ReceiveMessage", function (message) {
            if (!message) return;
            const msgId = getMessageId(message);
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            const previewText = message.isDeletedForEveryone ? 'This message was deleted' : message.content;
            const sidebarFound = updateSidebarLastMessage(eventConvId, previewText, msgId, false, false, true);

            const isOutgoing = currentUserId
                ? (getUserId(message.senderId).toLowerCase() === String(currentUserId).toLowerCase())
                : !!message.isOutgoing;

            const isActiveConversation = eventConvId.toLowerCase() === conversationId.toLowerCase();
            if (!isActiveConversation) {
                if (!sidebarFound) {
                    syncSidebarConversations(conversationId, handleSessionExpired);
                } else if (!isOutgoing && !seenIncomingIds.has(String(msgId))) {
                    seenIncomingIds.add(String(msgId));
                    incrementSidebarUnreadCount(eventConvId, 1);
                }

                if (!isOutgoing && typeof window.Receipts !== 'undefined' && typeof window.Receipts.markDelivered === 'function') {
                    window.Receipts.markDelivered(eventConvId, msgId);
                }
                return;
            }

            if (!sidebarFound) {
                syncSidebarConversations(conversationId, handleSessionExpired);
            }

            // Strict deduplication: check if message ID already exists in DOM (#msg-${id})
            if (isMessageRendered(msgId)) {
                return;
            }

            if (containerEl) {
                const el = createMessageBubbleElement(message, currentUserId);
                if (el && typeof containerEl.appendChild === 'function') {
                    containerEl.appendChild(el);
                }
            }
            if (emptyPlaceholderEl) {
                emptyPlaceholderEl.classList.add('hidden');
            }

            scrollToBottom(true);

            // Phase 7: Automatically mark incoming message as delivered and read if focused
            if (!isOutgoing) {
                if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.markDelivered === 'function') {
                    window.Receipts.markDelivered(conversationId, msgId);
                }

                if (isDocumentVisibleAndFocused()) {
                    if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.markRead === 'function') {
                        window.Receipts.markRead(conversationId, msgId);
                    }
                    setSidebarUnreadCount(conversationId, 0);
                } else if (sidebarFound && !seenIncomingIds.has(String(msgId))) {
                    seenIncomingIds.add(String(msgId));
                    incrementSidebarUnreadCount(conversationId, 1);
                }
            }
        });

        // 5. Phase 7: Register ReceiveReceiptUpdated handler
        connection.on("ReceiveReceiptUpdated", function (receipt) {
            if (!receipt) return;
            const convId = receipt.conversationId || receipt.ConversationId;
            if (String(convId).toLowerCase() !== conversationId.toLowerCase()) return;

            const actorUserId = receipt.userId || receipt.UserId;
            if (currentUserId && String(actorUserId).toLowerCase() === String(currentUserId).toLowerCase()) {
                return; // Ignore own receipts
            }

            const upToId = receipt.upToMessageId !== undefined ? receipt.upToMessageId : receipt.UpToMessageId;
            const rType = receipt.receiptType || receipt.ReceiptType || 'delivered';

            if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.updateMessageCheckmarks === 'function') {
                window.Receipts.updateMessageCheckmarks(containerEl || document, upToId, rType);
            }
        });

        // 5b. Phase 8: Register ReceiveMessageEdited, ReceiveMessageDeletedForEveryone, ReceiveReactionUpdated
        connection.on("ReceiveMessageEdited", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            const newContent = message.content !== undefined ? message.content : (message.Content || '');

            if (eventConvId.toLowerCase() !== conversationId.toLowerCase()) {
                updateSidebarLastMessage(eventConvId, newContent, msgId, true);
                return;
            }

            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                const contentEl = row.querySelector('.message-text-content');
                if (contentEl) {
                    contentEl.textContent = newContent;
                }
                const editedLabel = row.querySelector('.edited-label');
                if (editedLabel && (message.editedAtUtc !== undefined ? message.editedAtUtc : message.EditedAtUtc)) {
                    editedLabel.classList.remove('hidden');
                }
                const replyBtn = row.querySelector('.reply-action-btn');
                if (replyBtn) {
                    replyBtn.setAttribute('data-preview', newContent);
                }
            }

            updateReplyParentBanners(msgId, newContent);

            if (!row || isLatestRenderedMessage(containerEl, msgId)) {
                updateSidebarLastMessage(eventConvId, newContent, msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForEveryone", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            if (eventConvId.toLowerCase() !== conversationId.toLowerCase()) {
                updateSidebarLastMessage(eventConvId, 'This message was deleted', msgId, true);
                return;
            }

            const row = document.getElementById(`msg-${msgId}`);
            applyMessageDeletedForEveryone(row, msgId);

            if (!row || isLatestRenderedMessage(containerEl, msgId)) {
                updateSidebarLastMessage(eventConvId, 'This message was deleted', msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForMe", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            const newLastId = message.newLastMessageId !== undefined ? message.newLastMessageId : message.NewLastMessageId;
            const newLastPreview = message.newLastMessagePreview !== undefined ? message.newLastMessagePreview : message.NewLastMessagePreview;
            const isActive = eventConvId.toLowerCase() === conversationId.toLowerCase();

            applyMessageDeletedForMe(msgId, eventConvId, newLastId, newLastPreview, isActive);

            const sidebarItem = findSidebarItem(eventConvId);
            const currentUnread = sidebarItem ? (parseInt(sidebarItem.getAttribute('data-unread-count') || '0', 10) || 0) : 0;
            if (currentUnread > 0) {
                syncSidebarConversations(conversationId, handleSessionExpired);
            }
        });

        connection.on("ReceiveReactionUpdated", function (reaction) {
            if (!reaction) return;
            const msgId = reaction.messageId !== undefined ? reaction.messageId : reaction.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(reaction) || conversationId;
            if (eventConvId.toLowerCase() !== conversationId.toLowerCase()) {
                return;
            }

            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                let pillsContainer = row.querySelector('.reaction-pills-container');
                if (!pillsContainer) {
                    pillsContainer = document.createElement('div');
                    pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
                    const bubble = row.querySelector('.message-bubble') || row.querySelector('.max-w-\\[75\\%\\]');
                    if (bubble) bubble.appendChild(pillsContainer);
                }

                const groups = reaction.reactions !== undefined ? reaction.reactions : reaction.Reactions;
                if (Array.isArray(groups) &&
                    typeof window !== 'undefined' && window.Reactions &&
                    typeof window.Reactions.renderReactionPills === 'function' &&
                    typeof window.Reactions.mapReactionGroupsToSummaries === 'function') {
                    const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
                    const summaries = window.Reactions.mapReactionGroupsToSummaries(groups, currentUserId);
                    window.Reactions.renderReactionPills(pillsContainer, summaries, msgId, conversationId, isOutgoing);
                }
            }
        });

        // 6. Connect and JoinConversation
        async function start() {
            if (isTerminated) return;
            try {
                await connection.start();
                console.log('[SignalR] Connected to /hubs/chat');
                setStatus('connected');

                await connection.invoke("JoinConversation", conversationId);
                console.log('[SignalR] Joined conversation:', conversationId);

                // Fetch initial presence and mark receipts for initial SSR messages
                if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                    const listEl = document.getElementById('conversationList');
                    const headerEl = document.getElementById('active-conversation-header');
                    window.Presence.fetchPresence(listEl, headerEl);
                }

                if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.checkAndMarkIncoming === 'function') {
                    window.Receipts.checkAndMarkIncoming(containerEl || document, conversationId, currentUserId);
                }
            } catch (err) {
                console.error('[SignalR] Connection start failed:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                    return;
                }
                setStatus('disconnected', 'Failed to connect to chat.');
            }
        }

        // 7. LeaveConversation when user navigates away or switches conversations
        function handleLeave() {
            if (connection && connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("LeaveConversation", conversationId).catch(() => {});
            }
        }

        if (typeof window !== 'undefined') {
            window.addEventListener('beforeunload', handleLeave);
            window.addEventListener('pagehide', handleLeave);
        }

        // Initiate connection
        start();

        return {
            connection,
            reconcileState,
            createMessageBubbleHtml: (msg) => createMessageBubbleHtml(msg, currentUserId),
            createMessageBubbleElement: (msg) => createMessageBubbleElement(msg, currentUserId),
            isMessageRendered,
            scrollToBottom,
            updateSidebarLastMessage,
            setSidebarUnreadCount,
            incrementSidebarUnreadCount,
            syncSidebarConversations: () => syncSidebarConversations(conversationId, handleSessionExpired),
            broadcastConversationRead,
            handleSessionExpired
        };
    }

    /**
     * Initializes a lobby SignalR connection (e.g. on /Chat/Index or persistent sidebar pages)
     * without joining a specific conversation.
     * Keeps user online, syncs sidebar previews & unread counts live, and marks incoming messages delivered.
     *
     * @param {object} options
     * @param {string|null} [options.currentUserId]
     * @returns {object|null} Realtime controller instance
     */
    function initLobby(options) {
        const currentUserId = getEffectiveCurrentUserId(options && options.currentUserId);
        const statusDotEl = document.getElementById('connectionStatusDot');
        const statusTextEl = document.getElementById('connectionStatusText');
        let isTerminated = false;
        const seenIncomingIds = new Set();
        let lastVisibilitySyncAt = 0;

        ensureSidebarBroadcastChannel();
        updateDocumentTitleUnreadBadge();

        function handleSessionExpired() {
            if (isTerminated) return;
            isTerminated = true;
            try {
                if (connection) connection.stop();
            } catch {}
            if (typeof window !== 'undefined' && window.location) {
                window.location.href = '/Account/Login?sessionExpired=true';
            }
        }

        function isUnauthorizedError(err) {
            if (!err) return false;
            if (err.statusCode === 401 || err.statusCode === 403) return true;
            const str = String(err.message || err);
            return str.includes('401') || str.includes('403') ||
                   str.toLowerCase().includes('unauthorized') ||
                   str.toLowerCase().includes('forbidden') ||
                   str.toLowerCase().includes('session expired') ||
                   str.toLowerCase().includes('login');
        }

        function setStatus(state) {
            if (isTerminated) return;
            if (state === 'reconnecting') {
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-amber-500 animate-pulse';
                if (statusTextEl) statusTextEl.textContent = 'Reconnecting...';
            } else if (state === 'connected') {
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-[#6F9473]';
                if (statusTextEl) statusTextEl.textContent = 'Connected';
            } else if (state === 'disconnected') {
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-red-400';
                if (statusTextEl) statusTextEl.textContent = 'Disconnected';
            }
        }

        if (typeof signalR === 'undefined') {
            console.error('[SignalR] signalR client library is not loaded.');
            return null;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/chat")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.onreconnecting((error) => {
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            setStatus('reconnecting');
        });

        connection.onreconnected(async () => {
            if (isTerminated) return;
            setStatus('connected');
            await syncSidebarConversations(null, handleSessionExpired);
            if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                window.Presence.fetchPresence(document.getElementById('conversationList'), null);
            }
        });

        connection.onclose((error) => {
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            if (!isTerminated) setStatus('disconnected');
        });

        connection.on("ReceiveMessage", function (message) {
            if (!message) return;
            const msgId = getMessageId(message);
            const convId = getConversationId(message);
            if (!convId || !msgId) return;

            const previewText = message.isDeletedForEveryone ? 'This message was deleted' : message.content;
            const sidebarFound = updateSidebarLastMessage(convId, previewText, msgId, false, false, true);

            const isOutgoing = currentUserId
                ? (getUserId(message.senderId).toLowerCase() === String(currentUserId).toLowerCase())
                : !!message.isOutgoing;

            if (!sidebarFound) {
                syncSidebarConversations(null, handleSessionExpired);
            } else if (!isOutgoing && !seenIncomingIds.has(String(msgId))) {
                seenIncomingIds.add(String(msgId));
                incrementSidebarUnreadCount(convId, 1);
            }

            if (!isOutgoing && typeof window.Receipts !== 'undefined' && typeof window.Receipts.markDelivered === 'function') {
                window.Receipts.markDelivered(convId, msgId);
            }
        });

        connection.on("ReceiveMessageEdited", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            const convId = getConversationId(message);
            const content = message.content !== undefined ? message.content : (message.Content || '');
            if (convId && msgId) {
                updateSidebarLastMessage(convId, content, msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForEveryone", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            const convId = getConversationId(message);
            if (convId && msgId) {
                updateSidebarLastMessage(convId, 'This message was deleted', msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForMe", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            const convId = getConversationId(message);
            const newLastId = message.newLastMessageId !== undefined ? message.newLastMessageId : message.NewLastMessageId;
            const newLastPreview = message.newLastMessagePreview !== undefined ? message.newLastMessagePreview : message.NewLastMessagePreview;
            if (convId && msgId) {
                applyMessageDeletedForMe(msgId, convId, newLastId, newLastPreview, false);
                const sidebarItem = findSidebarItem(convId);
                const currentUnread = sidebarItem ? (parseInt(sidebarItem.getAttribute('data-unread-count') || '0', 10) || 0) : 0;
                if (currentUnread > 0) {
                    syncSidebarConversations(null, handleSessionExpired);
                }
            }
        });

        if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
            document.addEventListener('visibilitychange', () => {
                if (isTerminated || document.visibilityState !== 'visible') return;
                const now = Date.now();
                if (now - lastVisibilitySyncAt >= 10000) {
                    lastVisibilitySyncAt = now;
                    syncSidebarConversations(null, handleSessionExpired);
                }
            });
        }

        connection.start()
            .then(() => {
                setStatus('connected');
                if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                    window.Presence.fetchPresence(document.getElementById('conversationList'), null);
                }
            })
            .catch((err) => {
                console.error('[SignalR] Lobby connection start failed:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                    return;
                }
                setStatus('disconnected');
            });

        return {
            connection,
            updateSidebarLastMessage,
            setSidebarUnreadCount,
            incrementSidebarUnreadCount,
            syncSidebarConversations: () => syncSidebarConversations(null, handleSessionExpired),
            broadcastConversationRead,
            handleSessionExpired
        };
    }

    const api = {
        init,
        initLobby,
        escapeHtml,
        formatTime,
        getInitials,
        getMessageId,
        getUserId,
        getConversationId,
        isMessageRendered,
        isLatestRenderedMessage,
        createMessageBubbleHtml,
        createMessageBubbleElement,
        createSidebarConversationItemElement,
        renderSidebarConversations,
        syncSidebarConversations,
        updateReplyParentBanners,
        applyMessageDeletedForEveryone,
        applyMessageDeletedForMe,
        updateSidebarLastMessage,
        setSidebarUnreadCount,
        incrementSidebarUnreadCount,
        broadcastConversationRead,
        updateDocumentTitleUnreadBadge,
        applySidebarSearchFilter,
        scrollToBottom
    };

    if (typeof window !== 'undefined') {
        window.ChatRealtime = api;
    }
    if (typeof global !== 'undefined') {
        global.ChatRealtime = api;
    }
    if (typeof globalThis !== 'undefined') {
        globalThis.ChatRealtime = api;
    }
    if (typeof module !== 'undefined' && module.exports) {
        module.exports = api;
    }
})(typeof window !== 'undefined' ? window : (typeof global !== 'undefined' ? global : this));
