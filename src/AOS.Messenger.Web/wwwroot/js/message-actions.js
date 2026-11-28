/**
 * AOS Messenger - Message Actions Module
 *
 * Coordinates message-level user actions: Edit, Delete (Me & Everyone), and Reply.
 * Enforces server-side author constraints, 3-hour delete window checks, and safe DOM manipulation.
 *
 * Invariants:
 * - Pure DOM construction with document.createElement and .textContent (zero innerHTML).
 * - CSRF token included via RequestVerificationToken header.
 * - Modals have class 'hidden' by default so they never block pointer events.
 */

import {
    editMessage as apiEditMessage,
    deleteForMe as apiDeleteForMe,
    deleteForEveryone as apiDeleteForEveryone
} from './api-client.js';
import {
    updateSidebarLastMessage
} from './conversation-list.js';
import {
    isLatestRenderedMessage,
    applyMessageEditedToRow,
    applyMessageDeletedForEveryone,
    applyMessageDeletedForMe,
    teardownRowVideos,
    evictMediaUrlCacheForMessage
} from './message-renderer.js';

function updateSidebarLastMessageIfLatest(conversationId, messageId, previewText) {
    const container = document.getElementById('messages-container');
    if (!conversationId || !messageId || !isLatestRenderedMessage(container, messageId)) return;
    updateSidebarLastMessage(conversationId, previewText, messageId, true);
}

/**
 * Initializes message actions for the active conversation.
 * @param {string} conversationId
 * @param {string} currentUserId
 */
