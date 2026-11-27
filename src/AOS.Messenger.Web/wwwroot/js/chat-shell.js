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
import { setupMediaDragGuard } from './media-drag-guard.js';
import { createBottomScrollPinner } from './scroll-anchor.js';
import { createVoicePlayer } from './voice-player.js';
import { createVoiceRecorder } from './voice-recorder.js';

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
    let voicePlayer = null;
    let voiceRecorder = null;
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
        if (voiceRecorder) voiceRecorder.forceAbortAndWipe();
        if (voicePlayer) voicePlayer.destroy();
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
        if (voiceRecorder) voiceRecorder.forceAbortAndWipe();
        if (voicePlayer) voicePlayer.destroy();
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
    const messagesContainerEl = document.getElementById('messages-container');

    const bottomScrollPinner = createBottomScrollPinner({
        streamEl,
        containerEl: messagesContainerEl,
        shouldBlockPin: () => !state.activeConversationId || state.pendingConversationId !== null || state.isLoadingOlder
    });
    if (typeof messageRenderer.setBottomScrollHandler === 'function') {
        messageRenderer.setBottomScrollHandler((smooth) => {
            if (smooth) {
                bottomScrollPinner.pin({ smooth: true, settleMs: 2000, maxWindowMs: 4000 });
            } else {
                bottomScrollPinner.pin({ smooth: false, settleMs: 4000, maxWindowMs: 8000 });
            }
        });
    }

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
        onBeforeOpen: closeAllPopoversBeforeModal,
        onPackCreated: (pack) => {
            if (stickerPicker && typeof stickerPicker.refreshCatalog === 'function') {
                stickerPicker.refreshCatalog(pack?.id || null);
            }
        }
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
    voicePlayer = createVoicePlayer({
        onBeforeStartPlayback: () => {
            if (!voiceRecorder) return;
            if (voiceRecorder.getMode() === 'preview' && typeof voiceRecorder.pausePreview === 'function') {
                voiceRecorder.pausePreview();
            }
        }
    });
    if (typeof messageRenderer.setVoicePlayerCoordinator === 'function') {
        messageRenderer.setVoicePlayerCoordinator(voicePlayer);
    }
    mediaUploader = createMediaUploader({
        state,
        onError: (msg) => composer?.showActionError(msg),
        onUnauthorized: handleSessionExpired,
        onTrayChanged: () => composer?.syncDisabledState(),
        isVoiceDockActive: () => Boolean(voiceRecorder && voiceRecorder.isDirty()),
        onMessageSent: (convId, sentMessage, meta = null) => {
            const msgId = messageRenderer.getMessageId(sentMessage);
            const previewText = messageRenderer.formatMessagePreview(sentMessage);
            updateSidebarLastMessage(convId, previewText, msgId, false, false, true);
            if (!meta?.isVoice && typeof state.deleteDraft === 'function') {
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
            presence.refreshHeaderLastSeen();
            const peerName = conversation.otherParticipantName ?? conversation.OtherParticipantName;
            setDocumentBaseTitle(peerName ? `${peerName} - A♡S Messenger` : DEFAULT_SHELL_TITLE);
            if (composer) {
                composer.applyConversationPolicy(conversation);
            }
            const recBlocked = !!(conversation.isBlocked ?? conversation.IsBlocked);
            const recRawCanSend = conversation.canSend !== undefined ? conversation.canSend : conversation.CanSend;
            const recCanSend = recRawCanSend !== undefined ? !!recRawCanSend : !recBlocked;
            if (voiceRecorder && (recBlocked || !recCanSend)) {
                voiceRecorder.forceAbortAndWipe();
            }
        },
        onTypingIndicator: typingReceiver.handleTypingIndicator,
        onRecordingIndicator: typingReceiver.handleRecordingIndicator,
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
        onReconciledWindowReplaced: () => {
            bottomScrollPinner.pin({ smooth: false, settleMs: 4000, maxWindowMs: 8000 });
            armTopPaginationObserver();
        },
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
    voiceRecorder = createVoiceRecorder({
        connection: signalrClient.connection,
        getActiveConversationId,
        canRecordNow: () => {
            if (!state.activeConversationId || !state.activeMeta) return false;
            const blocked = !!(state.activeMeta.isBlocked ?? state.activeMeta.IsBlocked);
            const rawCanSend = state.activeMeta.canSend !== undefined ? state.activeMeta.canSend : state.activeMeta.CanSend;
            const canSend = rawCanSend !== undefined ? !!rawCanSend : !blocked;
            return canSend && !mediaUploader.hasPendingFiles();
        },
        onBeforeStart: () => {
            closeAllPopoversBeforeModal();
            if (voicePlayer) voicePlayer.stopAllPlayback();
        },
        onStartSendVoice: (voicePayload) => {
            if (!voicePayload || !voicePayload.conversationId) return null;
            const replySnapshot = composer.getActiveReply ? composer.getActiveReply() : null;
            composer.clearReply();
            composer.hideActionError();
            return mediaUploader.startVoiceUploadJob({
                conversationId: voicePayload.conversationId,
                blob: voicePayload.blob,
                fileName: voicePayload.fileName,
                contentType: voicePayload.contentType,
                durationMs: voicePayload.durationMs,
                waveformPeaks: voicePayload.waveformPeaks,
                replyTo: replySnapshot
            });
        },
        onDockStateChange: () => {
            if (mediaUploader && typeof mediaUploader.closeMenu === 'function') {
                mediaUploader.closeMenu();
            }
        },
        onError: (msg) => composer?.showActionError(msg)
    });
    voiceRecorder.init();
    composer.registerTransientResetHook(() => voiceRecorder.forceAbortAndWipe());

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
                        if (!topPaginationArmed || bottomScrollPinner.isActive() || !state.activeConversationId || state.pendingConversationId !== null || !state.hasMore || state.isLoadingOlder) {
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

    let pendingConfirmForm = null;
    let pendingConfirmResolve = null;
    let lastConfirmFocusedEl = null;
    let createdFallbackConfirmModal = null;

    function isChatConfirmModalOpen() {
        const modal = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('chat-confirm-modal')
            : null;
        return !!(modal && modal.classList && !modal.classList.contains('hidden'));
    }

    function closeChatConfirmModal({ restoreFocus = true, accepted = false } = {}) {
        pendingConfirmForm = null;
        const resolver = pendingConfirmResolve;
        pendingConfirmResolve = null;
        const restoreEl = restoreFocus ? lastConfirmFocusedEl : null;
        lastConfirmFocusedEl = null;
        const modal = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('chat-confirm-modal')
            : null;
        if (modal && modal.classList) modal.classList.add('hidden');
        if (restoreEl && typeof restoreEl.focus === 'function' && restoreEl.isConnected !== false) {
            restoreEl.focus();
        }
        if (typeof resolver === 'function') {
            resolver(Boolean(accepted));
        }
    }

    function confirmAsync({ title = 'Confirm action', message = '', acceptLabel = 'Confirm' } = {}) {
        if (pendingConfirmResolve) {
            return Promise.resolve(false);
        }
        const modal = ensureChatConfirmModal();
        if (!modal) {
            return Promise.resolve(true);
        }
        pendingConfirmForm = null;
        lastConfirmFocusedEl = typeof document !== 'undefined' ? document.activeElement : null;

        const titleEl = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-title') : null;
        const msgEl = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-message') : null;
        const cancelBtn = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-cancel-btn') : null;
        const acceptBtn = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-accept-btn') : null;
        if (titleEl) titleEl.textContent = title;
        if (msgEl) msgEl.textContent = message;
        if (acceptBtn) acceptBtn.textContent = acceptLabel;
        if (modal.classList) modal.classList.remove('hidden');
        if (cancelBtn && typeof cancelBtn.focus === 'function') {
            cancelBtn.focus();
        } else if (acceptBtn && typeof acceptBtn.focus === 'function') {
            acceptBtn.focus();
        }

        return new Promise((resolve) => {
            pendingConfirmResolve = resolve;
        });
    }

    router = createChatRouter({
        state,
        api: { getConversationBundle, getMessagesPage },
        renderer: messageRenderer,
        hooks: {
            getDraftText: () => composer.getText(),
            confirmBeforeSwitch: async () => {
                if (!voiceRecorder || !voiceRecorder.isDirty()) {
                    return true;
                }
                const discardConfirmed = await confirmAsync({
                    title: 'Discard unsent voice recording?',
                    message: 'You have an unsent voice recording in this chat. Switching chats will discard it.',
                    acceptLabel: 'Discard'
                });
                if (discardConfirmed && voiceRecorder) {
                    voiceRecorder.forceAbortAndWipe();
                }
                return discardConfirmed;
            },
            onBeforeSwitch: (_prevId, nextId) => {
                closeChatConfirmModal({ restoreFocus: false });
                if (voicePlayer) voicePlayer.stopAllPlayback();
                if (voiceRecorder) voiceRecorder.forceAbortAndWipe();
                bottomScrollPinner.disarm();
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
                presence.refreshHeaderLastSeen();
                presence.fetchPresence();
                markVisibleIncoming();
                armTopPaginationObserver();
            },
            onBeforeLoadOlder: () => {
                bottomScrollPinner.disarm();
            },
            onSwitchToEmpty: (prevId) => {
                bottomScrollPinner.disarm();
                disarmTopPaginationObserver();
                if (voicePlayer) voicePlayer.stopAllPlayback();
                if (voiceRecorder) voiceRecorder.forceAbortAndWipe();
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
                closeChatConfirmModal({ restoreFocus: false });
                if (voicePlayer) voicePlayer.stopAllPlayback();
                if (voiceRecorder) voiceRecorder.forceAbortAndWipe();
                bottomScrollPinner.disarm();
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
    const mediaDragGuard = setupMediaDragGuard();

    if (state.activeConversationId) {
        document.querySelectorAll('.message-timestamp[data-created-at]').forEach((el) => {
            const formatted = messageRenderer.formatTime(el.getAttribute('data-created-at'));
            if (formatted) {
                el.textContent = formatted;
            }
        });
        setActiveRow(state.activeConversationId);
        presence.refreshHeaderLastSeen();
        const initialName = state.activeMeta?.otherParticipantName ?? state.activeMeta?.OtherParticipantName;
        setDocumentBaseTitle(initialName ? `${initialName} - A♡S Messenger` : DEFAULT_SHELL_TITLE);
        composer.applyConversationPolicy(state.activeMeta);
        messageRenderer.hydrateMediaElements(document.getElementById('messages-container'));
        if (voicePlayer) voicePlayer.hydrateRoot(document.getElementById('messages-container'));
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
        if (streamEl && topPaginationArmed && !bottomScrollPinner.isActive() &&
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

    function ensureChatConfirmModal() {
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return null;
        const existing = document.getElementById('chat-confirm-modal');
        if (existing) return existing;
        if (typeof document.createElement !== 'function' || !document.body) return null;

        const modal = document.createElement('div');
        modal.id = 'chat-confirm-modal';
        modal.setAttribute('role', 'dialog');
        modal.setAttribute('aria-modal', 'true');
        modal.setAttribute('aria-labelledby', 'chat-confirm-title');
        modal.setAttribute('aria-describedby', 'chat-confirm-message');
        modal.className = 'hidden fixed inset-0 z-[75] flex items-center justify-center bg-black/55 p-4 backdrop-blur-xs';

        const card = document.createElement('div');
        card.className = 'bg-white dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] rounded-2xl shadow-xl w-full max-w-sm p-5 flex flex-col gap-3.5';

        const titleEl = document.createElement('h3');
        titleEl.id = 'chat-confirm-title';
        titleEl.className = 'text-sm font-bold text-[#2B2725] dark:text-[#F0E9E2]';
        const msgEl = document.createElement('p');
        msgEl.id = 'chat-confirm-message';
        msgEl.className = 'text-xs text-[#6E6560] dark:text-[#C9C0B9] mt-1 leading-relaxed break-words';
        card.appendChild(titleEl);
        card.appendChild(msgEl);

        const actionsRow = document.createElement('div');
        actionsRow.className = 'flex items-center justify-end gap-2 pt-1';
        const cancelBtn = document.createElement('button');
        cancelBtn.type = 'button';
        cancelBtn.id = 'chat-confirm-cancel-btn';
        cancelBtn.className = 'px-3.5 py-2 rounded-xl border border-[#E3DCD7] dark:border-[#514D48] text-xs font-semibold text-[#6E6560] dark:text-[#C9C0B9] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] transition-colors cursor-pointer disabled:opacity-50';
        cancelBtn.textContent = 'Cancel';
        const acceptBtn = document.createElement('button');
        acceptBtn.type = 'button';
        acceptBtn.id = 'chat-confirm-accept-btn';
        acceptBtn.className = 'px-4 py-2 rounded-xl bg-red-600 hover:bg-red-700 text-white text-xs font-bold active:scale-[0.98] transition-all shadow-2xs cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed';
        acceptBtn.textContent = 'Delete';
        actionsRow.appendChild(cancelBtn);
        actionsRow.appendChild(acceptBtn);
        card.appendChild(actionsRow);

        modal.appendChild(card);
        document.body.appendChild(modal);
        createdFallbackConfirmModal = modal;
        return modal;
    }

    function openChatConfirmModal(form, message) {
        const modal = ensureChatConfirmModal();
        if (!modal) return;
        pendingConfirmForm = form;
        lastConfirmFocusedEl = typeof document !== 'undefined' ? document.activeElement : null;

        const actionType = typeof form.getAttribute === 'function'
            ? form.getAttribute('data-header-action-form')
            : null;
        const titleText = actionType === 'clear'
            ? 'Clear chat history?'
            : actionType === 'delete'
                ? 'Delete conversation?'
                : 'Confirm action';
        const acceptText = actionType === 'clear'
            ? 'Clear history'
            : actionType === 'delete'
                ? 'Delete'
                : 'Confirm';

        const titleEl = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-title') : null;
        const msgEl = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-message') : null;
        const cancelBtn = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-cancel-btn') : null;
        const acceptBtn = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-accept-btn') : null;
        if (titleEl) titleEl.textContent = titleText;
        if (msgEl) msgEl.textContent = message;
        if (acceptBtn) acceptBtn.textContent = acceptText;
        if (modal.classList) modal.classList.remove('hidden');
        if (cancelBtn && typeof cancelBtn.focus === 'function') {
            cancelBtn.focus();
        } else if (acceptBtn && typeof acceptBtn.focus === 'function') {
            acceptBtn.focus();
        }
    }

    const onConfirmDocumentClick = (e) => {
        if (!isChatConfirmModalOpen() || !e || !e.target) return;
        const modal = typeof document.getElementById === 'function' ? document.getElementById('chat-confirm-modal') : null;
        const cancelBtn = typeof e.target.closest === 'function'
            ? e.target.closest('#chat-confirm-cancel-btn')
            : (e.target.id === 'chat-confirm-cancel-btn' ? e.target : null);
        if (cancelBtn || e.target === modal) {
            if (typeof e.preventDefault === 'function') e.preventDefault();
            closeChatConfirmModal({ restoreFocus: true });
            return;
        }
        const acceptBtn = typeof e.target.closest === 'function'
            ? e.target.closest('#chat-confirm-accept-btn')
            : (e.target.id === 'chat-confirm-accept-btn' ? e.target : null);
        if (acceptBtn) {
            if (typeof e.preventDefault === 'function') e.preventDefault();
            if (pendingConfirmResolve) {
                closeChatConfirmModal({ restoreFocus: false, accepted: true });
                return;
            }
            if (pendingConfirmForm) {
                const confirmedForm = pendingConfirmForm;
                closeChatConfirmModal({ restoreFocus: false, accepted: true });
                if (typeof confirmedForm.setAttribute === 'function') {
                    confirmedForm.setAttribute('data-confirm-bypass', 'true');
                }
                if (typeof confirmedForm.requestSubmit === 'function') {
                    confirmedForm.requestSubmit();
                } else if (typeof confirmedForm.submit === 'function') {
                    confirmedForm.submit();
                }
            }
        }
    };

    const onConfirmDocumentKeyDown = (e) => {
        if (!isChatConfirmModalOpen()) return;
        if (e.key === 'Escape') {
            if (typeof e.preventDefault === 'function') e.preventDefault();
            closeChatConfirmModal({ restoreFocus: true });
            return;
        }
        if (e.key === 'Tab' && typeof document !== 'undefined' && typeof document.getElementById === 'function') {
            const cancelBtn = document.getElementById('chat-confirm-cancel-btn');
            const acceptBtn = document.getElementById('chat-confirm-accept-btn');
            const focusables = [cancelBtn, acceptBtn].filter(btn => btn && !btn.disabled);
            if (focusables.length === 0) return;

            const currentIndex = focusables.indexOf(document.activeElement);
            if (e.shiftKey) {
                if (currentIndex <= 0) {
                    if (typeof e.preventDefault === 'function') e.preventDefault();
                    const targetBtn = focusables[focusables.length - 1];
                    if (typeof targetBtn.focus === 'function') targetBtn.focus();
                }
            } else if (currentIndex < 0 || currentIndex === focusables.length - 1) {
                if (typeof e.preventDefault === 'function') e.preventDefault();
                const targetBtn = focusables[0];
                if (typeof targetBtn.focus === 'function') targetBtn.focus();
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
        if (form.getAttribute('data-confirm-bypass') === 'true') {
            if (typeof form.removeAttribute === 'function') {
                form.removeAttribute('data-confirm-bypass');
            }
            return;
        }
        const promptMsg = form.getAttribute('data-confirm');
        if (promptMsg) {
            e.preventDefault();
            openChatConfirmModal(form, promptMsg);
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
    const onBeforeUnloadForVoice = (e) => {
        if (voiceRecorder && voiceRecorder.isDirty()) {
            e.preventDefault();
            e.returnValue = '';
        }
    };

    const onPageHideForVoice = () => {
        if (voiceRecorder) voiceRecorder.forceAbortAndWipe();
        if (voicePlayer) voicePlayer.stopAllPlayback();
    };

    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('focus', onWindowFocus);
        window.addEventListener('beforeunload', onBeforeUnloadForVoice);
        window.addEventListener('pagehide', onPageHideForVoice);
    }
    if (typeof document.addEventListener === 'function') {
        document.addEventListener('visibilitychange', onVisibilityChange);
        document.addEventListener('submit', onDocumentSubmit);
        document.addEventListener('click', onConfirmDocumentClick);
        document.addEventListener('keydown', onConfirmDocumentKeyDown);
    }

    function destroy() {
        disarmTopPaginationObserver();
        if (typeof messageRenderer.setBottomScrollHandler === 'function') {
            messageRenderer.setBottomScrollHandler(null);
        }
        bottomScrollPinner.destroy();
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
            window.removeEventListener('beforeunload', onBeforeUnloadForVoice);
            window.removeEventListener('pagehide', onPageHideForVoice);
        }
        if (typeof document.removeEventListener === 'function') {
            document.removeEventListener('visibilitychange', onVisibilityChange);
            document.removeEventListener('submit', onDocumentSubmit);
            document.removeEventListener('click', onConfirmDocumentClick);
            document.removeEventListener('keydown', onConfirmDocumentKeyDown);
        }
        closeChatConfirmModal({ restoreFocus: false });
        if (createdFallbackConfirmModal && createdFallbackConfirmModal.parentNode && typeof createdFallbackConfirmModal.parentNode.removeChild === 'function') {
            createdFallbackConfirmModal.parentNode.removeChild(createdFallbackConfirmModal);
        }
        createdFallbackConfirmModal = null;
        composer.destroy();
        if (voiceRecorder) voiceRecorder.destroy();
        if (voicePlayer) voicePlayer.destroy();
        if (typeof messageRenderer.setVoicePlayerCoordinator === 'function') {
            messageRenderer.setVoicePlayerCoordinator(null);
        }
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
        if (mediaDragGuard) {
            mediaDragGuard.destroy();
        }
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
        voicePlayer,
        voiceRecorder,
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
