/**
 * AOS Messenger - Unified Zero-Refresh Chat Shell Entrypoint
 *
 * Boots the single-shell experience for both /chat and /chat/{conversationId:guid},
 * hydrating initial SSR state from #chat-bootstrap without duplicate fetches and
 * maintaining strictly one persistent SignalR WebSocket per browser tab.
 */

import { chatState, createChatState } from './chat-state.js';
import {
    configureApiClient,
    getConversationBundle,
    getMessagesPage
} from './api-client.js';
import * as messageRenderer from './message-renderer.js';
import { createChatRouter } from './chat-router.js';
import { createComposer } from './composer.js';
import { createSignalRClient } from './signalr-client.js';
import {
    setActiveRow,
    setDocumentBaseTitle,
    resetDocumentTitleState,
    updateDocumentTitleUnreadBadge,
    ensureSidebarBroadcastChannel,
    syncSidebarConversations
} from './conversation-list.js';
import { initChatActions } from './chat-actions.js';
import { setupPresence } from './presence.js';
import { setupTypingEmitter, setupTypingReceiver } from './typing.js';
import { initReceipts, resetReceiptsState } from './receipts.js';
import { setupEmojiPicker } from './reactions.js';
import { setupMessageActions } from './message-actions.js';
import { createEmojiPicker } from './emoji-picker.js';
import { createEmojiAutocomplete } from './emoji-autocomplete.js';

const DEFAULT_SHELL_TITLE = 'A♡S Messenger - Desktop App';

/**
 * Initializes the Unified Chat Shell when #chat-shell-root is present.
 * @param {{
 *   state?: object,
 *   signalRLib?: object|null,
 *   fetchImpl?: Function|null,
 *   navigateReplace?: Function|null,
 *   reloadPage?: Function|null
 * }} [overrides]
 * @returns {object|null} Shell controller for runtime or tests
 */
