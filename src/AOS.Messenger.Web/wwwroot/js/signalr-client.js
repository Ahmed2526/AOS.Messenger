/**
 * AOS Messenger - Unified Single-WebSocket SignalR Client
 *
 * Maintains strictly one /hubs/chat HubConnection per tab across zero-refresh
 * conversation switches, syncs server focus via JoinConversation/LeaveConversation,
 * buffers realtime events targeting pending switches for deterministic (CreatedAtUtc, Id)
 * replay on commit, reconciles missed messages on reconnect (RT-005), and wipes all
 * sensitive state on true session revocation.
 */

import { getConversationBundle } from './api-client.js';
import {
    compareMessages,
    getMessageId,
    getUserId,
    isMessageRendered,
    isLatestRenderedMessage,
    insertMessageOrdered,
    scrollToBottom,
    applyMessageEditedToRow,
    applyMessageDeletedForEveryone,
    applyMessageDeletedForMe
} from './message-renderer.js';
import { mapReactionGroupsToSummaries, renderReactionPills } from './reactions.js';
import { updateMessageCheckmarks, markDelivered, markRead, checkAndMarkIncoming } from './receipts.js';
import {
    findSidebarItem,
    setSidebarUnreadCount,
    incrementSidebarUnreadCount,
    updateSidebarLastMessage,
    syncSidebarConversations
} from './conversation-list.js';

function normalizeId(val) {
    if (val === null || val === undefined) return '';
    if (typeof val === 'object' && val.value !== undefined) {
        return String(val.value);
    }
    return String(val);
}

function sameId(a, b) {
    if (!a || !b) return false;
    return normalizeId(a).trim().toLowerCase() === normalizeId(b).trim().toLowerCase();
}

function parseBigIntSafe(val) {
    if (val === null || val === undefined || val === '') return 0n;
    try {
        return BigInt(String(val).trim());
    } catch {
        return 0n;
    }
}

function isDocumentVisibleAndFocused() {
    if (typeof document === 'undefined') return true;
    const visible = document.visibilityState === undefined || document.visibilityState === 'visible';
    const focused = typeof document.hasFocus === 'function' ? document.hasFocus() : true;
    return visible && focused;
}

export const MAX_SEEN_INCOMING_IDS = 500;

/**
 * Classifies whether a SignalR negotiate/invoke error indicates a revoked or expired session.
 * Never triggers logout on hub throttling ("Rate limit exceeded") or conversation-level 403/NotFound errors.
 * @param {any} err
 * @returns {boolean}
 */
export function isSessionRevokedSignalRError(err) {
    if (!err) return false;
    if (err.statusCode === 403) return false;

    const lower = String(err.message || err).toLowerCase();
    if (lower.includes('rate limit exceeded') ||
        lower.includes('not a participant') ||
        lower.includes('conversation not found') ||
        lower.includes('blocked')) {
        return false;
    }

    if (err.statusCode === 401) return true;

    return /\b401\b/.test(lower) ||
        lower.includes('revoked or expired') ||
        lower.includes('session has been revoked') ||
        lower.includes('session expired') ||
        lower.includes('user is not authenticated') ||
        lower.includes('unauthorized') ||
        lower.includes('/account/login');
}

/**
 * Creates the single persistent SignalR client for the unified chat shell.
 * @param {{
 *   state: object,
 *   onSessionExpired: Function,
 *   onIdentityChanged?: Function|null,
 *   onConversationReconciled?: Function|null,
 *   onTypingIndicator?: Function|null,
 *   onPresenceChanged?: Function|null,
 *   onRefreshPresence?: Function|null,
 *   signalRLib?: object|null
 * }} deps
 * @returns {object}
 */
