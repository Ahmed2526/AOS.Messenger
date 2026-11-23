/**
 * AOS Messenger - Isolated Composer Module
 *
 * Enforces per-conversation draft isolation, pre-switch transient reset (reply/error/policy/hooks),
 * server-driven policy locking (canSend = !isBlocked, soft-deleted peer informational banner),
 * and HTTP POST submission with fresh crypto.randomUUID() IdempotencyKeys and epoch checks.
 */

import { sendMessage as apiSendMessage } from './api-client.js';
import { getMessageId, insertMessageOrdered, scrollToBottom } from './message-renderer.js';
import { updateSidebarLastMessage } from './conversation-list.js';

const POLICY_BASE_CLASS = 'px-4 py-2 text-xs text-center font-medium';
const POLICY_ERROR_CLASS = 'px-4 py-2 bg-red-50 dark:bg-red-950/30 border-b border-red-200 dark:border-red-900/50 text-xs text-red-700 dark:text-red-300 text-center font-medium';
const POLICY_INFO_CLASS = 'px-4 py-2 bg-[#F0E9DF] dark:bg-[#45423E] border-b border-[#E3DCD7] dark:border-[#514D48] text-xs text-[#6E6560] dark:text-[#C9C0B9] text-center font-medium';

/**
 * Creates a composer controller bound to the active chat shell.
 * @param {{ state: object, typingEmitter?: object|null }} deps
 * @returns {object}
 */
export function createComposer({ state, typingEmitter = null }) {
    let isSending = false;
    let locked = true;
    let activeReplyTo = null;
    let actionErrorTimer = null;
    const transientResetHooks = new Set();

    let initialized = false;
    let boundInputEl = null;
    let boundSendBtn = null;
    let boundCancelReplyBtn = null;

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
        const shouldDisable = locked || isSending;
        if (inputEl) inputEl.disabled = shouldDisable;
        if (sendBtn) sendBtn.disabled = shouldDisable;
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
        if (typeof document === 'undefined' || !message || typeof document.getElementById !== 'function') return;
        const el = document.getElementById('chat-action-error');
        if (!el) return;
        el.textContent = message;
        if (el.classList) el.classList.remove('hidden');
        if (actionErrorTimer) clearTimeout(actionErrorTimer);
        actionErrorTimer = setTimeout(() => {
            hideActionError();
        }, 5000);
    }

    function hideActionError() {
        if (actionErrorTimer) {
            clearTimeout(actionErrorTimer);
            actionErrorTimer = null;
        }
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
        const el = document.getElementById('chat-action-error');
        if (el) {
            el.textContent = '';
            if (el.classList) el.classList.add('hidden');
        }
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
        if (!content) return null;

        const epoch = state.switchEpoch;
        const replyToMessageId = activeReplyTo ? activeReplyTo.id : null;

        isSending = true;
        if (sendBtn) sendBtn.disabled = true;
        inputEl.disabled = true;

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
            const previewText = sentMessage?.isDeletedForEveryone
                ? 'This message was deleted'
                : (sentMessage?.content ?? content);

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
                const currentSendBtn = getSendBtn();
                const currentInput = getInputEl();
                if (currentSendBtn) currentSendBtn.disabled = locked;
                if (currentInput) {
                    currentInput.disabled = locked;
                    if (!locked && typeof currentInput.focus === 'function') {
                        currentInput.focus();
                    }
                }
            }
        }
    }

    const onInputResize = () => {
        adjustTextareaHeight(getInputEl());
    };

    const onInputKeyDown = (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
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
        boundCancelReplyBtn = typeof document.getElementById === 'function'
            ? document.getElementById('cancel-reply-btn')
            : null;

        if (boundInputEl && typeof boundInputEl.addEventListener === 'function') {
            boundInputEl.addEventListener('input', onInputResize);
            boundInputEl.addEventListener('keydown', onInputKeyDown);
        }
        if (boundSendBtn && typeof boundSendBtn.addEventListener === 'function') {
            boundSendBtn.addEventListener('click', onSendClick);
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
        if (boundInputEl && typeof boundInputEl.removeEventListener === 'function') {
            boundInputEl.removeEventListener('input', onInputResize);
            boundInputEl.removeEventListener('keydown', onInputKeyDown);
        }
        if (boundSendBtn && typeof boundSendBtn.removeEventListener === 'function') {
            boundSendBtn.removeEventListener('click', onSendClick);
        }
        if (boundCancelReplyBtn && typeof boundCancelReplyBtn.removeEventListener === 'function') {
            boundCancelReplyBtn.removeEventListener('click', onCancelReplyClick);
        }
        if (typeof document !== 'undefined' && typeof document.removeEventListener === 'function') {
            document.removeEventListener('click', onDocumentReplyClick);
        }
        boundInputEl = null;
        boundSendBtn = null;
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
        sendMessage
    };
}
