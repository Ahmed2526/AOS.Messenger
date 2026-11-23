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
export function setupTypingEmitter(connection, convIdOrGetter, inputElement) {
    const resolveConvId = typeof convIdOrGetter === 'function'
        ? convIdOrGetter
        : () => convIdOrGetter;

    if (!inputElement || !convIdOrGetter) {
        return {
            stopTyping: () => {},
            resetForSwitch: () => {},
            destroy: () => {}
        };
    }

    let isTyping = false;
    let activeTypingConversationId = null;
    let lastEmitTime = 0;
    let inactivityTimer = null;

    function canInvoke() {
        return connection &&
            typeof connection.invoke === 'function' &&
            connection.state === 'Connected';
    }

    function emitTyping(typing, targetConvId) {
        if (!canInvoke() || !targetConvId) return;
        try {
            connection.invoke('SendTypingIndicator', targetConvId, typing)
                .catch(err => console.debug('[Typing] SendTypingIndicator failed:', err));
        } catch (err) {
            console.debug('[Typing] SendTypingIndicator error:', err);
        }
    }

    function startTyping() {
        const targetConvId = resolveConvId();
        if (!targetConvId) return;

        if (isTyping && activeTypingConversationId &&
            String(activeTypingConversationId).toLowerCase() !== String(targetConvId).toLowerCase()) {
            emitTyping(false, activeTypingConversationId);
            isTyping = false;
            lastEmitTime = 0;
        }

        const now = Date.now();
        if (!isTyping || now - lastEmitTime >= 2500) {
            isTyping = true;
            activeTypingConversationId = targetConvId;
            lastEmitTime = now;
            emitTyping(true, targetConvId);
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
            const targetConvId = activeTypingConversationId || resolveConvId();
            isTyping = false;
            activeTypingConversationId = null;
            lastEmitTime = 0;
            if (targetConvId) {
                emitTyping(false, targetConvId);
            }
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
        resetForSwitch: stopTyping,
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
 * @param {string|Function} convIdOrGetter
 * @param {HTMLElement} typingContainerElement
 * @returns {object} Controller with resetForSwitch and destroy methods
 */
export function setupTypingReceiver(connection, convIdOrGetter, typingContainerElement) {
    const resolveConvId = typeof convIdOrGetter === 'function'
        ? convIdOrGetter
        : () => convIdOrGetter;

    if (!typingContainerElement || !convIdOrGetter) {
        return { resetForSwitch: () => {}, destroy: () => {} };
    }

    let autoHideTimer = null;

    function resetForSwitch() {
        if (autoHideTimer) {
            clearTimeout(autoHideTimer);
            autoHideTimer = null;
        }
        typingContainerElement.textContent = '';
        typingContainerElement.classList.add('hidden');
    }

    function handleTypingIndicator(typing) {
        if (!typing) return;
        const currentConvId = resolveConvId();
        if (!currentConvId) return;

        const convId = typing.conversationId || typing.ConversationId;
        if (!convId || String(convId).toLowerCase() !== String(currentConvId).toLowerCase()) return;

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
        handleTypingIndicator,
        resetForSwitch,
        destroy: () => {
            resetForSwitch();
            if (connection && typeof connection.off === 'function') {
                connection.off('ReceiveTypingIndicator', handleTypingIndicator);
            }
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
