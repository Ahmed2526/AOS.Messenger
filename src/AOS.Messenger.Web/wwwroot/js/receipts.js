/**
 * AOS Messenger - Delivery & Read Receipts Module
 *
 * Implements client-side receipt tracking, checkmark rendering, and receipt dispatch.
 *
 * Invariants (AGENTS.md):
 * - HTTP POST mutations for state changes with RequestVerificationToken.
 * - Realtime transport updates via SignalR ReceiveReceiptUpdated.
 * - Monotonic receipt progression: sent (1) -> delivered (2) -> read (3). Never regress.
 * - Safe DOM tree construction exclusively - zero innerHTML/outerHTML/insertAdjacentHTML.
 */

const lastSentDeliveredMap = new Map();
const lastSentReadMap = new Map();
let readDebounceTimer = null;

const STATUS_RANKS = {
    'sent': 1,
    'done': 1,
    'delivered': 2,
    'read': 3
};

/**
 * Gets anti-forgery request verification token from document.
 * @returns {string}
 */
function getVerificationToken() {
    if (typeof document === 'undefined') return '';
    const input = document.querySelector('input[name="__RequestVerificationToken"]');
    return input ? input.value : '';
}

/**
 * Safely parses a message ID into a BigInt.
 * @param {string|number|bigint} val
 * @returns {bigint|null}
 */
function toBigIntSafe(val) {
    if (val === null || val === undefined || val === '') return null;
    try {
        if (typeof val === 'bigint') return val;
        if (typeof val === 'number') return BigInt(Math.floor(val));
        const str = String(val).trim();
        return BigInt(str);
    } catch {
        return null;
    }
}

/**
 * Sends HTTP POST receipt request to server.
 * @param {string} endpoint
 * @param {bigint} upToMessageIdVal
 * @returns {Promise<Response|null>}
 */
async function postReceipt(endpoint, upToMessageIdVal) {
    try {
        const token = getVerificationToken();
        const headers = {
            'Content-Type': 'application/json',
            'Accept': 'application/json'
        };
        if (token) {
            headers['RequestVerificationToken'] = token;
        }

        const response = await fetch(endpoint, {
            method: 'POST',
            headers: headers,
            body: JSON.stringify({ upToMessageId: Number(upToMessageIdVal) })
        });

        if (!response.ok) {
            console.warn(`[Receipts] POST ${endpoint} failed with status ${response.status}`);
            return null;
        }

        return response;
    } catch (err) {
        console.error(`[Receipts] Error posting to ${endpoint}:`, err);
        return null;
    }
}

/**
 * Marks messages in a conversation as delivered up to the specified message ID.
 * Deduplicates to prevent redundant HTTP POSTs.
 *
 * @param {string} conversationId
 * @param {string|number|bigint} upToMessageId
 * @returns {Promise<Response|null>}
 */
export async function markDelivered(conversationId, upToMessageId) {
    if (!conversationId) return null;
    const upToIdVal = toBigIntSafe(upToMessageId);
    if (upToIdVal === null || upToIdVal <= 0n) return null;

    const normalizedConvId = String(conversationId).toLowerCase();
    const lastSent = lastSentDeliveredMap.get(normalizedConvId) || 0n;
    if (upToIdVal <= lastSent) {
        return null;
    }

    lastSentDeliveredMap.set(normalizedConvId, upToIdVal);
    const endpoint = `/api/conversations/${conversationId}/delivered`;
    return await postReceipt(endpoint, upToIdVal);
}

/**
 * Marks messages in a conversation as read up to the specified message ID.
 * Debounced (~300ms) when the conversation window is visible and focused.
 *
 * @param {string} conversationId
 * @param {string|number|bigint} upToMessageId
 * @returns {Promise<Response|null>}
 */
export function markRead(conversationId, upToMessageId) {
    if (!conversationId) return Promise.resolve(null);
    const upToIdVal = toBigIntSafe(upToMessageId);
    if (upToIdVal === null || upToIdVal <= 0n) return Promise.resolve(null);

    const normalizedConvId = String(conversationId).toLowerCase();
    const lastSent = lastSentReadMap.get(normalizedConvId) || 0n;
    if (upToIdVal <= lastSent) {
        return Promise.resolve(null);
    }

    const isVisibleAndFocused = typeof document !== 'undefined' &&
        document.visibilityState === 'visible' &&
        (typeof document.hasFocus === 'function' ? document.hasFocus() : true);

    if (readDebounceTimer) {
        clearTimeout(readDebounceTimer);
        readDebounceTimer = null;
    }

    return new Promise((resolve) => {
        readDebounceTimer = setTimeout(async () => {
            readDebounceTimer = null;
            if (typeof document !== 'undefined') {
                const stillVisibleAndFocused = document.visibilityState === 'visible' &&
                    (typeof document.hasFocus === 'function' ? document.hasFocus() : true);
                if (!stillVisibleAndFocused) {
                    resolve(null);
                    return;
                }
            }

            lastSentReadMap.set(normalizedConvId, upToIdVal);
            const endpoint = `/api/conversations/${conversationId}/read`;
            const resp = await postReceipt(endpoint, upToIdVal);
            resolve(resp);
        }, isVisibleAndFocused ? 300 : 50);
    });
}

