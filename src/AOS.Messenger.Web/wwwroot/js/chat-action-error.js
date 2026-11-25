/**
 * Shared coordinator for the #chat-action-error banner so overlapping errors from
 * composer, reactions, or actions never clear another module's newer message prematurely.
 */

let actionErrorTimer = null;

/**
 * Displays a transient error banner in #chat-action-error and resets any active hide timer.
 * @param {string} message
 * @param {number} [durationMs]
 */
export function showChatActionError(message, durationMs = 5000) {
    if (typeof document === 'undefined' || !message || typeof document.getElementById !== 'function') return;
    const el = document.getElementById('chat-action-error');
    if (!el) return;
    el.textContent = message;
    if (el.classList) {
        el.classList.remove('hidden');
    }
    if (actionErrorTimer) {
        clearTimeout(actionErrorTimer);
    }
    actionErrorTimer = setTimeout(() => {
        hideChatActionError();
    }, Math.max(1000, Number(durationMs) || 5000));
}

/**
 * Clears #chat-action-error and cancels any pending auto-hide timer.
 */
export function hideChatActionError() {
    if (actionErrorTimer) {
        clearTimeout(actionErrorTimer);
        actionErrorTimer = null;
    }
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return;
    const el = document.getElementById('chat-action-error');
    if (el) {
        el.textContent = '';
        if (el.classList) {
            el.classList.add('hidden');
        }
    }
}
