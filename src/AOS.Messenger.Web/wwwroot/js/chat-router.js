/**
 * AOS Messenger - Zero-Refresh Client-Side Chat Router
 *
 * Manages conversation link click interception, browser history synchronization (pushState/popstate),
 * mobile pane toggling, monotonic switchEpoch/AbortController cancellation, and keyset pagination.
 */

const CHAT_PATH_REGEX = /^\/(?:chat|Chat\/Conversation|Conversation)\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?$/i;

/**
 * Safely parses bootstrap metadata from <script id="chat-bootstrap" type="application/json">.
 * @returns {object|null}
 */
export function readChatBootstrap() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return null;
    }
    const el = document.getElementById('chat-bootstrap');
    if (!el) return null;

    try {
        const raw = el.textContent || '{}';
        return JSON.parse(raw);
    } catch {
        return null;
    }
}

/**
 * Extracts a conversation GUID from /chat/{id}, /Chat/Conversation/{id}, or /Conversation/{id}.
 * @param {string|null|undefined} pathname
 * @returns {string|null}
 */
export function parseConversationIdFromPath(pathname) {
    if (!pathname) return null;
    const match = CHAT_PATH_REGEX.exec(String(pathname));
    return match ? match[1] : null;
}

/**
 * Synchronizes mobile pane visibility between sidebar list and active chat pane.
 * @param {'chat'|'sidebar'} mode
 */
export function toggleMobilePane(mode) {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
    const sidebarPane = document.getElementById('app-sidebar-pane');
    const mainPane = document.getElementById('app-main-pane');

    if (mode === 'chat') {
        if (sidebarPane) {
            sidebarPane.className = 'hidden md:flex md:w-80 md:shrink-0 h-full';
        }
        if (mainPane) {
            mainPane.className = 'flex flex-1 w-full min-w-0 min-h-0 h-full overflow-hidden flex-col';
        }
    } else {
        if (sidebarPane) {
            sidebarPane.className = 'w-full md:w-80 md:shrink-0 flex h-full';
        }
        if (mainPane) {
            mainPane.className = 'hidden md:flex flex-1 w-full min-w-0 min-h-0 h-full overflow-y-auto flex-col';
        }
    }
}

function shouldIgnoreClick(e) {
    return e.defaultPrevented ||
        e.button !== 0 ||
        e.ctrlKey ||
        e.metaKey ||
        e.shiftKey ||
        e.altKey;
}

/**
 * Creates a client-side chat router bound to the unified chat shell.
 * @param {{ state: object, api: object, renderer: object, hooks?: object }} deps
 * @returns {object}
 */
