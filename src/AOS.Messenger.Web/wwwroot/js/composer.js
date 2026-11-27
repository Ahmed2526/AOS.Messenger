/**
 * AOS Messenger - Isolated Composer Module
 *
 * Enforces per-conversation draft isolation, pre-switch transient reset (reply/error/policy/hooks),
 * server-driven policy locking (canSend = !isBlocked, soft-deleted peer informational banner),
 * and HTTP POST submission with fresh crypto.randomUUID() IdempotencyKeys and epoch checks.
 */

import { sendMessage as apiSendMessage } from './api-client.js';
import { formatMessagePreview, getMessageId, insertMessageOrdered, scrollToBottom } from './message-renderer.js';
import { updateSidebarLastMessage } from './conversation-list.js';
import { evictUserAvatarCache, closeAvatarLightboxIfUser } from './avatar-loader.js';
import { showChatActionError, hideChatActionError } from './chat-action-error.js';

const POLICY_BASE_CLASS = 'px-4 py-2 text-xs text-center font-medium';
const POLICY_ERROR_CLASS = 'px-4 py-2 bg-red-50 dark:bg-red-950/30 border-b border-red-200 dark:border-red-900/50 text-xs text-red-700 dark:text-red-300 text-center font-medium';
const POLICY_INFO_CLASS = 'px-4 py-2 bg-[#F0E9DF] dark:bg-[#45423E] border-b border-[#E3DCD7] dark:border-[#514D48] text-xs text-[#6E6560] dark:text-[#C9C0B9] text-center font-medium';

/**
 * Creates a composer controller bound to the active chat shell.
 * @param {{ state: object, typingEmitter?: object|null, emojiPicker?: object|null, emojiAutocomplete?: object|null }} deps
 * @returns {object}
 */
