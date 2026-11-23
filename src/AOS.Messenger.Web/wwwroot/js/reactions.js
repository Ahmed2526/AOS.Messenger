/**
 * AOS Messenger - Reactions Module
 *
 * Implements emoji reaction handling, allowlisted emoji set, reaction pills rendering,
 * and user reaction toggling.
 *
 * Invariants:
 * - Strict server allowlist of 8 emojis.
 * - Pure DOM construction with document.createElement and .textContent (zero innerHTML).
 * - CSRF token verification via RequestVerificationToken header.
 */

import {
    toggleReaction as apiToggleReaction,
    removeReaction as apiRemoveReaction
} from './api-client.js';

export const ALLOWED_EMOJIS = ['👍', '❤️', '😂', '😮', '😢', '🙏', '🔥', '🎉'];

let actionErrorTimer = null;

function showActionError(message) {
    if (typeof document === 'undefined' || !message) return;
    const el = document.getElementById('chat-action-error');
    if (!el) return;
    el.textContent = message;
    el.classList.remove('hidden');
    if (actionErrorTimer) {
        clearTimeout(actionErrorTimer);
    }
    actionErrorTimer = setTimeout(() => {
        el.classList.add('hidden');
    }, 4000);
}

function hideActionError() {
    if (actionErrorTimer) {
        clearTimeout(actionErrorTimer);
        actionErrorTimer = null;
    }
    if (typeof document === 'undefined') return;
    const el = document.getElementById('chat-action-error');
    if (el) {
        el.textContent = '';
        el.classList.add('hidden');
    }
}

/**
 * Toggles a reaction for a given message.
 * @param {string} conversationId
 * @param {string|number} messageId
 * @param {string} emoji
 * @returns {Promise<object|null>}
 */
export async function toggleReaction(conversationId, messageId, emoji) {
    if (!conversationId || !messageId || !emoji) return null;

    try {
        return await apiToggleReaction(conversationId, messageId, emoji);
    } catch (err) {
        console.warn('[Reactions] Failed to toggle reaction:', err?.status || err);
        if (err?.code !== 'UNAUTHORIZED') {
            showActionError(err?.message || 'Failed to update reaction.');
        }
        return null;
    }
}

/**
 * Removes a reaction for a given message.
 * @param {string} conversationId
 * @param {string|number} messageId
 * @returns {Promise<boolean>}
 */
export async function removeReaction(conversationId, messageId) {
    if (!conversationId || !messageId) return false;

    try {
        await apiRemoveReaction(conversationId, messageId);
        return true;
    } catch (err) {
        console.warn('[Reactions] Error removing reaction:', err?.status || err);
        return false;
    }
}

/**
 * Converts server ReactionGroupDto[] ({ emoji, userIds }) into ReactionSummaryDto[]
 * for a given currentUserId.
 * @param {Array<{ emoji: string, userIds: Array<string> }>} groups
 * @param {string|null} currentUserId
 * @returns {Array<{ emoji: string, count: number, reactedByCurrentUser: boolean }>}
 */
export function mapReactionGroupsToSummaries(groups, currentUserId) {
    if (!Array.isArray(groups) || groups.length === 0) {
        return [];
    }

    const normUserId = currentUserId ? String(currentUserId).toLowerCase() : null;
    const summaries = [];

    for (const g of groups) {
        if (!g) continue;
        const emoji = g.emoji || g.Emoji;
        const userIds = g.userIds || g.UserIds || [];
        if (!emoji || !Array.isArray(userIds) || userIds.length === 0) continue;

        const reactedByCurrentUser = normUserId
            ? userIds.some(u => String(u).toLowerCase() === normUserId)
            : false;

        summaries.push({
            emoji,
            count: userIds.length,
            reactedByCurrentUser
        });
    }

    return summaries;
}

/**
 * Renders reaction pills inside the given container element.
 * @param {HTMLElement} container
 * @param {Array<{ emoji: string, count: number, reactedByCurrentUser: boolean }>} reactions
 * @param {string|number} messageId
 * @param {string} conversationId
 * @param {boolean|null} [isOutgoing]
 */
