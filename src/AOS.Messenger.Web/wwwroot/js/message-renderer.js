/**
 * AOS Messenger - Message Renderer Module
 *
 * Implements safe DOM construction for message bubbles and conversation stream items.
 *
 * Invariants:
 * - Safe DOM tree construction using document.createElement exclusively.
 * - Zero HTML string injection into DOM.
 * - All DOM elements built with document.createElement().
 * - All user text assigned strictly via .textContent or createTextNode().
 * - Strict escaping and deduplication.
 * - Outgoing message checkmark status rendering (sent -> delivered -> read).
 * - Phase 8: Message actions (React, Reply, Edit, Delete) and reaction pills.
 */

import { renderReactionPills } from './reactions.js';
import { formatLastSeen } from './presence.js';
import { closeChatActionsMenu } from './chat-actions.js';
import { updateSidebarLastMessage as updateSidebarLastMessageInList } from './conversation-list.js';
import { getMessageContentClassName, truncateGraphemes } from './emoji-utils.js';

/**
 * Sanitizes user-supplied text to prevent Cross-Site Scripting (XSS).
 * @param {string} str - Raw input string
 * @returns {string} HTML-escaped string
 */
export function escapeHtml(str) {
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
export function formatTime(isoString) {
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
export function getInitials(name, handle) {
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
 * @returns {string|null} Normalized ID string
 */
export function getMessageId(msg) {
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
export function getUserId(u) {
    if (!u) return '';
    if (typeof u === 'string') return u;
    if (typeof u === 'object' && u.value) return String(u.value);
    return String(u);
}

/**
 * Checks if a message ID is already rendered in the DOM.
 * @param {string|object} msgOrId
 * @returns {boolean} True if already rendered in message stream
 */
export function isMessageRendered(msgOrId) {
    const id = typeof msgOrId === 'object' ? getMessageId(msgOrId) : msgOrId;
    if (!id) return false;
    if (typeof document === 'undefined') return false;
    return !!(document.getElementById(`msg-${id}`) || document.querySelector(`[data-message-id="${id}"]`));
}

/**
 * Scrolls the message stream smoothly to the bottom.
 * @param {boolean} smooth
 */
export function scrollToBottom(smooth = true) {
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
 * Updates the last message snippet in the left conversation sidebar.
 * @param {string} convId
 * @param {string} previewText
 */
export function updateSidebarLastMessage(convId, previewText, messageId = null, onlyIfLatest = false, forceClearId = false, shouldReorder = false) {
    if (!convId || typeof document === 'undefined') return false;
    return updateSidebarLastMessageInList(convId, previewText, messageId, onlyIfLatest, forceClearId, shouldReorder);
}

/**
 * Checks whether msgId is currently the latest rendered row inside containerEl.
 * @param {HTMLElement|null} containerEl
 * @param {string|number} msgId
 * @returns {boolean}
 */
export function isLatestRenderedMessage(containerEl, msgId) {
    if (!containerEl || !msgId || typeof containerEl.querySelectorAll !== 'function') return false;
    const rows = containerEl.querySelectorAll('.message-bubble-row');
    if (!rows || rows.length === 0) return false;
    const lastRow = rows[rows.length - 1];
    return String(lastRow.getAttribute('data-message-id')) === String(msgId);
}

/**
 * Truncates a reply quote preview safely by grapheme clusters so complex emojis are never split.
 * @param {string} text
 * @param {number} [maxGraphemes]
 * @returns {string}
 */
export function truncateReplyPreview(text, maxGraphemes = 100) {
    return truncateGraphemes(text, maxGraphemes);
}

/**
 * Updates child reply quote banners referencing an edited parent message.
 * @param {string|number} parentMessageId
 * @param {string} newContent
 */
export function updateReplyParentBanners(parentMessageId, newContent) {
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
 * Replaces child reply quote banner text with a tombstoned placeholder when parent is deleted.
 * @param {string|number} parentMessageId
 */
export function scrubReplyParentBanners(parentMessageId) {
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
 * Applies an authoritative edit update to a rendered message row and its child reply banners.
 * @param {string|number} msgId
 * @param {string} newContent
 * @param {string|null|undefined} editedAtUtc
 */
export function applyMessageEditedToRow(msgId, newContent, editedAtUtc) {
    if (!msgId || typeof document === 'undefined') return;
    const row = typeof document.getElementById === 'function' ? document.getElementById(`msg-${msgId}`) : null;
    if (row) {
        const contentEl = row.querySelector('.message-text-content');
        if (contentEl && row.getAttribute('data-deleted') !== 'true') {
            contentEl.textContent = newContent || '';
            contentEl.className = getMessageContentClassName(newContent || '');
        }
        const editedLabel = row.querySelector('.edited-label');
        if (editedLabel && editedAtUtc) {
            editedLabel.classList.remove('hidden');
        }
        const replyBtn = row.querySelector('.reply-action-btn');
        if (replyBtn) {
            replyBtn.setAttribute('data-preview', newContent || '');
        }
    }
    updateReplyParentBanners(msgId, newContent || '');
}

/**
 * Transforms a rendered message row into a tombstoned state and scrubs child reply banners.
 * @param {HTMLElement|null} row
 * @param {string|number} msgId
 */
export function applyMessageDeletedForEveryone(row, msgId) {
    if (row && typeof document !== 'undefined') {
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

        for (const sel of ['.message-actions-bar', '[data-action="edit"]', '[data-action="delete"]', '[data-action="react"]', '.reply-action-btn', 'span[data-receipt-status]', '.reaction-pills-container', '.edited-label']) {
            const node = row.querySelector(sel);
            if (node && typeof node.remove === 'function') {
                node.remove();
            }
        }
    }

    if (msgId) {
        scrubReplyParentBanners(msgId);
    }
}

/**
 * Removes a caller-deleted message row and synchronizes sidebar preview safely.
 * @param {string|number} msgId
 * @param {string} convId
 * @param {string|number|null} newLastMessageId
 * @param {string|null} newLastMessagePreview
 * @param {boolean} isActiveConv
 */
export function applyMessageDeletedForMe(msgId, convId, newLastMessageId, newLastMessagePreview, isActiveConv) {
    if (typeof document === 'undefined') return;
    if (isActiveConv && msgId) {
        const row = typeof document.getElementById === 'function' ? document.getElementById(`msg-${msgId}`) : null;
        if (row && typeof row.remove === 'function') {
            row.remove();
        }

        if (typeof document.querySelectorAll === 'function') {
            document.querySelectorAll(`[data-reply-parent-id="${msgId}"]`).forEach((q) => {
                if (typeof q.remove === 'function') q.remove();
            });
        }

        const containerEl = typeof document.getElementById === 'function' ? document.getElementById('messages-container') : null;
        const emptyPlaceholderEl = typeof document.getElementById === 'function' ? document.getElementById('no-messages-placeholder') : null;
        if (containerEl && emptyPlaceholderEl && containerEl.querySelectorAll('.message-bubble-row').length === 0) {
            emptyPlaceholderEl.classList.remove('hidden');
            emptyPlaceholderEl.classList.add('flex');
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
 * Resolves initial checkmark status and icons based on peer watermarks.
 * @param {string|null} messageId
 * @param {object|null} peerWatermarks
 * @returns {{ status: string, icon: string, colorClass: string }}
 */
export function resolveReceiptStatus(messageId, peerWatermarks = null) {
    if (!messageId) {
        return { status: 'sent', icon: 'done', colorClass: 'text-white/70' };
    }

    let lastRead = null;
    let lastDelivered = null;

    if (peerWatermarks) {
        lastRead = peerWatermarks.peerLastReadMessageId || peerWatermarks.PeerLastReadMessageId;
        lastDelivered = peerWatermarks.peerLastDeliveredMessageId || peerWatermarks.PeerLastDeliveredMessageId;
    } else if (typeof document !== 'undefined') {
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
 * Creates DOM element tree for a single message bubble matching Stitch warm modern design tokens.
 * Pure DOM construction using document.createElement and .textContent - zero HTML string parsing.
 *
 * @param {object} message - MessageDto
 * @param {string|null} currentUserId - Authenticated user's GUID string
 * @param {object|null} peerWatermarks - Optional peer watermarks
 * @returns {HTMLElement} Message bubble row element
 */
export function createMessageBubbleElement(message, currentUserId, peerWatermarks = null) {
    if (typeof document === 'undefined') {
        return null;
    }

    const id = getMessageId(message);
    const senderId = getUserId(message.senderId);

    const isOutgoing = currentUserId
        ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
        : !!message.isOutgoing;

    const initials = getInitials(message.senderName, message.senderHandle);
    const senderName = message.senderName || 'Unknown';
    const timeStr = formatTime(message.createdAtUtc);
    const convId = message.conversationId ? (typeof message.conversationId === 'object' && message.conversationId.value ? message.conversationId.value : String(message.conversationId)) : '';

    // Root row container
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
        row.className = 'flex flex-wrap-reverse justify-end items-center gap-y-1 message-bubble-row group relative';
        row.setAttribute('data-is-outgoing', 'true');
    } else {
        row.className = 'flex flex-wrap justify-start items-center gap-2.5 message-bubble-row group relative';
        row.setAttribute('data-is-outgoing', 'false');

        // Avatar circle for incoming
        const avatar = document.createElement('div');
        avatar.className = 'w-8 h-8 rounded-full bg-[#B3929D]/20 dark:bg-[#B3929D]/25 text-[#735761] dark:text-[#E1BDC9] border border-[#B3929D]/30 font-bold text-xs flex items-center justify-center flex-shrink-0 mt-1';
        avatar.textContent = initials;
        row.appendChild(avatar);
    }

    if (senderId) {
        row.setAttribute('data-sender-id', senderId);
    }

    // Bubble container
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

    // Reply quote banner if replyTo exists
    if (message.replyTo) {
        const replyBanner = document.createElement('div');
        const rBgClass = isOutgoing
            ? 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-black/15 border-white/70 text-white/80 cursor-pointer hover:bg-black/25 transition-colors'
            : 'mb-1.5 px-2.5 py-1 rounded border-l-2 text-xs bg-[#F8F6F3] dark:bg-[#302F2C] border-[#B3929D] text-[#6E6560] dark:text-[#C9C0B9] cursor-pointer hover:bg-[#F0E9DF] dark:hover:bg-[#3E3C38] transition-colors';
        replyBanner.className = rBgClass;

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

    // Message content or deleted placeholder
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
        contentDiv.className = getMessageContentClassName(message.content || '');
        contentDiv.textContent = message.content || '';
        bubble.appendChild(contentDiv);
    }

    // Footer with time, edited flag, and receipt checkmarks
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

    // Delivery & Read Receipt checkmark indicator (outgoing only)
    if (isOutgoing && !message.isDeletedForEveryone) {
        const receipt = resolveReceiptStatus(id, peerWatermarks);
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

    // Reaction pills container below footer
    const pillsContainer = document.createElement('div');
    pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
    if (message.reactions && message.reactions.length > 0) {
        renderReactionPills(pillsContainer, message.reactions, id, convId, isOutgoing);
    } else {
        pillsContainer.classList.add('hidden');
    }
    bubble.appendChild(pillsContainer);

    row.appendChild(bubble);

    // Message action controls bar (hover/focus + visible on mobile touch)
    if (!message.isDeletedForEveryone) {
        const actionsBar = document.createElement('div');
        actionsBar.className = isOutgoing
            ? 'message-actions-bar opacity-100 sm:opacity-0 sm:group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity self-center shrink-0 mr-1 flex items-center gap-0.5'
            : 'message-actions-bar opacity-100 sm:opacity-0 sm:group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity self-center shrink-0 ml-1 flex items-center gap-0.5';

        // React button
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

        // Reply button
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

        // Edit button (sender only)
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

        // Delete button
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

        if (isOutgoing) {
            row.insertBefore(actionsBar, bubble);
        } else {
            row.appendChild(actionsBar);
        }
    }

    return row;
}

/**
 * Creates HTML string representation for a single message bubble (for testing parity).
 * Escapes all user content for XSS defense.
 *
 * @param {object} message - MessageDto
 * @param {string|null} currentUserId - Authenticated user's GUID string
 * @param {object|null} peerWatermarks - Optional peer watermarks
 * @returns {string} HTML string
 */
export function createMessageBubbleHtml(message, currentUserId, peerWatermarks = null) {
    const id = escapeHtml(getMessageId(message));
    const senderId = getUserId(message.senderId);

    const isOutgoing = currentUserId
        ? (senderId.toLowerCase() === String(currentUserId).toLowerCase())
        : !!message.isOutgoing;

    const initials = escapeHtml(getInitials(message.senderName, message.senderHandle));
    const senderName = escapeHtml(message.senderName);
    const timeStr = escapeHtml(formatTime(message.createdAtUtc));
    const editedStr = (message.editedAtUtc && !message.isDeletedForEveryone) ? '<span class="text-[9px] italic edited-label">edited</span>' : '';

    let replyBannerHtml = '';
    if (message.replyTo) {
        const rSender = escapeHtml(message.replyTo.senderName || message.replyTo.senderDisplayName || 'Unknown');
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
                <div class="truncate text-[10px]">${rContent}</div>
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
        contentHtml = `<div class="${getMessageContentClassName(message.content || '')}">${escapeHtml(message.content)}</div>`;
    }

    const replyBtnHtml = !message.isDeletedForEveryone
        ? `<button type="button" class="reply-action-btn opacity-0 group-hover:opacity-100 transition-opacity self-center ${isOutgoing ? 'mr-2' : 'ml-2'} text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#B3929D]" title="Reply to message" data-id="${id}" data-sender="${senderName}" data-preview="${escapeHtml(message.content)}">
            <span class="material-symbols-outlined text-[18px]">reply</span>
           </button>`
        : '';

    let receiptHtml = '';
    if (isOutgoing && !message.isDeletedForEveryone) {
        const receipt = resolveReceiptStatus(id, peerWatermarks);
        receiptHtml = `<span class="material-symbols-outlined text-[13px] select-none align-middle ${receipt.colorClass}" data-message-id="${id}" data-receipt-status="${receipt.status}">${receipt.icon}</span>`;
    }

    const senderIdAttr = senderId ? ` data-sender-id="${escapeHtml(senderId)}"` : '';

    if (isOutgoing) {
        return `
        <div id="msg-${id}" class="flex flex-wrap-reverse justify-end items-center gap-y-1 message-bubble-row group relative" data-message-id="${id}" data-is-outgoing="true"${senderIdAttr} data-created-at="${message.createdAtUtc}">
            ${replyBtnHtml}
            <div class="max-w-[75%] rounded-2xl rounded-tr-xs px-4 py-2 bg-[#B3929D] text-white shadow-2xs relative">
                ${replyBannerHtml}
                ${contentHtml}
                <div class="flex items-center justify-end gap-1 mt-1 text-[10px] text-white/80">
                    ${editedStr}
                    <span>${timeStr}</span>
                    ${receiptHtml}
                </div>
            </div>
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

function normalizeIdValue(value) {
    if (value === null || value === undefined) return '';
    if (typeof value === 'object' && value.value !== undefined) {
        return String(value.value);
    }
    return String(value);
}

function parseBigIntSafe(val) {
    if (val === null || val === undefined || val === '') return 0n;
    try {
        return BigInt(String(val));
    } catch {
        return 0n;
    }
}

/**
 * Deterministic total ordering key comparator: (CreatedAtUtc, Id).
 * @param {object} a
 * @param {object} b
 * @returns {number}
 */
export function compareMessages(a, b) {
    const rawTimeA = a?.createdAtUtc ?? a?.CreatedAtUtc ?? '';
    const rawTimeB = b?.createdAtUtc ?? b?.CreatedAtUtc ?? '';
    const timeA = rawTimeA ? Date.parse(rawTimeA) : NaN;
    const timeB = rawTimeB ? Date.parse(rawTimeB) : NaN;

    if (!Number.isNaN(timeA) && !Number.isNaN(timeB) && timeA !== timeB) {
        return timeA - timeB;
    }

    const idA = parseBigIntSafe(getMessageId(a));
    const idB = parseBigIntSafe(getMessageId(b));
    if (idA < idB) return -1;
    if (idA > idB) return 1;
    return 0;
}

function clearElementChildren(el) {
    if (!el) return;
    while (el.firstChild) {
        el.removeChild(el.firstChild);
    }
}

function syncMessagesContainerAttributes(containerEl, currentUserId, meta) {
    if (!containerEl) return;

    if (currentUserId !== undefined && currentUserId !== null) {
        containerEl.setAttribute('data-current-user-id', String(currentUserId));
    }

    if (!meta) return;

    const convId = normalizeIdValue(meta.id ?? meta.Id);
    const peerUserId = normalizeIdValue(meta.otherParticipantId ?? meta.OtherParticipantId);
    const isOnline = !!(meta.isOnline ?? meta.IsOnline);
    const lastSeen = meta.lastSeenAtUtc ?? meta.LastSeenAtUtc ?? '';
    const lastDelivered = normalizeIdValue(meta.peerLastDeliveredMessageId ?? meta.PeerLastDeliveredMessageId);
    const lastRead = normalizeIdValue(meta.peerLastReadMessageId ?? meta.PeerLastReadMessageId);

    if (convId) containerEl.setAttribute('data-conversation-id', convId);
    containerEl.setAttribute('data-peer-user-id', peerUserId);
    containerEl.setAttribute('data-peer-is-online', isOnline ? 'true' : 'false');
    containerEl.setAttribute('data-peer-last-seen', lastSeen || '');
    containerEl.setAttribute('data-peer-last-delivered-id', lastDelivered);
    containerEl.setAttribute('data-peer-last-read-id', lastRead);
}

/**
 * Inserts a message into containerEl in deterministic (CreatedAtUtc, Id) order with ID deduplication.
 * @param {HTMLElement|null} containerEl
 * @param {object} message
 * @param {string|null} currentUserId
 * @param {object|null} [peerWatermarks]
 * @returns {HTMLElement|null}
 */
export function insertMessageOrdered(containerEl, message, currentUserId, peerWatermarks = null) {
    if (!containerEl || !message || typeof document === 'undefined') return null;

    const msgId = getMessageId(message);
    if (!msgId) return null;

    if ((typeof containerEl.querySelector === 'function' && containerEl.querySelector(`[data-message-id="${msgId}"]`)) ||
        document.getElementById(`msg-${msgId}`)) {
        return null;
    }

    const el = createMessageBubbleElement(message, currentUserId, peerWatermarks);
    if (!el) return null;

    const rows = typeof containerEl.querySelectorAll === 'function'
        ? containerEl.querySelectorAll('.message-bubble-row')
        : [];

    let insertBeforeNode = null;
    for (let i = rows.length - 1; i >= 0; i--) {
        const row = rows[i];
        const rowModel = {
            id: row.getAttribute('data-message-id'),
            createdAtUtc: row.getAttribute('data-created-at')
        };
        if (compareMessages(message, rowModel) < 0) {
            insertBeforeNode = row;
        } else {
            break;
        }
    }

    if (insertBeforeNode && typeof containerEl.insertBefore === 'function') {
        containerEl.insertBefore(el, insertBeforeNode);
    } else if (typeof containerEl.appendChild === 'function') {
        containerEl.appendChild(el);
    }

    const emptyPlaceholder = document.getElementById('no-messages-placeholder');
    if (emptyPlaceholder) {
        emptyPlaceholder.classList.add('hidden');
    }

    return el;
}

/**
 * Renders a full message page sorted ascending by (CreatedAtUtc, Id) using a DocumentFragment.
 * @param {HTMLElement|null} containerEl
 * @param {Array<object>} messages
 * @param {string|null} currentUserId
 * @param {object|null} [peerWatermarks]
 * @param {{ scrollToEnd?: boolean }} [options]
 */
export function renderMessages(containerEl, messages, currentUserId, peerWatermarks = null, { scrollToEnd = true } = {}) {
    if (!containerEl || typeof document === 'undefined') return;

    clearElementChildren(containerEl);
    syncMessagesContainerAttributes(containerEl, currentUserId, peerWatermarks);

    const skeletonEl = document.getElementById('chat-loading-skeleton');
    if (skeletonEl) skeletonEl.classList.add('hidden');

    hideSafeError();

    const list = Array.isArray(messages) ? [...messages] : [];
    const seenIds = new Set();
    const deduped = [];
    for (const msg of list) {
        const msgId = getMessageId(msg);
        if (!msgId || seenIds.has(msgId)) continue;
        seenIds.add(msgId);
        deduped.push(msg);
    }

    deduped.sort(compareMessages);

    const emptyPlaceholder = document.getElementById('no-messages-placeholder');
    if (deduped.length === 0) {
        if (emptyPlaceholder) {
            emptyPlaceholder.classList.remove('hidden');
            emptyPlaceholder.classList.add('flex');
        }
        return;
    }

    if (emptyPlaceholder) {
        emptyPlaceholder.classList.add('hidden');
    }

    const frag = typeof document.createDocumentFragment === 'function'
        ? document.createDocumentFragment()
        : containerEl;

    for (const msg of deduped) {
        const el = createMessageBubbleElement(msg, currentUserId, peerWatermarks);
        if (el) {
            frag.appendChild(el);
        }
    }

    if (frag !== containerEl && typeof containerEl.appendChild === 'function') {
        containerEl.appendChild(frag);
    }

    if (scrollToEnd) {
        const streamEl = document.getElementById('message-stream');
        if (streamEl) {
            streamEl.scrollTop = streamEl.scrollHeight;
        }
    }
}

/**
 * Prepends older keyset-paginated messages while preserving the caller's scroll offset.
 * @param {HTMLElement|null} containerEl
 * @param {HTMLElement|null} streamEl
 * @param {Array<object>} messages
 * @param {string|null} currentUserId
 * @param {object|null} [peerWatermarks]
 * @returns {number}
 */
export function prependOlderMessages(containerEl, streamEl, messages, currentUserId, peerWatermarks = null) {
    if (!containerEl || !Array.isArray(messages) || messages.length === 0 || typeof document === 'undefined') {
        return 0;
    }

    const prevScrollHeight = streamEl ? streamEl.scrollHeight : 0;
    const prevScrollTop = streamEl ? streamEl.scrollTop : 0;

    const sorted = [...messages].sort(compareMessages);
    const frag = typeof document.createDocumentFragment === 'function'
        ? document.createDocumentFragment()
        : null;

    let added = 0;
    for (const msg of sorted) {
        const msgId = getMessageId(msg);
        if (!msgId || isMessageRendered(msgId)) continue;
        const el = createMessageBubbleElement(msg, currentUserId, peerWatermarks);
        if (el && frag) {
            frag.appendChild(el);
            added++;
        }
    }

    if (added > 0 && frag) {
        if (containerEl.firstChild && typeof containerEl.insertBefore === 'function') {
            containerEl.insertBefore(frag, containerEl.firstChild);
        } else if (typeof containerEl.appendChild === 'function') {
            containerEl.appendChild(frag);
        }

        if (streamEl) {
            streamEl.scrollTop = prevScrollTop + (streamEl.scrollHeight - prevScrollHeight);
        }
    }

    return added;
}

/**
 * Clears peer identity, handle, initials, bio, and status badges from the active conversation header
 * so stale peer metadata never remains visible while loading or displaying an error for another chat.
 */
export function clearConversationHeader() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;

    const headerEl = document.getElementById('active-conversation-header');
    if (headerEl) {
        if (typeof headerEl.setAttribute === 'function') {
            headerEl.setAttribute('data-peer-user-id', '');
        }
        const initialsEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-initials]') : null;
        if (initialsEl) initialsEl.textContent = '';

        const nameEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-name]') : null;
        if (nameEl) nameEl.textContent = '';

        const handleEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-handle]') : null;
        if (handleEl) handleEl.textContent = '';

        const bioEl = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-bio]') : null;
        if (bioEl) {
            bioEl.textContent = '';
            if (bioEl.classList) bioEl.classList.add('hidden');
        }

        const bioSep = typeof headerEl.querySelector === 'function' ? headerEl.querySelector('[data-peer-bio-sep]') : null;
        if (bioSep && bioSep.classList) bioSep.classList.add('hidden');

        if (typeof headerEl.querySelector === 'function') {
            for (const selector of ['[data-peer-pinned-badge]', '[data-peer-muted-badge]', '[data-peer-blocked-badge]']) {
                const badge = headerEl.querySelector(selector);
                if (badge && badge.classList) badge.classList.add('hidden');
            }
        }

        const typingEl = document.getElementById('typing-indicator');
        if (typingEl) {
            typingEl.textContent = '';
            if (typingEl.classList) typingEl.classList.add('hidden');
        }
    }

    const emptyPromptEl = typeof document.querySelector === 'function'
        ? document.querySelector('[data-empty-state-peer-prompt]')
        : null;
    if (emptyPromptEl) {
        emptyPromptEl.textContent = 'Send a message below to start the conversation.';
    }
}

/**
 * Disables all 10 desktop & mobile header action forms, clears their hidden conversationId,
 * and scrubs stale peer identity from the header before awaiting a switch.
 */
export function lockAndClearHeaderActions() {
    closeChatActionsMenu();
    clearConversationHeader();
    if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;

    const forms = document.querySelectorAll('form[data-header-action-form]');
    for (const form of forms) {
        const convInput = form.querySelector('input[name="conversationId"]');
        if (convInput) convInput.value = '';
        const submitBtn = form.querySelector('button[type="submit"]');
        if (submitBtn) submitBtn.disabled = true;
    }
}

function updateHeaderActionForms(convId, isPinned, isMuted, isArchived = false) {
    if (typeof document === 'undefined' || typeof document.querySelectorAll !== 'function') return;

    const forms = document.querySelectorAll('form[data-header-action-form]');
    for (const form of forms) {
        const actionType = form.getAttribute('data-header-action-form');
        const convInput = form.querySelector('input[name="conversationId"]');
        if (convInput) convInput.value = convId;

        const submitBtn = form.querySelector('button[type="submit"]');
        if (submitBtn) {
            submitBtn.disabled = !convId;
        }

        if (actionType === 'pin') {
            const pinInput = form.querySelector('input[name="isPinned"]');
            if (pinInput) pinInput.value = isPinned ? 'false' : 'true';
            const icon = form.querySelector('.material-symbols-outlined');
            if (icon) icon.textContent = isPinned ? 'keep_off' : 'push_pin';
            const label = isPinned ? 'Unpin conversation' : 'Pin conversation';
            if (submitBtn && submitBtn.getAttribute('title')) {
                submitBtn.setAttribute('title', label);
            }
            const spans = form.querySelectorAll('span');
            if (spans.length > 1) {
                spans[spans.length - 1].textContent = label;
            }
        } else if (actionType === 'mute') {
            const muteInput = form.querySelector('input[name="isMuted"]');
            if (muteInput) muteInput.value = isMuted ? 'false' : 'true';
            const icon = form.querySelector('.material-symbols-outlined');
            if (icon) icon.textContent = isMuted ? 'volume_up' : 'volume_off';
            const label = isMuted ? 'Unmute conversation' : 'Mute conversation';
            if (submitBtn && submitBtn.getAttribute('title')) {
                submitBtn.setAttribute('title', label);
            }
            const spans = form.querySelectorAll('span');
            if (spans.length > 1) {
                spans[spans.length - 1].textContent = label;
            }
        } else if (actionType === 'archive') {
            const archiveInput = form.querySelector('input[name="isArchived"]');
            if (archiveInput) archiveInput.value = isArchived ? 'false' : 'true';
            const icon = form.querySelector('.material-symbols-outlined');
            if (icon) icon.textContent = isArchived ? 'unarchive' : 'archive';
            const label = isArchived ? 'Unarchive conversation' : 'Archive conversation';
            if (submitBtn && submitBtn.getAttribute('title')) {
                submitBtn.setAttribute('title', label);
            }
            const spans = form.querySelectorAll('span');
            if (spans.length > 1) {
                spans[spans.length - 1].textContent = label;
            }
        }
    }
}

/**
 * Updates the active conversation header, presence state, badges, and all 10 desktop/mobile action forms.
 * @param {object} meta
 */
export function renderConversationHeader(meta) {
    if (!meta || typeof document === 'undefined') return;

    const convId = normalizeIdValue(meta.id ?? meta.Id);
    const peerId = normalizeIdValue(meta.otherParticipantId ?? meta.OtherParticipantId);
    const peerName = meta.otherParticipantName ?? meta.OtherParticipantName ?? 'Unknown';
    const peerHandle = meta.otherParticipantHandle ?? meta.OtherParticipantHandle ?? '';
    const peerBio = meta.otherParticipantBio ?? meta.OtherParticipantBio ?? '';
    const isBlocked = !!(meta.isBlocked ?? meta.IsBlocked);
    const isPinned = !!(meta.isPinned ?? meta.IsPinned);
    const isMuted = !!(meta.isMuted ?? meta.IsMuted);
    const isArchived = !!(meta.isArchived ?? meta.IsArchived);
    const isOnline = !!(meta.isOnline ?? meta.IsOnline);
    const lastSeen = meta.lastSeenAtUtc ?? meta.LastSeenAtUtc ?? null;

    const headerEl = document.getElementById('active-conversation-header');
    if (headerEl) {
        headerEl.setAttribute('data-peer-user-id', peerId);

        const initialsEl = headerEl.querySelector('[data-peer-initials]');
        if (initialsEl) initialsEl.textContent = getInitials(peerName, peerHandle);

        const nameEl = headerEl.querySelector('[data-peer-name]');
        if (nameEl) nameEl.textContent = peerName;

        const handleEl = headerEl.querySelector('[data-peer-handle]');
        if (handleEl) handleEl.textContent = peerHandle ? `@${peerHandle}` : '';

        const pinnedBadge = headerEl.querySelector('[data-peer-pinned-badge]');
        if (pinnedBadge) pinnedBadge.classList.toggle('hidden', !isPinned);

        const mutedBadge = headerEl.querySelector('[data-peer-muted-badge]');
        if (mutedBadge) mutedBadge.classList.toggle('hidden', !isMuted);

        const blockedBadge = headerEl.querySelector('[data-peer-blocked-badge]');
        if (blockedBadge) blockedBadge.classList.toggle('hidden', !isBlocked);

        const bioSep = headerEl.querySelector('[data-peer-bio-sep]');
        const bioEl = headerEl.querySelector('[data-peer-bio]');
        const hasBio = !!(peerBio && String(peerBio).trim());
        if (bioSep) bioSep.classList.toggle('hidden', !hasBio);
        if (bioEl) {
            bioEl.textContent = hasBio ? String(peerBio) : '';
            bioEl.classList.toggle('hidden', !hasBio);
        }

        const dotEl = document.getElementById('header-presence-dot');
        if (dotEl) {
            dotEl.className = isOnline
                ? 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-[#6F9473] border-2 border-[#FFFFFF] dark:border-[#3A3835]'
                : 'absolute bottom-0 right-0 w-2.5 h-2.5 rounded-full bg-stone-400 dark:bg-stone-500 border-2 border-[#FFFFFF] dark:border-[#3A3835]';
        }

        const presenceTextEl = document.getElementById('header-presence-text');
        if (presenceTextEl) {
            presenceTextEl.textContent = isOnline ? 'Online' : formatLastSeen(lastSeen);
        }

        const typingEl = document.getElementById('typing-indicator');
        if (typingEl) {
            typingEl.textContent = '';
            typingEl.classList.add('hidden');
        }
    }

    const emptyPromptEl = document.querySelector('[data-empty-state-peer-prompt]');
    if (emptyPromptEl) {
        emptyPromptEl.textContent = `Send a message below to start the conversation with ${peerName}.`;
    }

    updateHeaderActionForms(convId, isPinned, isMuted, isArchived);

    const announcerEl = document.getElementById('chat-live-announcer');
    if (announcerEl) {
        announcerEl.textContent = `Switched to conversation with ${peerName}`;
    }
}

/**
 * Clears active message pane DOM elements without destroying shell structure.
 */
export function clearPane() {
    if (typeof document === 'undefined') return;
    const containerEl = document.getElementById('messages-container');
    clearElementChildren(containerEl);
    if (containerEl) {
        containerEl.setAttribute('data-conversation-id', '');
        containerEl.setAttribute('data-peer-user-id', '');
        containerEl.setAttribute('data-peer-is-online', 'false');
        containerEl.setAttribute('data-peer-last-seen', '');
        containerEl.setAttribute('data-peer-last-delivered-id', '');
        containerEl.setAttribute('data-peer-last-read-id', '');
        containerEl.setAttribute('data-next-cursor', '');
        containerEl.setAttribute('data-has-more', 'false');
    }
    const emptyPlaceholder = document.getElementById('no-messages-placeholder');
    if (emptyPlaceholder) emptyPlaceholder.classList.add('hidden');
    const loadingTop = document.getElementById('messages-loading-top');
    if (loadingTop) loadingTop.classList.add('hidden');
    const skeletonEl = document.getElementById('chat-loading-skeleton');
    if (skeletonEl) skeletonEl.classList.add('hidden');
    hideSafeError();
}

export function showEmptyState() {
    if (typeof document === 'undefined') return;
    const emptyEl = document.getElementById('chat-empty-state');
    const activeEl = document.getElementById('chat-active-pane');
    if (emptyEl) emptyEl.classList.remove('hidden');
    if (activeEl) activeEl.classList.add('hidden');
}

export function showActivePane() {
    if (typeof document === 'undefined') return;
    const emptyEl = document.getElementById('chat-empty-state');
    const activeEl = document.getElementById('chat-active-pane');
    if (emptyEl) emptyEl.classList.add('hidden');
    if (activeEl) activeEl.classList.remove('hidden');
}

export function showLoadingSkeleton() {
    if (typeof document === 'undefined') return;
    clearPane();
    const skeletonEl = document.getElementById('chat-loading-skeleton');
    if (skeletonEl) skeletonEl.classList.remove('hidden');
}

export function hideSafeError() {
    if (typeof document === 'undefined') return;
    const errEl = document.getElementById('chat-error-banner');
    if (errEl) {
        errEl.textContent = '';
        errEl.classList.add('hidden');
    }
}

export function showSafeError(message) {
    if (typeof document === 'undefined') return;
    clearPane();
    const errEl = document.getElementById('chat-error-banner');
    if (errEl) {
        errEl.textContent = message || 'Could not load conversation. Try again.';
        errEl.classList.remove('hidden');
    }
}

/**
 * Immediately purges all sensitive private data from the DOM before session-expiry redirect.
 */
export function wipeSensitiveDom() {
    if (typeof document === 'undefined') return;

    clearPane();
    lockAndClearHeaderActions();

    const headerEl = document.getElementById('active-conversation-header');
    if (headerEl) {
        headerEl.setAttribute('data-peer-user-id', '');
        const initialsEl = headerEl.querySelector('[data-peer-initials]');
        if (initialsEl) initialsEl.textContent = '';
        const nameEl = headerEl.querySelector('[data-peer-name]');
        if (nameEl) nameEl.textContent = '';
        const handleEl = headerEl.querySelector('[data-peer-handle]');
        if (handleEl) handleEl.textContent = '';
        const bioEl = headerEl.querySelector('[data-peer-bio]');
        if (bioEl) {
            bioEl.textContent = '';
            bioEl.classList.add('hidden');
        }
        const bioSep = headerEl.querySelector('[data-peer-bio-sep]');
        if (bioSep) bioSep.classList.add('hidden');
        for (const selector of ['[data-peer-pinned-badge]', '[data-peer-muted-badge]', '[data-peer-blocked-badge]']) {
            const badge = headerEl.querySelector(selector);
            if (badge) badge.classList.add('hidden');
        }
        const presenceText = document.getElementById('header-presence-text');
        if (presenceText) presenceText.textContent = '';
        const typingEl = document.getElementById('typing-indicator');
        if (typingEl) {
            typingEl.textContent = '';
            typingEl.classList.add('hidden');
        }
    }

    const inputEl = document.getElementById('message-content-input');
    if (inputEl) {
        inputEl.value = '';
        inputEl.disabled = true;
    }
    const sendBtn = document.getElementById('send-message-btn');
    if (sendBtn) sendBtn.disabled = true;

    const replyBanner = document.getElementById('composer-reply-banner');
    if (replyBanner) replyBanner.classList.add('hidden');
    const replySender = document.getElementById('reply-banner-sender');
    if (replySender) replySender.textContent = '';
    const replyPreview = document.getElementById('reply-banner-preview');
    if (replyPreview) replyPreview.textContent = '';

    for (const id of ['chat-policy-notice', 'chat-action-error', 'edit-message-modal', 'delete-message-modal', 'reaction-picker-popover']) {
        const el = document.getElementById(id);
        if (el) {
            el.classList.add('hidden');
            if (id === 'chat-policy-notice' || id === 'chat-action-error') {
                el.textContent = '';
            }
        }
    }

    const editInput = document.getElementById('edit-message-input');
    if (editInput) editInput.value = '';

    const sidebarList = document.getElementById('conversationList');
    if (sidebarList) {
        clearElementChildren(sidebarList);
    }

    const announcerEl = document.getElementById('chat-live-announcer');
    if (announcerEl) announcerEl.textContent = '';

    if (typeof document.title === 'string') {
        document.title = 'A♡S Messenger';
    }
}