export function initChatShell(overrides = {}) {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return null;
    }

    const shellRoot = document.getElementById('chat-shell-root');
    if (!shellRoot) {
        return null;
    }

    const state = overrides.state || (overrides.isolatedState ? createChatState() : chatState);
    let signalrClient = null;
    let composerEmojiPicker = null;
    let emojiAutocomplete = null;
    let scrollReadDebounce = null;
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

    let expiredHandled = false;
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
        if (composerEmojiPicker) composerEmojiPicker.close();
        if (emojiAutocomplete) emojiAutocomplete.close();
        state.wipeAllSensitiveState();
        resetReceiptsState();
        resetDocumentTitleState();
        messageRenderer.wipeSensitiveDom();
        navigateReplace(loginUrl);
    }

    function handleIdentityChanged() {
        if (expiredHandled) return;
        expiredHandled = true;

        if (signalrClient && typeof signalrClient.stop === 'function') {
            signalrClient.stop();
        }
        if (composerEmojiPicker) composerEmojiPicker.close();
        if (emojiAutocomplete) emojiAutocomplete.close();
        state.wipeAllSensitiveState();
        resetReceiptsState();
        resetDocumentTitleState();
        messageRenderer.wipeSensitiveDom();
        navigateReplace('/chat');
    }

    const apiConfig = {
        onSessionExpired: handleSessionExpired,
        onCsrfMismatch: reloadCurrentPage
    };
    if ('fetchImpl' in overrides) {
        apiConfig.fetchImpl = overrides.fetchImpl;
    }
    configureApiClient(apiConfig);

    initChatActions();
    ensureSidebarBroadcastChannel();

    const sidebarListEl = document.getElementById('conversationList');
    const activeHeaderEl = document.getElementById('active-conversation-header');
    const typingContainerEl = document.getElementById('typing-indicator');
    const inputEl = document.getElementById('message-content-input');
    const streamEl = document.getElementById('message-stream');

    const getActiveConversationId = () => state.activeConversationId;
    const getCurrentUserId = () => state.currentUserId;
    const getSwitchEpoch = () => state.switchEpoch;

    const typingReceiver = setupTypingReceiver(null, getActiveConversationId, typingContainerEl);
    const presence = setupPresence(null, sidebarListEl, activeHeaderEl, { skipInitialFetch: true });
    const receipts = initReceipts(null, getActiveConversationId, getCurrentUserId);
    const emojiPicker = setupEmojiPicker(getActiveConversationId, getCurrentUserId, { getSwitchEpoch });
    composerEmojiPicker = createEmojiPicker({ getUserId: getCurrentUserId });
    emojiAutocomplete = createEmojiAutocomplete({ getUserId: getCurrentUserId });
    const messageActions = setupMessageActions(getActiveConversationId, getCurrentUserId, {
        getSwitchEpoch,
        emojiPicker: composerEmojiPicker,
        emojiAutocomplete
    });

    let composer = null;
    signalrClient = createSignalRClient({
        state,
        onSessionExpired: handleSessionExpired,
        onIdentityChanged: handleIdentityChanged,
        onConversationReconciled: (conversation) => {
            if (!conversation) return;
            messageRenderer.renderConversationHeader(conversation);
            const peerName = conversation.otherParticipantName ?? conversation.OtherParticipantName;
            setDocumentBaseTitle(peerName ? `${peerName} - A♡S Messenger` : DEFAULT_SHELL_TITLE);
            if (composer) {
                composer.applyConversationPolicy(conversation);
            }
        },
        onTypingIndicator: typingReceiver.handleTypingIndicator,
        onPresenceChanged: presence.handlePresenceChanged,
        onRefreshPresence: () => presence.fetchPresence(),
        signalRLib: overrides.signalRLib || null
    });

    const typingEmitter = setupTypingEmitter(signalrClient.connection, getActiveConversationId, inputEl);
    composer = createComposer({
        state,
        typingEmitter,
        emojiPicker: composerEmojiPicker,
        emojiAutocomplete
    });
    composer.init();

    function markVisibleIncoming() {
        if (!state.activeConversationId || state.pendingConversationId !== null) return;
        const containerEl = document.getElementById('messages-container') || document;
        receipts.checkAndMarkIncoming(containerEl);
    }

    const router = createChatRouter({
        state,
        api: { getConversationBundle, getMessagesPage },
        renderer: messageRenderer,
        hooks: {
            getDraftText: () => composer.getText(),
            onBeforeSwitch: (_prevId, nextId) => {
                typingEmitter.resetForSwitch();
                typingReceiver.resetForSwitch();
                receipts.resetForSwitch();
                emojiPicker.resetForSwitch();
                composerEmojiPicker.resetForSwitch();
                emojiAutocomplete.resetForSwitch();
                messageActions.resetForSwitch();
                presence.resetHeaderPresence();
                composer.resetTransientState();
                composer.setLocked(true);

                if (nextId) {
                    setActiveRow(nextId);
                }
            },
            onAfterSwitch: (nextId, conversation, bufferedEvents) => {
                setActiveRow(nextId);
                const peerName = conversation?.otherParticipantName ?? conversation?.OtherParticipantName;
                setDocumentBaseTitle(peerName ? `${peerName} - A♡S Messenger` : DEFAULT_SHELL_TITLE);

                composer.setText(state.getDraft(nextId));
                composer.applyConversationPolicy(conversation);

                signalrClient.replayBufferedEvents(nextId, bufferedEvents);
                signalrClient.notifyActiveConversation(nextId);
                presence.fetchPresence();
                markVisibleIncoming();
            },
            onSwitchToEmpty: (prevId) => {
                setActiveRow(null);
                setDocumentBaseTitle(DEFAULT_SHELL_TITLE);
                signalrClient.leaveActiveConversation(prevId);
            },
            onSwitchError: () => {
                setActiveRow(null);
                setDocumentBaseTitle(DEFAULT_SHELL_TITLE);
                signalrClient.leaveActiveConversation();
            },
            onSessionExpired: handleSessionExpired,
            onIdentityChanged: handleIdentityChanged
        }
    });

    // Hydrate SSR state from #chat-bootstrap without triggering an initial duplicate fetch
    router.init();

    if (state.activeConversationId) {
        document.querySelectorAll('.message-timestamp[data-created-at]').forEach((el) => {
            const formatted = messageRenderer.formatTime(el.getAttribute('data-created-at'));
            if (formatted) {
                el.textContent = formatted;
            }
        });
        setActiveRow(state.activeConversationId);
        const initialName = state.activeMeta?.otherParticipantName ?? state.activeMeta?.OtherParticipantName;
        setDocumentBaseTitle(initialName ? `${initialName} - A♡S Messenger` : DEFAULT_SHELL_TITLE);
        composer.applyConversationPolicy(state.activeMeta);
        messageRenderer.scrollToBottom(false);
        markVisibleIncoming();
    } else {
        setActiveRow(null);
        composer.setLocked(true);
        setDocumentBaseTitle(DEFAULT_SHELL_TITLE);
    }

    updateDocumentTitleUnreadBadge();
    Promise.resolve(signalrClient.start()).then((started) => {
        if (!started && !expiredHandled) {
            presence.fetchPresence();
        }
    });

    const onStreamScroll = () => {
        if (!state.activeConversationId || state.pendingConversationId !== null) return;
        if (streamEl && streamEl.scrollTop <= 40) {
            router.loadOlderMessages();
        }
        if (scrollReadDebounce) clearTimeout(scrollReadDebounce);
        scrollReadDebounce = setTimeout(() => {
            markVisibleIncoming();
        }, 300);
    };

    const onWindowFocus = () => {
        markVisibleIncoming();
    };

    const onVisibilityChange = () => {
        if (document.visibilityState !== 'visible') return;
        if (state.activeConversationId && state.pendingConversationId === null) {
            markVisibleIncoming();
        } else if (!state.activeConversationId && state.pendingConversationId === null) {
            const now = Date.now();
            if (now - lastVisibilitySyncAt >= 10000) {
                lastVisibilitySyncAt = now;
                syncSidebarConversations(() => state.activeConversationId);
            }
        }
    };

    const onDocumentSubmit = (e) => {
        const form = e.target;
        if (!form || typeof form.getAttribute !== 'function') return;
        if (form.hasAttribute('data-header-action-form')) {
            const convInput = form.querySelector('input[name="conversationId"]');
            if (!convInput || !convInput.value) {
                e.preventDefault();
                return;
            }
        }
        const promptMsg = form.getAttribute('data-confirm');
        if (promptMsg && typeof window !== 'undefined' && typeof window.confirm === 'function' && !window.confirm(promptMsg)) {
            e.preventDefault();
        }
    };

    if (streamEl && typeof streamEl.addEventListener === 'function') {
        streamEl.addEventListener('scroll', onStreamScroll);
    }
    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('focus', onWindowFocus);
    }
    if (typeof document.addEventListener === 'function') {
        document.addEventListener('visibilitychange', onVisibilityChange);
        document.addEventListener('submit', onDocumentSubmit);
    }

    function destroy() {
        if (scrollReadDebounce) {
            clearTimeout(scrollReadDebounce);
            scrollReadDebounce = null;
        }
        if (streamEl && typeof streamEl.removeEventListener === 'function') {
            streamEl.removeEventListener('scroll', onStreamScroll);
        }
        if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
            window.removeEventListener('focus', onWindowFocus);
        }
        if (typeof document.removeEventListener === 'function') {
            document.removeEventListener('visibilitychange', onVisibilityChange);
            document.removeEventListener('submit', onDocumentSubmit);
        }
        composer.destroy();
        router.destroy();
        typingEmitter.destroy();
        typingReceiver.destroy();
        presence.destroy();
        receipts.destroy();
        emojiPicker.destroy();
        composerEmojiPicker.destroy();
        emojiAutocomplete.destroy();
        messageActions.destroy();
        signalrClient.destroy();
    }

    return {
        state,
        router,
        composer,
        signalrClient,
        handleSessionExpired,
        destroy
    };
}

if (typeof document !== 'undefined' && !globalThis.__AOS_TEST_MANUAL_SHELL_INIT__) {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => initChatShell());
    } else {
        initChatShell();
    }
}
