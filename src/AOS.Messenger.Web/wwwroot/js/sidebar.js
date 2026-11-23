/**
 * AOS Messenger - Conversation Sidebar Controller
 *
 * Implements client-side conversation list search filtering and initializes
 * live SignalR sidebar updates + presence on authenticated non-chat pages
 * (e.g. /Profile, /Settings, /Chat/Archived, /Chat/NewConversation, /Account/Sessions)
 * using the unified ES modules.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 * - Single SignalR connection per browser tab (does not duplicate Chat/Index or Chat/Conversation connections).
 */

import { createChatState } from './chat-state.js';
import { configureApiClient } from './api-client.js';
import { createSignalRClient } from './signalr-client.js';
import {
    ensureSidebarBroadcastChannel,
    syncSidebarConversations,
    updateDocumentTitleUnreadBadge,
    resetDocumentTitleState
} from './conversation-list.js';
import { setupPresence } from './presence.js';
import { resetReceiptsState } from './receipts.js';

export function initSidebar(overrides = {}) {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return null;
    }

    const searchInput = document.getElementById('conversationSearchInput');
    if (searchInput && typeof searchInput.addEventListener === 'function') {
        searchInput.addEventListener('input', (e) => {
            const query = (e.target?.value || '').toLowerCase().trim();
            const items = document.querySelectorAll('.conversation-item');
            items.forEach((item) => {
                const text = (item.getAttribute('data-search') || '').toLowerCase();
                item.style.display = text.includes(query) ? '' : 'none';
            });
        });
    }

    const sidebarListEl = document.getElementById('conversationList');
    const hasDedicatedChatPage = !!(
        document.getElementById('chat-shell-root') ||
        document.getElementById('conversation-page-root') ||
        document.getElementById('main-chat-container')
    );

    if (!sidebarListEl || hasDedicatedChatPage) {
        return null;
    }

    const state = overrides.state || createChatState();
    state.currentUserId = sidebarListEl.getAttribute('data-current-user-id') || state.currentUserId || null;

    let signalrClient = null;
    let expiredHandled = false;
    let lastVisibilitySyncAt = 0;

    function navigateReplace(targetUrl) {
        if (typeof overrides.navigateReplace === 'function') {
            overrides.navigateReplace(targetUrl);
            return;
        }
        if (typeof window !== 'undefined' && window.location) {
            if (typeof window.location.replace === 'function') {
                window.location.replace(targetUrl);
            } else {
                window.location.href = targetUrl;
            }
        }
    }

    function reloadCurrentPage() {
        if (typeof overrides.reloadPage === 'function') {
            overrides.reloadPage();
            return;
        }
        if (typeof window !== 'undefined' && window.location && typeof window.location.reload === 'function') {
            window.location.reload();
        }
    }

    function handleSessionExpired() {
        if (expiredHandled) return;
        expiredHandled = true;

        const baseLoginUrl = state.loginUrl || '/Account/Login';
        const loginUrl = baseLoginUrl.includes('sessionExpired=')
            ? baseLoginUrl
            : `${baseLoginUrl}?sessionExpired=true`;

        if (signalrClient && typeof signalrClient.stop === 'function') {
            signalrClient.stop();
        }
        state.wipeAllSensitiveState();
        resetReceiptsState();
        resetDocumentTitleState();

        while (sidebarListEl.firstChild) {
            sidebarListEl.removeChild(sidebarListEl.firstChild);
        }
        if (typeof document.title === 'string') {
            document.title = 'A♡S Messenger';
        }
        navigateReplace(loginUrl);
    }

    const apiConfig = {
        onSessionExpired: handleSessionExpired,
        onCsrfMismatch: reloadCurrentPage
    };
    if ('fetchImpl' in overrides) {
        apiConfig.fetchImpl = overrides.fetchImpl;
    }
    configureApiClient(apiConfig);

    ensureSidebarBroadcastChannel();
    updateDocumentTitleUnreadBadge();

    const presence = setupPresence(null, sidebarListEl, null, { skipInitialFetch: true });

    signalrClient = createSignalRClient({
        state,
        onSessionExpired: handleSessionExpired,
        onPresenceChanged: presence.handlePresenceChanged,
        onRefreshPresence: () => presence.fetchPresence(),
        signalRLib: overrides.signalRLib || null
    });

    Promise.resolve(signalrClient.start()).then((started) => {
        if (!started && !expiredHandled) {
            presence.fetchPresence();
        }
    });

    const onVisibilityChange = () => {
        if (document.visibilityState !== 'visible' || expiredHandled) return;
        const now = Date.now();
        if (now - lastVisibilitySyncAt >= 10000) {
            lastVisibilitySyncAt = now;
            syncSidebarConversations(() => null);
        }
    };

    if (typeof document.addEventListener === 'function') {
        document.addEventListener('visibilitychange', onVisibilityChange);
    }

    return {
        state,
        signalrClient,
        presence,
        handleSessionExpired,
        destroy: () => {
            if (typeof document.removeEventListener === 'function') {
                document.removeEventListener('visibilitychange', onVisibilityChange);
            }
            presence.destroy();
            signalrClient.destroy();
        }
    };
}

if (typeof document !== 'undefined' && !globalThis.__AOS_TEST_MANUAL_SIDEBAR_INIT__) {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => initSidebar());
    } else {
        initSidebar();
    }
}
