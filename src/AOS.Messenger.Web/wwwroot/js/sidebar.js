/**
 * AOS Messenger - Conversation Sidebar Controller
 *
 * Implements client-side conversation list search filtering and initializes
 * live SignalR sidebar updates + presence on authenticated non-chat pages
 * (e.g. /Profile, /Settings, /Chat/Archived, /Chat/NewConversation, /Account/Sessions).
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 * - Single SignalR connection per browser tab (does not duplicate Chat/Index or Chat/Conversation connections).
 */

import './receipts.js';
import { setupPresence } from './presence.js';

(() => {
    'use strict';

    function initSidebar() {
        const searchInput = document.getElementById('conversationSearchInput');
        if (searchInput) {
            searchInput.addEventListener('input', function (e) {
                const query = (e.target.value || '').toLowerCase().trim();
                const items = document.querySelectorAll('.conversation-item');
                items.forEach(item => {
                    const text = (item.getAttribute('data-search') || '').toLowerCase();
                    item.style.display = text.includes(query) ? '' : 'none';
                });
            });
        }

        const sidebarListEl = document.getElementById('conversationList');
        const hasDedicatedChatPage = !!(
            document.getElementById('conversation-page-root') ||
            document.getElementById('main-chat-container')
        );

        if (sidebarListEl &&
            !hasDedicatedChatPage &&
            typeof window.ChatRealtime !== 'undefined' &&
            typeof window.ChatRealtime.initLobby === 'function') {
            const currentUserId = sidebarListEl.getAttribute('data-current-user-id') || null;
            const realtime = window.ChatRealtime.initLobby({ currentUserId });
            const connection = realtime ? realtime.connection : null;
            setupPresence(connection, sidebarListEl, null);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initSidebar);
    } else {
        initSidebar();
    }
})();
