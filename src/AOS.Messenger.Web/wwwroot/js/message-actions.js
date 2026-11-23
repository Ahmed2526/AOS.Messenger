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
    updateReplyParentBanners,
    applyMessageDeletedForEveryone,
    applyMessageDeletedForMe
} from './message-renderer.js';

function updateSidebarLastMessageIfLatest(conversationId, messageId, previewText) {
    const container = document.getElementById('messages-container');
    if (!conversationId || !messageId || !isLatestRenderedMessage(container, messageId)) return;
    updateSidebarLastMessage(conversationId, previewText, messageId, true);
}

function updateReplyParentBannersLocal(parentMessageId, newContent) {
    if (!parentMessageId) return;
    updateReplyParentBanners(parentMessageId, newContent);
}

/**
 * Initializes message actions for the active conversation.
 * @param {string} conversationId
 * @param {string} currentUserId
 */
export function setupMessageActions(convIdOrGetter, userIdOrGetter, options = {}) {
    if (typeof document === 'undefined' || !convIdOrGetter) {
        return { resetForSwitch: () => {}, closeEditModal: () => {}, closeDeleteModal: () => {}, destroy: () => {} };
    }

    const resolveConvId = typeof convIdOrGetter === 'function' ? convIdOrGetter : () => convIdOrGetter;
    const getSwitchEpoch = typeof options?.getSwitchEpoch === 'function' ? options.getSwitchEpoch : () => 0;

    // Modals
    const editModal = document.getElementById('edit-message-modal');
    const editInput = document.getElementById('edit-message-input');
    const editErrorEl = document.getElementById('edit-message-error');
    const saveEditBtn = document.getElementById('save-edit-btn');
    const cancelEditBtn = document.getElementById('cancel-edit-btn');

    const deleteModal = document.getElementById('delete-message-modal');
    const deleteErrorEl = document.getElementById('delete-message-error');
    const deleteForMeBtn = document.getElementById('delete-for-me-btn');
    const deleteForEveryoneBtn = document.getElementById('delete-for-everyone-btn');
    const cancelDeleteBtn = document.getElementById('cancel-delete-btn');

    let activeEditMessageId = null;
    let activeDeleteMessageId = null;

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
    function openEditModal(messageId, currentContent) {
        activeEditMessageId = messageId;
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
        setEditError('');
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
        if (!newContent) {
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
            const updated = await apiEditMessage(targetConvId, targetMsgId, newContent);
            if (epoch !== getSwitchEpoch()) return;

            const row = document.getElementById(`msg-${targetMsgId}`);
            if (row) {
                const contentEl = row.querySelector('.message-text-content');
                if (contentEl) {
                    contentEl.textContent = updated.content;
                }
                const editedLabel = row.querySelector('.edited-label');
                if (editedLabel && updated.editedAtUtc) {
                    editedLabel.classList.remove('hidden');
                }
                const replyBtn = row.querySelector('.reply-action-btn');
                if (replyBtn) {
                    replyBtn.setAttribute('data-preview', updated.content || '');
                }
            }

            updateReplyParentBannersLocal(targetMsgId, updated.content);
            updateSidebarLastMessageIfLatest(targetConvId, targetMsgId, updated.content);
            closeEditModal();
        } catch (err) {
            if (epoch !== getSwitchEpoch()) return;
            console.warn('[MessageActions] Edit failed:', err?.status || err);
            setEditError(err?.message || 'Failed to save message edit.');
        } finally {
            saveEditBtn.disabled = false;
        }
    };

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

    function resetForSwitch() {
        closeEditModal();
        closeDeleteModal();
    }

    const onDeleteModalBackdropClick = (e) => {
        if (e.target === deleteModal) {
            closeDeleteModal();
        }
    };

    const onEscapeKeyDown = (e) => {
        if (e.key === 'Escape') {
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
                row.remove();
            }

            document.querySelectorAll(`[data-reply-parent-id="${msgId}"]`).forEach(el => el.remove());

            const remainingRows = container ? container.querySelectorAll('.message-bubble-row') : [];
            if (container && remainingRows.length === 0) {
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
                        fallbackPreview = prevRow.querySelector('.message-text-content')?.textContent || '';
                    }
                }

                applyMessageDeletedForMe(msgId, targetConvId, fallbackLastId, fallbackPreview, true);
            }

            closeDeleteModal();
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
            updateSidebarLastMessageIfLatest(targetConvId, msgId, 'This message was deleted');
            closeDeleteModal();
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

    const onActionClick = (e) => {
        if (!e.target || typeof e.target.closest !== 'function') return;

        const editBtn = e.target.closest('[data-action="edit"]');
        if (editBtn) {
            e.preventDefault();
            e.stopPropagation();
            const row = editBtn.closest('.message-bubble-row');
            if (!row) return;
            const messageId = row.getAttribute('data-message-id');
            const contentEl = row.querySelector('.message-text-content');
            const text = contentEl ? contentEl.textContent : '';
            openEditModal(messageId, text);
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
                const targetRow = document.getElementById(`msg-${parentId}`);
                if (targetRow) {
                    targetRow.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    targetRow.classList.add('ring-2', 'ring-[#B3929D]', 'rounded-xl');
                    setTimeout(() => {
                        targetRow.classList.remove('ring-2', 'ring-[#B3929D]', 'rounded-xl');
                    }, 2000);
                }
            }
        }
    };

    document.addEventListener('click', onActionClick);

    return {
        resetForSwitch,
        closeEditModal,
        closeDeleteModal,
        destroy: () => {
            resetForSwitch();
            document.removeEventListener('click', onActionClick);
            document.removeEventListener('keydown', onEscapeKeyDown);
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
