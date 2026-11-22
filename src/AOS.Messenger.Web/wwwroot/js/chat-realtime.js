/**
 * AOS Messenger - Phase 6 & Phase 7 Realtime Chat & State Reconciliation
 *
 * Implements client-side SignalR connection lifecycle, live message streaming,
 * keyset state reconciliation on reconnect, ID deduplication, XSS sanitization,
 * delivery/read receipts, typing indicators, and presence synchronization.
 *
 * Architectural Invariants (AGENTS.md):
 * - SQL Server is the authoritative persistent store. SignalR is transport only.
 * - State Reconciliation queries GET /api/conversations/{id}/messages on reconnected.
 * - Deduplication by message ID prevents dual rendering from SignalR and REST.
 * - Safe DOM tree construction using document.createElement exclusively.
 * - Zero HTML string injection into live DOM.
 * - All text content bound strictly via textContent or createTextNode.
 * - UI classes align with Stitch warm modern token system.
 */
(function (global) {
    'use strict';

    /**
     * Sanitizes user-supplied text to prevent Cross-Site Scripting (XSS).
     * @param {string} str - Raw input string
     * @returns {string} HTML-escaped string
     */
    function escapeHtml(str) {
        if (str === null || str === undefined) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    /**
     * Formats an ISO-8601 UTC timestamp to client local time (e.g., '10:42 AM').
     * @param {string} isoString - ISO date string
     * @returns {string} Formatted time string
     */
    function formatTime(isoString) {
        if (!isoString) return '';
        try {
            const d = new Date(isoString);
            return d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', hour12: true });
        } catch {
            return '';
        }
    }

    /**
     * Generates a 1- or 2-letter uppercase initials string from a name or handle.
     * @param {string} name
     * @param {string} handle
     * @returns {string} Initials
     */
    function getInitials(name, handle) {
        const raw = (name && name.trim()) || (handle && handle.trim()) || 'U';
        const parts = raw.split(/\s+/).filter(Boolean);
        if (parts.length > 1 && parts[0].length > 0 && parts[1].length > 0) {
            return (parts[0][0] + parts[1][0]).toUpperCase();
        }
        if (parts.length === 1 && parts[0].length > 0) {
            return parts[0].substring(0, Math.min(2, parts[0].length)).toUpperCase();
        }
        return 'U';
    }

    /**
     * Normalizes a message ID from string, GUID, or strongly-typed object.
     * @param {object|string} msg
     * @returns {string|null} Normalized GUID string
     */
    function getMessageId(msg) {
        if (!msg) return null;
        if (typeof msg === 'string' || typeof msg === 'number' || typeof msg === 'bigint') return String(msg);
        if (msg.id && typeof msg.id === 'object' && msg.id.value !== undefined) return String(msg.id.value);
        if (msg.id !== undefined && msg.id !== null) return String(msg.id);
        if (msg.messageId !== undefined && msg.messageId !== null) return String(msg.messageId);
        return null;
    }

    /**
     * Normalizes a user ID from string, GUID, or strongly-typed object.
     * @param {object|string} u
     * @returns {string} Normalized user ID string
     */
    function getUserId(u) {
        if (!u) return '';
        if (typeof u === 'string') return u;
        if (typeof u === 'object' && u.value) return String(u.value);
        return String(u);
    }

    /**
     * Normalizes a conversation ID from string, GUID, or strongly-typed object.
     * @param {object|string} obj
     * @returns {string} Normalized conversation ID string
     */
    function getConversationId(obj) {
        if (!obj) return '';
        if (typeof obj === 'string') return obj;
        const val = obj.conversationId !== undefined ? obj.conversationId : obj.ConversationId;
        if (!val) return '';
        if (typeof val === 'object' && val.value) return String(val.value);
        return String(val);
    }

    /**
     * Checks if a message ID is already rendered in the DOM.
     * @param {string|object} msgOrId
     * @returns {boolean} True if already rendered in message stream
     */
    function isMessageRendered(msgOrId) {
        const id = typeof msgOrId === 'object' ? getMessageId(msgOrId) : msgOrId;
        if (!id) return false;
        if (typeof document === 'undefined') return false;
        return !!(document.getElementById(`msg-${id}`) || document.querySelector(`[data-message-id="${id}"]`));
    }

    /**
     * Scrolls the message stream smoothly to the bottom.
     * @param {boolean} smooth
     */
    function scrollToBottom(smooth = true) {
        if (typeof document === 'undefined') return;
        const streamEl = document.getElementById('message-stream');
        if (streamEl) {
            if (smooth) {
                streamEl.scrollTo({ top: streamEl.scrollHeight, behavior: 'smooth' });
            } else {
                streamEl.scrollTop = streamEl.scrollHeight;
            }
        }
    }

    /**
     * Checks whether the given message ID is the latest rendered row inside containerEl.
     * @param {HTMLElement|null} containerEl
     * @param {string|number} msgId
     * @returns {boolean}
     */
    function isLatestRenderedMessage(containerEl, msgId) {
        if (!containerEl || !msgId || typeof containerEl.querySelectorAll !== 'function') return false;
        const rows = containerEl.querySelectorAll('.message-bubble-row');
        if (!rows || rows.length === 0) return false;
        const lastRow = rows[rows.length - 1];
        return String(lastRow.getAttribute('data-message-id')) === String(msgId);
    }

    /**
     * Updates the last message snippet in the left conversation sidebar.
     * When onlyIfLatest is true, avoids overwriting newer previews when older messages are edited/deleted.
     * @param {string} convId
     * @param {string} previewText
     * @param {string|number|null} [messageId]
     * @param {boolean} [onlyIfLatest]
     */
    function updateSidebarLastMessage(convId, previewText, messageId = null, onlyIfLatest = false, forceClearId = false) {
        if (!convId || typeof document === 'undefined') return;
        const normalizedId = typeof convId === 'object' && convId.value ? String(convId.value) : String(convId);
        const lowerId = normalizedId.toLowerCase();
        const item = document.querySelector(`.conversation-item[data-conversation-id="${normalizedId}"]`) ||
                     document.querySelector(`.conversation-item[data-conversation-id="${lowerId}"]`) ||
                     document.querySelector(`.conversation-item[href*="${normalizedId}"]`) ||
                     document.querySelector(`.conversation-item[href*="${lowerId}"]`);
        if (!item) return;

        if (onlyIfLatest && messageId !== null && messageId !== undefined) {
            const currentLastId = item.getAttribute('data-last-message-id');
            if (currentLastId && String(currentLastId) !== String(messageId)) {
                return;
            }
        }

        const previewEl = item.querySelector('.conversation-last-message') ||
                          item.querySelector('.text-\\[11px\\].truncate');
        if (previewEl) {
            previewEl.textContent = previewText || 'No messages yet';
        }

        if (forceClearId) {
            item.removeAttribute('data-last-message-id');
        } else if (!onlyIfLatest && messageId !== null && messageId !== undefined) {
            item.setAttribute('data-last-message-id', String(messageId));
        }
    }

    /**
     * Truncates a reply preview string safely by Unicode code points.
     * @param {string} text
     * @param {number} [maxCodePoints]
     * @returns {string}
     */
    function truncateReplyPreview(text, maxCodePoints = 100) {
        const chars = Array.from(String(text || ''));
        return chars.length > maxCodePoints ? `${chars.slice(0, maxCodePoints).join('')}...` : chars.join('');
    }

    /**
     * Updates child reply quote banners when a parent message is edited.
     * @param {string|number} parentMessageId
     * @param {string} newContent
     */
    function updateReplyParentBanners(parentMessageId, newContent) {
        if (!parentMessageId || typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;
        const preview = truncateReplyPreview(newContent);
        const quotes = document.querySelectorAll(`[data-reply-parent-id="${parentMessageId}"]`);
        quotes.forEach((quote) => {
            const previewEl = quote.querySelector('.reply-preview-text') ||
                              (quote.children && quote.children.length > 1 ? quote.children[1] : null);
            if (previewEl) {
                previewEl.textContent = preview;
            }
        });
    }

    /**
     * Applies caller-side DeleteForMe removal and authoritative sidebar preview sync.
     * @param {string|number} msgId
     * @param {string} convId
     * @param {string|number|null} newLastMessageId
     * @param {string|null} newLastMessagePreview
     * @param {boolean} isActiveConv
     */
    function applyMessageDeletedForMe(msgId, convId, newLastMessageId, newLastMessagePreview, isActiveConv) {
        if (typeof document === 'undefined') return;
        if (isActiveConv && msgId) {
            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                row.remove();
            }

            if (typeof document.querySelectorAll === 'function') {
                document.querySelectorAll(`[data-reply-parent-id="${msgId}"]`).forEach((q) => q.remove());
            }

            const containerEl = document.getElementById('messages-container');
            const emptyPlaceholderEl = document.getElementById('no-messages-placeholder');
            if (containerEl && emptyPlaceholderEl && containerEl.querySelectorAll('.message-bubble-row').length === 0) {
                emptyPlaceholderEl.classList.remove('hidden');
            }
        }

        if (convId) {
            const hasNew = newLastMessageId !== null && newLastMessageId !== undefined && newLastMessageId !== '';
            updateSidebarLastMessage(
                convId,
                hasNew ? (newLastMessagePreview || '') : 'No messages yet',
                hasNew ? newLastMessageId : null,
                false,
                !hasNew);
        }
    }

    /**
     * Scrubs reply quote banners referencing a tombstoned parent message anywhere in the DOM.
     * @param {string|number} parentMessageId
     */
    function scrubReplyParentBanners(parentMessageId) {
        if (!parentMessageId || typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;
        const quotes = document.querySelectorAll(`[data-reply-parent-id="${parentMessageId}"]`);
        quotes.forEach((quote) => {
            const previewEl = quote.querySelector('.reply-preview-text') ||
                              (quote.children && quote.children.length > 1 ? quote.children[1] : null);
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

    /**
     * Transforms a message row into a tombstoned state and scrubs child reply banners.
     * @param {HTMLElement|null} row
     * @param {string|number} msgId
     */
    function applyMessageDeletedForEveryone(row, msgId) {
        if (row) {
            row.setAttribute('data-deleted', 'true');
            const isOutgoing = row.getAttribute('data-is-outgoing') === 'true' || row.classList.contains('justify-end');
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
        }

        if (msgId) {
            scrubReplyParentBanners(msgId);
        }
    }

    /**
     * Resolves initial checkmark status and icons based on peer watermarks.
     * @param {string|null} messageId
     * @returns {{ status: string, icon: string, colorClass: string }}
     */
    function resolveReceiptStatus(messageId) {
        if (!messageId) {
            return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
        }

        let lastRead = null;
        let lastDelivered = null;

        if (typeof document !== 'undefined') {
            const c = document.getElementById('messages-container');
            if (c) {
                lastRead = c.getAttribute('data-peer-last-read-id');
                lastDelivered = c.getAttribute('data-peer-last-delivered-id');
            }
        }

        let msgIdVal = 0n;
        try {
            msgIdVal = BigInt(messageId);
        } catch {
            return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
        }

        let readVal = 0n;
        try {
            if (lastRead) readVal = BigInt(lastRead);
        } catch {}

        let delivVal = 0n;
        try {
            if (lastDelivered) delivVal = BigInt(lastDelivered);
        } catch {}

        if (readVal > 0n && msgIdVal <= readVal) {
            return { status: 'read', icon: 'done_all', colorClass: 'text-sky-300' };
        }
        if (delivVal > 0n && msgIdVal <= delivVal) {
            return { status: 'delivered', icon: 'done_all', colorClass: 'text-white/70' };
        }

        return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
    }

    /**
     * Creates a DOM element tree for a single message bubble using safe DOM manipulation APIs.
     * Guaranteed zero string-to-DOM parsing.
     *
     * @param {object} message
     * @param {string|null} currentUserId
     * @returns {HTMLElement|null}
     */
    function createMessageBubbleElement(message, currentUserId) {
        if (typeof document === 'undefined') {
            return null;
        }

        const id = getMessageId(message);
        const senderId = getUserId(message.senderId);
        const convId = getConversationId(message);

        const isOutgoing = currentUserId
            ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
            : !!message.isOutgoing;

        const initials = getInitials(message.senderName, message.senderHandle);
        const senderName = message.senderName || 'Unknown';
        const timeStr = formatTime(message.createdAtUtc);

        const row = document.createElement('div');
        if (id) {
            row.id = `msg-${id}`;
            row.setAttribute('data-message-id', id);
        }
        row.setAttribute('data-deleted', message.isDeletedForEveryone ? 'true' : 'false');
        if (message.createdAtUtc) {
            row.setAttribute('data-created-at', message.createdAtUtc);
        }

        if (isOutgoing) {
            row.className = 'flex flex-wrap justify-end items-center message-bubble-row group relative';
            row.setAttribute('data-is-outgoing', 'true');
        } else {
            row.className = 'flex flex-wrap justify-start items-center gap-2.5 message-bubble-row group relative';
            row.setAttribute('data-is-outgoing', 'false');

            const avatar = document.createElement('div');
            avatar.className = 'w-8 h-8 rounded-full bg-[#B3929D]/20 dark:bg-[#B3929D]/25 text-[#735761] dark:text-[#E1BDC9] border border-[#B3929D]/30 font-bold text-xs flex items-center justify-center flex-shrink-0 mt-1';
            avatar.textContent = initials;
            row.appendChild(avatar);
        }

        if (senderId) {
            row.setAttribute('data-sender-id', senderId);
        }

        const bubble = document.createElement('div');
        if (isOutgoing) {
            bubble.className = 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative';
        } else {
            bubble.className = 'message-bubble max-w-[75%] min-w-0 rounded-2xl rounded-tl-xs px-4 py-2 bg-[#FFFFFF] dark:bg-[#3A3835] text-[#2B2725] dark:text-[#F0E9E2] border border-[#E3DCD7] dark:border-[#514D48] shadow-2xs relative';

            const senderHeader = document.createElement('div');
            senderHeader.className = 'text-[11px] font-bold text-[#735761] dark:text-[#E1BDC9] mb-0.5';
            senderHeader.textContent = senderName;
            bubble.appendChild(senderHeader);
        }

        if (message.replyTo) {
            const replyBanner = document.createElement('div');
            replyBanner.className = isOutgoing
                ? 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-black/15 border-white/70 text-white/80 cursor-pointer hover:bg-black/25 transition-colors'
                : 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-[#F8F6F3] dark:bg-[#302F2C] border-[#B3929D] text-[#6E6560] dark:text-[#C9C0B9] cursor-pointer hover:bg-[#F0E9DF] dark:hover:bg-[#3E3C38] transition-colors';

            const parentId = getMessageId(message.replyTo);
            if (parentId) {
                replyBanner.setAttribute('data-reply-parent-id', parentId);
            }

            const rSenderEl = document.createElement('div');
            rSenderEl.className = isOutgoing ? 'font-bold text-[10px] text-white/95' : 'font-bold text-[10px] text-[#2B2725] dark:text-[#F0E9E2]';
            rSenderEl.textContent = message.replyTo.senderName || message.replyTo.senderDisplayName || 'Unknown';
            replyBanner.appendChild(rSenderEl);

            const rContentEl = document.createElement('div');
            rContentEl.className = 'reply-preview-text truncate text-[10px]';
            if (message.replyTo.isDeletedForEveryone) {
                const deletedSpan = document.createElement('span');
                deletedSpan.className = 'italic';
                deletedSpan.textContent = 'This message was deleted';
                rContentEl.appendChild(deletedSpan);
            } else {
                rContentEl.textContent = message.replyTo.previewContent || message.replyTo.contentPreview || '';
            }
            replyBanner.appendChild(rContentEl);
            bubble.appendChild(replyBanner);
        }

        if (message.isDeletedForEveryone) {
            const delDiv = document.createElement('div');
            delDiv.className = isOutgoing
                ? 'message-text-content italic text-xs text-white/80 flex items-center gap-1.5 py-0.5'
                : 'message-text-content italic text-xs text-[#6E6560] dark:text-[#C9C0B9] flex items-center gap-1.5 py-0.5';

            const blockIcon = document.createElement('span');
            blockIcon.className = 'material-symbols-outlined text-[15px]';
            blockIcon.textContent = 'block';
            delDiv.appendChild(blockIcon);

            const delText = document.createElement('span');
            delText.textContent = 'This message was deleted';
            delDiv.appendChild(delText);

            bubble.appendChild(delDiv);
        } else {
            const contentDiv = document.createElement('div');
            contentDiv.className = 'message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words';
            contentDiv.textContent = message.content || '';
            bubble.appendChild(contentDiv);
        }

        const footer = document.createElement('div');
        footer.className = isOutgoing
            ? 'flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80'
            : 'flex items-center justify-end gap-1 mt-1 text-[10px] text-[#6E6560] dark:text-[#C9C0B9]';

        const editedSpan = document.createElement('span');
        editedSpan.className = `text-[9px] italic edited-label ${message.editedAtUtc && !message.isDeletedForEveryone ? '' : 'hidden'}`;
        editedSpan.textContent = 'edited';
        footer.appendChild(editedSpan);

        const timeSpan = document.createElement('span');
        timeSpan.className = 'message-timestamp';
        if (message.createdAtUtc) {
            timeSpan.setAttribute('data-created-at', message.createdAtUtc);
        }
        timeSpan.textContent = timeStr;
        footer.appendChild(timeSpan);

        // Checkmark for outgoing message
        if (isOutgoing && !message.isDeletedForEveryone) {
            const receipt = resolveReceiptStatus(id);
            const statusSpan = document.createElement('span');
            statusSpan.className = `material-symbols-outlined text-[13px] select-none align-middle ${receipt.colorClass}`;
            statusSpan.textContent = receipt.icon;
            statusSpan.setAttribute('data-receipt-status', receipt.status);
            if (id) {
                statusSpan.setAttribute('data-message-id', id);
            }
            footer.appendChild(statusSpan);
        }

        bubble.appendChild(footer);

        const pillsContainer = document.createElement('div');
        pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
        if (message.reactions && message.reactions.length > 0 &&
            typeof window !== 'undefined' && window.Reactions && typeof window.Reactions.renderReactionPills === 'function') {
            window.Reactions.renderReactionPills(pillsContainer, message.reactions, id, convId, isOutgoing);
        } else {
            pillsContainer.classList.add('hidden');
        }
        bubble.appendChild(pillsContainer);

        row.appendChild(bubble);

        if (!message.isDeletedForEveryone) {
            const actionsBar = document.createElement('div');
            actionsBar.className = isOutgoing
                ? 'message-actions-bar opacity-100 sm:opacity-0 sm:group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity self-center shrink-0 mr-1 flex items-center gap-0.5'
                : 'message-actions-bar opacity-100 sm:opacity-0 sm:group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity self-center shrink-0 ml-1 flex items-center gap-0.5';

            const reactBtn = document.createElement('button');
            reactBtn.type = 'button';
            reactBtn.setAttribute('data-action', 'react');
            reactBtn.className = 'w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
            reactBtn.title = 'Add reaction';
            reactBtn.setAttribute('aria-label', 'Add reaction');

            const reactIcon = document.createElement('span');
            reactIcon.className = 'material-symbols-outlined text-[16px]';
            reactIcon.textContent = 'add_reaction';
            reactBtn.appendChild(reactIcon);
            actionsBar.appendChild(reactBtn);

            const replyBtn = document.createElement('button');
            replyBtn.type = 'button';
            replyBtn.className = 'reply-action-btn w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
            replyBtn.title = 'Reply to message';
            replyBtn.setAttribute('aria-label', 'Reply to message');
            if (id) replyBtn.setAttribute('data-id', id);
            replyBtn.setAttribute('data-sender', senderName);
            replyBtn.setAttribute('data-preview', message.content || '');

            const replyIcon = document.createElement('span');
            replyIcon.className = 'material-symbols-outlined text-[16px]';
            replyIcon.textContent = 'reply';
            replyBtn.appendChild(replyIcon);
            actionsBar.appendChild(replyBtn);

            if (isOutgoing) {
                const editBtn = document.createElement('button');
                editBtn.type = 'button';
                editBtn.setAttribute('data-action', 'edit');
                editBtn.className = 'w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D] hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
                editBtn.title = 'Edit message';
                editBtn.setAttribute('aria-label', 'Edit message');

                const editIcon = document.createElement('span');
                editIcon.className = 'material-symbols-outlined text-[16px]';
                editIcon.textContent = 'edit';
                editBtn.appendChild(editIcon);
                actionsBar.appendChild(editBtn);
            }

            const delBtn = document.createElement('button');
            delBtn.type = 'button';
            delBtn.setAttribute('data-action', 'delete');
            delBtn.className = 'w-6 h-6 sm:w-7 sm:h-7 rounded flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-red-500 hover:bg-[#F0E9DF]/60 dark:hover:bg-[#45423E]/60 transition-colors cursor-pointer';
            delBtn.title = 'Delete message';
            delBtn.setAttribute('aria-label', 'Delete message');

            const delIcon = document.createElement('span');
            delIcon.className = 'material-symbols-outlined text-[16px]';
            delIcon.textContent = 'delete';
            delBtn.appendChild(delIcon);
            actionsBar.appendChild(delBtn);

            row.appendChild(actionsBar);
        }

        return row;
    }

    /**
     * Creates HTML string for a single message bubble matching Stitch warm modern design tokens.
     * Fully escapes all user content for XSS defense.
     * Provided for string serialization and unit testing parity.
     *
     * @param {object} message - MessageDto
     * @param {string|null} currentUserId - Authenticated user's GUID string
     * @returns {string} HTML string
     */
    function createMessageBubbleHtml(message, currentUserId) {
        const id = escapeHtml(getMessageId(message));
        const senderId = getUserId(message.senderId);

        const isOutgoing = currentUserId
            ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
            : !!message.isOutgoing;

        const initials = escapeHtml(getInitials(message.senderName, message.senderHandle));
        const senderName = escapeHtml(message.senderName);
        const timeStr = escapeHtml(formatTime(message.createdAtUtc));
        const editedStr = `<span class="text-[9px] italic edited-label ${message.editedAtUtc && !message.isDeletedForEveryone ? '' : 'hidden'}">edited</span>`;

        let replyBannerHtml = '';
        if (message.replyTo) {
            const rSender = escapeHtml(message.replyTo.senderName || 'Unknown');
            const rContent = message.replyTo.isDeletedForEveryone
                ? '<span class="italic">This message was deleted</span>'
                : escapeHtml(message.replyTo.previewContent);
            const rBgClass = isOutgoing
                ? 'bg-black/15 border-white/70 text-white/80'
                : 'bg-[#F8F6F3] dark:bg-[#302F2C] border-[#B3929D] text-[#6E6560] dark:text-[#C9C0B9]';
            const rSenderColor = isOutgoing ? 'text-white/95' : 'text-[#2B2725] dark:text-[#F0E9E2]';
            const parentId = escapeHtml(getMessageId(message.replyTo));

            replyBannerHtml = `
                <div class="mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs ${rBgClass} cursor-pointer" data-reply-parent-id="${parentId}">
                    <div class="font-bold text-[10px] ${rSenderColor}">${rSender}</div>
                    <div class="reply-preview-text truncate text-[10px]">${rContent}</div>
                </div>`;
        }

        let contentHtml = '';
        if (message.isDeletedForEveryone) {
            const delColor = isOutgoing ? 'text-white/80' : 'text-[#6E6560] dark:text-[#C9C0B9]';
            contentHtml = `
                <div class="message-text-content italic text-xs ${delColor} flex items-center gap-1.5 py-0.5">
                    <span class="material-symbols-outlined text-[15px]">block</span>
                    <span>This message was deleted</span>
                </div>`;
        } else {
            contentHtml = `<div class="message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words">${escapeHtml(message.content)}</div>`;
        }

        const replyBtnHtml = !message.isDeletedForEveryone
            ? `<button type="button" class="reply-action-btn opacity-0 group-hover:opacity-100 transition-opacity self-center ${isOutgoing ? 'mr-2' : 'ml-2'} text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D]" title="Reply to message" data-id="${id}" data-sender="${senderName}" data-preview="${escapeHtml(message.content)}">
                <span class="material-symbols-outlined text-[18px]">reply</span>
               </button>`
            : '';

        let receiptHtml = '';
        if (isOutgoing && !message.isDeletedForEveryone) {
            const receipt = resolveReceiptStatus(id);
            receiptHtml = `<span class="material-symbols-outlined text-[13px] select-none align-middle ${receipt.colorClass}" data-message-id="${id}" data-receipt-status="${receipt.status}">${receipt.icon}</span>`;
        }

        const senderIdAttr = senderId ? ` data-sender-id="${escapeHtml(senderId)}"` : '';

        if (isOutgoing) {
            return `
            <div id="msg-${id}" class="flex justify-end message-bubble-row group relative" data-message-id="${id}" data-is-outgoing="true"${senderIdAttr} data-created-at="${message.createdAtUtc}">
                <div class="max-w-[75%] rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative">
                    ${replyBannerHtml}
                    ${contentHtml}
                    <div class="flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80">
                        ${editedStr}
                        <span>${timeStr}</span>
                        ${receiptHtml}
                    </div>
                </div>
                ${replyBtnHtml}
            </div>`;
        } else {
            return `
            <div id="msg-${id}" class="flex justify-start gap-2.5 message-bubble-row group relative" data-message-id="${id}" data-is-outgoing="false"${senderIdAttr} data-created-at="${message.createdAtUtc}">
                <div class="w-8 h-8 rounded-full bg-[#B3929D]/20 dark:bg-[#B3929D]/25 text-[#735761] dark:text-[#E1BDC9] border border-[#B3929D]/30 font-bold text-xs flex items-center justify-center flex-shrink-0 mt-1">
                    ${initials}
                </div>
                <div class="max-w-[75%] rounded-2xl rounded-tl-xs px-4 py-2 bg-[#FFFFFF] dark:bg-[#3A3835] text-[#2B2725] dark:text-[#F0E9E2] border border-[#E3DCD7] dark:border-[#514D48] shadow-2xs relative">
                    <div class="text-[11px] font-bold text-[#735761] dark:text-[#E1BDC9] mb-0.5">${senderName}</div>
                    ${replyBannerHtml}
                    ${contentHtml}
                    <div class="flex items-center justify-end gap-1 mt-1 text-[10px] text-[#6E6560] dark:text-[#C9C0B9]">
                        ${editedStr}
                        <span>${timeStr}</span>
                    </div>
                </div>
                ${replyBtnHtml}
            </div>`;
        }
    }

    /**
     * Initializes the SignalR Hub connection and state reconciliation for an active conversation.
     *
     * @param {object} options
     * @param {string} options.conversationId - Conversation GUID
     * @param {string} options.currentUserId - Authenticated User GUID
     * @param {string|null} [options.initialCursor] - Initial cursor for keyset pagination
     * @returns {object} Realtime controller instance
     */
    function init(options) {
        if (!options || !options.conversationId) {
            console.error('[SignalR] init requires a valid conversationId.');
            return null;
        }

        const conversationId = String(options.conversationId);
        const currentUserId = options.currentUserId ? String(options.currentUserId) : null;
        let latestReceivedCursor = options.initialCursor || null;

        const bannerEl = document.getElementById('connection-banner');
        const bannerTextEl = document.getElementById('connection-banner-text');
        const statusDotEl = document.getElementById('connectionStatusDot');
        const statusTextEl = document.getElementById('connectionStatusText');
        const containerEl = document.getElementById('messages-container') || document.getElementById('message-stream');
        const emptyPlaceholderEl = document.getElementById('no-messages-placeholder');

        let isTerminated = false;

        /**
         * Permanently halts SignalR and redirects to login when session is revoked or expired.
         * Prevents infinite reconnect loops on 401/403 (RT-012).
         */
        function handleSessionExpired() {
            if (isTerminated) return;
            isTerminated = true;
            console.warn('[SignalR] Session revoked or expired (401/403). Stopping connection and redirecting to login.');
            try {
                if (connection) {
                    connection.stop();
                }
            } catch (e) {
                // Ignore errors during stop
            }
            if (typeof window !== 'undefined' && window.location) {
                window.location.href = '/Account/Login?sessionExpired=true';
            }
        }

        /**
         * Checks whether an error signifies an unauthorized / forbidden / revoked session.
         */
        function isUnauthorizedError(err) {
            if (!err) return false;
            if (err.statusCode === 401 || err.statusCode === 403) return true;
            const str = String(err.message || err);
            return str.includes('401') || str.includes('403') ||
                   str.toLowerCase().includes('unauthorized') ||
                   str.toLowerCase().includes('forbidden') ||
                   str.toLowerCase().includes('session expired') ||
                   str.toLowerCase().includes('login');
        }

        /**
         * Updates connection status banners and global titlebar indicators.
         */
        function setStatus(state, message) {
            if (isTerminated) return;
            if (state === 'reconnecting') {
                if (bannerEl) {
                    bannerEl.classList.remove('hidden');
                    if (bannerTextEl) bannerTextEl.textContent = message || 'Reconnecting to chat...';
                }
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-amber-500 animate-pulse';
                if (statusTextEl) statusTextEl.textContent = 'Reconnecting...';
            } else if (state === 'connected') {
                if (bannerEl) bannerEl.classList.add('hidden');
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-[#6F9473]';
                if (statusTextEl) statusTextEl.textContent = 'Connected';
            } else if (state === 'disconnected') {
                if (bannerEl) {
                    bannerEl.classList.remove('hidden');
                    if (bannerTextEl) bannerTextEl.textContent = message || 'Disconnected from chat. Reconnecting...';
                }
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-red-400';
                if (statusTextEl) statusTextEl.textContent = 'Disconnected';
            }
        }

        if (typeof signalR === 'undefined') {
            console.error('[SignalR] signalR client library is not loaded. Ensure signalr.min.js is included.');
            return null;
        }

        // Build SignalR HubConnection with automated exponential backoff reconnect policy
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/chat")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        // 1. Connection lifecycle: onreconnecting
        connection.onreconnecting((error) => {
            console.warn('[SignalR] Connection lost, attempting reconnection...', error);
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            setStatus('reconnecting', 'Reconnecting to chat...');
        });

        // 2. Connection lifecycle: onreconnected (with state reconciliation)
        connection.onreconnected(async (connectionId) => {
            if (isTerminated) return;
            console.log('[SignalR] Reconnected successfully with connectionId:', connectionId);
            setStatus('connected');

            try {
                // Re-join conversation group room
                await connection.invoke("JoinConversation", conversationId);
                console.log('[SignalR] Re-joined conversation room:', conversationId);

                // Re-fetch presence via /api/presence
                if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                    const listEl = document.getElementById('conversationList');
                    const headerEl = document.getElementById('active-conversation-header');
                    window.Presence.fetchPresence(listEl, headerEl);
                }

                // Reconcile state: query authoritative REST API for missed messages
                await reconcileState();

                // Trigger markRead/markDelivered for incoming messages
                if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.checkAndMarkIncoming === 'function') {
                    window.Receipts.checkAndMarkIncoming(containerEl || document, conversationId, currentUserId);
                }
            } catch (err) {
                console.error('[SignalR] Error during post-reconnect initialization:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                }
            }
        });

        // 3. Connection lifecycle: onclose
        connection.onclose((error) => {
            console.warn('[SignalR] Connection closed:', error);
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            if (!isTerminated) {
                setStatus('disconnected', 'Disconnected from chat.');
            }
        });

        /**
         * Inspects existing message bubbles in the DOM to find the newest rendered message
         * by (createdAtUtc, id). Used during state reconciliation to ensure ancient
         * messages from history are never appended to the live stream (RT-005).
         *
         * @returns {{ time: number, id: bigint } | null}
         */
        function getNewestRenderedMessageInfo() {
            if (!containerEl || typeof document === 'undefined') return null;
            const rows = containerEl.querySelectorAll('.message-bubble-row');
            if (!rows || rows.length === 0) return null;

            let maxTime = -Infinity;
            let maxId = 0n;
            let found = false;

            for (const row of rows) {
                const timeAttr = row.getAttribute('data-created-at');
                const idAttr = row.getAttribute('data-message-id');
                if (timeAttr) {
                    const t = new Date(timeAttr).getTime();
                    if (!isNaN(t)) {
                        found = true;
                        let idVal = 0n;
                        try {
                            if (idAttr) idVal = BigInt(idAttr);
                        } catch {
                            idVal = 0n;
                        }
                        if (t > maxTime || (t === maxTime && idVal > maxId)) {
                            maxTime = t;
                            maxId = idVal;
                        }
                    }
                }
            }

            return found ? { time: maxTime, id: maxId } : null;
        }

        /**
         * Inserts a list of messages into the stream, deduplicating against existing DOM elements
         * and filtering to messages newer than the newest rendered message (RT-005).
         *
         * @param {Array<object>} messages
         */
        function insertMissingMessages(messages) {
            if (!messages || messages.length === 0) return;

            const newestRendered = getNewestRenderedMessageInfo();

            // 1. Deduplicate against DOM and filter to messages strictly newer than newest rendered message
            const missingNewer = [];
            for (const msg of messages) {
                const msgId = getMessageId(msg);
                if (!msgId || isMessageRendered(msgId)) {
                    continue; // Deduplication: skip if already rendered
                }

                if (newestRendered && msg.createdAtUtc) {
                    const msgTime = new Date(msg.createdAtUtc).getTime();
                    let msgIdVal = 0n;
                    try {
                        msgIdVal = BigInt(msgId);
                    } catch {
                        msgIdVal = 0n;
                    }

                    if (msgTime < newestRendered.time || (msgTime === newestRendered.time && msgIdVal <= newestRendered.id)) {
                        // Older or equal to newest rendered message; skip to avoid appending ancient history
                        continue;
                    }
                }

                missingNewer.push(msg);
            }

            if (missingNewer.length === 0) return;

            // 2. Sort chronological (oldest to newest) to preserve conversational sequence
            missingNewer.sort((a, b) => {
                const ta = new Date(a.createdAtUtc).getTime();
                const tb = new Date(b.createdAtUtc).getTime();
                if (ta !== tb) return ta - tb;
                let idA = 0n;
                let idB = 0n;
                try { idA = BigInt(getMessageId(a) || '0'); } catch { idA = 0n; }
                try { idB = BigInt(getMessageId(b) || '0'); } catch { idB = 0n; }
                return idA < idB ? -1 : idA > idB ? 1 : 0;
            });

            let addedCount = 0;
            let lastAddedMessage = null;

            for (const msg of missingNewer) {
                const msgId = getMessageId(msg);
                if (!msgId || isMessageRendered(msgId)) {
                    continue;
                }

                if (containerEl) {
                    const el = createMessageBubbleElement(msg, currentUserId);
                    if (el && typeof containerEl.appendChild === 'function') {
                        containerEl.appendChild(el);
                        addedCount++;
                        lastAddedMessage = msg;
                    }
                }
            }

            if (addedCount > 0) {
                if (emptyPlaceholderEl) {
                    emptyPlaceholderEl.classList.add('hidden');
                }
                scrollToBottom(true);
                if (lastAddedMessage) {
                    const lastPreview = lastAddedMessage.isDeletedForEveryone ? 'This message was deleted' : lastAddedMessage.content;
                    updateSidebarLastMessage(conversationId, lastPreview, getMessageId(lastAddedMessage), false);
                }
            }
        }

        /**
         * State Reconciliation: Fetches missed messages from the authoritative REST API
         * and seamlessly appends any missing items to the live stream.
         */
        async function reconcileState() {
            if (isTerminated) return;
            try {
                console.log('[SignalR] Reconciling state for conversation:', conversationId);

                // NOTE [RT-005]: Query `/api/conversations/${conversationId}/messages?pageSize=30` directly
                const latestUrl = `/api/conversations/${conversationId}/messages?pageSize=30`;
                const latestResp = await fetch(latestUrl, {
                    headers: { 'Accept': 'application/json' }
                });

                if (latestResp.status === 401 || latestResp.status === 403 ||
                    (latestResp.redirected && latestResp.url.includes('/Account/Login'))) {
                    handleSessionExpired();
                    return;
                }

                if (!latestResp.ok) {
                    console.warn('[SignalR] Failed to fetch latest messages for reconciliation:', latestResp.status);
                    return;
                }

                const contentType = latestResp.headers.get('content-type') || '';
                if (!contentType.includes('application/json')) {
                    if (latestResp.url && latestResp.url.includes('/Account/Login')) {
                        handleSessionExpired();
                        return;
                    }
                }

                const latestData = await latestResp.json();
                if (latestData && latestData.messages && latestData.messages.length > 0) {
                    insertMissingMessages(latestData.messages);
                }
            } catch (err) {
                console.error('[SignalR] State reconciliation error:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                }
            }
        }

        // 4. Register live incoming message handler
        connection.on("ReceiveMessage", function (message) {
            if (!message) return;
            const msgId = getMessageId(message);
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            const previewText = message.isDeletedForEveryone ? 'This message was deleted' : message.content;
            updateSidebarLastMessage(eventConvId, previewText, msgId, false);

            const isOutgoing = currentUserId
                ? (getUserId(message.senderId).toLowerCase() === String(currentUserId).toLowerCase())
                : !!message.isOutgoing;

            const isActiveConversation = eventConvId.toLowerCase() === conversationId.toLowerCase();
            if (!isActiveConversation) {
                if (!isOutgoing && typeof window.Receipts !== 'undefined' && typeof window.Receipts.markDelivered === 'function') {
                    window.Receipts.markDelivered(eventConvId, msgId);
                }
                return;
            }

            // Strict deduplication: check if message ID already exists in DOM (#msg-${id})
            if (isMessageRendered(msgId)) {
                return;
            }

            if (containerEl) {
                const el = createMessageBubbleElement(message, currentUserId);
                if (el && typeof containerEl.appendChild === 'function') {
                    containerEl.appendChild(el);
                }
            }
            if (emptyPlaceholderEl) {
                emptyPlaceholderEl.classList.add('hidden');
            }

            scrollToBottom(true);

            // Phase 7: Automatically mark incoming message as delivered and read if focused
            if (!isOutgoing && typeof window.Receipts !== 'undefined') {
                if (typeof window.Receipts.markDelivered === 'function') {
                    window.Receipts.markDelivered(conversationId, msgId);
                }
                if (typeof window.Receipts.markRead === 'function' &&
                    document.visibilityState === 'visible' &&
                    (typeof document.hasFocus === 'function' ? document.hasFocus() : true)) {
                    window.Receipts.markRead(conversationId, msgId);
                }
            }
        });

        // 5. Phase 7: Register ReceiveReceiptUpdated handler
        connection.on("ReceiveReceiptUpdated", function (receipt) {
            if (!receipt) return;
            const convId = receipt.conversationId || receipt.ConversationId;
            if (String(convId).toLowerCase() !== conversationId.toLowerCase()) return;

            const actorUserId = receipt.userId || receipt.UserId;
            if (currentUserId && String(actorUserId).toLowerCase() === String(currentUserId).toLowerCase()) {
                return; // Ignore own receipts
            }

            const upToId = receipt.upToMessageId !== undefined ? receipt.upToMessageId : receipt.UpToMessageId;
            const rType = receipt.receiptType || receipt.ReceiptType || 'delivered';

            if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.updateMessageCheckmarks === 'function') {
                window.Receipts.updateMessageCheckmarks(containerEl || document, upToId, rType);
            }
        });

        // 5b. Phase 8: Register ReceiveMessageEdited, ReceiveMessageDeletedForEveryone, ReceiveReactionUpdated
        connection.on("ReceiveMessageEdited", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            const newContent = message.content !== undefined ? message.content : (message.Content || '');

            if (eventConvId.toLowerCase() !== conversationId.toLowerCase()) {
                updateSidebarLastMessage(eventConvId, newContent, msgId, true);
                return;
            }

            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                const contentEl = row.querySelector('.message-text-content');
                if (contentEl) {
                    contentEl.textContent = newContent;
                }
                const editedLabel = row.querySelector('.edited-label');
                if (editedLabel && (message.editedAtUtc !== undefined ? message.editedAtUtc : message.EditedAtUtc)) {
                    editedLabel.classList.remove('hidden');
                }
                const replyBtn = row.querySelector('.reply-action-btn');
                if (replyBtn) {
                    replyBtn.setAttribute('data-preview', newContent);
                }
            }

            updateReplyParentBanners(msgId, newContent);

            if (!row || isLatestRenderedMessage(containerEl, msgId)) {
                updateSidebarLastMessage(eventConvId, newContent, msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForEveryone", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            if (eventConvId.toLowerCase() !== conversationId.toLowerCase()) {
                updateSidebarLastMessage(eventConvId, 'This message was deleted', msgId, true);
                return;
            }

            const row = document.getElementById(`msg-${msgId}`);
            applyMessageDeletedForEveryone(row, msgId);

            if (!row || isLatestRenderedMessage(containerEl, msgId)) {
                updateSidebarLastMessage(eventConvId, 'This message was deleted', msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForMe", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(message) || conversationId;
            const newLastId = message.newLastMessageId !== undefined ? message.newLastMessageId : message.NewLastMessageId;
            const newLastPreview = message.newLastMessagePreview !== undefined ? message.newLastMessagePreview : message.NewLastMessagePreview;
            const isActive = eventConvId.toLowerCase() === conversationId.toLowerCase();

            applyMessageDeletedForMe(msgId, eventConvId, newLastId, newLastPreview, isActive);
        });

        connection.on("ReceiveReactionUpdated", function (reaction) {
            if (!reaction) return;
            const msgId = reaction.messageId !== undefined ? reaction.messageId : reaction.MessageId;
            if (!msgId) return;

            const eventConvId = getConversationId(reaction) || conversationId;
            if (eventConvId.toLowerCase() !== conversationId.toLowerCase()) {
                return;
            }

            const row = document.getElementById(`msg-${msgId}`);
            if (row) {
                let pillsContainer = row.querySelector('.reaction-pills-container');
                if (!pillsContainer) {
                    pillsContainer = document.createElement('div');
                    pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
                    const bubble = row.querySelector('.message-bubble') || row.querySelector('.max-w-\\[75\\%\\]');
                    if (bubble) bubble.appendChild(pillsContainer);
                }

                const groups = reaction.reactions !== undefined ? reaction.reactions : reaction.Reactions;
                if (Array.isArray(groups) &&
                    typeof window !== 'undefined' && window.Reactions &&
                    typeof window.Reactions.renderReactionPills === 'function' &&
                    typeof window.Reactions.mapReactionGroupsToSummaries === 'function') {
                    const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
                    const summaries = window.Reactions.mapReactionGroupsToSummaries(groups, currentUserId);
                    window.Reactions.renderReactionPills(pillsContainer, summaries, msgId, conversationId, isOutgoing);
                }
            }
        });

        // 6. Connect and JoinConversation
        async function start() {
            if (isTerminated) return;
            try {
                await connection.start();
                console.log('[SignalR] Connected to /hubs/chat');
                setStatus('connected');

                await connection.invoke("JoinConversation", conversationId);
                console.log('[SignalR] Joined conversation:', conversationId);

                // Fetch initial presence and mark receipts for initial SSR messages
                if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                    const listEl = document.getElementById('conversationList');
                    const headerEl = document.getElementById('active-conversation-header');
                    window.Presence.fetchPresence(listEl, headerEl);
                }

                if (typeof window.Receipts !== 'undefined' && typeof window.Receipts.checkAndMarkIncoming === 'function') {
                    window.Receipts.checkAndMarkIncoming(containerEl || document, conversationId, currentUserId);
                }
            } catch (err) {
                console.error('[SignalR] Connection start failed:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                    return;
                }
                setStatus('disconnected', 'Failed to connect to chat.');
            }
        }

        // 7. LeaveConversation when user navigates away or switches conversations
        function handleLeave() {
            if (connection && connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("LeaveConversation", conversationId).catch(() => {});
            }
        }

        if (typeof window !== 'undefined') {
            window.addEventListener('beforeunload', handleLeave);
            window.addEventListener('pagehide', handleLeave);
        }

        // Initiate connection
        start();

        return {
            connection,
            reconcileState,
            createMessageBubbleHtml: (msg) => createMessageBubbleHtml(msg, currentUserId),
            createMessageBubbleElement: (msg) => createMessageBubbleElement(msg, currentUserId),
            isMessageRendered,
            scrollToBottom,
            updateSidebarLastMessage,
            handleSessionExpired
        };
    }

    /**
     * Initializes a lobby SignalR connection (e.g. on /Chat/Index) without joining a specific conversation.
     * Keeps user online, syncs sidebar previews live, and marks incoming messages delivered.
     *
     * @param {object} options
     * @param {string|null} [options.currentUserId]
     * @returns {object|null} Realtime controller instance
     */
    function initLobby(options) {
        const currentUserId = options && options.currentUserId ? String(options.currentUserId) : null;
        const statusDotEl = document.getElementById('connectionStatusDot');
        const statusTextEl = document.getElementById('connectionStatusText');
        let isTerminated = false;

        function handleSessionExpired() {
            if (isTerminated) return;
            isTerminated = true;
            try {
                if (connection) connection.stop();
            } catch {}
            if (typeof window !== 'undefined' && window.location) {
                window.location.href = '/Account/Login?sessionExpired=true';
            }
        }

        function isUnauthorizedError(err) {
            if (!err) return false;
            if (err.statusCode === 401 || err.statusCode === 403) return true;
            const str = String(err.message || err);
            return str.includes('401') || str.includes('403') ||
                   str.toLowerCase().includes('unauthorized') ||
                   str.toLowerCase().includes('forbidden') ||
                   str.toLowerCase().includes('session expired') ||
                   str.toLowerCase().includes('login');
        }

        function setStatus(state) {
            if (isTerminated) return;
            if (state === 'reconnecting') {
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-amber-500 animate-pulse';
                if (statusTextEl) statusTextEl.textContent = 'Reconnecting...';
            } else if (state === 'connected') {
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-[#6F9473]';
                if (statusTextEl) statusTextEl.textContent = 'Connected';
            } else if (state === 'disconnected') {
                if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-red-400';
                if (statusTextEl) statusTextEl.textContent = 'Disconnected';
            }
        }

        if (typeof signalR === 'undefined') {
            console.error('[SignalR] signalR client library is not loaded.');
            return null;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/chat")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.onreconnecting((error) => {
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            setStatus('reconnecting');
        });

        connection.onreconnected(() => {
            if (isTerminated) return;
            setStatus('connected');
            if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                window.Presence.fetchPresence(document.getElementById('conversationList'), null);
            }
        });

        connection.onclose((error) => {
            if (isUnauthorizedError(error)) {
                handleSessionExpired();
                return;
            }
            if (!isTerminated) setStatus('disconnected');
        });

        connection.on("ReceiveMessage", function (message) {
            if (!message) return;
            const msgId = getMessageId(message);
            const convId = getConversationId(message);
            if (!convId || !msgId) return;

            const previewText = message.isDeletedForEveryone ? 'This message was deleted' : message.content;
            updateSidebarLastMessage(convId, previewText, msgId, false);

            const isOutgoing = currentUserId
                ? (getUserId(message.senderId).toLowerCase() === String(currentUserId).toLowerCase())
                : !!message.isOutgoing;

            if (!isOutgoing && typeof window.Receipts !== 'undefined' && typeof window.Receipts.markDelivered === 'function') {
                window.Receipts.markDelivered(convId, msgId);
            }
        });

        connection.on("ReceiveMessageEdited", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            const convId = getConversationId(message);
            const content = message.content !== undefined ? message.content : (message.Content || '');
            if (convId && msgId) {
                updateSidebarLastMessage(convId, content, msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForEveryone", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            const convId = getConversationId(message);
            if (convId && msgId) {
                updateSidebarLastMessage(convId, 'This message was deleted', msgId, true);
            }
        });

        connection.on("ReceiveMessageDeletedForMe", function (message) {
            if (!message) return;
            const msgId = message.messageId !== undefined ? message.messageId : message.MessageId;
            const convId = getConversationId(message);
            const newLastId = message.newLastMessageId !== undefined ? message.newLastMessageId : message.NewLastMessageId;
            const newLastPreview = message.newLastMessagePreview !== undefined ? message.newLastMessagePreview : message.NewLastMessagePreview;
            if (convId && msgId) {
                applyMessageDeletedForMe(msgId, convId, newLastId, newLastPreview, false);
            }
        });

        connection.start()
            .then(() => {
                setStatus('connected');
                if (typeof window.Presence !== 'undefined' && typeof window.Presence.fetchPresence === 'function') {
                    window.Presence.fetchPresence(document.getElementById('conversationList'), null);
                }
            })
            .catch((err) => {
                console.error('[SignalR] Lobby connection start failed:', err);
                if (isUnauthorizedError(err)) {
                    handleSessionExpired();
                    return;
                }
                setStatus('disconnected');
            });

        return {
            connection,
            updateSidebarLastMessage,
            handleSessionExpired
        };
    }

    const api = {
        init,
        initLobby,
        escapeHtml,
        formatTime,
        getInitials,
        getMessageId,
        getUserId,
        getConversationId,
        isMessageRendered,
        isLatestRenderedMessage,
        createMessageBubbleHtml,
        createMessageBubbleElement,
        updateReplyParentBanners,
        applyMessageDeletedForEveryone,
        applyMessageDeletedForMe,
        updateSidebarLastMessage,
        scrollToBottom
    };

    if (typeof window !== 'undefined') {
        window.ChatRealtime = api;
    }
    if (typeof global !== 'undefined') {
        global.ChatRealtime = api;
    }
    if (typeof globalThis !== 'undefined') {
        globalThis.ChatRealtime = api;
    }
    if (typeof module !== 'undefined' && module.exports) {
        module.exports = api;
    }
})(typeof window !== 'undefined' ? window : (typeof global !== 'undefined' ? global : this));
