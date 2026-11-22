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

function getCsrfToken() {
    const input = document.querySelector('input[name="__RequestVerificationToken"]');
    return input ? input.value : '';
}

function isLatestRenderedMessage(messageId) {
    const container = document.getElementById('messages-container');
    if (!container || !messageId) return false;
    const rows = container.querySelectorAll('.message-bubble-row');
    if (!rows || rows.length === 0) return false;
    const lastRow = rows[rows.length - 1];
    return String(lastRow.getAttribute('data-message-id')) === String(messageId);
}

function updateSidebarLastMessageIfLatest(conversationId, messageId, previewText) {
    if (!conversationId || !messageId || !isLatestRenderedMessage(messageId)) return;
    if (typeof window !== 'undefined' && window.ChatRealtime && typeof window.ChatRealtime.updateSidebarLastMessage === 'function') {
        window.ChatRealtime.updateSidebarLastMessage(conversationId, previewText, messageId, true);
    }
}

function truncatePreviewLocal(text, maxCodePoints = 100) {
    const chars = Array.from(String(text || ''));
    return chars.length > maxCodePoints ? `${chars.slice(0, maxCodePoints).join('')}...` : chars.join('');
}

function updateReplyParentBannersLocal(parentMessageId, newContent) {
    if (!parentMessageId) return;
    if (typeof window !== 'undefined' && window.ChatRealtime && typeof window.ChatRealtime.updateReplyParentBanners === 'function') {
        window.ChatRealtime.updateReplyParentBanners(parentMessageId, newContent);
        return;
    }

    const preview = truncatePreviewLocal(newContent);
    const quotes = document.querySelectorAll(`[data-reply-parent-id="${parentMessageId}"]`);
    quotes.forEach((quote) => {
        const previewEl = quote.querySelector('.reply-preview-text') || (quote.children && quote.children.length > 1 ? quote.children[1] : null);
        if (previewEl) {
            previewEl.textContent = preview;
        }
    });
}

function scrubReplyParentBanners(parentMessageId) {
    if (!parentMessageId) return;
    const quotes = document.querySelectorAll(`[data-reply-parent-id="${parentMessageId}"]`);
    quotes.forEach((quote) => {
        const previewEl = quote.querySelector('.reply-preview-text') || (quote.children && quote.children.length > 1 ? quote.children[1] : null);
        if (previewEl) {
            while (previewEl.firstChild) {
                previewEl.removeChild(previewEl.firstChild);
            }
            const deletedSpan = document.createElement('span');
            deletedSpan.className = 'italic';
            deletedSpan.textContent = 'This message was deleted';
            previewEl.appendChild(deletedSpan);
        }
    });
}

async function extractErrorMessage(resp, fallback) {
    try {
        const text = await resp.text();
        if (text) {
            const parsed = JSON.parse(text);
            if (parsed && (parsed.detail || parsed.title || parsed.message)) {
                return parsed.detail || parsed.title || parsed.message;
            }
        }
    } catch {}
    return fallback;
}

/**
 * Initializes message actions for the active conversation.
 * @param {string} conversationId
 * @param {string} currentUserId
 */
