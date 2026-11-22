/**
 * AOS Messenger - Chat Index Lobby Page Controller
 *
 * Initializes SignalR connection, live sidebar preview updates, and online presence
 * tracking while viewing the conversation list on /Chat/Index.
 */

import './receipts.js';
import { setupPresence } from './presence.js';

(() => {
    'use strict';

    const rootEl = document.getElementById('main-chat-container');
    const currentUserId = rootEl ? (rootEl.getAttribute('data-current-user-id') || null) : null;
    const sidebarListEl = document.getElementById('conversationList');

    const realtime = typeof ChatRealtime !== 'undefined' && typeof ChatRealtime.initLobby === 'function'
        ? ChatRealtime.initLobby({ currentUserId })
        : null;

    const connection = realtime ? realtime.connection : null;
    setupPresence(connection, sidebarListEl, null);
})();