export function createSignalRClient({
    state,
    onSessionExpired,
    onIdentityChanged = null,
    onConversationReconciled = null,
    onTypingIndicator = null,
    onPresenceChanged = null,
    onRefreshPresence = null,
    signalRLib = null
}) {
    const lib = signalRLib || (typeof signalR !== 'undefined' ? signalR : null);
    if (!lib || typeof lib.HubConnectionBuilder !== 'function') {
        console.error('[SignalR] signalR client library is not loaded.');
        return {
            connection: null,
            start: async () => false,
            stop: async () => {},
            notifyActiveConversation: async () => {},
            leaveActiveConversation: async () => {},
            replayBufferedEvents: () => {},
            reconcileActiveConversation: async () => {},
            destroy: () => {}
        };
    }

    const connection = new lib.HubConnectionBuilder()
        .withUrl('/hubs/chat')
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

    let isTerminated = false;
    let joinedConversationId = null;
    let requestedConversationId = null;
    const seenIncomingIds = new Set();

    function trackSeenIncomingId(msgId) {
        const key = String(msgId);
        if (seenIncomingIds.has(key)) {
            seenIncomingIds.delete(key);
        }
        while (seenIncomingIds.size >= MAX_SEEN_INCOMING_IDS) {
            const oldestKey = seenIncomingIds.values().next().value;
            if (oldestKey === undefined) break;
            seenIncomingIds.delete(oldestKey);
        }
        seenIncomingIds.add(key);
    }

    function isConnected() {
        const connectedConst = lib.HubConnectionState && lib.HubConnectionState.Connected
            ? lib.HubConnectionState.Connected
            : 'Connected';
        return connection.state === 'Connected' || connection.state === connectedConst;
    }

    function triggerSessionExpired() {
        if (isTerminated) return;
        isTerminated = true;
        requestedConversationId = null;
        joinedConversationId = null;
        seenIncomingIds.clear();
        try {
            if (connection && typeof connection.stop === 'function') {
                Promise.resolve(connection.stop()).catch(() => {});
            }
        } catch {}
        if (typeof onSessionExpired === 'function') {
            onSessionExpired();
        }
    }

    function setStatus(connState, message) {
        if (isTerminated || typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
        const bannerEl = document.getElementById('connection-banner');
        const bannerTextEl = document.getElementById('connection-banner-text');
        const statusDotEl = document.getElementById('connectionStatusDot');
        const statusTextEl = document.getElementById('connectionStatusText');

        if (connState === 'reconnecting') {
            if (bannerEl && bannerEl.classList) {
                bannerEl.classList.remove('hidden');
                if (bannerTextEl) bannerTextEl.textContent = message || 'Reconnecting to chat...';
            }
            if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-amber-500 animate-pulse';
            if (statusTextEl) statusTextEl.textContent = 'Reconnecting...';
        } else if (connState === 'connected') {
            if (bannerEl && bannerEl.classList) bannerEl.classList.add('hidden');
            if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-[#6F9473]';
            if (statusTextEl) statusTextEl.textContent = 'Connected';
        } else if (connState === 'disconnected') {
            if (bannerEl && bannerEl.classList) {
                bannerEl.classList.remove('hidden');
                if (bannerTextEl) bannerTextEl.textContent = message || 'Disconnected from chat. Reconnecting...';
            }
            if (statusDotEl) statusDotEl.className = 'w-2 h-2 rounded-full bg-red-400';
            if (statusTextEl) statusTextEl.textContent = 'Disconnected';
        }
    }

    /**
     * Synchronizes active focus on ChatHub without disconnecting the WebSocket.
     * Skips redundant LeaveConversation on A -> B switches; JoinConversation updates focus in place.
     * Tracks requestedConversationId synchronously before awaiting invoke so rapid A -> B -> A switches
     * while JoinConversation(B) is in flight never skip re-sending JoinConversation(A).
     * @param {string|null} nextId
     */
    async function notifyActiveConversation(nextId) {
        if (isTerminated) return;
        if (!nextId) {
            await leaveActiveConversation();
            return;
        }

        const target = normalizeId(nextId).trim();
        if (!target || !isConnected()) return;
        if (requestedConversationId && sameId(requestedConversationId, target)) return;

        requestedConversationId = target;
        try {
            await connection.invoke('JoinConversation', target);
            if (!isTerminated && sameId(requestedConversationId, target) && sameId(state.activeConversationId, target)) {
                joinedConversationId = target;
            }
        } catch (err) {
            if (sameId(requestedConversationId, target)) {
                requestedConversationId = null;
            }
            if (isSessionRevokedSignalRError(err)) {
                triggerSessionExpired();
            } else {
                console.warn('[SignalR] JoinConversation failed:', err?.message || err);
            }
        }
    }

    /**
     * Clears active conversation focus on the server (used on transition to empty /chat or tab unload).
     * @param {string|null} [oldId]
     */
    async function leaveActiveConversation(oldId = null) {
        const target = normalizeId(oldId || requestedConversationId || joinedConversationId).trim();
        requestedConversationId = null;
        joinedConversationId = null;
        if (!target || isTerminated || !isConnected()) return;

        try {
            await connection.invoke('LeaveConversation', target);
        } catch (err) {
            if (isSessionRevokedSignalRError(err)) {
                triggerSessionExpired();
            }
        }
    }

    function getNewestRenderedMessageInfo(containerEl) {
        if (!containerEl || typeof containerEl.querySelectorAll !== 'function') return null;
        const rows = containerEl.querySelectorAll('.message-bubble-row');
        if (!rows || rows.length === 0) return null;

        let maxTime = -Infinity;
        let maxId = 0n;
        let found = false;

        for (let i = 0; i < rows.length; i++) {
            const row = rows[i];
            const timeAttr = row.getAttribute('data-created-at');
            const idAttr = row.getAttribute('data-message-id');
            if (timeAttr) {
                const t = Date.parse(timeAttr);
                if (!Number.isNaN(t)) {
                    found = true;
                    const idVal = parseBigIntSafe(idAttr);
                    if (t > maxTime || (t === maxTime && idVal > maxId)) {
                        maxTime = t;
                        maxId = idVal;
                    }
                }
            }
        }

        return found ? { time: maxTime, id: maxId } : null;
    }

    function normalizeReactionSummaries(rawReactions, currentUserId) {
        if (!Array.isArray(rawReactions) || rawReactions.length === 0) return [];
        if (rawReactions.some(r => r && (Array.isArray(r.userIds) || Array.isArray(r.UserIds)))) {
            return mapReactionGroupsToSummaries(rawReactions, currentUserId);
        }
        return rawReactions
            .filter(r => r && (r.emoji || r.Emoji) && ((r.count ?? r.Count ?? 0) > 0))
            .map(r => ({
                emoji: r.emoji ?? r.Emoji,
                count: Number(r.count ?? r.Count ?? 0),
                reactedByCurrentUser: Boolean(r.reactedByCurrentUser ?? r.ReactedByCurrentUser)
            }));
    }

    /**
     * Reconciles active conversation after reconnect using the latest conversation bundle (RT-005, W3).
     * Reconciles conversation metadata/policy, receipt watermarks, tombstones, edits, reactions on
     * already-rendered rows, and inserts any missing newer messages while skipping ancient history.
     * @param {string} convId
     * @param {number} [expectedEpoch]
     */
    async function reconcileActiveConversation(convId, expectedEpoch = state.switchEpoch) {
        if (isTerminated || !convId) return;

        try {
            const bundle = await getConversationBundle(convId, { pageSize: 30 });
            if (isTerminated || expectedEpoch !== state.switchEpoch || !sameId(state.activeConversationId, convId)) {
                return;
            }

            const viewerUserId = normalizeId(bundle?.viewerUserId ?? bundle?.ViewerUserId).trim();
            if (viewerUserId && state.currentUserId && !sameId(viewerUserId, state.currentUserId)) {
                if (typeof onIdentityChanged === 'function') {
                    onIdentityChanged();
                } else {
                    triggerSessionExpired();
                }
                return;
            }

            const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
                ? document.getElementById('messages-container')
                : null;

            const conversation = bundle?.conversation ?? bundle?.Conversation ?? null;
            if (conversation && typeof conversation === 'object') {
                state.activeMeta = { ...(state.activeMeta || {}), ...conversation };

                const peerDelivered = conversation.peerLastDeliveredMessageId ?? conversation.PeerLastDeliveredMessageId;
                const peerRead = conversation.peerLastReadMessageId ?? conversation.PeerLastReadMessageId;
                const containerForReceipts = containerEl || (typeof document !== 'undefined' ? document : null);

                if (peerDelivered !== undefined && peerDelivered !== null) {
                    applyWatermarkAdvance(peerDelivered, 'delivered');
                    if (containerForReceipts) {
                        updateMessageCheckmarks(containerForReceipts, peerDelivered, 'delivered');
                    }
                }
                if (peerRead !== undefined && peerRead !== null) {
                    applyWatermarkAdvance(peerRead, 'read');
                    if (containerForReceipts) {
                        updateMessageCheckmarks(containerForReceipts, peerRead, 'read');
                    }
                }

                if (typeof onConversationReconciled === 'function') {
                    onConversationReconciled(state.activeMeta);
                }
            }

            const messages = bundle?.messages ?? bundle?.Messages ?? [];
            if (!containerEl || !Array.isArray(messages) || messages.length === 0) return;

            const newestRendered = getNewestRenderedMessageInfo(containerEl);
            const missingNewer = [];

            for (const msg of messages) {
                const msgId = getMessageId(msg);
                if (!msgId) continue;

                if (isMessageRendered(msgId)) {
                    const row = typeof document !== 'undefined' && typeof document.getElementById === 'function'
                        ? (document.getElementById(`msg-${msgId}`) || containerEl.querySelector(`[data-message-id="${msgId}"]`))
                        : null;
                    const isDeleted = Boolean(msg.isDeletedForEveryone ?? msg.IsDeletedForEveryone);
                    const newContent = msg.content !== undefined ? msg.content : (msg.Content ?? '');
                    const editedAtUtc = msg.editedAtUtc !== undefined ? msg.editedAtUtc : (msg.EditedAtUtc ?? null);
                    const rawReactions = msg.reactions !== undefined ? msg.reactions : msg.Reactions;

                    if (isDeleted) {
                        if (row && row.getAttribute('data-deleted') !== 'true') {
                            applyMessageDeletedForEveryone(row, msgId);
                        }
                        if (isLatestRenderedMessage(containerEl, msgId)) {
                            updateSidebarLastMessage(convId, 'This message was deleted', msgId, true);
                        }
                    } else {
                        if (row) {
                            const contentEl = row.querySelector('.message-text-content');
                            const editedLabel = row.querySelector('.edited-label');
                            const wasEditedHidden = editedLabel ? editedLabel.classList.contains('hidden') : true;
                            if ((contentEl && contentEl.textContent !== (newContent || '')) || (Boolean(editedAtUtc) && wasEditedHidden)) {
                                applyMessageEditedToRow(msgId, newContent, editedAtUtc);
                            }
                            if (Array.isArray(rawReactions)) {
                                let pillsContainer = row.querySelector('.reaction-pills-container');
                                if (!pillsContainer && typeof document.createElement === 'function') {
                                    pillsContainer = document.createElement('div');
                                    pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
                                    const bubble = row.querySelector('.message-bubble') || row.querySelector('.max-w-\\[75\\%\\]');
                                    if (bubble) bubble.appendChild(pillsContainer);
                                }
                                if (pillsContainer) {
                                    const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
                                    const summaries = normalizeReactionSummaries(rawReactions, state.currentUserId);
                                    renderReactionPills(pillsContainer, summaries, msgId, convId, isOutgoing);
                                }
                            }
                        }
                        if (isLatestRenderedMessage(containerEl, msgId)) {
                            updateSidebarLastMessage(convId, newContent || '', msgId, true);
                        }
                    }

                    if (state.messagesById && typeof state.messagesById.set === 'function') {
                        state.messagesById.set(String(msgId), msg);
                    }
                    continue;
                }

                const rawCreatedAt = msg.createdAtUtc ?? msg.CreatedAtUtc;
                if (newestRendered && rawCreatedAt) {
                    const msgTime = Date.parse(rawCreatedAt);
                    const msgIdVal = parseBigIntSafe(msgId);
                    if (!Number.isNaN(msgTime) &&
                        (msgTime < newestRendered.time || (msgTime === newestRendered.time && msgIdVal <= newestRendered.id))) {
                        continue;
                    }
                }

                missingNewer.push(msg);
            }

            if (missingNewer.length === 0) return;

            missingNewer.sort(compareMessages);

            let lastInserted = null;
            for (const msg of missingNewer) {
                const msgId = getMessageId(msg);
                if (!msgId || isMessageRendered(msgId)) continue;
                const el = insertMessageOrdered(containerEl, msg, state.currentUserId, state.activeMeta);
                if (el) {
                    if (state.messagesById && typeof state.messagesById.set === 'function') {
                        state.messagesById.set(msgId, msg);
                    }
                    lastInserted = msg;
                }
            }

            if (lastInserted) {
                scrollToBottom(true);
                const lastPreview = (lastInserted.isDeletedForEveryone ?? lastInserted.IsDeletedForEveryone)
                    ? 'This message was deleted'
                    : (lastInserted.content ?? lastInserted.Content ?? '');
                updateSidebarLastMessage(convId, lastPreview, getMessageId(lastInserted), false, false, true);
            }
        } catch (err) {
            if (isSessionRevokedSignalRError(err)) {
                triggerSessionExpired();
            } else if (err?.code !== 'UNAUTHORIZED') {
                console.warn('[SignalR] State reconciliation error:', err?.status || err);
            }
        }
    }

    function applyWatermarkAdvance(upToId, rType) {
        const nextVal = parseBigIntSafe(upToId);
        if (nextVal <= 0n) return;

        const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('messages-container')
            : null;
        const normalizedType = String(rType || '').toLowerCase();

        if (normalizedType === 'read') {
            if (state.activeMeta) {
                const cur = parseBigIntSafe(state.activeMeta.peerLastReadMessageId ?? state.activeMeta.PeerLastReadMessageId);
                if (nextVal > cur) state.activeMeta.peerLastReadMessageId = String(nextVal);
            }
            if (containerEl) {
                const curAttr = parseBigIntSafe(containerEl.getAttribute('data-peer-last-read-id'));
                if (nextVal > curAttr) containerEl.setAttribute('data-peer-last-read-id', String(nextVal));
            }
        } else if (normalizedType === 'delivered') {
            if (state.activeMeta) {
                const cur = parseBigIntSafe(state.activeMeta.peerLastDeliveredMessageId ?? state.activeMeta.PeerLastDeliveredMessageId);
                if (nextVal > cur) state.activeMeta.peerLastDeliveredMessageId = String(nextVal);
            }
            if (containerEl) {
                const curAttr = parseBigIntSafe(containerEl.getAttribute('data-peer-last-delivered-id'));
                if (nextVal > curAttr) containerEl.setAttribute('data-peer-last-delivered-id', String(nextVal));
            }
        }
    }

    function handleReceiveMessage(message, { isReplay = false } = {}) {
        if (!message || isTerminated) return;
        const msgId = getMessageId(message);
        if (!msgId) return;

        const eventConvId = normalizeId(message.conversationId !== undefined ? message.conversationId : message.ConversationId);
        if (!eventConvId) return;

        const senderId = getUserId(message.senderId);
        const isOutgoing = state.currentUserId
            ? senderId.toLowerCase() === String(state.currentUserId).toLowerCase()
            : !!message.isOutgoing;
        const previewText = message.isDeletedForEveryone ? 'This message was deleted' : (message.content ?? '');

        if (!isReplay && state.bufferRealtimeEvent(eventConvId, { type: 'ReceiveMessage', payload: message })) {
            const foundDuringSwitch = updateSidebarLastMessage(eventConvId, previewText, msgId, false, false, true);
            if (!foundDuringSwitch) {
                syncSidebarConversations(() => state.activeConversationId);
            }
            if (!isOutgoing) {
                markDelivered(eventConvId, msgId);
            }
            return;
        }

        const sidebarFound = updateSidebarLastMessage(eventConvId, previewText, msgId, false, false, true);
        const isActive = sameId(eventConvId, state.activeConversationId);

        if (!isActive) {
            if (!sidebarFound && !isReplay) {
                syncSidebarConversations(() => state.activeConversationId);
            } else if (!isOutgoing && !seenIncomingIds.has(String(msgId))) {
                trackSeenIncomingId(msgId);
                incrementSidebarUnreadCount(eventConvId, 1);
            }
            if (!isOutgoing) {
                markDelivered(eventConvId, msgId);
            }
            return;
        }

        if (!sidebarFound && !isReplay) {
            syncSidebarConversations(() => state.activeConversationId);
        }

        const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('messages-container')
            : null;
        const inserted = containerEl
            ? insertMessageOrdered(containerEl, message, state.currentUserId, state.activeMeta)
            : null;

        if (inserted) {
            if (state.messagesById && typeof state.messagesById.set === 'function') {
                state.messagesById.set(msgId, message);
            }
            scrollToBottom(true);
        }

        if (!isOutgoing) {
            markDelivered(eventConvId, msgId);

            if (isDocumentVisibleAndFocused()) {
                markRead(eventConvId, msgId);
                setSidebarUnreadCount(eventConvId, 0);
            } else if (sidebarFound && !seenIncomingIds.has(String(msgId))) {
                trackSeenIncomingId(msgId);
                incrementSidebarUnreadCount(eventConvId, 1);
            }
        }
    }

    function handleReceiveMessageEdited(message, { isReplay = false } = {}) {
        if (!message || isTerminated) return;
        const msgId = normalizeId(message.messageId !== undefined ? message.messageId : message.MessageId);
        if (!msgId) return;

        const eventConvId = normalizeId(message.conversationId !== undefined ? message.conversationId : message.ConversationId);
        const newContent = message.content !== undefined ? message.content : (message.Content || '');
        const editedAtUtc = message.editedAtUtc !== undefined ? message.editedAtUtc : message.EditedAtUtc;

        if (!isReplay && eventConvId && state.bufferRealtimeEvent(eventConvId, { type: 'ReceiveMessageEdited', payload: message })) {
            updateSidebarLastMessage(eventConvId, newContent, msgId, true);
            return;
        }

        if (!sameId(eventConvId, state.activeConversationId)) {
            if (eventConvId) {
                updateSidebarLastMessage(eventConvId, newContent, msgId, true);
            }
            return;
        }

        applyMessageEditedToRow(msgId, newContent, editedAtUtc);

        if (state.messagesById && state.messagesById.has(String(msgId))) {
            const cached = state.messagesById.get(String(msgId));
            if (cached && typeof cached === 'object') {
                cached.content = newContent;
                if (editedAtUtc) cached.editedAtUtc = editedAtUtc;
            }
        }

        const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('messages-container')
            : null;
        const row = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById(`msg-${msgId}`)
            : null;
        if (!row || isLatestRenderedMessage(containerEl, msgId)) {
            updateSidebarLastMessage(eventConvId, newContent, msgId, true);
        }
    }

    function handleReceiveMessageDeletedForEveryone(message, { isReplay = false } = {}) {
        if (!message || isTerminated) return;
        const msgId = normalizeId(message.messageId !== undefined ? message.messageId : message.MessageId);
        if (!msgId) return;

        const eventConvId = normalizeId(message.conversationId !== undefined ? message.conversationId : message.ConversationId);

        if (!isReplay && eventConvId && state.bufferRealtimeEvent(eventConvId, { type: 'ReceiveMessageDeletedForEveryone', payload: message })) {
            updateSidebarLastMessage(eventConvId, 'This message was deleted', msgId, true);
            return;
        }

        if (!sameId(eventConvId, state.activeConversationId)) {
            if (eventConvId) {
                updateSidebarLastMessage(eventConvId, 'This message was deleted', msgId, true);
            }
            return;
        }

        const row = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById(`msg-${msgId}`)
            : null;
        applyMessageDeletedForEveryone(row, msgId);

        if (state.messagesById && state.messagesById.has(String(msgId))) {
            const cached = state.messagesById.get(String(msgId));
            if (cached && typeof cached === 'object') {
                cached.isDeletedForEveryone = true;
            }
        }

        const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('messages-container')
            : null;
        if (!row || isLatestRenderedMessage(containerEl, msgId)) {
            updateSidebarLastMessage(eventConvId, 'This message was deleted', msgId, true);
        }
    }

    function handleReceiveMessageDeletedForMe(message, { isReplay = false } = {}) {
        if (!message || isTerminated) return;
        const msgId = normalizeId(message.messageId !== undefined ? message.messageId : message.MessageId);
        if (!msgId) return;

        const eventConvId = normalizeId(message.conversationId !== undefined ? message.conversationId : message.ConversationId);
        const newLastId = message.newLastMessageId !== undefined ? message.newLastMessageId : message.NewLastMessageId;
        const newLastPreview = message.newLastMessagePreview !== undefined ? message.newLastMessagePreview : message.NewLastMessagePreview;

        if (!isReplay && eventConvId && state.bufferRealtimeEvent(eventConvId, { type: 'ReceiveMessageDeletedForMe', payload: message })) {
            applyMessageDeletedForMe(msgId, eventConvId, newLastId, newLastPreview, false);
            return;
        }

        const isActive = sameId(eventConvId, state.activeConversationId);
        applyMessageDeletedForMe(msgId, eventConvId, newLastId, newLastPreview, isActive);

        if (isActive && state.messagesById) {
            state.messagesById.delete(String(msgId));
        }

        const sidebarItem = findSidebarItem(eventConvId);
        const currentUnread = sidebarItem ? (parseInt(sidebarItem.getAttribute('data-unread-count') || '0', 10) || 0) : 0;
        if (currentUnread > 0 && !isReplay) {
            syncSidebarConversations(() => state.activeConversationId);
        }
    }

    function handleReceiveReactionUpdated(reaction, { isReplay = false } = {}) {
        if (!reaction || isTerminated) return;
        const msgId = normalizeId(reaction.messageId !== undefined ? reaction.messageId : reaction.MessageId);
        if (!msgId) return;

        const eventConvId = normalizeId(reaction.conversationId !== undefined ? reaction.conversationId : reaction.ConversationId);

        if (!isReplay && eventConvId && state.bufferRealtimeEvent(eventConvId, { type: 'ReceiveReactionUpdated', payload: reaction })) {
            return;
        }

        if (!sameId(eventConvId, state.activeConversationId)) return;

        const groups = reaction.reactions !== undefined ? reaction.reactions : reaction.Reactions;
        if (!Array.isArray(groups)) return;

        const row = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById(`msg-${msgId}`)
            : null;
        if (row) {
            let pillsContainer = row.querySelector('.reaction-pills-container');
            if (!pillsContainer && typeof document.createElement === 'function') {
                pillsContainer = document.createElement('div');
                pillsContainer.className = 'reaction-pills-container flex flex-wrap gap-1 mt-1';
                const bubble = row.querySelector('.message-bubble') || row.querySelector('.max-w-\\[75\\%\\]');
                if (bubble) bubble.appendChild(pillsContainer);
            }
            if (pillsContainer) {
                const isOutgoing = row.getAttribute('data-is-outgoing') === 'true';
                const summaries = mapReactionGroupsToSummaries(groups, state.currentUserId);
                renderReactionPills(pillsContainer, summaries, msgId, eventConvId, isOutgoing);
            }
        }
    }

    function handleReceiveReceiptUpdated(receipt, { isReplay = false } = {}) {
        if (!receipt || isTerminated) return;
        const eventConvId = normalizeId(receipt.conversationId !== undefined ? receipt.conversationId : receipt.ConversationId);

        if (!isReplay && eventConvId && state.bufferRealtimeEvent(eventConvId, { type: 'ReceiveReceiptUpdated', payload: receipt })) {
            return;
        }

        if (!sameId(eventConvId, state.activeConversationId)) return;

        const actorUserId = normalizeId(receipt.userId !== undefined ? receipt.userId : receipt.UserId);
        if (state.currentUserId && actorUserId && actorUserId.toLowerCase() === String(state.currentUserId).toLowerCase()) {
            return;
        }

        const upToId = receipt.upToMessageId !== undefined ? receipt.upToMessageId : receipt.UpToMessageId;
        const rType = receipt.receiptType || receipt.ReceiptType || 'delivered';
        const containerEl = (typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('messages-container')
            : null) || (typeof document !== 'undefined' ? document : null);

        applyWatermarkAdvance(upToId, rType);
        if (containerEl) {
            updateMessageCheckmarks(containerEl, upToId, rType);
        }
    }

    /**
     * Replays realtime events buffered during an active conversation fetch in deterministic order.
     * @param {string} convId
     * @param {Array<{ type: string, payload: object }>} events
     */
    function replayBufferedEvents(convId, events) {
        if (!convId || !Array.isArray(events) || events.length === 0 || isTerminated) return;
        if (!sameId(convId, state.activeConversationId)) return;

        for (const evt of events) {
            if (!evt || !sameId(convId, state.activeConversationId)) break;
            switch (evt.type) {
                case 'ReceiveMessage':
                    handleReceiveMessage(evt.payload, { isReplay: true });
                    break;
                case 'ReceiveMessageEdited':
                    handleReceiveMessageEdited(evt.payload, { isReplay: true });
                    break;
                case 'ReceiveMessageDeletedForEveryone':
                    handleReceiveMessageDeletedForEveryone(evt.payload, { isReplay: true });
                    break;
                case 'ReceiveMessageDeletedForMe':
                    handleReceiveMessageDeletedForMe(evt.payload, { isReplay: true });
                    break;
                case 'ReceiveReactionUpdated':
                    handleReceiveReactionUpdated(evt.payload, { isReplay: true });
                    break;
                case 'ReceiveReceiptUpdated':
                    handleReceiveReceiptUpdated(evt.payload, { isReplay: true });
                    break;
            }
        }
    }

    // Lifecycle registrations (once per tab)
    connection.onreconnecting((error) => {
        if (isSessionRevokedSignalRError(error)) {
            triggerSessionExpired();
            return;
        }
        setStatus('reconnecting', 'Reconnecting to chat...');
    });

    connection.onreconnected(async () => {
        if (isTerminated) return;
        requestedConversationId = null;
        joinedConversationId = null;
        setStatus('connected');

        const activeId = state.activeConversationId;
        const epoch = state.switchEpoch;

        try {
            if (activeId && epoch === state.switchEpoch) {
                const target = normalizeId(activeId).trim();
                requestedConversationId = target;
                await connection.invoke('JoinConversation', target);
                if (isTerminated) return;
                if (epoch === state.switchEpoch && sameId(requestedConversationId, target) && sameId(state.activeConversationId, target)) {
                    joinedConversationId = target;
                    await reconcileActiveConversation(activeId, epoch);
                }
            }

            if (isTerminated) return;
            await syncSidebarConversations(() => state.activeConversationId);
            if (typeof onRefreshPresence === 'function') {
                onRefreshPresence();
            }

            if (activeId && epoch === state.switchEpoch && sameId(state.activeConversationId, activeId) && !isTerminated) {
                const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
                    ? (document.getElementById('messages-container') || document)
                    : null;
                if (containerEl) {
                    checkAndMarkIncoming(containerEl, activeId, state.currentUserId);
                }
            }
        } catch (err) {
            if (isSessionRevokedSignalRError(err)) {
                triggerSessionExpired();
            } else {
                console.warn('[SignalR] Error during post-reconnect sync:', err?.message || err);
            }
        }
    });

    connection.onclose((error) => {
        requestedConversationId = null;
        joinedConversationId = null;
        if (isSessionRevokedSignalRError(error)) {
            triggerSessionExpired();
            return;
        }
        if (!isTerminated) {
            setStatus('disconnected', 'Disconnected from chat.');
        }
    });

    // Register all 8 IChatClient server-push event handlers once on this connection
    connection.on('ReceiveMessage', (msg) => handleReceiveMessage(msg));
    connection.on('ReceiveMessageEdited', (msg) => handleReceiveMessageEdited(msg));
    connection.on('ReceiveMessageDeletedForEveryone', (msg) => handleReceiveMessageDeletedForEveryone(msg));
    connection.on('ReceiveMessageDeletedForMe', (msg) => handleReceiveMessageDeletedForMe(msg));
    connection.on('ReceiveReactionUpdated', (reaction) => handleReceiveReactionUpdated(reaction));
    connection.on('ReceiveReceiptUpdated', (receipt) => handleReceiveReceiptUpdated(receipt));
    connection.on('ReceiveTypingIndicator', (typing) => {
        if (!isTerminated && typeof onTypingIndicator === 'function') {
            onTypingIndicator(typing);
        }
    });
    connection.on('ReceivePresenceChanged', (presence) => {
        if (!isTerminated && typeof onPresenceChanged === 'function') {
            onPresenceChanged(presence);
        }
    });

    const handlePageLeave = () => {
        const current = state.activeConversationId || requestedConversationId || joinedConversationId;
        if (current && isConnected() && !isTerminated) {
            Promise.resolve(connection.invoke('LeaveConversation', current)).catch(() => {});
        }
    };

    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('beforeunload', handlePageLeave);
        window.addEventListener('pagehide', handlePageLeave);
    }

    async function start() {
        if (isTerminated) return false;
        try {
            await connection.start();
            if (isTerminated) return false;
            setStatus('connected');

            const activeId = state.activeConversationId;
            if (activeId) {
                await notifyActiveConversation(activeId);
            }

            if (typeof onRefreshPresence === 'function') {
                onRefreshPresence();
            }

            if (state.activeConversationId && state.pendingConversationId === null && !isTerminated) {
                const containerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
                    ? (document.getElementById('messages-container') || document)
                    : null;
                if (containerEl) {
                    checkAndMarkIncoming(containerEl, state.activeConversationId, state.currentUserId);
                }
            }
            return true;
        } catch (err) {
            if (isSessionRevokedSignalRError(err)) {
                triggerSessionExpired();
                return false;
            }
            setStatus('disconnected', 'Failed to connect to chat.');
            return false;
        }
    }

    async function stop() {
        if (isTerminated) return;
        isTerminated = true;
        requestedConversationId = null;
        joinedConversationId = null;
        seenIncomingIds.clear();
        try {
            if (connection && typeof connection.stop === 'function') {
                await connection.stop();
            }
        } catch {}
    }

    function destroy() {
        if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
            window.removeEventListener('beforeunload', handlePageLeave);
            window.removeEventListener('pagehide', handlePageLeave);
        }
        stop();
    }

    return {
        connection,
        start,
        stop,
        destroy,
        notifyActiveConversation,
        leaveActiveConversation,
        replayBufferedEvents,
        reconcileActiveConversation
    };
}