export function setupMessageActions(conversationId, currentUserId) {
    if (!conversationId) return;

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

    // Composer reply elements
    const replyBanner = document.getElementById('composer-reply-banner');
    const replySenderEl = document.getElementById('reply-banner-sender');
    const replyPreviewEl = document.getElementById('reply-banner-preview');
    const messageInput = document.getElementById('message-content-input');

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
        if (editModal) {
            editModal.classList.add('hidden');
        }
    }

    if (cancelEditBtn) {
        cancelEditBtn.addEventListener('click', closeEditModal);
    }

    if (editModal) {
        editModal.addEventListener('click', (e) => {
            if (e.target === editModal) {
                closeEditModal();
            }
        });
    }

    if (saveEditBtn && editInput) {
        saveEditBtn.addEventListener('click', async () => {
            const newContent = editInput.value.trim();
            if (!newContent) {
                setEditError('Message content cannot be empty.');
                return;
            }
            if (!activeEditMessageId) return;

            setEditError('');
            saveEditBtn.disabled = true;
            try {
                const token = getCsrfToken();
                const headers = {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json'
                };
                if (token) headers['RequestVerificationToken'] = token;

                const resp = await fetch(`/api/conversations/${conversationId}/messages/${activeEditMessageId}`, {
                    method: 'PUT',
                    headers: headers,
                    body: JSON.stringify({ content: newContent })
                });

                if (!resp.ok) {
                    console.warn('[MessageActions] Edit failed with status:', resp.status);
                    setEditError(await extractErrorMessage(resp, 'Failed to save message edit.'));
                    return;
                }

                const updated = await resp.json();

                // Update UI in-place
                const row = document.getElementById(`msg-${activeEditMessageId}`);
                if (row) {
                    const contentEl = row.querySelector('.message-text-content');
                    if (contentEl) {
                        contentEl.textContent = updated.content;
                    }
                    const editedLabel = row.querySelector('.edited-label');
                    if (editedLabel) {
                        if (updated.editedAtUtc) {
                            editedLabel.classList.remove('hidden');
                        }
                    }
                    const replyBtn = row.querySelector('.reply-action-btn');
                    if (replyBtn) {
                        replyBtn.setAttribute('data-preview', updated.content || '');
                    }
                }

                updateReplyParentBannersLocal(activeEditMessageId, updated.content);
                updateSidebarLastMessageIfLatest(conversationId, activeEditMessageId, updated.content);
                closeEditModal();
            } catch (err) {
                console.error('[MessageActions] Error saving edit:', err);
                setEditError('Network error while saving changes.');
            } finally {
                saveEditBtn.disabled = false;
            }
        });
    }

    // --- DELETE MODAL HANDLING ---
    function openDeleteModal(messageId, isOutgoing, createdAtUtc) {
        activeDeleteMessageId = messageId;
        setDeleteError('');

        // Check if Delete For Everyone is eligible (author-only and within 3 hours)
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

    if (cancelDeleteBtn) {
        cancelDeleteBtn.addEventListener('click', closeDeleteModal);
    }

    if (deleteModal) {
        deleteModal.addEventListener('click', (e) => {
            if (e.target === deleteModal) {
                closeDeleteModal();
            }
        });
    }

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            if (editModal && !editModal.classList.contains('hidden')) {
                closeEditModal();
            }
            if (deleteModal && !deleteModal.classList.contains('hidden')) {
                closeDeleteModal();
            }
        }
    });

    if (deleteForMeBtn) {
        deleteForMeBtn.addEventListener('click', async () => {
            if (!activeDeleteMessageId) return;

            setDeleteError('');
            deleteForMeBtn.disabled = true;
            try {
                const token = getCsrfToken();
                const headers = { 'Accept': 'application/json' };
                if (token) headers['RequestVerificationToken'] = token;

                const msgId = activeDeleteMessageId;
                const wasLatest = isLatestRenderedMessage(msgId);
                const resp = await fetch(`/api/conversations/${conversationId}/messages/${msgId}/delete-for-me`, {
                    method: 'POST',
                    headers: headers
                });

                if (!resp.ok) {
                    setDeleteError(await extractErrorMessage(resp, 'Failed to hide message.'));
                    return;
                }

                const container = document.getElementById('messages-container');
                const row = document.getElementById(`msg-${msgId}`);
                if (row) {
                    row.remove();
                }

                document.querySelectorAll(`[data-reply-parent-id="${msgId}"]`).forEach(el => el.remove());

                const remainingRows = container ? container.querySelectorAll('.message-bubble-row') : [];
                if (container && remainingRows.length === 0) {
                    const emptyPlaceholder = document.getElementById('no-messages-placeholder');
                    if (emptyPlaceholder) emptyPlaceholder.classList.remove('hidden');
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

                    if (typeof window !== 'undefined' && window.ChatRealtime && typeof window.ChatRealtime.applyMessageDeletedForMe === 'function') {
                        window.ChatRealtime.applyMessageDeletedForMe(msgId, conversationId, fallbackLastId, fallbackPreview, true);
                    } else if (typeof window !== 'undefined' && window.ChatRealtime && typeof window.ChatRealtime.updateSidebarLastMessage === 'function') {
                        window.ChatRealtime.updateSidebarLastMessage(conversationId, fallbackPreview, fallbackLastId, false);
                    }
                }

                closeDeleteModal();
            } catch (err) {
                console.error('[MessageActions] Error deleting for me:', err);
                setDeleteError('Network error while deleting message.');
            } finally {
                deleteForMeBtn.disabled = false;
            }
        });
    }

    if (deleteForEveryoneBtn) {
        deleteForEveryoneBtn.addEventListener('click', async () => {
            if (!activeDeleteMessageId) return;

            setDeleteError('');
            deleteForEveryoneBtn.disabled = true;
            try {
                const token = getCsrfToken();
                const headers = { 'Accept': 'application/json' };
                if (token) headers['RequestVerificationToken'] = token;

                const resp = await fetch(`/api/conversations/${conversationId}/messages/${activeDeleteMessageId}/delete-for-everyone`, {
                    method: 'POST',
                    headers: headers
                });

                if (!resp.ok) {
                    setDeleteError(await extractErrorMessage(resp, 'Failed to delete message for everyone.'));
                    return;
                }

                const msgId = activeDeleteMessageId;
                const row = document.getElementById(`msg-${msgId}`);
                if (typeof window !== 'undefined' && window.ChatRealtime && typeof window.ChatRealtime.applyMessageDeletedForEveryone === 'function') {
                    window.ChatRealtime.applyMessageDeletedForEveryone(row, msgId);
                } else if (row) {
                    markMessageDeletedForEveryone(row, msgId);
                }
                updateSidebarLastMessageIfLatest(conversationId, msgId, 'This message was deleted');
                closeDeleteModal();
            } catch (err) {
                console.error('[MessageActions] Error deleting for everyone:', err);
                setDeleteError('Network error while deleting message.');
            } finally {
                deleteForEveryoneBtn.disabled = false;
            }
        });
    }

    // Helper to transform message row into tombstone
    function markMessageDeletedForEveryone(row, msgId) {
        row.setAttribute('data-deleted', 'true');
        const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
        const contentEl = row.querySelector('.message-text-content');
        if (contentEl) {
            while (contentEl.firstChild) {
                contentEl.removeChild(contentEl.firstChild);
            }
            contentEl.className = isOutgoing
                ? 'message-text-content italic text-xs text-white/80 flex items-center gap-1.5 py-0.5'
                : 'message-text-content italic text-xs text-[#6E6560] dark:text-[#C9C0B9] flex items-center gap-1.5 py-0.5';

            const blockIcon = document.createElement('span');
            blockIcon.className = 'material-symbols-outlined text-[15px]';
            blockIcon.textContent = 'block';
            contentEl.appendChild(blockIcon);

            const delText = document.createElement('span');
            delText.textContent = 'This message was deleted';
            contentEl.appendChild(delText);
        }

        const actionsBar = row.querySelector('.message-actions-bar');
        if (actionsBar) actionsBar.remove();

        const editBtn = row.querySelector('[data-action="edit"]');
        if (editBtn) editBtn.remove();

        const delBtn = row.querySelector('[data-action="delete"]');
        if (delBtn) delBtn.remove();

        const reactBtn = row.querySelector('[data-action="react"]');
        if (reactBtn) reactBtn.remove();

        const replyBtn = row.querySelector('.reply-action-btn');
        if (replyBtn) replyBtn.remove();

        const receiptIcon = row.querySelector('span[data-receipt-status]');
        if (receiptIcon) receiptIcon.remove();

        const pillsContainer = row.querySelector('.reaction-pills-container');
        if (pillsContainer) pillsContainer.remove();

        const editedLabel = row.querySelector('.edited-label');
        if (editedLabel) editedLabel.remove();

        scrubReplyParentBanners(msgId || row.getAttribute('data-message-id'));
    }

    // Delegate action clicks: Edit, Delete, and Reply Quote Click-to-Scroll
    document.addEventListener('click', (e) => {
        // Edit button
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

        // Delete button
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

        // Reply quote block click-to-scroll to parent message
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
    });
}