export function renderReactionPills(container, reactions, messageId, conversationId, isOutgoing = null) {
    if (!container) return;

    // Clear existing pills cleanly
    while (container.firstChild) {
        container.removeChild(container.firstChild);
    }

    if (!reactions || reactions.length === 0) {
        container.classList.add('hidden');
        return;
    }

    container.classList.remove('hidden');

    if (isOutgoing === null) {
        const row = typeof container.closest === 'function' ? container.closest('.message-bubble-row') : null;
        isOutgoing = row ? row.getAttribute('data-is-outgoing') === 'true' : false;
    }

    for (const r of reactions) {
        if (!r.count || r.count <= 0) continue;

        const pill = document.createElement('button');
        pill.type = 'button';
        pill.setAttribute('data-emoji', r.emoji);
        pill.setAttribute('data-message-id', String(messageId));

        const baseClass = 'reaction-pill inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs border transition-all cursor-pointer select-none';
        let stateClass;
        let countClass;

        if (isOutgoing) {
            stateClass = r.reactedByCurrentUser
                ? 'border-white/80 bg-white/20 text-white font-bold'
                : 'border-white/30 bg-black/10 text-white/90 hover:bg-black/20';
            countClass = 'text-[10px] font-semibold text-white/90';
        } else {
            stateClass = r.reactedByCurrentUser
                ? 'border-[#B3929D] bg-[#B3929D]/20 text-[#735761] dark:text-[#E1BDC9] font-bold shadow-2xs'
                : 'border-[#E3DCD7] dark:border-[#514D48] bg-white dark:bg-[#3A3835] text-[#2B2725] dark:text-[#F0E9E2] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E]';
            countClass = 'text-[10px] font-semibold text-[#6E6560] dark:text-[#C9C0B9]';
        }

        pill.className = `${baseClass} ${stateClass}`;
        pill.title = r.reactedByCurrentUser ? `Remove ${r.emoji} reaction` : `React with ${r.emoji}`;

        const emojiSpan = document.createElement('span');
        emojiSpan.className = 'text-xs select-none';
        emojiSpan.textContent = r.emoji;
        pill.appendChild(emojiSpan);

        const countSpan = document.createElement('span');
        countSpan.className = countClass;
        countSpan.textContent = String(r.count);
        pill.appendChild(countSpan);

        container.appendChild(pill);
    }
}

/**
 * Updates reaction pills in the DOM for a specific message using server ReactionGroupDto[].
 * @param {string|number} messageId
 * @param {Array<object>} reactionGroups
 * @param {string} conversationId
 * @param {string|null} currentUserId
 */
function applyReactionGroupsToRow(messageId, reactionGroups, conversationId, currentUserId) {
    if (!messageId || !Array.isArray(reactionGroups)) return;
    const row = document.getElementById(`msg-${messageId}`);
    if (!row) return;

    let pillsContainer = row.querySelector('.reaction-pills-container');
    if (!pillsContainer) {
        pillsContainer = document.createElement('div');
        pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
        const bubble = row.querySelector('.message-bubble') || row.querySelector('.max-w-\\[75\\%\\]');
        if (bubble) bubble.appendChild(pillsContainer);
    }

    const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
    const summaries = mapReactionGroupsToSummaries(reactionGroups, currentUserId);
    renderReactionPills(pillsContainer, summaries, messageId, conversationId, isOutgoing);
}

/**
 * Creates and attaches the floating emoji reaction picker and delegates pill clicks.
 * @param {string} conversationId
 * @param {string|null} [currentUserId]
 */
