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
    syncSidebarConversations,
    updateSidebarLastMessage,
    updateSidebarPeerProfile
} from './conversation-list.js';
import {
    hydrateAllAvatars,
    bindAvatarLightboxDelegation,
    updateUserAvatarAcrossDom,
    closeAvatarLightbox,
    clearAvatarUrlCache
} from './avatar-loader.js';
import { initChatActions } from './chat-actions.js';
import { setupPresence } from './presence.js';
import { setupTypingEmitter, setupTypingReceiver } from './typing.js';
import { initReceipts, resetReceiptsState } from './receipts.js';
import { setupEmojiPicker } from './reactions.js';
import { setupMessageActions } from './message-actions.js';
import { createEmojiPicker } from './emoji-picker.js';
import { createEmojiAutocomplete } from './emoji-autocomplete.js';
import { createMediaLightbox } from './media-lightbox.js';
import { createMediaUploader } from './media-uploader.js';
import {
    createStickerPicker,
    hydrateStickerImages,
    clearStickerUrlCache
} from './sticker-picker.js';
import { createStickerCreator } from './sticker-creator.js';

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
    let router = null;
    let composerEmojiPicker = null;
    let stickerPicker = null;
    let stickerCreator = null;
    let emojiAutocomplete = null;
    let composer = null;
    let mediaLightbox = null;
    let mediaUploader = null;
    let scrollReadDebounce = null;
    let lastVisibilitySyncAt = 0;
    let topPaginationObserver = null;
    let paginationObserverArmRaf = null;
    let topPaginationArmed = false;
    let unbindAvatarLightbox = null;

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
        disarmTopPaginationObserver();
        if (composerEmojiPicker) composerEmojiPicker.close();
        if (stickerPicker) stickerPicker.invalidateCache();
        if (stickerCreator) stickerCreator.abortAndWipe();
        if (emojiAutocomplete) emojiAutocomplete.close();
        if (mediaUploader) mediaUploader.abortAllAndWipe();
        if (mediaLightbox) mediaLightbox.wipe();
        clearAvatarUrlCache();
        clearStickerUrlCache();
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
        disarmTopPaginationObserver();
        if (composerEmojiPicker) composerEmojiPicker.close();
        if (stickerPicker) stickerPicker.invalidateCache();
        if (stickerCreator) stickerCreator.abortAndWipe();
        if (emojiAutocomplete) emojiAutocomplete.close();
        if (mediaUploader) mediaUploader.abortAllAndWipe();
        if (mediaLightbox) mediaLightbox.wipe();
        clearAvatarUrlCache();
        clearStickerUrlCache();
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
    const closeAllPopoversBeforeModal = () => {
        if (composerEmojiPicker && typeof composerEmojiPicker.close === 'function') composerEmojiPicker.close();
        if (emojiAutocomplete && typeof emojiAutocomplete.close === 'function') emojiAutocomplete.close();
        if (emojiPicker && typeof emojiPicker.closePopover === 'function') emojiPicker.closePopover();
        const attachMenu = document.getElementById('composer-attach-menu');
        if (attachMenu && attachMenu.classList) attachMenu.classList.add('hidden');
        const attachBtn = document.getElementById('composer-attach-btn');
        if (attachBtn && typeof attachBtn.setAttribute === 'function') attachBtn.setAttribute('aria-expanded', 'false');
    };
    stickerCreator = createStickerCreator({
        onBeforeOpen: closeAllPopoversBeforeModal
    });
    stickerPicker = createStickerPicker({
        getActiveConversationId,
        getCurrentUserId,
        onSendSticker: (sticker) => composer?.sendSticker(sticker),
        onOpenCreator: (opts) => {
            closeAllPopoversBeforeModal();
            stickerCreator.open(opts);
        },
        onBeforeOpenModal: closeAllPopoversBeforeModal,
        onNotice: (msg) => composer?.showActionError(msg)
    });
    composerEmojiPicker = createEmojiPicker({
        getUserId: getCurrentUserId,
        stickerPicker
    });
    emojiAutocomplete = createEmojiAutocomplete({ getUserId: getCurrentUserId });
    mediaLightbox = createMediaLightbox();
    mediaUploader = createMediaUploader({
        state,
        onError: (msg) => composer?.showActionError(msg),
        onUnauthorized: handleSessionExpired,
        onMessageSent: (convId, sentMessage) => {
            const msgId = messageRenderer.getMessageId(sentMessage);
            const previewText = messageRenderer.formatMessagePreview(sentMessage);
            updateSidebarLastMessage(convId, previewText, msgId, false, false, true);
            if (typeof state.deleteDraft === 'function') {
                state.deleteDraft(convId);
            }
            const isSameActive = state.activeConversationId
                && String(state.activeConversationId).trim().toLowerCase() === String(convId).trim().toLowerCase()
                && state.pendingConversationId === null;
            if (isSameActive) {
                const containerEl = document.getElementById('messages-container');
                if (containerEl) {
                    messageRenderer.insertMessageOrdered(containerEl, sentMessage, state.currentUserId, state.activeMeta);
                }
                if (msgId && state.messagesById && typeof state.messagesById.set === 'function') {
                    state.messagesById.set(msgId, sentMessage);
                }
                messageRenderer.scrollToBottom(true);
            }
        }
    });
    mediaUploader.init();
    const messageActions = setupMessageActions(getActiveConversationId, getCurrentUserId, {
        getSwitchEpoch,
        emojiPicker: composerEmojiPicker,
        emojiAutocomplete,
        mediaLightbox,
        stickerPicker,
        loadOlderMessages: (opts) => (router ? router.loadOlderMessages(opts) : Promise.resolve({ loaded: 0, hasMore: false, error: null })),
        hasMoreOlder: () => Boolean(state.hasMore && state.nextCursor),
        onMessagesMutated: () => evaluatePaginationUi(),
        onMessageDeleted: (msgId) => {
            if (msgId && state.messagesById) {
                state.messagesById.delete(String(msgId));
            }
        },
        showActionNotice: (msg) => composer?.showActionError(msg),
        onToggleReaction: (msgId, emoji) => emojiPicker.toggleReactionForMessage(msgId, emoji),
        onReply: (id, sender, preview) => composer?.setReply(id, sender, preview),
        onBeforeOpenMobileSheet: closeAllPopoversBeforeModal
    });

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
        onIncomingMessage: (convId, msg) => mediaUploader.reconcileIncomingMessage(convId, msg),
        onMessageDeleted: (msgId) => {
            mediaLightbox.closeIfMessage(msgId);
            if (stickerPicker) stickerPicker.closeIfMessage(msgId);
            if (messageActions && typeof messageActions.closeMobileActionSheetIfMessage === 'function') {
                messageActions.closeMobileActionSheetIfMessage(msgId);
            }
        },
        onReconciledWindowReplaced: () => armTopPaginationObserver(),
        onUserProfileUpdated: (profile) => {
            if (!profile || expiredHandled) return;
            const userId = messageRenderer.getUserId(profile.userId ?? profile.UserId);
            const displayName = profile.displayName ?? profile.DisplayName ?? null;
            const avatarVersion = profile.avatarVersion !== undefined ? profile.avatarVersion : profile.AvatarVersion;
            if (!userId) return;

            updateUserAvatarAcrossDom(userId, avatarVersion, displayName);
            updateSidebarPeerProfile(userId, displayName);

            const activePeerId = messageRenderer.getUserId(
                state.activeMeta?.otherParticipantId ?? state.activeMeta?.OtherParticipantId
            );
            if (activePeerId && activePeerId.toLowerCase() === userId.toLowerCase()) {
                state.activeMeta = {
                    ...(state.activeMeta || {}),
                    otherParticipantAvatarVersion: avatarVersion || null,
                    ...(displayName ? { otherParticipantName: displayName } : {})
                };
                if (displayName) {
                    const nameEl = document.querySelector('#active-conversation-header [data-peer-name]');
                    if (nameEl) nameEl.textContent = displayName;
                }
            }
        },
        signalRLib: overrides.signalRLib || null
    });

    const typingEmitter = setupTypingEmitter(signalrClient.connection, getActiveConversationId, inputEl);
    composer = createComposer({
        state,
        typingEmitter,
        emojiPicker: composerEmojiPicker,
        emojiAutocomplete,
        mediaUploader,
        onStickerSent: (sticker) => {
            if (stickerPicker) stickerPicker.recordLocalRecent(sticker);
        }
    });
    composer.init();
    composer.registerTransientResetHook(() => mediaUploader.resetTrayForSwitch());

    function markVisibleIncoming() {
        if (!state.activeConversationId || state.pendingConversationId !== null) return;
        const containerEl = document.getElementById('messages-container') || document;
        receipts.checkAndMarkIncoming(containerEl);
    }

    const loadOlderBtnEl = document.getElementById('messages-load-older-btn');
    const loadOlderRetryBtnEl = document.getElementById('messages-load-older-retry-btn');

    function evaluatePaginationUi() {
        const containerEl = document.getElementById('messages-container');
        messageRenderer.syncPaginationUi({
            hasMore: state.hasMore,
            hasMessages: Boolean(containerEl && containerEl.children && containerEl.children.length > 0),
            hasPaginatedOlder: state.hasPaginatedOlder,
            isLoading: state.isLoadingOlder,
            errorMessage: null,
            streamEl
        });
    }

    function disarmTopPaginationObserver() {
        if (paginationObserverArmRaf !== null && typeof cancelAnimationFrame === 'function') {
            cancelAnimationFrame(paginationObserverArmRaf);
        }
        paginationObserverArmRaf = null;
        if (topPaginationObserver && typeof topPaginationObserver.disconnect === 'function') {
            topPaginationObserver.disconnect();
        }
        topPaginationObserver = null;
        topPaginationArmed = false;
    }

    function armTopPaginationObserver() {
        disarmTopPaginationObserver();
        const expectedEpoch = state.switchEpoch;
        const scheduleArm = () => {
            paginationObserverArmRaf = null;
            if (expectedEpoch !== state.switchEpoch || !state.activeConversationId || state.pendingConversationId !== null) {
                return;
            }
            topPaginationArmed = true;
            evaluatePaginationUi();

            if (typeof IntersectionObserver === 'function' && streamEl) {
                const sentinelEl = document.getElementById('messages-top-sentinel');
                if (sentinelEl) {
                    topPaginationObserver = new IntersectionObserver((entries) => {
                        if (!topPaginationArmed || !state.activeConversationId || state.pendingConversationId !== null || !state.hasMore || state.isLoadingOlder) {
                            return;
                        }
                        if (!entries.some(e => e.isIntersecting)) {
                            return;
                        }
                        if (streamEl.scrollHeight <= streamEl.clientHeight + 24 || streamEl.scrollTop > 240) {
                            return;
                        }
                        if (router) {
                            router.loadOlderMessages();
                        }
                    }, { root: streamEl, rootMargin: '240px 0px 0px 0px', threshold: 0 });
                    topPaginationObserver.observe(sentinelEl);
                }
            }
        };

        if (typeof requestAnimationFrame === 'function') {
            paginationObserverArmRaf = requestAnimationFrame(scheduleArm);
        } else {
            scheduleArm();
        }
    }

    router = createChatRouter({
        state,
        api: { getConversationBundle, getMessagesPage },
        renderer: messageRenderer,
        hooks: {
            getDraftText: () => composer.getText(),
            onBeforeSwitch: (_prevId, nextId) => {
                disarmTopPaginationObserver();
                typingEmitter.resetForSwitch();
                typingReceiver.resetForSwitch();
                receipts.resetForSwitch();
                emojiPicker.resetForSwitch();
                composerEmojiPicker.resetForSwitch();
                emojiAutocomplete.resetForSwitch();
                messageActions.resetForSwitch();
                mediaLightbox.resetForSwitch();
                if (stickerPicker) {
                    stickerPicker.closeStickerInfoModal();
                    if (typeof stickerPicker.closeConfirmModal === 'function') stickerPicker.closeConfirmModal();
                }
                if (stickerCreator) stickerCreator.close();
                closeAvatarLightbox();
                mediaUploader.resetTrayForSwitch();
                mediaUploader.clearRenderedPendingJobs();
                mediaUploader.setLocked(true);
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
                mediaUploader.renderPendingJobsForConversation(nextId);

                signalrClient.replayBufferedEvents(nextId, bufferedEvents);
                signalrClient.notifyActiveConversation(nextId);
                presence.fetchPresence();
                markVisibleIncoming();
                armTopPaginationObserver();
            },
            onSwitchToEmpty: (prevId) => {
                disarmTopPaginationObserver();
                setActiveRow(null);
                mediaLightbox.resetForSwitch();
                if (stickerPicker) {
                    stickerPicker.closeStickerInfoModal();
                    if (typeof stickerPicker.closeConfirmModal === 'function') stickerPicker.closeConfirmModal();
                }
                if (stickerCreator) stickerCreator.close();
                closeAvatarLightbox();
                mediaUploader.clearRenderedPendingJobs();
                setDocumentBaseTitle(DEFAULT_SHELL_TITLE);
                signalrClient.leaveActiveConversation(prevId);
            },
            onSwitchError: () => {
                disarmTopPaginationObserver();
                setActiveRow(null);
                mediaLightbox.resetForSwitch();
                if (stickerPicker) {
                    stickerPicker.closeStickerInfoModal();
                    if (typeof stickerPicker.closeConfirmModal === 'function') stickerPicker.closeConfirmModal();
                }
                if (stickerCreator) stickerCreator.close();
                closeAvatarLightbox();
                mediaUploader.clearRenderedPendingJobs();
                setDocumentBaseTitle(DEFAULT_SHELL_TITLE);
                signalrClient.leaveActiveConversation();
            },
            onSessionExpired: handleSessionExpired,
            onIdentityChanged: handleIdentityChanged
        }
    });

    // Hydrate SSR state from #chat-bootstrap without triggering an initial duplicate fetch
    router.init();
    hydrateAllAvatars(document);
    unbindAvatarLightbox = bindAvatarLightboxDelegation(document);

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
        messageRenderer.hydrateMediaElements(document.getElementById('messages-container'));
        hydrateStickerImages(document.getElementById('messages-container'));
        mediaUploader.renderPendingJobsForConversation(state.activeConversationId);
        messageRenderer.scrollToBottom(false);
        markVisibleIncoming();
        armTopPaginationObserver();
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
        if (streamEl && topPaginationArmed &&
            (streamEl.scrollHeight === 0 || streamEl.scrollHeight > streamEl.clientHeight + 24) &&
            streamEl.scrollTop <= 160) {
            router.loadOlderMessages();
        }
        if (scrollReadDebounce) clearTimeout(scrollReadDebounce);
        scrollReadDebounce = setTimeout(() => {
            markVisibleIncoming();
        }, 300);
    };

    const onManualLoadOlderClick = () => {
        if (router) {
            router.loadOlderMessages({ force: true });
        }
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
    if (loadOlderBtnEl && typeof loadOlderBtnEl.addEventListener === 'function') {
        loadOlderBtnEl.addEventListener('click', onManualLoadOlderClick);
    }
    if (loadOlderRetryBtnEl && typeof loadOlderRetryBtnEl.addEventListener === 'function') {
        loadOlderRetryBtnEl.addEventListener('click', onManualLoadOlderClick);
    }
    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('focus', onWindowFocus);
    }
    if (typeof document.addEventListener === 'function') {
        document.addEventListener('visibilitychange', onVisibilityChange);
        document.addEventListener('submit', onDocumentSubmit);
    }

    function destroy() {
        disarmTopPaginationObserver();
        if (scrollReadDebounce) {
            clearTimeout(scrollReadDebounce);
            scrollReadDebounce = null;
        }
        if (streamEl && typeof streamEl.removeEventListener === 'function') {
            streamEl.removeEventListener('scroll', onStreamScroll);
        }
        if (loadOlderBtnEl && typeof loadOlderBtnEl.removeEventListener === 'function') {
            loadOlderBtnEl.removeEventListener('click', onManualLoadOlderClick);
        }
        if (loadOlderRetryBtnEl && typeof loadOlderRetryBtnEl.removeEventListener === 'function') {
            loadOlderRetryBtnEl.removeEventListener('click', onManualLoadOlderClick);
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
        if (stickerPicker) stickerPicker.destroy();
        if (stickerCreator) stickerCreator.destroy();
        emojiAutocomplete.destroy();
        messageActions.destroy();
        mediaUploader.destroy();
        mediaLightbox.destroy();
        if (typeof unbindAvatarLightbox === 'function') {
            unbindAvatarLightbox();
            unbindAvatarLightbox = null;
        }
        closeAvatarLightbox();
        signalrClient.destroy();
    }

    return {
        state,
        router,
        composer,
        mediaUploader,
        mediaLightbox,
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