export function createChatRouter({ state, api, renderer, hooks = {} }) {
    let initialized = false;
    let onDocumentClick = null;
    let onPopState = null;

    /**
     * Switches the active chat pane to nextId with full epoch/AbortController race protection.
     * @param {string} nextId
     * @param {{ pushUrl?: boolean, force?: boolean }} [options]
     */
    async function switchConversation(nextId, { pushUrl = true, force = false } = {}) {
        if (!nextId) return;

        const normNext = String(nextId).trim().toLowerCase();
        if (!normNext) return;

        const normActive = state.activeConversationId ? String(state.activeConversationId).toLowerCase() : null;
        const normPending = state.pendingConversationId ? String(state.pendingConversationId).toLowerCase() : null;

        if (!force && (normNext === normActive || normNext === normPending)) {
            return;
        }

        const previousId = state.activeConversationId;
        if (previousId && typeof hooks.getDraftText === 'function') {
            const draftText = hooks.getDraftText();
            if (draftText && draftText.trim().length > 0) {
                state.setDraft(previousId, draftText);
            } else {
                state.deleteDraft(previousId);
            }
        }

        const epoch = state.beginSwitch(nextId);
        const controller = new AbortController();
        state.activeAbortController = controller;

        if (typeof hooks.onBeforeSwitch === 'function') {
            hooks.onBeforeSwitch(previousId, nextId);
        }
        if (typeof renderer.lockAndClearHeaderActions === 'function') {
            renderer.lockAndClearHeaderActions();
        }
        toggleMobilePane('chat');
        if (typeof renderer.showActivePane === 'function') {
            renderer.showActivePane();
        }
        if (typeof renderer.showLoadingSkeleton === 'function') {
            renderer.showLoadingSkeleton();
        }

        try {
            const bundle = await api.getConversationBundle(nextId, { signal: controller.signal });
            if (epoch !== state.switchEpoch || controller.signal.aborted) {
                return;
            }

            const viewerUserId = bundle?.viewerUserId ?? bundle?.ViewerUserId ?? null;
            if (viewerUserId && state.currentUserId &&
                String(viewerUserId).toLowerCase() !== String(state.currentUserId).toLowerCase()) {
                if (typeof hooks.onIdentityChanged === 'function') {
                    hooks.onIdentityChanged();
                }
                return;
            }

            const conversation = bundle?.conversation ?? bundle?.Conversation ?? null;
            const messages = bundle?.messages ?? bundle?.Messages ?? [];
            const nextCursor = bundle?.nextCursor ?? bundle?.NextCursor ?? null;
            const hasMore = !!(bundle?.hasMore ?? bundle?.HasMore);

            const bufferedEvents = state.commitActiveConversation(
                nextId,
                conversation,
                messages,
                nextCursor,
                hasMore
            );

            if (typeof renderer.renderConversationHeader === 'function') {
                renderer.renderConversationHeader(conversation);
            }

            const containerEl = typeof document !== 'undefined' ? document.getElementById('messages-container') : null;
            if (typeof renderer.renderMessages === 'function') {
                renderer.renderMessages(containerEl, messages, state.currentUserId, conversation, { scrollToEnd: true });
            }

            if (pushUrl && typeof history !== 'undefined' && typeof history.pushState === 'function') {
                history.pushState({ conversationId: String(nextId) }, '', `/chat/${encodeURIComponent(String(nextId))}`);
            }

            if (typeof hooks.onAfterSwitch === 'function') {
                hooks.onAfterSwitch(String(nextId), conversation, bufferedEvents);
            }
        } catch (err) {
            if (err?.name === 'AbortError' || epoch !== state.switchEpoch) {
                return;
            }

            state.clearPendingSwitch();

            if (err?.code === 'UNAUTHORIZED') {
                if (typeof hooks.onSessionExpired === 'function') {
                    hooks.onSessionExpired();
                }
                return;
            }

            if (typeof hooks.onSwitchError === 'function') {
                hooks.onSwitchError(String(nextId), err);
            }

            const errorMsg = err?.status === 403 || err?.status === 404
                ? 'This conversation is no longer available.'
                : err?.status === 429
                    ? 'Too many requests. Please wait a moment and try again.'
                    : 'Could not load conversation. Try again.';

            if (typeof renderer.showSafeError === 'function') {
                renderer.showSafeError(errorMsg);
            }
        }
    }

    /**
     * Transitions the shell back to the empty / sidebar state (e.g. /chat or mobile back button).
     * @param {{ pushUrl?: boolean }} [options]
     */
    function switchToEmptyState({ pushUrl = true } = {}) {
        const previousId = state.activeConversationId;
        if (previousId && typeof hooks.getDraftText === 'function') {
            const draftText = hooks.getDraftText();
            if (draftText && draftText.trim().length > 0) {
                state.setDraft(previousId, draftText);
            } else {
                state.deleteDraft(previousId);
            }
        }

        state.resetActiveChatState();

        if (typeof hooks.onBeforeSwitch === 'function') {
            hooks.onBeforeSwitch(previousId, null);
        }
        if (typeof renderer.lockAndClearHeaderActions === 'function') {
            renderer.lockAndClearHeaderActions();
        }
        if (typeof renderer.clearPane === 'function') {
            renderer.clearPane();
        }
        if (typeof renderer.showEmptyState === 'function') {
            renderer.showEmptyState();
        }
        toggleMobilePane('sidebar');

        if (pushUrl && typeof history !== 'undefined' && typeof history.pushState === 'function') {
            history.pushState({ conversationId: null }, '', '/chat');
        }

        if (typeof hooks.onSwitchToEmpty === 'function') {
            hooks.onSwitchToEmpty(previousId);
        }
    }

    /**
     * Loads the previous keyset page of messages while guarding against mid-flight chat switches.
     */
    async function loadOlderMessages() {
        const convId = state.activeConversationId;
        if (!convId || state.isLoadingOlder || !state.hasMore || !state.nextCursor) {
            return;
        }

        const epoch = state.switchEpoch;
        state.isLoadingOlder = true;

        if (state.paginationAbortController) {
            state.paginationAbortController.abort();
        }
        const controller = new AbortController();
        state.paginationAbortController = controller;

        const loadingTopEl = typeof document !== 'undefined' ? document.getElementById('messages-loading-top') : null;
        if (loadingTopEl) loadingTopEl.classList.remove('hidden');

        try {
            const page = await api.getMessagesPage(convId, {
                cursor: state.nextCursor,
                pageSize: 30,
                signal: controller.signal
            });

            if (epoch !== state.switchEpoch || controller.signal.aborted) {
                return;
            }

            state.nextCursor = page?.nextCursor ?? page?.NextCursor ?? null;
            state.hasMore = !!(page?.hasMore ?? page?.HasMore);

            const messages = page?.messages ?? page?.Messages ?? [];
            const containerEl = typeof document !== 'undefined' ? document.getElementById('messages-container') : null;
            const streamEl = typeof document !== 'undefined' ? document.getElementById('message-stream') : null;

            if (messages.length > 0 && typeof renderer.prependOlderMessages === 'function') {
                renderer.prependOlderMessages(containerEl, streamEl, messages, state.currentUserId, state.activeMeta);
            }
        } catch (err) {
            if (err?.name === 'AbortError' || epoch !== state.switchEpoch) {
                return;
            }
            if (err?.code === 'UNAUTHORIZED' && typeof hooks.onSessionExpired === 'function') {
                hooks.onSessionExpired();
            }
        } finally {
            if (epoch === state.switchEpoch) {
                state.isLoadingOlder = false;
                state.paginationAbortController = null;
                if (loadingTopEl) loadingTopEl.classList.add('hidden');
            }
        }
    }

    /**
     * Activates click interception and popstate handling when #chat-shell-root is mounted.
     * @returns {boolean} True if initialized on a chat shell view
     */
    function init() {
        if (initialized || typeof document === 'undefined') {
            return initialized;
        }

        const shellRoot = document.getElementById('chat-shell-root');
        if (!shellRoot) {
            return false;
        }

        const bootstrap = readChatBootstrap();
        if (bootstrap) {
            state.initFromBootstrap(bootstrap);
        }

        if (typeof history !== 'undefined' && typeof history.replaceState === 'function' && typeof window !== 'undefined') {
            history.replaceState(
                { conversationId: state.activeConversationId ?? null },
                '',
                `${window.location.pathname}${window.location.search || ''}`
            );
        }

        onDocumentClick = (e) => {
            if (shouldIgnoreClick(e) || !e.target || typeof e.target.closest !== 'function') {
                return;
            }

            const convLink = e.target.closest('a[data-conversation-link]');
            if (convLink) {
                const convId = convLink.getAttribute('data-conversation-id') ||
                    parseConversationIdFromPath(convLink.getAttribute('href'));
                if (convId) {
                    e.preventDefault();
                    switchConversation(convId, { pushUrl: true });
                }
                return;
            }

            const backLink = e.target.closest('a[data-back-to-chats]');
            if (backLink) {
                e.preventDefault();
                switchToEmptyState({ pushUrl: true });
            }
        };

        onPopState = (e) => {
            const fromState = e.state && e.state.conversationId ? String(e.state.conversationId) : null;
            const fromPath = typeof window !== 'undefined' ? parseConversationIdFromPath(window.location.pathname) : null;
            const targetId = fromState || fromPath;

            if (targetId) {
                switchConversation(targetId, { pushUrl: false });
            } else {
                switchToEmptyState({ pushUrl: false });
            }
        };

        document.addEventListener('click', onDocumentClick);
        if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
            window.addEventListener('popstate', onPopState);
        }

        initialized = true;
        return true;
    }

    function destroy() {
        if (!initialized) return;
        if (typeof document !== 'undefined' && onDocumentClick) {
            document.removeEventListener('click', onDocumentClick);
        }
        if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function' && onPopState) {
            window.removeEventListener('popstate', onPopState);
        }
        onDocumentClick = null;
        onPopState = null;
        initialized = false;
    }

    return {
        init,
        destroy,
        switchConversation,
        switchToEmptyState,
        loadOlderMessages
    };
}