export function createComposer({ state, typingEmitter = null, emojiPicker = null, emojiAutocomplete = null, mediaUploader = null, onStickerSent = null }) {
    let isSending = false;
    let locked = true;
    let activeReplyTo = null;
    const transientResetHooks = new Set();

    let initialized = false;
    let boundInputEl = null;
    let boundSendBtn = null;
    let boundCancelReplyBtn = null;
    let boundEmojiBtn = null;
    let detachEmojiAutocomplete = null;

    function getInputEl() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('message-content-input')
            : null;
    }

    function getSendBtn() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('send-message-btn')
            : null;
    }

    function getEmojiBtn() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('composer-emoji-btn')
            : null;
    }

    function closeEmojiOverlays() {
        if (emojiPicker && typeof emojiPicker.close === 'function') {
            emojiPicker.close();
        }
        if (emojiAutocomplete && typeof emojiAutocomplete.close === 'function') {
            emojiAutocomplete.close();
        }
    }

    function adjustTextareaHeight(inputEl) {
        if (!inputEl || !inputEl.style) return;
        inputEl.style.height = 'auto';
        if (inputEl.value && typeof inputEl.scrollHeight === 'number') {
            inputEl.style.height = `${Math.min(inputEl.scrollHeight, 128)}px`;
        }
    }

    function getText() {
        const inputEl = getInputEl();
        return inputEl ? (inputEl.value || '') : '';
    }

    function setText(text) {
        const inputEl = getInputEl();
        if (!inputEl) return;
        inputEl.value = text === null || text === undefined ? '' : String(text);
        adjustTextareaHeight(inputEl);
    }

    function syncDisabledState() {
        const inputEl = getInputEl();
        const sendBtn = getSendBtn();
        const emojiBtn = getEmojiBtn();
        const shouldDisable = locked || isSending;
        if (inputEl) inputEl.disabled = shouldDisable;
        if (sendBtn) sendBtn.disabled = shouldDisable;
        if (emojiBtn) emojiBtn.disabled = shouldDisable;
        if (mediaUploader && typeof mediaUploader.setLocked === 'function') {
            mediaUploader.setLocked(shouldDisable);
        }
        if (shouldDisable) {
            closeEmojiOverlays();
        }
    }

    function setLocked(isLocked) {
        locked = !!isLocked;
        syncDisabledState();
    }

    function showPolicyNotice(message, variant = 'error') {
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
        const noticeEl = document.getElementById('chat-policy-notice');
        if (!noticeEl) return;
        noticeEl.textContent = message || '';
        noticeEl.className = variant === 'info' ? POLICY_INFO_CLASS : POLICY_ERROR_CLASS;
    }

    function hidePolicyNotice() {
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
        const noticeEl = document.getElementById('chat-policy-notice');
        if (!noticeEl) return;
        noticeEl.textContent = '';
        noticeEl.className = `hidden ${POLICY_BASE_CLASS}`;
    }

    function applyConversationPolicy(meta) {
        if (!meta) {
            setLocked(true);
            hidePolicyNotice();
            return;
        }

        const isBlocked = !!(meta.isBlocked ?? meta.IsBlocked);
        const isPeerDeleted = !!(meta.isPeerDeleted ?? meta.IsPeerDeleted);
        const rawCanSend = meta.canSend !== undefined ? meta.canSend : meta.CanSend;
        const canSend = rawCanSend !== undefined ? !!rawCanSend : !isBlocked;

        if (isBlocked || !canSend) {
            setLocked(true);
            showPolicyNotice('You cannot send messages in this conversation.', 'error');
        } else if (isPeerDeleted) {
            setLocked(false);
            showPolicyNotice(
                'This account has been deleted. Messages will only be visible if the account is recovered.',
                'info'
            );
        } else {
            setLocked(false);
            hidePolicyNotice();
        }
    }

    function setReply(id, sender, preview) {
        if (!id || locked || !state.activeConversationId) return;
        activeReplyTo = { id: String(id), sender: sender || '', preview: preview || '' };
        if (typeof document !== 'undefined' && typeof document.getElementById === 'function') {
            const replyBanner = document.getElementById('composer-reply-banner');
            const replySenderEl = document.getElementById('reply-banner-sender');
            const replyPreviewEl = document.getElementById('reply-banner-preview');
            if (replySenderEl) replySenderEl.textContent = sender || '';
            if (replyPreviewEl) replyPreviewEl.textContent = preview || '';
            if (replyBanner && replyBanner.classList) replyBanner.classList.remove('hidden');
            const inputEl = getInputEl();
            if (inputEl && typeof inputEl.focus === 'function') inputEl.focus();
        }
    }

    function clearReply() {
        activeReplyTo = null;
        if (typeof document !== 'undefined' && typeof document.getElementById === 'function') {
            const replyBanner = document.getElementById('composer-reply-banner');
            const replySenderEl = document.getElementById('reply-banner-sender');
            const replyPreviewEl = document.getElementById('reply-banner-preview');
            if (replyBanner && replyBanner.classList) replyBanner.classList.add('hidden');
            if (replySenderEl) replySenderEl.textContent = '';
            if (replyPreviewEl) replyPreviewEl.textContent = '';
        }
    }

    function showActionError(message) {
        showChatActionError(message, 5000);
    }

    function hideActionError() {
        hideChatActionError();
    }

    /**
     * Registers an optional transient reset hook (e.g., future media attachments).
     * @param {Function} hookFn
     * @returns {Function} Unsubscribe callback
     */
    function registerTransientResetHook(hookFn) {
        if (typeof hookFn !== 'function') return () => {};
        transientResetHooks.add(hookFn);
        return () => transientResetHooks.delete(hookFn);
    }

    /**
     * Clears transient composer state before switching conversations so reply, error,
     * policy banners, and pending hooks from Chat A can never leak into Chat B.
     */
    function resetTransientState() {
        isSending = false;
        clearReply();
        hideActionError();
        hidePolicyNotice();
        closeEmojiOverlays();

        const inputEl = getInputEl();
        if (inputEl) {
            inputEl.value = '';
            if (inputEl.style) inputEl.style.height = 'auto';
        }

        for (const fn of transientResetHooks) {
            try {
                fn();
            } catch (err) {
                console.warn('[Composer] Transient reset hook error:', err);
            }
        }
    }

    async function sendMessage() {
        const inputEl = getInputEl();
        const sendBtn = getSendBtn();
        if (isSending || locked || !inputEl) return null;

        const targetConvId = state.activeConversationId;
        if (!targetConvId) return null;

        const content = (inputEl.value || '').trim();
        const hasPendingMedia = !!(mediaUploader && typeof mediaUploader.hasPendingFiles === 'function' && mediaUploader.hasPendingFiles());
        if (!content && !hasPendingMedia) return null;

        if (hasPendingMedia) {
            const replySnapshot = activeReplyTo ? { ...activeReplyTo } : null;
            if (typingEmitter && typeof typingEmitter.stopTyping === 'function') {
                typingEmitter.stopTyping();
            }
            closeEmojiOverlays();
            inputEl.value = '';
            if (inputEl.style) inputEl.style.height = 'auto';
            clearReply();
            hideActionError();
            if (typeof state.deleteDraft === 'function') {
                state.deleteDraft(targetConvId);
            }
            return mediaUploader.startUploadJob({
                conversationId: targetConvId,
                caption: content,
                replyTo: replySnapshot
            });
        }

        const epoch = state.switchEpoch;
        const replyToMessageId = activeReplyTo ? activeReplyTo.id : null;

        isSending = true;
        closeEmojiOverlays();
        syncDisabledState();

        if (typingEmitter && typeof typingEmitter.stopTyping === 'function') {
            typingEmitter.stopTyping();
        }

        const idempotencyKey = crypto.randomUUID();
        const payload = {
            content: content,
            idempotencyKey: idempotencyKey,
            replyToMessageId: replyToMessageId
        };

        try {
            const sentMessage = await apiSendMessage(targetConvId, payload);
            const msgId = getMessageId(sentMessage);
            const previewText = formatMessagePreview(sentMessage) || content;

            // Always sync sidebar preview and consume any saved draft for the target chat
            updateSidebarLastMessage(targetConvId, previewText, msgId, false, false, true);
            if (typeof state.deleteDraft === 'function') {
                state.deleteDraft(targetConvId);
            }

            // Guard against user switching chats while HTTP POST was in flight
            if (epoch !== state.switchEpoch) {
                return sentMessage;
            }

            const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
                ? document.getElementById('messages-container')
                : null;
            if (containerEl) {
                insertMessageOrdered(containerEl, sentMessage, state.currentUserId, state.activeMeta);
            }
            if (msgId && state.messagesById && typeof state.messagesById.set === 'function') {
                state.messagesById.set(msgId, sentMessage);
            }

            const currentInput = getInputEl();
            if (currentInput) {
                currentInput.value = '';
                if (currentInput.style) currentInput.style.height = 'auto';
            }
            clearReply();
            hideActionError();
            scrollToBottom(true);

            return sentMessage;
        } catch (err) {
            if (epoch !== state.switchEpoch) {
                return null;
            }
            if (err?.code === 'UNAUTHORIZED' || err?.code === 'CSRF_MISMATCH') {
                return null;
            }
            if (err?.status === 403 || err?.code === 'FORBIDDEN') {
                if (state.activeMeta && typeof state.activeMeta === 'object') {
                    state.activeMeta.isBlocked = true;
                    state.activeMeta.canSend = false;
                }
                const peerId = state.activeMeta?.otherParticipantId ?? state.activeMeta?.OtherParticipantId;
                if (peerId) {
                    evictUserAvatarCache(peerId);
                    closeAvatarLightboxIfUser(peerId);
                }
                setLocked(true);
                showPolicyNotice('You cannot send messages in this conversation.', 'error');
                return null;
            }
            if (err?.status === 429 || err?.code === 'RATE_LIMITED') {
                showActionError('You are sending messages too quickly. Please wait a moment.');
                return null;
            }
            showActionError(err?.message || 'Could not send message. Please try again.');
            return null;
        } finally {
            if (epoch === state.switchEpoch) {
                isSending = false;
                syncDisabledState();
                const currentInput = getInputEl();
                if (currentInput && !locked && typeof currentInput.focus === 'function') {
                    currentInput.focus();
                }
            }
        }
    }

    async function sendSticker(sticker) {
        if (isSending || locked || !sticker) return null;
        const stickerId = sticker.id || sticker.Id || (typeof sticker === 'string' ? sticker : '');
        if (!stickerId) return null;

        const targetConvId = state.activeConversationId;
        if (!targetConvId) return null;

        const epoch = state.switchEpoch;
        const replyToMessageId = activeReplyTo ? activeReplyTo.id : null;

        isSending = true;
        closeEmojiOverlays();
        syncDisabledState();

        if (typingEmitter && typeof typingEmitter.stopTyping === 'function') {
            typingEmitter.stopTyping();
        }

        const payload = {
            content: null,
            idempotencyKey: crypto.randomUUID(),
            replyToMessageId: replyToMessageId,
            stickerId: stickerId
        };

        try {
            const sentMessage = await apiSendMessage(targetConvId, payload);
            const msgId = getMessageId(sentMessage);
            const previewText = formatMessagePreview(sentMessage) || '🏷️ Sticker';

            updateSidebarLastMessage(targetConvId, previewText, msgId, false, false, true);
            if (typeof onStickerSent === 'function') {
                onStickerSent(sticker);
            }

            if (epoch !== state.switchEpoch) {
                return sentMessage;
            }

            const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
                ? document.getElementById('messages-container')
                : null;
            if (containerEl) {
                insertMessageOrdered(containerEl, sentMessage, state.currentUserId, state.activeMeta);
            }
            if (msgId && state.messagesById && typeof state.messagesById.set === 'function') {
                state.messagesById.set(msgId, sentMessage);
            }

            clearReply();
            hideActionError();
            scrollToBottom(true);
            return sentMessage;
        } catch (err) {
            if (epoch !== state.switchEpoch) return null;
            if (err?.code === 'UNAUTHORIZED' || err?.code === 'CSRF_MISMATCH') return null;
            if (err?.status === 403 || err?.code === 'FORBIDDEN') {
                if (state.activeMeta && typeof state.activeMeta === 'object') {
                    state.activeMeta.isBlocked = true;
                    state.activeMeta.canSend = false;
                }
                const peerId = state.activeMeta?.otherParticipantId ?? state.activeMeta?.OtherParticipantId;
                if (peerId) {
                    evictUserAvatarCache(peerId);
                    closeAvatarLightboxIfUser(peerId);
                }
                setLocked(true);
                showPolicyNotice('You cannot send messages in this conversation.', 'error');
                return null;
            }
            if (err?.status === 429 || err?.code === 'RATE_LIMITED') {
                showActionError('You are sending messages too quickly. Please wait a moment.');
                return null;
            }
            showActionError(err?.message || 'Could not send sticker. Please try again.');
            return null;
        } finally {
            if (epoch === state.switchEpoch) {
                isSending = false;
                syncDisabledState();
            }
        }
    }

    const onInputResize = () => {
        adjustTextareaHeight(getInputEl());
    };

    const onInputKeyDown = (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            if (emojiAutocomplete && typeof emojiAutocomplete.isOpen === 'function' && emojiAutocomplete.isOpen()) {
                return;
            }
            e.preventDefault();
            sendMessage();
        }
    };

    const onEmojiBtnClick = (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
        if (locked || isSending) return;
        const inputEl = getInputEl();
        const emojiBtn = getEmojiBtn();
        if (!inputEl || !emojiBtn || inputEl.disabled) return;
        if (emojiAutocomplete && typeof emojiAutocomplete.close === 'function') {
            emojiAutocomplete.close();
        }
        const attachMenu = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('composer-attach-menu')
            : null;
        if (attachMenu && attachMenu.classList) {
            attachMenu.classList.add('hidden');
        }
        const attachBtn = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('composer-attach-btn')
            : null;
        if (attachBtn && typeof attachBtn.setAttribute === 'function') {
            attachBtn.setAttribute('aria-expanded', 'false');
        }
        if (emojiPicker && typeof emojiPicker.toggle === 'function') {
            emojiPicker.toggle(emojiBtn, inputEl, { surface: 'composer', initialMode: 'emoji' });
        }
    };

    const onSendClick = (e) => {
        e.preventDefault();
        sendMessage();
    };

    const onCancelReplyClick = (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        clearReply();
    };

    const onDocumentReplyClick = (e) => {
        if (!e.target || typeof e.target.closest !== 'function') return;
        const btn = e.target.closest('.reply-action-btn');
        if (!btn) return;
        const id = btn.getAttribute('data-id');
        const sender = btn.getAttribute('data-sender');
        const preview = btn.getAttribute('data-preview');
        setReply(id, sender, preview);
    };

    function init() {
        if (initialized || typeof document === 'undefined') return;
        boundInputEl = getInputEl();
        boundSendBtn = getSendBtn();
        boundEmojiBtn = getEmojiBtn();
        boundCancelReplyBtn = typeof document.getElementById === 'function'
            ? document.getElementById('cancel-reply-btn')
            : null;

        if (boundInputEl && typeof boundInputEl.addEventListener === 'function') {
            boundInputEl.addEventListener('input', onInputResize);
            boundInputEl.addEventListener('keydown', onInputKeyDown);
            if (emojiAutocomplete && typeof emojiAutocomplete.attach === 'function') {
                detachEmojiAutocomplete = emojiAutocomplete.attach(boundInputEl);
            }
        }
        if (boundSendBtn && typeof boundSendBtn.addEventListener === 'function') {
            boundSendBtn.addEventListener('click', onSendClick);
        }
        if (boundEmojiBtn && typeof boundEmojiBtn.addEventListener === 'function') {
            boundEmojiBtn.addEventListener('click', onEmojiBtnClick);
        }
        if (boundCancelReplyBtn && typeof boundCancelReplyBtn.addEventListener === 'function') {
            boundCancelReplyBtn.addEventListener('click', onCancelReplyClick);
        }
        if (typeof document.addEventListener === 'function') {
            document.addEventListener('click', onDocumentReplyClick);
        }

        initialized = true;
    }

    function destroy() {
        if (!initialized) return;
        hideActionError();
        closeEmojiOverlays();
        if (detachEmojiAutocomplete) {
            detachEmojiAutocomplete();
            detachEmojiAutocomplete = null;
        }
        if (boundInputEl && typeof boundInputEl.removeEventListener === 'function') {
            boundInputEl.removeEventListener('input', onInputResize);
            boundInputEl.removeEventListener('keydown', onInputKeyDown);
        }
        if (boundSendBtn && typeof boundSendBtn.removeEventListener === 'function') {
            boundSendBtn.removeEventListener('click', onSendClick);
        }
        if (boundEmojiBtn && typeof boundEmojiBtn.removeEventListener === 'function') {
            boundEmojiBtn.removeEventListener('click', onEmojiBtnClick);
        }
        if (boundCancelReplyBtn && typeof boundCancelReplyBtn.removeEventListener === 'function') {
            boundCancelReplyBtn.removeEventListener('click', onCancelReplyClick);
        }
        if (typeof document !== 'undefined' && typeof document.removeEventListener === 'function') {
            document.removeEventListener('click', onDocumentReplyClick);
        }
        boundInputEl = null;
        boundSendBtn = null;
        boundEmojiBtn = null;
        boundCancelReplyBtn = null;
        initialized = false;
    }

    return {
        init,
        destroy,
        getText,
        setText,
        setLocked,
        applyConversationPolicy,
        showPolicyNotice,
        hidePolicyNotice,
        setReply,
        clearReply,
        getActiveReply: () => activeReplyTo,
        showActionError,
        hideActionError,
        registerTransientResetHook,
        resetTransientState,
        sendMessage,
        sendSticker
    };
}