export function setupEmojiPicker(convIdOrGetter, userIdOrGetter = null, options = {}) {
    if (typeof document === 'undefined') {
        return { resetForSwitch: () => {}, closePopover: () => {}, destroy: () => {} };
    }

    const popover = document.getElementById('reaction-picker-popover');
    if (!popover || !convIdOrGetter) {
        return { resetForSwitch: () => {}, closePopover: () => {}, destroy: () => {} };
    }

    const resolveConvId = typeof convIdOrGetter === 'function' ? convIdOrGetter : () => convIdOrGetter;
    const resolveUserId = typeof userIdOrGetter === 'function'
        ? userIdOrGetter
        : () => (userIdOrGetter || document.getElementById('messages-container')?.getAttribute('data-current-user-id') || null);
    const getSwitchEpoch = typeof options?.getSwitchEpoch === 'function' ? options.getSwitchEpoch : () => 0;

    let currentTargetMessageId = null;
    const inFlightMessages = new Set();

    function closePopover() {
        popover.classList.add('hidden');
        popover.removeAttribute('data-target-message-id');
        currentTargetMessageId = null;
    }

    function resetForSwitch() {
        closePopover();
        inFlightMessages.clear();
        hideActionError();
    }

    function positionPopover(anchorEl) {
        if (!anchorEl) return;
        popover.classList.remove('hidden');

        const margin = 8;
        const anchorRect = anchorEl.getBoundingClientRect();
        const popRect = popover.getBoundingClientRect();
        const vw = window.innerWidth || document.documentElement.clientWidth || 375;
        const vh = window.innerHeight || document.documentElement.clientHeight || 667;

        const maxLeft = Math.max(margin, vw - popRect.width - margin);
        let left = anchorRect.left + (anchorRect.width / 2) - (popRect.width / 2);
        left = Math.max(margin, Math.min(left, maxLeft));

        let top = anchorRect.top - popRect.height - 6;
        if (top < margin) {
            top = anchorRect.bottom + 6;
        }
        const maxTop = Math.max(margin, vh - popRect.height - margin);
        top = Math.max(margin, Math.min(top, maxTop));

        popover.style.left = `${Math.round(left)}px`;
        popover.style.top = `${Math.round(top)}px`;
    }

    async function handleToggle(msgId, emoji) {
        const convId = resolveConvId();
        const resolvedUserId = resolveUserId();
        const epoch = getSwitchEpoch();
        const key = String(msgId);
        if (!convId || !emoji || !key || inFlightMessages.has(key)) return;
        inFlightMessages.add(key);
        try {
            const result = await toggleReaction(convId, msgId, emoji);
            if (epoch !== getSwitchEpoch()) return;
            if (result && Array.isArray(result.reactions)) {
                applyReactionGroupsToRow(msgId, result.reactions, convId, resolvedUserId);
            }
        } finally {
            inFlightMessages.delete(key);
        }
    }

    const onOutsideClick = (e) => {
        if (!popover.classList.contains('hidden') &&
            !popover.contains(e.target) &&
            !(e.target && typeof e.target.closest === 'function' && e.target.closest('[data-action="react"]'))) {
            closePopover();
        }
    };

    const onKeyDown = (e) => {
        if (e.key === 'Escape' && !popover.classList.contains('hidden')) {
            closePopover();
        }
    };

    const onStreamScroll = () => {
        if (!popover.classList.contains('hidden')) {
            closePopover();
        }
    };

    const onWindowResize = () => {
        if (!popover.classList.contains('hidden')) {
            closePopover();
        }
    };

    const onDelegateClick = async (e) => {
        if (!e.target || typeof e.target.closest !== 'function') return;

        const pill = e.target.closest('.reaction-pill');
        if (pill) {
            e.preventDefault();
            e.stopPropagation();
            const emoji = pill.getAttribute('data-emoji');
            const row = pill.closest('.message-bubble-row');
            if (row && row.getAttribute('data-deleted') === 'true') return;
            const msgId = pill.getAttribute('data-message-id') || row?.getAttribute('data-message-id');
            if (emoji && msgId) {
                await handleToggle(msgId, emoji);
            }
            return;
        }

        const reactBtn = e.target.closest('[data-action="react"]');
        if (!reactBtn) return;

        e.preventDefault();
        e.stopPropagation();

        const row = reactBtn.closest('.message-bubble-row');
        if (!row || row.getAttribute('data-deleted') === 'true') return;

        const messageId = row.getAttribute('data-message-id');
        if (!messageId) return;

        // If clicking same message's react button, toggle off
        if (currentTargetMessageId === messageId && !popover.classList.contains('hidden')) {
            closePopover();
            return;
        }

        currentTargetMessageId = messageId;
        popover.setAttribute('data-target-message-id', messageId);
        positionPopover(reactBtn);
    };

    document.addEventListener('click', onOutsideClick);
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('click', onDelegateClick);

    const streamEl = document.getElementById('message-stream');
    if (streamEl) {
        streamEl.addEventListener('scroll', onStreamScroll, { passive: true });
    }

    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('resize', onWindowResize);
    }

    const emojiBtnHandlers = [];
    popover.querySelectorAll('[data-emoji-btn]').forEach(btn => {
        const handler = async (e) => {
            e.preventDefault();
            const emoji = btn.getAttribute('data-emoji-btn');
            if (emoji && currentTargetMessageId) {
                const msgId = currentTargetMessageId;
                closePopover();
                await handleToggle(msgId, emoji);
            }
        };
        btn.addEventListener('click', handler);
        emojiBtnHandlers.push({ btn, handler });
    });

    return {
        resetForSwitch,
        closePopover,
        destroy: () => {
            resetForSwitch();
            document.removeEventListener('click', onOutsideClick);
            document.removeEventListener('keydown', onKeyDown);
            document.removeEventListener('click', onDelegateClick);
            if (streamEl) {
                streamEl.removeEventListener('scroll', onStreamScroll);
            }
            if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
                window.removeEventListener('resize', onWindowResize);
            }
            for (const { btn, handler } of emojiBtnHandlers) {
                btn.removeEventListener('click', handler);
            }
        }
    };
}

if (typeof window !== 'undefined') {
    window.Reactions = {
        ALLOWED_EMOJIS,
        toggleReaction,
        removeReaction,
        mapReactionGroupsToSummaries,
        renderReactionPills,
        setupEmojiPicker
    };
}