export function setupMessageActions(convIdOrGetter, userIdOrGetter, options = {}) {
    if (typeof document === 'undefined' || !convIdOrGetter) {
        return {
            resetForSwitch: () => {},
            closeEditModal: () => {},
            closeDeleteModal: () => {},
            closeMobileActionSheet: () => {},
            closeMobileActionSheetIfMessage: () => {},
            destroy: () => {}
        };
    }

    const resolveConvId = typeof convIdOrGetter === 'function' ? convIdOrGetter : () => convIdOrGetter;
    const getSwitchEpoch = typeof options?.getSwitchEpoch === 'function' ? options.getSwitchEpoch : () => 0;
    const emojiPicker = options?.emojiPicker || null;
    const emojiAutocomplete = options?.emojiAutocomplete || null;
    const mediaLightbox = options?.mediaLightbox || null;
    const stickerPicker = options?.stickerPicker || null;
    const loadOlderMessagesFn = typeof options?.loadOlderMessages === 'function' ? options.loadOlderMessages : null;
    const hasMoreOlderFn = typeof options?.hasMoreOlder === 'function' ? options.hasMoreOlder : null;
    const onMessagesMutated = typeof options?.onMessagesMutated === 'function' ? options.onMessagesMutated : null;
    const onMessageDeleted = typeof options?.onMessageDeleted === 'function' ? options.onMessageDeleted : null;
    const showActionNotice = typeof options?.showActionNotice === 'function' ? options.showActionNotice : null;
    const onToggleReaction = typeof options?.onToggleReaction === 'function' ? options.onToggleReaction : null;
    const onReply = typeof options?.onReply === 'function' ? options.onReply : null;
    const onBeforeOpenMobileSheet = typeof options?.onBeforeOpenMobileSheet === 'function' ? options.onBeforeOpenMobileSheet : null;
    const isMobileViewport = typeof options?.isMobileViewport === 'function'
        ? options.isMobileViewport
        : () => typeof window !== 'undefined' && (window.innerWidth || document.documentElement?.clientWidth || 1024) < 640;
    const longPressDelayMs = Number.isFinite(options?.longPressDelayMs) ? Math.max(0, options.longPressDelayMs) : 450;
    const LONG_PRESS_MOVE_TOLERANCE_PX = 10;
    let isJumpingToReply = false;
    const MAX_REPLY_JUMP_PAGES = 3;

    // Modals
    const editModal = document.getElementById('edit-message-modal');
    const editInput = document.getElementById('edit-message-input');
    const editEmojiBtn = document.getElementById('edit-message-emoji-btn');
    const editErrorEl = document.getElementById('edit-message-error');
    const saveEditBtn = document.getElementById('save-edit-btn');
    const cancelEditBtn = document.getElementById('cancel-edit-btn');

    const deleteModal = document.getElementById('delete-message-modal');
    const deleteErrorEl = document.getElementById('delete-message-error');
    const deleteForMeBtn = document.getElementById('delete-for-me-btn');
    const deleteForEveryoneBtn = document.getElementById('delete-for-everyone-btn');
    const cancelDeleteBtn = document.getElementById('cancel-delete-btn');

    // WhatsApp-style Mobile Message Action Sheet
    const mobileSheet = document.getElementById('mobile-message-action-sheet');
    const mobileSheetTitle = document.getElementById('mobile-action-sheet-title');
    const mobileReactionsWrap = document.getElementById('mobile-action-reactions');
    const mobileReplyBtn = document.getElementById('mobile-action-reply-btn');
    const mobileCopyBtn = document.getElementById('mobile-action-copy-btn');
    const mobileStickerInfoBtn = document.getElementById('mobile-action-sticker-info-btn');
    const mobileStickerInfoIcon = document.getElementById('mobile-action-sticker-info-icon');
    const mobileEditBtn = document.getElementById('mobile-action-edit-btn');
    const mobileDeleteBtn = document.getElementById('mobile-action-delete-btn');
    const mobileCancelBtn = document.getElementById('mobile-action-cancel-btn');

    let activeEditMessageId = null;
    let activeEditHasAttachments = false;
    let activeDeleteMessageId = null;
    let activeMobileSheetContext = null;
    let highlightedMobileBubble = null;
    let longPressTimer = null;
    let longPressStart = null;
    let suppressNextClickUntil = 0;

    function setEditError(message) {
        if (!editErrorEl) return;
        if (message) {
            editErrorEl.textContent = message;
            editErrorEl.classList.remove('hidden');
        } else {
            editErrorEl.textContent = '';
            editErrorEl.classList.add('hidden');
        }
    }

    function setDeleteError(message) {
        if (!deleteErrorEl) return;
        if (message) {
            deleteErrorEl.textContent = message;
            deleteErrorEl.classList.remove('hidden');
        } else {
            deleteErrorEl.textContent = '';
            deleteErrorEl.classList.add('hidden');
        }
    }

    // --- EDIT MODAL HANDLING ---
    function openEditModal(messageId, currentContent, hasAttachments = false) {
        activeEditMessageId = messageId;
        activeEditHasAttachments = !!hasAttachments;
        setEditError('');
        if (editInput) {
            editInput.value = currentContent || '';
        }
        if (editModal) {
            editModal.classList.remove('hidden');
        }
        if (editInput) {
            editInput.focus();
        }
    }

    function closeEditModal() {
        activeEditMessageId = null;
        activeEditHasAttachments = false;
        setEditError('');
        if (emojiPicker && typeof emojiPicker.close === 'function') {
            emojiPicker.close();
        }
        if (emojiAutocomplete && typeof emojiAutocomplete.close === 'function') {
            emojiAutocomplete.close();
        }
        if (editInput) {
            editInput.value = '';
        }
        if (editModal) {
            editModal.classList.add('hidden');
        }
    }

    const onEditModalBackdropClick = (e) => {
        if (e.target === editModal) {
            closeEditModal();
        }
    };

    const onSaveEditClick = async () => {
        const newContent = editInput ? editInput.value.trim() : '';
        if (!newContent && !activeEditHasAttachments) {
            setEditError('Message content cannot be empty.');
            return;
        }
        const targetConvId = resolveConvId();
        const targetMsgId = activeEditMessageId;
        const epoch = getSwitchEpoch();
        if (!targetConvId || !targetMsgId) return;

        setEditError('');
        saveEditBtn.disabled = true;
        try {
            const updated = await apiEditMessage(targetConvId, targetMsgId, newContent || null);
            if (epoch !== getSwitchEpoch()) return;

            const updatedContent = updated?.content ?? updated?.Content ?? '';
            const updatedEditedAt = updated?.editedAtUtc ?? updated?.EditedAtUtc ?? null;
            const previewText = updated?.previewText ?? updated?.PreviewText ?? updatedContent;

            applyMessageEditedToRow(targetMsgId, updatedContent, updatedEditedAt, previewText);
            updateSidebarLastMessageIfLatest(targetConvId, targetMsgId, previewText);
            closeEditModal();
        } catch (err) {
            if (epoch !== getSwitchEpoch()) return;
            console.warn('[MessageActions] Edit failed:', err?.status || err);
            setEditError(err?.message || 'Failed to save message edit.');
        } finally {
            saveEditBtn.disabled = false;
        }
    };

    const onEditEmojiBtnClick = (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
        if (!editEmojiBtn || !editInput || editInput.disabled) return;
        if (emojiAutocomplete && typeof emojiAutocomplete.close === 'function') {
            emojiAutocomplete.close();
        }
        if (emojiPicker && typeof emojiPicker.toggle === 'function') {
            emojiPicker.toggle(editEmojiBtn, editInput, { surface: 'edit-modal' });
        }
    };

    const detachEditAutocomplete = editInput && emojiAutocomplete && typeof emojiAutocomplete.attach === 'function'
        ? emojiAutocomplete.attach(editInput)
        : null;

    if (editEmojiBtn) {
        editEmojiBtn.addEventListener('click', onEditEmojiBtnClick);
    }
    if (cancelEditBtn) {
        cancelEditBtn.addEventListener('click', closeEditModal);
    }
    if (editModal) {
        editModal.addEventListener('click', onEditModalBackdropClick);
    }
    if (saveEditBtn && editInput) {
        saveEditBtn.addEventListener('click', onSaveEditClick);
    }

    // --- DELETE MODAL HANDLING ---
    function openDeleteModal(messageId, isOutgoing, createdAtUtc) {
        activeDeleteMessageId = messageId;
        setDeleteError('');

        let eligibleForEveryone = false;
        if (isOutgoing && createdAtUtc) {
            const created = new Date(createdAtUtc).getTime();
            const now = Date.now();
            const threeHoursMs = 3 * 60 * 60 * 1000;
            eligibleForEveryone = (now - created) <= threeHoursMs;
        }

        if (deleteForEveryoneBtn) {
            if (eligibleForEveryone) {
                deleteForEveryoneBtn.classList.remove('hidden');
            } else {
                deleteForEveryoneBtn.classList.add('hidden');
            }
        }

        if (deleteModal) {
            deleteModal.classList.remove('hidden');
        }
    }

    function closeDeleteModal() {
        activeDeleteMessageId = null;
        setDeleteError('');
        if (deleteModal) {
            deleteModal.classList.add('hidden');
        }
    }

    function cancelLongPress() {
        if (longPressTimer !== null) {
            clearTimeout(longPressTimer);
            longPressTimer = null;
        }
        longPressStart = null;
    }

    function clearHighlightedMobileBubble() {
        if (highlightedMobileBubble && highlightedMobileBubble.classList) {
            highlightedMobileBubble.classList.remove('ring-2', 'ring-[#B3929D]', 'ring-offset-1');
        }
        highlightedMobileBubble = null;
    }

    function setElVisible(el, visible) {
        if (!el || !el.classList) return;
        if (visible) {
            el.classList.remove('hidden');
        } else {
            el.classList.add('hidden');
        }
    }

    function closeMobileActionSheet() {
        cancelLongPress();
        clearHighlightedMobileBubble();
        activeMobileSheetContext = null;
        if (mobileSheetTitle) {
            mobileSheetTitle.textContent = 'Message actions';
        }
        if (mobileSheet && mobileSheet.classList) {
            mobileSheet.classList.add('hidden');
        }
    }

    function closeMobileActionSheetIfMessage(messageId) {
        if (!messageId || !activeMobileSheetContext) return;
        if (String(activeMobileSheetContext.messageId) === String(messageId)) {
            closeMobileActionSheet();
        }
    }

    function getUserReactedEmojisForRow(row) {
        const reacted = new Set();
        if (!row || typeof row.querySelectorAll !== 'function') return reacted;
        const pills = row.querySelectorAll('.reaction-pill');
        for (let i = 0; i < pills.length; i++) {
            const pill = pills[i];
            const emoji = pill?.getAttribute('data-emoji');
            if (!emoji) continue;
            const title = pill.getAttribute('title') || '';
            if (title.startsWith('Remove ') || (pill.classList && pill.classList.contains('font-bold'))) {
                reacted.add(emoji);
            }
        }
        return reacted;
    }

    function openMobileActionSheet(row) {
        if (!mobileSheet || !row || row.getAttribute('data-deleted') === 'true') return;
        const messageId = row.getAttribute('data-message-id');
        if (!messageId) return;

        if (onBeforeOpenMobileSheet) {
            onBeforeOpenMobileSheet();
        }

        clearHighlightedMobileBubble();
        const bubble = typeof row.querySelector === 'function' ? row.querySelector('.message-bubble') : null;
        if (bubble && bubble.classList) {
            bubble.classList.add('ring-2', 'ring-[#B3929D]', 'ring-offset-1');
            highlightedMobileBubble = bubble;
        }

        const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
        const isSticker = row.getAttribute('data-is-sticker') === 'true';
        const isVoice = row.getAttribute('data-is-voice') === 'true';
        const hasAttachments = row.getAttribute('data-has-attachments') === 'true';
        const createdAt = row.getAttribute('data-created-at') || '';
        const contentEl = typeof row.querySelector === 'function' ? row.querySelector('.message-text-content') : null;
        const hasVisibleText = Boolean(contentEl && (!contentEl.classList || !contentEl.classList.contains('hidden')) && (contentEl.textContent || '').trim().length > 0);
        const textContent = hasVisibleText ? (contentEl.textContent || '') : '';
        const replyBtnInRow = typeof row.querySelector === 'function' ? row.querySelector('.reply-action-btn') : null;
        const senderName = replyBtnInRow?.getAttribute('data-sender') || '';
        const rawPreview = row.getAttribute('data-message-preview') || replyBtnInRow?.getAttribute('data-preview') || textContent || 'Message';
        const stickerTriggerInRow = isSticker && typeof row.querySelector === 'function'
            ? row.querySelector('[data-sticker-info-trigger="true"]')
            : null;
        const stickerFavorited = stickerTriggerInRow?.getAttribute('data-sticker-favorited') === 'true';
        const composerInput = document.getElementById('message-content-input');
        const canReply = !(composerInput && composerInput.disabled);

        activeMobileSheetContext = {
            row,
            messageId,
            isOutgoing,
            isSticker,
            isVoice,
            hasAttachments,
            createdAt,
            senderName,
            preview: rawPreview,
            textContent,
            stickerTriggerInRow
        };

        if (mobileSheetTitle) {
            mobileSheetTitle.textContent = rawPreview;
        }

        if (mobileReactionsWrap && typeof mobileReactionsWrap.querySelectorAll === 'function') {
            const reactedEmojis = getUserReactedEmojisForRow(row);
            const reactionBtns = mobileReactionsWrap.querySelectorAll('[data-mobile-react-emoji]');
            for (let i = 0; i < reactionBtns.length; i++) {
                const btn = reactionBtns[i];
                const emoji = btn.getAttribute('data-mobile-react-emoji') || '';
                const isSelected = reactedEmojis.has(emoji);
                btn.setAttribute('aria-pressed', isSelected ? 'true' : 'false');
                if (btn.classList) {
                    if (isSelected) {
                        btn.classList.add('bg-[#B3929D]/25', 'ring-1', 'ring-[#B3929D]');
                    } else {
                        btn.classList.remove('bg-[#B3929D]/25', 'ring-1', 'ring-[#B3929D]');
                    }
                }
            }
        }

        setElVisible(mobileReplyBtn, canReply);
        setElVisible(mobileCopyBtn, !isSticker && textContent.length > 0);
        setElVisible(mobileStickerInfoBtn, isSticker && Boolean(stickerTriggerInRow));
        if (mobileStickerInfoIcon) {
            mobileStickerInfoIcon.textContent = stickerFavorited ? 'star' : 'star_outline';
        }
        setElVisible(mobileEditBtn, isOutgoing && !isSticker && !isVoice);
        setElVisible(mobileDeleteBtn, true);

        mobileSheet.classList.remove('hidden');

        const focusTarget = (canReply && mobileReplyBtn) || mobileDeleteBtn || mobileCancelBtn;
        if (focusTarget && typeof focusTarget.focus === 'function') {
            focusTarget.focus();
        }
    }

    function resetForSwitch() {
        closeMobileActionSheet();
        closeEditModal();
        closeDeleteModal();
    }

    const onMobileSheetBackdropClick = (e) => {
        if (e.target === mobileSheet) {
            closeMobileActionSheet();
        }
    };

    const onMobileReactionClick = async (e) => {
        const btn = e.target && typeof e.target.closest === 'function'
            ? e.target.closest('[data-mobile-react-emoji]')
            : null;
        if (!btn || !activeMobileSheetContext) return;
        e.preventDefault();
        e.stopPropagation();
        const emoji = btn.getAttribute('data-mobile-react-emoji');
        const msgId = activeMobileSheetContext.messageId;
        closeMobileActionSheet();
        if (emoji && msgId && onToggleReaction) {
            await onToggleReaction(msgId, emoji);
        }
    };

    const onMobileReplyClick = (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        const ctx = activeMobileSheetContext;
        if (!ctx) return;
        closeMobileActionSheet();
        if (onReply) {
            onReply(ctx.messageId, ctx.senderName, ctx.preview);
        } else {
            const btn = ctx.row && typeof ctx.row.querySelector === 'function'
                ? ctx.row.querySelector('.reply-action-btn')
                : null;
            if (btn && typeof btn.click === 'function') btn.click();
        }
    };

    const onMobileCopyClick = async (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        const text = activeMobileSheetContext?.textContent || '';
        closeMobileActionSheet();
        if (!text) return;
        try {
            if (typeof navigator !== 'undefined' && navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
                await navigator.clipboard.writeText(text);
            }
        } catch {
            // Clipboard permission denied or unavailable in non-secure context
        }
    };

    const onMobileStickerInfoClick = (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        const ctx = activeMobileSheetContext;
        if (!ctx) return;
        const trigger = ctx.stickerTriggerInRow;
        closeMobileActionSheet();
        if (trigger && stickerPicker && typeof stickerPicker.openStickerInfoModal === 'function') {
            stickerPicker.openStickerInfoModal({
                id: trigger.getAttribute('data-sticker-id') || '',
                packId: trigger.getAttribute('data-sticker-pack-id') || '',
                packName: trigger.getAttribute('data-sticker-pack-name') || 'Stickers',
                packAuthor: trigger.getAttribute('data-sticker-pack-author') || 'Community Sticker',
                isAnimated: trigger.getAttribute('data-sticker-animated') === 'true',
                isSystem: trigger.getAttribute('data-sticker-system') === 'true',
                staticUrl: trigger.getAttribute('data-sticker-static-url') || null,
                emojiTags: trigger.getAttribute('data-sticker-tags') || '',
                isFavorited: trigger.getAttribute('data-sticker-favorited') === 'true'
            }, {
                conversationId: resolveConvId(),
                messageId: ctx.messageId
            });
        } else if (trigger && typeof trigger.click === 'function') {
            trigger.click();
        }
    };

    const onMobileEditClick = (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        const ctx = activeMobileSheetContext;
        if (!ctx || !ctx.isOutgoing || ctx.isSticker || ctx.isVoice) return;
        closeMobileActionSheet();
        openEditModal(ctx.messageId, ctx.textContent, ctx.hasAttachments);
    };

    const onMobileDeleteClick = (e) => {
        if (e && typeof e.preventDefault === 'function') e.preventDefault();
        const ctx = activeMobileSheetContext;
        if (!ctx) return;
        closeMobileActionSheet();
        openDeleteModal(ctx.messageId, ctx.isOutgoing, ctx.createdAt);
    };

    const isExcludedMobileActionTarget = (target) => Boolean(
        target.closest('.reaction-pill') ||
        target.closest('video') ||
        target.closest('.voice-player-control') ||
        target.closest('[data-voice-waveform]')
    );

    const onPointerDownForLongPress = (e) => {
        cancelLongPress();
        if (!mobileSheet || !isMobileViewport()) return;
        if (e.isPrimary === false) return;
        if (typeof e.button === 'number' && e.button !== 0) return;
        if (!e.target || typeof e.target.closest !== 'function') return;
        if (isExcludedMobileActionTarget(e.target)) return;

        const bubble = e.target.closest('.message-bubble');
        if (!bubble) return;
        const row = bubble.closest('.message-bubble-row');
        if (!row || row.getAttribute('data-deleted') === 'true') return;

        longPressStart = {
            x: typeof e.clientX === 'number' ? e.clientX : 0,
            y: typeof e.clientY === 'number' ? e.clientY : 0,
            row
        };
        longPressTimer = setTimeout(() => {
            const targetRow = longPressStart?.row || null;
            longPressTimer = null;
            longPressStart = null;
            if (!targetRow || !isMobileViewport() || targetRow.getAttribute('data-deleted') === 'true') return;
            suppressNextClickUntil = Date.now() + 500;
            try {
                if (typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function') {
                    navigator.vibrate(15);
                }
            } catch {}
            openMobileActionSheet(targetRow);
        }, longPressDelayMs);
    };

    const onPointerMoveForLongPress = (e) => {
        if (!longPressStart || longPressTimer === null) return;
        const dx = Math.abs((typeof e.clientX === 'number' ? e.clientX : 0) - longPressStart.x);
        const dy = Math.abs((typeof e.clientY === 'number' ? e.clientY : 0) - longPressStart.y);
        if (dx > LONG_PRESS_MOVE_TOLERANCE_PX || dy > LONG_PRESS_MOVE_TOLERANCE_PX) {
            cancelLongPress();
        }
    };

    const onPointerEndForLongPress = () => {
        cancelLongPress();
    };

    const onMobileBubbleContextMenu = (e) => {
        if (!mobileSheet || !isMobileViewport()) return;
        if (!e.target || typeof e.target.closest !== 'function') return;
        if (isExcludedMobileActionTarget(e.target)) return;
        const bubble = e.target.closest('.message-bubble');
        if (!bubble) return;
        const row = bubble.closest('.message-bubble-row');
        if (!row || row.getAttribute('data-deleted') === 'true') return;

        if (typeof e.preventDefault === 'function') e.preventDefault();
        cancelLongPress();
        openMobileActionSheet(row);
    };

    const onCaptureSuppressLongPressClick = (e) => {
        if (suppressNextClickUntil <= 0) return;
        const now = Date.now();
        if (now > suppressNextClickUntil) {
            suppressNextClickUntil = 0;
            return;
        }
        if (e.target && typeof e.target.closest === 'function' && e.target.closest('.message-bubble')) {
            suppressNextClickUntil = 0;
            if (typeof e.preventDefault === 'function') e.preventDefault();
            if (typeof e.stopPropagation === 'function') e.stopPropagation();
        }
    };

    const onWindowResizeForMobileSheet = () => {
        if (activeMobileSheetContext && !isMobileViewport()) {
            closeMobileActionSheet();
        }
    };

    const onDeleteModalBackdropClick = (e) => {
        if (e.target === deleteModal) {
            closeDeleteModal();
        }
    };

    const onEscapeKeyDown = (e) => {
        if (e.key === 'Escape') {
            if ((emojiPicker && typeof emojiPicker.isOpen === 'function' && emojiPicker.isOpen()) ||
                (emojiAutocomplete && typeof emojiAutocomplete.isOpen === 'function' && emojiAutocomplete.isOpen())) {
                return;
            }
            if (mobileSheet && !mobileSheet.classList.contains('hidden')) {
                closeMobileActionSheet();
                return;
            }
            if (editModal && !editModal.classList.contains('hidden')) {
                closeEditModal();
            }
            if (deleteModal && !deleteModal.classList.contains('hidden')) {
                closeDeleteModal();
            }
        }
    };

    const onDeleteForMeClick = async () => {
        const targetConvId = resolveConvId();
        const msgId = activeDeleteMessageId;
        const epoch = getSwitchEpoch();
        if (!targetConvId || !msgId) return;

        setDeleteError('');
        deleteForMeBtn.disabled = true;
        try {
            const container = document.getElementById('messages-container');
            const wasLatest = isLatestRenderedMessage(container, msgId);
            await apiDeleteForMe(targetConvId, msgId);
            if (epoch !== getSwitchEpoch()) return;

            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                teardownRowVideos(row);
                row.remove();
            }
            evictMediaUrlCacheForMessage(msgId);
            if (onMessageDeleted) {
                onMessageDeleted(msgId);
            }

            document.querySelectorAll(`[data-reply-parent-id="${msgId}"]`).forEach(el => el.remove());

            const remainingRows = container ? container.querySelectorAll('.message-bubble-row') : [];
            const hasMore = (hasMoreOlderFn ? Boolean(hasMoreOlderFn()) : false)
                || Boolean(container && typeof container.getAttribute === 'function' && container.getAttribute('data-has-more') === 'true');
            if (container && remainingRows.length === 0 && !hasMore) {
                const emptyPlaceholder = document.getElementById('no-messages-placeholder');
                if (emptyPlaceholder) {
                    emptyPlaceholder.classList.remove('hidden');
                    emptyPlaceholder.classList.add('flex');
                }
            }

            if (wasLatest) {
                let fallbackLastId = null;
                let fallbackPreview = 'No messages yet';
                if (remainingRows.length > 0) {
                    const prevRow = remainingRows[remainingRows.length - 1];
                    fallbackLastId = prevRow.getAttribute('data-message-id');
                    if (prevRow.getAttribute('data-deleted') === 'true') {
                        fallbackPreview = 'This message was deleted';
                    } else {
                        const attrPreview = prevRow.getAttribute('data-message-preview');
                        fallbackPreview = attrPreview !== null && attrPreview !== ''
                            ? attrPreview
                            : (prevRow.querySelector('.message-text-content')?.textContent || '');
                    }
                }

                applyMessageDeletedForMe(msgId, targetConvId, fallbackLastId, fallbackPreview, true, { hasMore });
            }

            if (mediaLightbox && typeof mediaLightbox.closeIfMessage === 'function') {
                mediaLightbox.closeIfMessage(msgId);
            }
            if (stickerPicker && typeof stickerPicker.closeIfMessage === 'function') {
                stickerPicker.closeIfMessage(msgId);
            }

            closeDeleteModal();
            if (onMessagesMutated) {
                onMessagesMutated();
            }
        } catch (err) {
            if (epoch !== getSwitchEpoch()) return;
            console.warn('[MessageActions] Error deleting for me:', err?.status || err);
            setDeleteError(err?.message || 'Failed to hide message.');
        } finally {
            deleteForMeBtn.disabled = false;
        }
    };

    const onDeleteForEveryoneClick = async () => {
        const targetConvId = resolveConvId();
        const msgId = activeDeleteMessageId;
        const epoch = getSwitchEpoch();
        if (!targetConvId || !msgId) return;

        setDeleteError('');
        deleteForEveryoneBtn.disabled = true;
        try {
            await apiDeleteForEveryone(targetConvId, msgId);
            if (epoch !== getSwitchEpoch()) return;

            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                applyMessageDeletedForEveryone(row, msgId);
            }
            if (mediaLightbox && typeof mediaLightbox.closeIfMessage === 'function') {
                mediaLightbox.closeIfMessage(msgId);
            }
            if (stickerPicker && typeof stickerPicker.closeIfMessage === 'function') {
                stickerPicker.closeIfMessage(msgId);
            }
            updateSidebarLastMessageIfLatest(targetConvId, msgId, 'This message was deleted');
            closeDeleteModal();
            if (onMessagesMutated) {
                onMessagesMutated();
            }
        } catch (err) {
            if (epoch !== getSwitchEpoch()) return;
            console.warn('[MessageActions] Error deleting for everyone:', err?.status || err);
            setDeleteError(err?.message || 'Failed to delete message for everyone.');
        } finally {
            deleteForEveryoneBtn.disabled = false;
        }
    };

    if (cancelDeleteBtn) {
        cancelDeleteBtn.addEventListener('click', closeDeleteModal);
    }
    if (deleteModal) {
        deleteModal.addEventListener('click', onDeleteModalBackdropClick);
    }
    document.addEventListener('keydown', onEscapeKeyDown);
    if (deleteForMeBtn) {
        deleteForMeBtn.addEventListener('click', onDeleteForMeClick);
    }
    if (deleteForEveryoneBtn) {
        deleteForEveryoneBtn.addEventListener('click', onDeleteForEveryoneClick);
    }
    if (mobileSheet) {
        mobileSheet.addEventListener('click', onMobileSheetBackdropClick);
    }
    if (mobileReactionsWrap) {
        mobileReactionsWrap.addEventListener('click', onMobileReactionClick);
    }
    if (mobileReplyBtn) {
        mobileReplyBtn.addEventListener('click', onMobileReplyClick);
    }
    if (mobileCopyBtn) {
        mobileCopyBtn.addEventListener('click', onMobileCopyClick);
    }
    if (mobileStickerInfoBtn) {
        mobileStickerInfoBtn.addEventListener('click', onMobileStickerInfoClick);
    }
    if (mobileEditBtn) {
        mobileEditBtn.addEventListener('click', onMobileEditClick);
    }
    if (mobileDeleteBtn) {
        mobileDeleteBtn.addEventListener('click', onMobileDeleteClick);
    }
    if (mobileCancelBtn) {
        mobileCancelBtn.addEventListener('click', closeMobileActionSheet);
    }

    const streamElForLongPress = document.getElementById('message-stream');
    if (streamElForLongPress && typeof streamElForLongPress.addEventListener === 'function') {
        streamElForLongPress.addEventListener('scroll', onPointerEndForLongPress, { passive: true });
    }
    document.addEventListener('pointerdown', onPointerDownForLongPress);
    document.addEventListener('pointermove', onPointerMoveForLongPress, { passive: true });
    document.addEventListener('pointerup', onPointerEndForLongPress);
    document.addEventListener('pointercancel', onPointerEndForLongPress);
    document.addEventListener('contextmenu', onMobileBubbleContextMenu);
    document.addEventListener('click', onCaptureSuppressLongPressClick, true);
    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('resize', onWindowResizeForMobileSheet);
    }

    const scrollAndHighlightRow = (row) => {
        if (!row) return;
        if (typeof row.scrollIntoView === 'function') {
            row.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
        if (row.classList) {
            row.classList.add('ring-2', 'ring-[#B3929D]', 'rounded-xl');
            setTimeout(() => {
                row.classList.remove('ring-2', 'ring-[#B3929D]', 'rounded-xl');
            }, 2000);
        }
    };

    const jumpToReplyParent = async (parentId) => {
        if (!parentId) return;
        const immediate = document.getElementById(`msg-${parentId}`);
        if (immediate) {
            scrollAndHighlightRow(immediate);
            return;
        }
        if (!loadOlderMessagesFn || isJumpingToReply) {
            if (!loadOlderMessagesFn && showActionNotice) {
                showActionNotice('Original message is older or no longer available.');
            }
            return;
        }

        const startEpoch = getSwitchEpoch();
        isJumpingToReply = true;
        try {
            for (let page = 0; page < MAX_REPLY_JUMP_PAGES; page++) {
                if (startEpoch !== getSwitchEpoch()) return;
                if (hasMoreOlderFn && !hasMoreOlderFn()) break;

                const res = await loadOlderMessagesFn({ force: true });
                if (startEpoch !== getSwitchEpoch()) return;

                const found = document.getElementById(`msg-${parentId}`);
                if (found) {
                    scrollAndHighlightRow(found);
                    return;
                }
                if (!res || res.error || !res.hasMore) break;
            }
            if (startEpoch === getSwitchEpoch() && showActionNotice) {
                showActionNotice('Original message is older or no longer available.');
            }
        } finally {
            isJumpingToReply = false;
        }
    };

    const onActionClick = (e) => {
        if (!e.target || typeof e.target.closest !== 'function') return;

        const editBtn = e.target.closest('[data-action="edit"]');
        if (editBtn) {
            e.preventDefault();
            e.stopPropagation();
            const row = editBtn.closest('.message-bubble-row');
            if (!row) return;
            const messageId = row.getAttribute('data-message-id');
            const hasAttachments = row.getAttribute('data-has-attachments') === 'true';
            const contentEl = row.querySelector('.message-text-content');
            const text = contentEl ? contentEl.textContent : '';
            openEditModal(messageId, text, hasAttachments);
            return;
        }

        const delBtn = e.target.closest('[data-action="delete"]');
        if (delBtn) {
            e.preventDefault();
            e.stopPropagation();
            const row = delBtn.closest('.message-bubble-row');
            if (!row) return;
            const messageId = row.getAttribute('data-message-id');
            const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
            const createdAt = row.getAttribute('data-created-at');
            openDeleteModal(messageId, isOutgoing, createdAt);
            return;
        }

        const replyQuote = e.target.closest('[data-reply-parent-id]');
        if (replyQuote) {
            e.preventDefault();
            const parentId = replyQuote.getAttribute('data-reply-parent-id');
            if (parentId) {
                jumpToReplyParent(parentId);
            }
        }
    };

    document.addEventListener('click', onActionClick);

    return {
        resetForSwitch,
        closeEditModal,
        closeDeleteModal,
        closeMobileActionSheet,
        closeMobileActionSheetIfMessage,
        openMobileActionSheet,
        destroy: () => {
            resetForSwitch();
            if (detachEditAutocomplete) detachEditAutocomplete();
            if (editEmojiBtn) editEmojiBtn.removeEventListener('click', onEditEmojiBtnClick);
            document.removeEventListener('click', onActionClick);
            document.removeEventListener('keydown', onEscapeKeyDown);
            document.removeEventListener('pointerdown', onPointerDownForLongPress);
            document.removeEventListener('pointermove', onPointerMoveForLongPress);
            document.removeEventListener('pointerup', onPointerEndForLongPress);
            document.removeEventListener('pointercancel', onPointerEndForLongPress);
            document.removeEventListener('contextmenu', onMobileBubbleContextMenu);
            document.removeEventListener('click', onCaptureSuppressLongPressClick, true);
            if (streamElForLongPress && typeof streamElForLongPress.removeEventListener === 'function') {
                streamElForLongPress.removeEventListener('scroll', onPointerEndForLongPress);
            }
            if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
                window.removeEventListener('resize', onWindowResizeForMobileSheet);
            }
            if (mobileSheet) mobileSheet.removeEventListener('click', onMobileSheetBackdropClick);
            if (mobileReactionsWrap) mobileReactionsWrap.removeEventListener('click', onMobileReactionClick);
            if (mobileReplyBtn) mobileReplyBtn.removeEventListener('click', onMobileReplyClick);
            if (mobileCopyBtn) mobileCopyBtn.removeEventListener('click', onMobileCopyClick);
            if (mobileStickerInfoBtn) mobileStickerInfoBtn.removeEventListener('click', onMobileStickerInfoClick);
            if (mobileEditBtn) mobileEditBtn.removeEventListener('click', onMobileEditClick);
            if (mobileDeleteBtn) mobileDeleteBtn.removeEventListener('click', onMobileDeleteClick);
            if (mobileCancelBtn) mobileCancelBtn.removeEventListener('click', closeMobileActionSheet);
            if (cancelEditBtn) cancelEditBtn.removeEventListener('click', closeEditModal);
            if (editModal) editModal.removeEventListener('click', onEditModalBackdropClick);
            if (saveEditBtn && editInput) saveEditBtn.removeEventListener('click', onSaveEditClick);
            if (cancelDeleteBtn) cancelDeleteBtn.removeEventListener('click', closeDeleteModal);
            if (deleteModal) deleteModal.removeEventListener('click', onDeleteModalBackdropClick);
            if (deleteForMeBtn) deleteForMeBtn.removeEventListener('click', onDeleteForMeClick);
            if (deleteForEveryoneBtn) deleteForEveryoneBtn.removeEventListener('click', onDeleteForEveryoneClick);
        }
    };
}
