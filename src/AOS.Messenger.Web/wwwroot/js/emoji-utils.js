/**
 * AOS Messenger - Emoji Utilities Module
 *
 * Provides WhatsApp-style tiered emoji-only message classification (matching SSR EmojiPresentationHelper),
 * grapheme-safe truncation, Fitzpatrick skin-tone application, caret-safe textarea insertion
 * respecting the 4,000 UTF-16 code-unit limit, and per-userId localStorage preference storage.
 *
 * Security Invariants:
 * - Zero innerHTML / HTML parsing.
 * - localStorage stores only non-sensitive UI preferences (recent emojis, skin tone), scoped per userId.
 */

export const MAX_MESSAGE_LENGTH = 4000;
export const MAX_RECENT_EMOJIS = 24;

export const NORMAL_MESSAGE_TEXT_CLASS = 'message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words';
export const SINGLE_EMOJI_MESSAGE_TEXT_CLASS = 'message-text-content emoji-jumbo-single text-4xl leading-tight whitespace-pre-wrap break-words';
export const FEW_EMOJI_MESSAGE_TEXT_CLASS = 'message-text-content emoji-jumbo-few text-3xl leading-snug whitespace-pre-wrap break-words';

const VALID_SKIN_TONE_MODIFIERS = new Set([
    '',
    '\u{1F3FB}',
    '\u{1F3FC}',
    '\u{1F3FD}',
    '\u{1F3FE}',
    '\u{1F3FF}'
]);

