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
export function updateSidebarLastMessage(convId, previewText) {
    if (!convId || typeof document === 'undefined') return;
    const normalizedId = typeof convId === 'object' && convId.value ? convId.value : String(convId);
    const item = document.querySelector(`.conversation-item[data-conversation-id="${normalizedId}"]`) ||
                 document.querySelector(`.conversation-item[href*="${normalizedId}"]`);
    if (item) {
        const previewEl = item.querySelector('.conversation-last-message') ||
                          item.querySelector('.text-\\[11px\\].truncate');
        if (previewEl) {
            previewEl.textContent = previewText || 'No messages yet';
        }
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
        row.className = 'flex flex-wrap justify-end items-center message-bubble-row group relative';
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
        contentDiv.className = 'message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words';
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

        row.appendChild(actionsBar);
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
        contentHtml = `<div class="message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words">${escapeHtml(message.content)}</div>`;
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
