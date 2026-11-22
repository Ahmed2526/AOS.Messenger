/**
 * AOS Messenger - Conversation Page Controller
 *
 * Coordinates active conversation interactions: message sending, keyset pagination,
 * reply quoting, textarea auto-expansion, delivery & read receipts, typing indicators,
 * presence synchronization, and real-time state reconciliation.
 *
 * Architectural Invariants:
 * - Safe DOM tree construction using document.createElement exclusively.
 * - Zero HTML string injection into live DOM.
 * - All text content bound strictly via textContent or createTextNode.
 */

import {
    createMessageBubbleElement,
    formatTime,
    getMessageId,
    isMessageRendered,
    scrollToBottom,
    updateSidebarLastMessage
} from './message-renderer.js';

import {
    initReceipts,
    markDelivered,
    markRead
} from './receipts.js';

import {
    setupTypingEmitter,
    setupTypingReceiver
} from './typing.js';

import {
    setupPresence,
    fetchPresence
} from './presence.js';

import {
    setupEmojiPicker
} from './reactions.js';

import {
    setupMessageActions
} from './message-actions.js';

(() => {
    'use strict';

    // 1. Read metadata from #conversation-page-root dataset.init, <script type="application/json" id="conversation-init-data">, or container attributes
    let conversationId = '';
    let currentUserId = '';
    let peerUserId = '';
    let peerLastDeliveredId = null;
    let peerLastReadId = null;
    let nextCursor = null;
    let hasMoreMessages = false;

    const rootEl = document.getElementById('conversation-page-root');
    if (rootEl && rootEl.dataset.init) {
        try {
            let rawInit = rootEl.dataset.init;
            if (rawInit.includes('&quot;') || rawInit.includes('&amp;')) {
                rawInit = rawInit.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&');
            }
            const parsed = JSON.parse(rawInit);
            conversationId = parsed.conversationId || '';
            currentUserId = parsed.currentUserId || '';
            peerUserId = parsed.peerUserId || '';
            peerLastDeliveredId = parsed.peerLastDeliveredMessageId || null;
            peerLastReadId = parsed.peerLastReadMessageId || null;
            nextCursor = parsed.nextCursor || null;
            hasMoreMessages = !!parsed.hasMore;
        } catch (err) {
            console.error('Failed to parse conversation init data:', err);
        }
    }

    if (!conversationId) {
        const initDataEl = document.getElementById('conversation-init-data');
        if (initDataEl) {
            try {
                let rawText = initDataEl.textContent || '{}';
                if (rawText.includes('&quot;') || rawText.includes('&amp;')) {
                    rawText = rawText.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&');
                }
                const parsed = JSON.parse(rawText);
                conversationId = parsed.conversationId || '';
                currentUserId = parsed.currentUserId || '';
                peerUserId = parsed.peerUserId || '';
                peerLastDeliveredId = parsed.peerLastDeliveredMessageId || null;
                peerLastReadId = parsed.peerLastReadMessageId || null;
                nextCursor = parsed.nextCursor || null;
                hasMoreMessages = !!parsed.hasMore;
            } catch (err) {
                console.error('[ConversationPage] Failed to parse conversation-init-data:', err);
            }
        }
    }

    const containerEl = document.getElementById('messages-container');
    if (containerEl) {
        conversationId = conversationId || containerEl.getAttribute('data-conversation-id') || '';
        currentUserId = currentUserId || containerEl.getAttribute('data-current-user-id') || '';
        peerUserId = peerUserId || containerEl.getAttribute('data-peer-user-id') || '';
        if (!peerLastDeliveredId && containerEl.hasAttribute('data-peer-last-delivered-id')) {
            peerLastDeliveredId = containerEl.getAttribute('data-peer-last-delivered-id') || null;
        }
        if (!peerLastReadId && containerEl.hasAttribute('data-peer-last-read-id')) {
            peerLastReadId = containerEl.getAttribute('data-peer-last-read-id') || null;
        }
        if (!nextCursor && containerEl.hasAttribute('data-next-cursor')) {
            const val = containerEl.getAttribute('data-next-cursor');
            nextCursor = val ? val : null;
        }
        if (containerEl.hasAttribute('data-has-more')) {
            hasMoreMessages = containerEl.getAttribute('data-has-more') === 'true';
        }
    }

    if (!conversationId) {
        return;
    }

    let isLoadingOlder = false;
    let isSending = false;
    let activeReplyTo = null; // { id, sender, preview }

    // 2. Initialize SignalR Realtime streaming and state reconciliation
    const realtime = typeof ChatRealtime !== 'undefined' ? ChatRealtime.init({
        conversationId: conversationId,
        currentUserId: currentUserId,
        initialCursor: nextCursor
    }) : null;

    const connection = realtime ? realtime.connection : null;

    const streamEl = document.getElementById('message-stream');
    const loadingTopEl = document.getElementById('messages-loading-top');
    const emptyPlaceholderEl = document.getElementById('no-messages-placeholder');
    const inputEl = document.getElementById('message-content-input');
    const sendBtn = document.getElementById('send-message-btn');
    const replyBanner = document.getElementById('composer-reply-banner');
    const replySenderEl = document.getElementById('reply-banner-sender');
    const replyPreviewEl = document.getElementById('reply-banner-preview');
    const cancelReplyBtn = document.getElementById('cancel-reply-btn');
    const csrfToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    // 3. Initialize Phase 7 Modular ES Modules: Receipts, Typing, Presence
    const receipts = initReceipts(connection, conversationId, currentUserId);

    const typingContainerEl = document.getElementById('typing-indicator');
    const typingEmitter = setupTypingEmitter(connection, conversationId, inputEl);
    const typingReceiver = setupTypingReceiver(connection, conversationId, typingContainerEl);

    const sidebarListEl = document.getElementById('conversationList');
    const activeHeaderEl = document.getElementById('active-conversation-header');
    const presence = setupPresence(connection, sidebarListEl, activeHeaderEl);

    // Initialize Phase 8: Reactions & Message Actions
    setupEmojiPicker(conversationId, currentUserId);
    setupMessageActions(conversationId, currentUserId);

    // Format SSR timestamps into client local time on initial render
    document.querySelectorAll('.message-timestamp[data-created-at]').forEach(el => {
        const formatted = formatTime(el.getAttribute('data-created-at'));
        if (formatted) {
            el.textContent = formatted;
        }
    });

    // Auto-scroll to bottom on initial load
    scrollToBottom();

    // Auto-resizing textarea
    if (inputEl) {
        inputEl.addEventListener('input', () => {
            inputEl.style.height = 'auto';
            inputEl.style.height = Math.min(inputEl.scrollHeight, 128) + 'px';
        });

        // Keyboard shortcut: Return sends, Shift+Return inserts newline
        inputEl.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMessage();
            }
        });
    }

    if (sendBtn) {
        sendBtn.addEventListener('click', (e) => {
            e.preventDefault();
            sendMessage();
        });
    }

    // Reply handling
    function setReply(id, sender, preview) {
        activeReplyTo = { id, sender, preview };
        if (replySenderEl) replySenderEl.textContent = sender || '';
        if (replyPreviewEl) replyPreviewEl.textContent = preview || '';
        if (replyBanner) replyBanner.classList.remove('hidden');
        if (inputEl) inputEl.focus();
    }

    function clearReply() {
        activeReplyTo = null;
        if (replyBanner) replyBanner.classList.add('hidden');
        if (replySenderEl) replySenderEl.textContent = '';
        if (replyPreviewEl) replyPreviewEl.textContent = '';
    }

    if (cancelReplyBtn) {
        cancelReplyBtn.addEventListener('click', clearReply);
    }

    // Attach reply listener dynamically for message bubbles
    document.addEventListener('click', (e) => {
        const btn = e.target.closest('.reply-action-btn');
        if (!btn) return;
        const id = btn.getAttribute('data-id');
        const sender = btn.getAttribute('data-sender');
        const preview = btn.getAttribute('data-preview');
        setReply(id, sender, preview);
    });

    // Form submit confirmation dialogs for Clear and Delete actions
    document.querySelectorAll('form[data-confirm]').forEach(form => {
        form.addEventListener('submit', (e) => {
            const promptMsg = form.getAttribute('data-confirm');
            if (promptMsg && !window.confirm(promptMsg)) {
                e.preventDefault();
            }
        });
    });

    // Keyset pagination on scrolling to top
    async function loadOlderMessages() {
        if (isLoadingOlder || !hasMoreMessages || !nextCursor || !streamEl || !containerEl) return;
        isLoadingOlder = true;
        if (loadingTopEl) loadingTopEl.classList.remove('hidden');

        const previousScrollHeight = streamEl.scrollHeight;
        const previousScrollTop = streamEl.scrollTop;

        try {
            const url = `/api/conversations/${conversationId}/messages?cursor=${encodeURIComponent(nextCursor)}&pageSize=30`;
            const resp = await fetch(url, {
                headers: { 'Accept': 'application/json' }
            });

            if (resp.status === 401 || resp.status === 403 || (resp.redirected && resp.url.includes('/Account/Login'))) {
                if (realtime && typeof realtime.handleSessionExpired === 'function') {
                    realtime.handleSessionExpired();
                } else {
                    window.location.href = '/Account/Login?sessionExpired=true';
                }
                return;
            }

            if (!resp.ok) {
                console.error('Failed to load older messages', resp.status);
                return;
            }

            const pageData = await resp.json();
            nextCursor = pageData.nextCursor;
            hasMoreMessages = pageData.hasMore;

            if (pageData.messages && pageData.messages.length > 0) {
                // Keyset page messages come newest-first; reverse to maintain ascending chronological order in stream
                const olderMessages = [...pageData.messages].reverse();
                const frag = document.createDocumentFragment();

                for (const msg of olderMessages) {
                    const msgId = getMessageId(msg);
                    if (!isMessageRendered(msgId)) {
                        const el = createMessageBubbleElement(msg, currentUserId);
                        if (el) frag.appendChild(el);
                    }
                }

                containerEl.insertBefore(frag, containerEl.firstChild);

                // Preserve scroll position
                const newScrollHeight = streamEl.scrollHeight;
                streamEl.scrollTop = previousScrollTop + (newScrollHeight - previousScrollHeight);
            }
        } catch (err) {
            console.error('Error fetching older messages:', err);
        } finally {
            isLoadingOlder = false;
            if (loadingTopEl) loadingTopEl.classList.add('hidden');
        }
    }

    /**
     * Finds the latest visible peer message in the stream and sends markDelivered & markRead.
     */
    function markVisiblePeerMessages() {
        if (!containerEl || !conversationId) return;

        const rows = containerEl.querySelectorAll('.message-bubble-row');
        let maxIncomingId = 0n;
        const normUserId = currentUserId ? String(currentUserId).toLowerCase() : null;

        for (let i = 0; i < rows.length; i++) {
            const row = rows[i];
            const isOutgoingAttr = row.getAttribute('data-is-outgoing');
            const senderIdAttr = row.getAttribute('data-sender-id');
            const isOutgoing = isOutgoingAttr === 'true' ||
                (normUserId && senderIdAttr && senderIdAttr.toLowerCase() === normUserId) ||
                row.classList.contains('justify-end');

            if (!isOutgoing) {
                const idAttr = row.getAttribute('data-message-id');
                if (idAttr) {
                    try {
                        const idVal = BigInt(idAttr);
                        if (idVal > maxIncomingId) {
                            maxIncomingId = idVal;
                        }
                    } catch {}
                }
            }
        }

        if (maxIncomingId > 0n) {
            markDelivered(conversationId, maxIncomingId);
            if (document.visibilityState === 'visible' && (typeof document.hasFocus === 'function' ? document.hasFocus() : true)) {
                markRead(conversationId, maxIncomingId);
            }
        }
    }

    // Scroll listener for pagination and read receipts
    if (streamEl) {
        let scrollReadDebounce = null;
        streamEl.addEventListener('scroll', () => {
            if (streamEl.scrollTop <= 40) {
                loadOlderMessages();
            }

            if (scrollReadDebounce) clearTimeout(scrollReadDebounce);
            scrollReadDebounce = setTimeout(() => {
                markVisiblePeerMessages();
            }, 300);
        });
    }

    // Window focus and visibility listeners to trigger markRead
    window.addEventListener('focus', () => {
        markVisiblePeerMessages();
    });

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            markVisiblePeerMessages();
        }
    });

    // Trigger initial markRead / markDelivered for loaded SSR messages
    markVisiblePeerMessages();

    // Send message via API with immediate deduplication
    async function sendMessage() {
        if (isSending || !inputEl) return;
        const content = inputEl.value.trim();
        if (!content) return;

        isSending = true;
        if (sendBtn) sendBtn.disabled = true;
        inputEl.disabled = true;

        // Stop typing indicator immediately upon message send
        if (typingEmitter && typeof typingEmitter.stopTyping === 'function') {
            typingEmitter.stopTyping();
        }

        const idempotencyKey = crypto.randomUUID();
        const payload = {
            content: content,
            idempotencyKey: idempotencyKey,
            replyToMessageId: activeReplyTo ? activeReplyTo.id : null
        };

        try {
            const headers = {
                'Content-Type': 'application/json',
                'Accept': 'application/json'
            };
            if (csrfToken) {
                headers['RequestVerificationToken'] = csrfToken;
            }

            const resp = await fetch(`/api/conversations/${conversationId}/messages`, {
                method: 'POST',
                headers: headers,
                body: JSON.stringify(payload)
            });

            if (resp.status === 401 || resp.status === 403 || (resp.redirected && resp.url.includes('/Account/Login'))) {
                if (realtime && typeof realtime.handleSessionExpired === 'function') {
                    realtime.handleSessionExpired();
                } else {
                    window.location.href = '/Account/Login?sessionExpired=true';
                }
                return;
            }

            if (!resp.ok) {
                console.warn('API returned non-OK status:', resp.status);
                // Fallback to MVC form post if API fails
                const fbContent = document.getElementById('fallback-content');
                const fbKey = document.getElementById('fallback-idempotency-key');
                const fbReply = document.getElementById('fallback-reply-to');
                const fbForm = document.getElementById('message-fallback-form');
                if (fbContent) fbContent.value = content;
                if (fbKey) fbKey.value = idempotencyKey;
                if (fbReply && activeReplyTo) fbReply.value = activeReplyTo.id;
                if (fbForm) fbForm.submit();
                return;
            }

            const sentMessage = await resp.json();
            const msgId = getMessageId(sentMessage);

            // Deduplicate before appending (in case SignalR broadcast arrived first)
            if (!isMessageRendered(msgId) && containerEl) {
                const el = createMessageBubbleElement(sentMessage, currentUserId);
                if (el) containerEl.appendChild(el);
                if (emptyPlaceholderEl) {
                    emptyPlaceholderEl.classList.add('hidden');
                }
                updateSidebarLastMessage(conversationId, sentMessage.content);
                const sidebarItem = document.querySelector(`.conversation-item[data-conversation-id="${conversationId}"]`);
                if (sidebarItem && msgId) {
                    sidebarItem.setAttribute('data-last-message-id', String(msgId));
                }
            }

            // Reset input and reply
            inputEl.value = '';
            inputEl.style.height = 'auto';
            clearReply();
            scrollToBottom(true);
        } catch (err) {
            console.error('Error sending message:', err);
            // Fallback to form submit
            const fbContent = document.getElementById('fallback-content');
            const fbKey = document.getElementById('fallback-idempotency-key');
            const fbReply = document.getElementById('fallback-reply-to');
            const fbForm = document.getElementById('message-fallback-form');
            if (fbContent) fbContent.value = content;
            if (fbKey) fbKey.value = idempotencyKey;
            if (fbReply && activeReplyTo) fbReply.value = activeReplyTo.id;
            if (fbForm) fbForm.submit();
        } finally {
            isSending = false;
            if (sendBtn) sendBtn.disabled = false;
            if (inputEl) {
                inputEl.disabled = false;
                inputEl.focus();
            }
        }
    }
})();
