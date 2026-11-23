/**
 * AOS Messenger - Client-Side Chat Shell State Store
 *
 * Holds monotonic switch epochs, AbortControllers, active/pending conversation IDs,
 * bounded in-memory-only drafts (LRU max 50), and buffered realtime events during switches.
 *
 * Security Invariants:
 * - Zero disk/storage persistence: drafts and messages exist strictly in JS heap memory
 *   (never localStorage or sessionStorage) and are purged immediately on wipeAllSensitiveState().
 */

export const MAX_DRAFTS = 50;
export const MAX_DRAFT_LENGTH = 4000;
export const MAX_BUFFERED_EVENTS = 200;

/**
 * Extracts a normalized string ID from a message DTO or ID wrapper.
 * @param {object|string|number|null|undefined} msg
 * @returns {string|null}
 */
function normalizeMessageId(msg) {
    if (msg === null || msg === undefined) return null;
    if (typeof msg === 'string' || typeof msg === 'number' || typeof msg === 'bigint') {
        return String(msg);
    }
    if (msg.id && typeof msg.id === 'object' && msg.id.value !== undefined) {
        return String(msg.id.value);
    }
    if (msg.id !== undefined && msg.id !== null) {
        return String(msg.id);
    }
    if (msg.messageId !== undefined && msg.messageId !== null) {
        return String(msg.messageId);
    }
    return null;
}

/**
 * Normalizes a conversation ID to a trimmed lowercase lookup key.
 * @param {string|object|null|undefined} convId
 * @returns {string|null}
 */
function normalizeKey(convId) {
    if (!convId) return null;
    const raw = typeof convId === 'object' && convId.value !== undefined ? convId.value : convId;
    const str = String(raw || '').trim();
    return str ? str.toLowerCase() : null;
}

/**
 * Creates an isolated chat state store instance.
 * @returns {object}
 */
