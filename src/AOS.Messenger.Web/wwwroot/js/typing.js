/**
 * AOS Messenger - Realtime Typing Indicators Module
 *
 * Implements client-side typing indicator emission with rate throttling
 * and incoming typing indicator reception with auto-hide timer.
 *
 * Invariants (AGENTS.md):
 * - Ephemeral hub invocations over SignalR (SendTypingIndicator).
 * - Safe DOM tree construction exclusively - zero innerHTML/outerHTML/insertAdjacentHTML.
 * - Text assigned strictly via textContent.
 */

/**
 * Sets up typing indicator emitter on the chat input element.
 * Throttles SendTypingIndicator(..., true) to at most once every 2.5s.
 * Emits false immediately on message send, input cleared, blur, or 3.5s inactivity.
 *
 * @param {object} connection - SignalR HubConnection
 * @param {string} conversationId
 * @param {HTMLInputElement|HTMLTextAreaElement} inputElement
 * @returns {object} Controller with stopTyping and destroy methods
 */
export function setupTypingEmitter(connection, conversationId, inputElement) {
    if (!inputElement || !conversationId) {
        return {
            stopTyping: () => {},
            destroy: () => {}
        };
    }

    let isTyping = false;
    let lastEmitTime = 0;
    let inactivityTimer = null;

    function canInvoke() {
        return connection &&
            typeof connection.invoke === 'function' &&
            connection.state === 'Connected';
    }

    function emitTyping(typing) {
        if (!canInvoke()) return;
        try {
            connection.invoke('SendTypingIndicator', conversationId, typing)
                .catch(err => console.debug('[Typing] SendTypingIndicator failed:', err));
        } catch (err) {
            console.debug('[Typing] SendTypingIndicator error:', err);
        }
    }

    function startTyping() {
        const now = Date.now();
        if (!isTyping || now - lastEmitTime >= 2500) {
            isTyping = true;
            lastEmitTime = now;
            emitTyping(true);
        }

        if (inactivityTimer) {
            clearTimeout(inactivityTimer);
        }
        inactivityTimer = setTimeout(() => {
            stopTyping();
        }, 3500);
    }

    function stopTyping() {
        if (inactivityTimer) {
            clearTimeout(inactivityTimer);
            inactivityTimer = null;
        }

        if (isTyping) {
            isTyping = false;
            lastEmitTime = 0;
            emitTyping(false);
        }
    }

    function onInput() {
        const val = inputElement.value || '';
        if (val.trim() === '') {
            stopTyping();
        } else {
            startTyping();
        }
    }

    function onBlur() {
        stopTyping();
    }

    inputElement.addEventListener('input', onInput);
    inputElement.addEventListener('blur', onBlur);

    return {
        stopTyping,
        destroy: () => {
            stopTyping();
            inputElement.removeEventListener('input', onInput);
            inputElement.removeEventListener('blur', onBlur);
        }
    };
}

/**
 * Sets up typing indicator receiver for a conversation.
 * Listens for ReceiveTypingIndicator and updates typingContainerElement.
 * Auto-hides after 5 seconds if no updates arrive.
 *
 * @param {object} connection - SignalR HubConnection
 * @param {string} conversationId
 * @param {HTMLElement} typingContainerElement
 * @returns {object} Controller with destroy method
 */
export function setupTypingReceiver(connection, conversationId, typingContainerElement) {
    if (!typingContainerElement || !conversationId) {
        return { destroy: () => {} };
    }

    const normalizedConvId = String(conversationId).toLowerCase();
    let autoHideTimer = null;

    function handleTypingIndicator(typing) {
        if (!typing) return;
        const convId = typing.conversationId || typing.ConversationId;
        if (String(convId).toLowerCase() !== normalizedConvId) return;

        const isTyping = (typing.isTyping !== undefined) ? typing.isTyping : typing.IsTyping;

        if (autoHideTimer) {
            clearTimeout(autoHideTimer);
            autoHideTimer = null;
        }

        if (isTyping) {
            typingContainerElement.textContent = 'typing...';
            typingContainerElement.classList.remove('hidden');

            autoHideTimer = setTimeout(() => {
                typingContainerElement.textContent = '';
                typingContainerElement.classList.add('hidden');
                autoHideTimer = null;
            }, 5000);
        } else {
            typingContainerElement.textContent = '';
            typingContainerElement.classList.add('hidden');
        }
    }

    if (connection && typeof connection.on === 'function') {
        connection.on('ReceiveTypingIndicator', handleTypingIndicator);
    }

    return {
        destroy: () => {
            if (autoHideTimer) {
                clearTimeout(autoHideTimer);
                autoHideTimer = null;
            }
            if (connection && typeof connection.off === 'function') {
                connection.off('ReceiveTypingIndicator', handleTypingIndicator);
            }
            typingContainerElement.textContent = '';
            typingContainerElement.classList.add('hidden');
        }
    };
}

const typingApi = {
    setupTypingEmitter,
    setupTypingReceiver
};

if (typeof window !== 'undefined') {
    window.Typing = typingApi;
}
if (typeof globalThis !== 'undefined') {
    globalThis.Typing = typingApi;
}