const EMOJI_GRAPHEME_RE = /^(?:[#*0-9]\uFE0F?\u20E3|\p{Regional_Indicator}{2}|\u{1F3F4}[\u{E0020}-\u{E007E}]+\u{E007F}|(?:\p{Extended_Pictographic}(?:\uFE0F|[\u{1F3FB}-\u{1F3FF}])?)(?:\u200D(?:\p{Extended_Pictographic}(?:\uFE0F|[\u{1F3FB}-\u{1F3FF}])?))*)$/u;
const TEXT_ONLY_PICTO_RE = /^[\u00A9\u00AE\u2122\u203C\u2049\u2139\u2194-\u2199\u21A9\u21AA]$/;

let cachedSegmenter = null;

function getGraphemeSegmenter() {
    if (cachedSegmenter) return cachedSegmenter;
    if (typeof Intl !== 'undefined' && typeof Intl.Segmenter === 'function') {
        cachedSegmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
    }
    return cachedSegmenter;
}

/**
 * Splits a string into user-perceived grapheme clusters.
 * @param {string} text
 * @returns {Array<string>}
 */
export function splitGraphemes(text) {
    const str = text === null || text === undefined ? '' : String(text);
    if (!str) return [];
    const seg = getGraphemeSegmenter();
    if (seg) {
        const result = [];
        for (const item of seg.segment(str)) {
            result.push(item.segment);
        }
        return result;
    }
    return Array.from(str);
}

/**
 * Truncates text safely by grapheme clusters so ZWJ / skin-tone emojis are never split.
 * @param {string} text
 * @param {number} [maxGraphemes]
 * @returns {string}
 */
export function truncateGraphemes(text, maxGraphemes = 100) {
    const graphemes = splitGraphemes(text);
    if (graphemes.length <= maxGraphemes) {
        return graphemes.join('');
    }
    return `${graphemes.slice(0, maxGraphemes).join('')}...`;
}

/**
 * Tests whether a single grapheme cluster is an emoji.
 * @param {string} grapheme
 * @returns {boolean}
 */
export function isEmojiGrapheme(grapheme) {
    if (!grapheme || typeof grapheme !== 'string') return false;
    return EMOJI_GRAPHEME_RE.test(grapheme) && !TEXT_ONLY_PICTO_RE.test(grapheme);
}

/**
 * Classifies a message for WhatsApp-style tiered emoji-only rendering:
 * - 'single': exactly 1 emoji (ignoring whitespace)
 * - 'few': 2 or 3 emojis (ignoring whitespace)
 * - 'normal': 4+ emojis, mixed text + emojis, or plain text
 * @param {string|null|undefined} content
 * @returns {'single'|'few'|'normal'}
 */
export function classifyEmojiOnlyMessage(content) {
    if (content === null || content === undefined) return 'normal';
    const str = String(content);
    if (!str.trim()) return 'normal';

    let emojiCount = 0;
    const graphemes = splitGraphemes(str);
    for (let i = 0; i < graphemes.length; i++) {
        const g = graphemes[i];
        if (!g.trim()) continue;
        if (!isEmojiGrapheme(g)) {
            return 'normal';
        }
        emojiCount++;
        if (emojiCount > 3) {
            return 'normal';
        }
    }

    if (emojiCount === 1) return 'single';
    if (emojiCount === 2 || emojiCount === 3) return 'few';
    return 'normal';
}

/**
 * Returns the Tailwind CSS class string for a message bubble's text element.
 * Mirrors EmojiPresentationHelper.GetMessageContentCssClasses on the server.
 * @param {string|null|undefined} content
 * @returns {string}
 */
export function getMessageContentClassName(content) {
    const tier = classifyEmojiOnlyMessage(content);
    if (tier === 'single') return SINGLE_EMOJI_MESSAGE_TEXT_CLASS;
    if (tier === 'few') return FEW_EMOJI_MESSAGE_TEXT_CLASS;
    return NORMAL_MESSAGE_TEXT_CLASS;
}

/**
 * Applies a Fitzpatrick skin-tone modifier to a base emoji if supported.
 * @param {string} baseEmoji
 * @param {string} toneModifier
 * @param {boolean} supportsSkinTone
 * @returns {string}
 */
export function applySkinTone(baseEmoji, toneModifier, supportsSkinTone) {
    if (!baseEmoji || !supportsSkinTone || !toneModifier || !VALID_SKIN_TONE_MODIFIERS.has(toneModifier)) {
        return baseEmoji || '';
    }
    const cps = Array.from(baseEmoji);
    if (cps.length === 0) return baseEmoji;
    if (cps.length >= 2 && cps[1] === '\uFE0F') {
        return cps[0] + toneModifier + cps.slice(2).join('');
    }
    return cps[0] + toneModifier + cps.slice(1).join('');
}

/**
 * Inserts text into a textarea at the current caret or replaces the selected range.
 * Respects UTF-16 maxLength without slicing surrogate pairs or ZWJ sequences.
 * Dispatches a bubbling 'input' event so auto-resize and typing indicators stay in sync.
 *
 * @param {HTMLTextAreaElement|HTMLInputElement|null} inputEl
 * @param {string} textToInsert
 * @param {{ maxLength?: number, focus?: boolean, replaceRange?: { start: number, end: number }|null }} [options]
 * @returns {boolean} True if text was inserted
 */
export function insertTextAtCaret(inputEl, textToInsert, options = {}) {
    if (!inputEl || inputEl.disabled || !textToInsert) return false;

    const maxLength = typeof options.maxLength === 'number' ? options.maxLength : MAX_MESSAGE_LENGTH;
    const shouldFocus = options.focus !== false;
    const current = String(inputEl.value ?? '');

    let start;
    let end;
    if (options.replaceRange &&
        typeof options.replaceRange.start === 'number' &&
        typeof options.replaceRange.end === 'number') {
        start = Math.max(0, Math.min(options.replaceRange.start, current.length));
        end = Math.max(start, Math.min(options.replaceRange.end, current.length));
    } else if (typeof inputEl.selectionStart === 'number' && typeof inputEl.selectionEnd === 'number') {
        start = Math.max(0, Math.min(inputEl.selectionStart, current.length));
        end = Math.max(start, Math.min(inputEl.selectionEnd, current.length));
    } else {
        start = current.length;
        end = current.length;
    }

    const nextValue = current.slice(0, start) + textToInsert + current.slice(end);
    if (nextValue.length > maxLength) {
        return false;
    }

    inputEl.value = nextValue;
    const nextCaret = start + textToInsert.length;

    if (shouldFocus && typeof inputEl.focus === 'function') {
        inputEl.focus();
    }
    if (typeof inputEl.setSelectionRange === 'function') {
        try {
            inputEl.setSelectionRange(nextCaret, nextCaret);
        } catch {
            // Ignore in synthetic/unfocused environments
        }
    } else {
        inputEl.selectionStart = nextCaret;
        inputEl.selectionEnd = nextCaret;
    }

    if (typeof inputEl.dispatchEvent === 'function') {
        try {
            const ev = typeof Event === 'function'
                ? new Event('input', { bubbles: true })
                : { type: 'input', target: inputEl };
            inputEl.dispatchEvent(ev);
        } catch {
            // Ignore dispatch failures in minimal test mocks
        }
    }

    return true;
}

function normalizeUserScope(userId) {
    const raw = userId && typeof userId === 'object' && userId.value !== undefined ? userId.value : userId;
    const str = String(raw || '').trim().toLowerCase();
    return str || 'anonymous';
}

function getSafeStorage() {
    try {
        if (typeof globalThis !== 'undefined' && globalThis.localStorage) {
            return globalThis.localStorage;
        }
    } catch {
        // Storage unavailable (private mode / sandbox)
    }
    return null;
}

export function getRecentEmojisStorageKey(userId) {
    return `aos-messenger-emoji-recents:${normalizeUserScope(userId)}`;
}

export function getSkinToneStorageKey(userId) {
    return `aos-messenger-emoji-skintone:${normalizeUserScope(userId)}`;
}

/**
 * Loads recently used emojis for the given userId from localStorage.
 * @param {string|null|undefined} userId
 * @returns {Array<string>}
 */
export function loadRecentEmojis(userId) {
    const storage = getSafeStorage();
    if (!storage) return [];
    try {
        const raw = storage.getItem(getRecentEmojisStorageKey(userId));
        if (!raw) return [];
        const parsed = JSON.parse(raw);
        if (!Array.isArray(parsed)) return [];
        return parsed
            .filter((item) => typeof item === 'string' && item.length > 0 && item.length <= 32)
            .slice(0, MAX_RECENT_EMOJIS);
    } catch {
        return [];
    }
}

/**
 * Records a recently used emoji at the front of the user's MRU list in localStorage.
 * @param {string|null|undefined} userId
 * @param {string} emoji
 * @returns {Array<string>} Updated recents list
 */
export function recordRecentEmoji(userId, emoji) {
    if (!emoji || typeof emoji !== 'string') return loadRecentEmojis(userId);
    const current = loadRecentEmojis(userId);
    const updated = [emoji, ...current.filter((e) => e !== emoji)].slice(0, MAX_RECENT_EMOJIS);
    const storage = getSafeStorage();
    if (storage) {
        try {
            storage.setItem(getRecentEmojisStorageKey(userId), JSON.stringify(updated));
        } catch {
            // Ignore storage quota / privacy errors
        }
    }
    return updated;
}

/**
 * Loads the user's preferred Fitzpatrick skin-tone modifier ('' for default).
 * @param {string|null|undefined} userId
 * @returns {string}
 */
export function loadPreferredSkinTone(userId) {
    const storage = getSafeStorage();
    if (!storage) return '';
    try {
        const saved = storage.getItem(getSkinToneStorageKey(userId));
        return saved && VALID_SKIN_TONE_MODIFIERS.has(saved) ? saved : '';
    } catch {
        return '';
    }
}

/**
 * Persists the user's preferred Fitzpatrick skin-tone modifier scoped by userId.
 * @param {string|null|undefined} userId
 * @param {string} modifier
 */
export function savePreferredSkinTone(userId, modifier) {
    const safeModifier = VALID_SKIN_TONE_MODIFIERS.has(modifier) ? modifier : '';
    const storage = getSafeStorage();
    if (!storage) return;
    try {
        storage.setItem(getSkinToneStorageKey(userId), safeModifier);
    } catch {
        // Ignore storage quota / privacy errors
    }
}