/**
 * Updates outgoing message checkmark indicators within a container up to a target message ID.
 * Status order: sent (1) < delivered (2) < read (3). Never regresses.
 *
 * @param {HTMLElement|Document} container
 * @param {string|number|bigint} upToMessageId
 * @param {string} receiptType - 'sent' | 'delivered' | 'read'
 */
export function updateMessageCheckmarks(container, upToMessageId, receiptType) {
    if (!container || typeof document === 'undefined') return;
    const targetIdVal = toBigIntSafe(upToMessageId);
    if (targetIdVal === null || targetIdVal <= 0n) return;

    const normalizedType = (receiptType || '').toLowerCase().trim();
    const targetRank = STATUS_RANKS[normalizedType] || 0;
    if (targetRank <= 0) return;

    const targetStatus = normalizedType === 'done' ? 'sent' : normalizedType;
    const checkmarkNodes = container.querySelectorAll('span[data-receipt-status]');

    for (let i = 0; i < checkmarkNodes.length; i++) {
        const node = checkmarkNodes[i];
        let msgIdStr = node.getAttribute('data-message-id');
        if (!msgIdStr) {
            const row = node.closest('[data-message-id]');
            if (row) {
                msgIdStr = row.getAttribute('data-message-id');
            }
        }

        const msgIdVal = toBigIntSafe(msgIdStr);
        if (msgIdVal === null || msgIdVal > targetIdVal) {
            continue;
        }

        const currentStatus = (node.getAttribute('data-receipt-status') || 'sent').toLowerCase();
        const currentRank = STATUS_RANKS[currentStatus] || 1;

        if (targetRank > currentRank) {
            node.setAttribute('data-receipt-status', targetStatus);

            if (targetStatus === 'read') {
                node.textContent = 'done_all';
                node.className = 'material-symbols-outlined text-[13px] select-none align-middle text-sky-300';
            } else if (targetStatus === 'delivered') {
                node.textContent = 'done_all';
                node.className = 'material-symbols-outlined text-[13px] select-none align-middle text-white/70';
            } else {
                node.textContent = 'done';
                node.className = 'material-symbols-outlined text-[13px] select-none align-middle text-white/70';
            }
        }
    }
}

/**
 * Finds all incoming messages in the container and triggers delivered/read receipts.
 *
 * @param {HTMLElement|Document} container
 * @param {string} conversationId
 * @param {string|null} currentUserId
 */
export function checkAndMarkIncoming(container, conversationId, currentUserId) {
    if (!container || !conversationId) return;
    const rows = container.querySelectorAll('.message-bubble-row');
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
            const idVal = toBigIntSafe(row.getAttribute('data-message-id'));
            if (idVal !== null && idVal > maxIncomingId) {
                maxIncomingId = idVal;
            }
        }
    }

    if (maxIncomingId > 0n) {
        markDelivered(conversationId, maxIncomingId);
        if (typeof document !== 'undefined' && document.visibilityState === 'visible' &&
            (typeof document.hasFocus === 'function' ? document.hasFocus() : true)) {
            markRead(conversationId, maxIncomingId);
        }
    }
}

/**
 * Initializes receipts for a conversation:
 * - Listens for ReceiveReceiptUpdated on the SignalR connection.
 * - Handles initial watermark progression.
 *
 * @param {object} connection - SignalR HubConnection
 * @param {string} conversationId
 * @param {string|null} currentUserId
 * @returns {object} Receipts controller
 */
export function initReceipts(connection, conversationId, currentUserId) {
    const normalizedConvId = String(conversationId || '').toLowerCase();
    const normalizedUserId = currentUserId ? String(currentUserId).toLowerCase() : null;

    if (connection && typeof connection.on === 'function') {
        connection.on('ReceiveReceiptUpdated', (receipt) => {
            if (!receipt) return;
            const convId = receipt.conversationId || receipt.ConversationId;
            if (String(convId).toLowerCase() !== normalizedConvId) return;

            const actorUserId = receipt.userId || receipt.UserId;
            // Only update our outgoing checkmarks if the peer updated receipts
            if (normalizedUserId && String(actorUserId).toLowerCase() === normalizedUserId) {
                return;
            }

            const upToId = receipt.upToMessageId !== undefined ? receipt.upToMessageId : receipt.UpToMessageId;
            const rType = receipt.receiptType || receipt.ReceiptType || 'delivered';
            const container = document.getElementById('messages-container') || document;
            updateMessageCheckmarks(container, upToId, rType);
        });
    }

    const controller = {
        markDelivered: (upToId) => markDelivered(conversationId, upToId),
        markRead: (upToId) => markRead(conversationId, upToId),
        updateMessageCheckmarks: (container, upToId, rType) => updateMessageCheckmarks(container, upToId, rType),
        checkAndMarkIncoming: (container) => checkAndMarkIncoming(container || document.getElementById('messages-container') || document, conversationId, currentUserId)
    };

    return controller;
}

const receiptsApi = {
    initReceipts,
    markDelivered,
    markRead,
    updateMessageCheckmarks,
    checkAndMarkIncoming
};

if (typeof window !== 'undefined') {
    window.Receipts = receiptsApi;
}
if (typeof globalThis !== 'undefined') {
    globalThis.Receipts = receiptsApi;
}