export function createChatState() {
    const state = {
        currentUserId: null,
        loginUrl: '/Account/Login',
        activeConversationId: null,
        pendingConversationId: null,
        switchEpoch: 0,
        activeAbortController: null,
        paginationAbortController: null,
        activeMeta: null,
        nextCursor: null,
        hasMore: false,
        isLoadingOlder: false,
        messagesById: new Map(),
        drafts: new Map(),
        pendingRealtimeBuffer: new Map(),

        /**
         * Hydrates initial state from #chat-bootstrap JSON parsed at shell startup.
         * @param {object|null|undefined} bootstrap
         */
        initFromBootstrap(bootstrap) {
            state.currentUserId = bootstrap?.currentUserId ? String(bootstrap.currentUserId) : null;
            state.loginUrl = bootstrap?.loginUrl ? String(bootstrap.loginUrl) : '/Account/Login';

            if (bootstrap?.initialConversationId) {
                state.activeConversationId = String(bootstrap.initialConversationId);
                state.pendingConversationId = null;
                state.activeMeta = bootstrap.meta ?? null;
                state.nextCursor = bootstrap.nextCursor ?? null;
                state.hasMore = !!bootstrap.hasMore;
            } else {
                state.activeConversationId = null;
                state.pendingConversationId = null;
                state.activeMeta = null;
                state.nextCursor = null;
                state.hasMore = false;
            }
            state.isLoadingOlder = false;
        },

        /**
         * Stores an unsent draft in bounded in-memory-only LRU storage.
         * Deletes the entry if empty or whitespace.
         * @param {string|null|undefined} conversationId
         * @param {string|null|undefined} text
         */
        setDraft(conversationId, text) {
            const key = normalizeKey(conversationId);
            if (!key) return;

            const raw = text === null || text === undefined ? '' : String(text);
            if (!raw.trim()) {
                state.drafts.delete(key);
                return;
            }

            const bounded = raw.length > MAX_DRAFT_LENGTH ? raw.slice(0, MAX_DRAFT_LENGTH) : raw;
            state.drafts.delete(key);
            state.drafts.set(key, bounded);

            while (state.drafts.size > MAX_DRAFTS) {
                const oldestKey = state.drafts.keys().next().value;
                if (oldestKey === undefined) break;
                state.drafts.delete(oldestKey);
            }
        },

        /**
         * Retrieves an in-memory draft for a conversation (or '' if absent).
         * @param {string|null|undefined} conversationId
         * @returns {string}
         */
        getDraft(conversationId) {
            const key = normalizeKey(conversationId);
            if (!key) return '';
            return state.drafts.get(key) ?? '';
        },

        /**
         * Removes an in-memory draft for a conversation.
         * @param {string|null|undefined} conversationId
         */
        deleteDraft(conversationId) {
            const key = normalizeKey(conversationId);
            if (!key) return;
            state.drafts.delete(key);
        },

        /**
         * Transitions into switching state BEFORE awaiting network I/O so old-chat realtime events
         * cannot inject into the loading pane and clicking back to the previous chat works cleanly.
         * @param {string} nextId
         * @returns {number} New monotonic switchEpoch
         */
        beginSwitch(nextId) {
            const epoch = ++state.switchEpoch;

            if (state.activeAbortController) {
                state.activeAbortController.abort();
                state.activeAbortController = null;
            }
            if (state.paginationAbortController) {
                state.paginationAbortController.abort();
                state.paginationAbortController = null;
            }

            const normalizedNextId = String(nextId);
            state.activeConversationId = null;
            state.pendingConversationId = normalizedNextId;
            state.activeMeta = null;
            state.nextCursor = null;
            state.hasMore = false;
            state.isLoadingOlder = false;
            state.messagesById.clear();
            state.pendingRealtimeBuffer.clear();

            const key = normalizeKey(normalizedNextId);
            if (key) {
                state.pendingRealtimeBuffer.set(key, []);
            }

            return epoch;
        },

        /**
         * Commits fetched bundle metadata and messages and drains any realtime events
         * buffered for this conversation while the fetch was in flight.
         * @param {string} id
         * @param {object|null} meta
         * @param {Array<object>} [messages]
         * @param {string|null} [nextCursor]
         * @param {boolean} [hasMore]
         * @returns {Array<object>} Buffered realtime events for this conversation
         */
        commitActiveConversation(id, meta, messages = [], nextCursor = null, hasMore = false) {
            const normalizedId = String(id);
            const key = normalizeKey(normalizedId);

            state.activeConversationId = normalizedId;
            state.pendingConversationId = null;
            state.activeAbortController = null;
            state.activeMeta = meta ?? null;
            state.nextCursor = nextCursor ?? null;
            state.hasMore = !!hasMore;
            state.isLoadingOlder = false;

            state.messagesById.clear();
            if (Array.isArray(messages)) {
                for (const msg of messages) {
                    const msgId = normalizeMessageId(msg);
                    if (msgId) {
                        state.messagesById.set(msgId, msg);
                    }
                }
            }

            const buffered = key && state.pendingRealtimeBuffer.has(key)
                ? [...(state.pendingRealtimeBuffer.get(key) || [])]
                : [];
            state.pendingRealtimeBuffer.clear();

            return buffered;
        },

        /**
         * Clears pending switch markers after an error without committing a conversation.
         */
        clearPendingSwitch() {
            state.pendingConversationId = null;
            state.activeAbortController = null;
            state.isLoadingOlder = false;
            state.pendingRealtimeBuffer.clear();
        },

        /**
         * Buffers an incoming SignalR event if it targets the currently pending switch conversation.
         * @param {string|null|undefined} conversationId
         * @param {object} event
         * @returns {boolean} True if buffered
         */
        bufferRealtimeEvent(conversationId, event) {
            const pendingKey = normalizeKey(state.pendingConversationId);
            const eventKey = normalizeKey(conversationId);
            if (!pendingKey || !eventKey || pendingKey !== eventKey || !event) {
                return false;
            }

            let list = state.pendingRealtimeBuffer.get(pendingKey);
            if (!list) {
                list = [];
                state.pendingRealtimeBuffer.set(pendingKey, list);
            }

            if (list.length >= MAX_BUFFERED_EVENTS) {
                return false;
            }

            list.push(event);
            return true;
        },

        /**
         * Clears active conversation state while preserving identity and unsent drafts.
         */
        resetActiveChatState() {
            ++state.switchEpoch;

            if (state.activeAbortController) {
                state.activeAbortController.abort();
                state.activeAbortController = null;
            }
            if (state.paginationAbortController) {
                state.paginationAbortController.abort();
                state.paginationAbortController = null;
            }

            state.activeConversationId = null;
            state.pendingConversationId = null;
            state.activeMeta = null;
            state.nextCursor = null;
            state.hasMore = false;
            state.isLoadingOlder = false;
            state.messagesById.clear();
            state.pendingRealtimeBuffer.clear();
        },

        /**
         * Purges all active state, drafts, and user identity on session revocation or identity change.
         */
        wipeAllSensitiveState() {
            state.resetActiveChatState();
            state.drafts.clear();
            state.currentUserId = null;
        }
    };

    return state;
}

export const chatState = createChatState();
