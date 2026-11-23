/**
 * AOS Messenger - Emoji :shortcode Autocomplete Module
 *
 * Detects WhatsApp-style :shortcode tokens while typing (at start of text or after whitespace/opening punctuation),
 * lazily loads the self-hosted emoji-data.js catalog, and displays a keyboard-accessible suggestion strip.
 *
 * Keyboard arbitration:
 * - ArrowUp / ArrowDown cycle matches
 * - Enter / Tab insert the selected emoji and suppress composer Enter-to-send
 * - Escape dismisses the suggestion strip without closing parent modals
 * - Never auto-replaces classic text emoticons like :) or <3
 *
 * Security Invariants:
 * - Pure DOM construction via document.createElement and .textContent only.
 */

import { loadDefaultEmojiCatalog, searchEmojiEntries } from './emoji-picker.js';
import {
    applySkinTone,
    insertTextAtCaret,
    loadPreferredSkinTone,
    recordRecentEmoji
} from './emoji-utils.js';

const MAX_SUGGESTIONS = 8;
const SHORTCODE_TRIGGER_RE = /(?:^|[\s([{"'])(:[a-z0-9_+-]{2,32})$/i;

/**
 * Finds an active :shortcode token immediately preceding caretPos.
 * @param {string} value
 * @param {number} caretPos
 * @returns {{ query: string, start: number, end: number } | null}
 */
export function findShortcodeTrigger(value, caretPos) {
    if (typeof value !== 'string' || typeof caretPos !== 'number' || caretPos < 3) {
        return null;
    }
    const prefix = value.slice(0, caretPos);
    const match = SHORTCODE_TRIGGER_RE.exec(prefix);
    if (!match) return null;

    const token = match[1];
    const query = token.slice(1).toLowerCase();
    if (query.length < 2) return null;

    return {
        query,
        start: caretPos - token.length,
        end: caretPos
    };
}

function clearChildren(el) {
    if (!el) return;
    while (el.firstChild) {
        el.removeChild(el.firstChild);
    }
}

/**
 * Creates a reusable :shortcode autocomplete controller attachable to multiple textareas
 * (Composer + Edit Message modal).
 * @param {{
 *   getUserId?: Function,
 *   loadCatalog?: Function
 * }} [options]
 * @returns {object}
 */
export function createEmojiAutocomplete(options = {}) {
    const getUserId = typeof options.getUserId === 'function' ? options.getUserId : () => null;
    const loadCatalog = typeof options.loadCatalog === 'function' ? options.loadCatalog : loadDefaultEmojiCatalog;

    let catalog = null;
    let isOpenState = false;
    let activeInputEl = null;
    let activeTriggerRange = null;
    let activeMatches = [];
    let selectedIndex = 0;
    let searchToken = 0;
    let popoverEl = null;

    const attached = new Map();

    function ensurePopoverBuilt() {
        if (popoverEl || typeof document === 'undefined' || !document.body) return popoverEl;

        const existing = typeof document.getElementById === 'function'
            ? document.getElementById('emoji-autocomplete-popover')
            : null;
        if (existing && typeof existing.remove === 'function') {
            existing.remove();
        }

        popoverEl = document.createElement('div');
        popoverEl.id = 'emoji-autocomplete-popover';
        popoverEl.setAttribute('role', 'listbox');
        popoverEl.setAttribute('aria-label', 'Emoji suggestions');
        popoverEl.className = 'hidden fixed z-[65] bg-[#FFFFFF] dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] rounded-xl shadow-lg py-1 max-h-60 overflow-y-auto custom-scroll select-none';
        document.body.appendChild(popoverEl);
        return popoverEl;
    }

    function positionPopover() {
        if (!popoverEl || !activeInputEl || !isOpenState) return;
        const margin = 8;
        const vw = (typeof window !== 'undefined' && window.innerWidth) ||
            (typeof document !== 'undefined' && document.documentElement && document.documentElement.clientWidth) ||
            1024;
        const vh = (typeof window !== 'undefined' && window.innerHeight) ||
            (typeof document !== 'undefined' && document.documentElement && document.documentElement.clientHeight) ||
            768;

        const rect = typeof activeInputEl.getBoundingClientRect === 'function'
            ? activeInputEl.getBoundingClientRect()
            : { left: margin, right: vw - margin, top: vh - 64, bottom: vh - 24, width: 320, height: 40 };

        const width = Math.min(Math.max(220, Math.round(rect.width)), Math.min(340, vw - margin * 2));
        const maxLeft = Math.max(margin, vw - width - margin);
        const left = Math.max(margin, Math.min(Math.round(rect.left), maxLeft));

        const estHeight = Math.min(240, activeMatches.length * 36 + 10);
        let top = Math.round(rect.top) - estHeight - 6;
        if (top < margin) {
            top = Math.min(Math.round(rect.bottom) + 6, Math.max(margin, vh - estHeight - margin));
        }

        if (popoverEl.style) {
            popoverEl.style.left = `${left}px`;
            popoverEl.style.top = `${top}px`;
            popoverEl.style.width = `${width}px`;
        }
    }

    function close() {
        searchToken++;
        isOpenState = false;
        activeInputEl = null;
        activeTriggerRange = null;
        activeMatches = [];
        selectedIndex = 0;
        if (popoverEl && popoverEl.classList) {
            popoverEl.classList.add('hidden');
        }
    }

    function commitMatch(index) {
        if (!isOpenState || !activeInputEl || !activeTriggerRange) return false;
        const entry = activeMatches[index];
        if (!entry) return false;

        const skinTone = loadPreferredSkinTone(getUserId());
        const emoji = applySkinTone(entry[0], skinTone, entry[4] === 1);
        const targetEl = activeInputEl;
        const range = activeTriggerRange;

        close();

        const inserted = insertTextAtCaret(targetEl, emoji, {
            focus: true,
            replaceRange: range
        });
        if (inserted) {
            recordRecentEmoji(getUserId(), emoji);
        }
        return inserted;
    }

    function renderMatches() {
        ensurePopoverBuilt();
        if (!popoverEl) return;
        clearChildren(popoverEl);

        if (activeMatches.length === 0) {
            close();
            return;
        }

        const skinTone = loadPreferredSkinTone(getUserId());
        const frag = document.createDocumentFragment();

        for (let i = 0; i < activeMatches.length; i++) {
            const entry = activeMatches[i];
            const rendered = applySkinTone(entry[0], skinTone, entry[4] === 1);
            const isSelected = i === selectedIndex;

            const item = document.createElement('button');
            item.type = 'button';
            item.setAttribute('role', 'option');
            item.setAttribute('aria-selected', isSelected ? 'true' : 'false');
            item.setAttribute('data-emoji-suggestion-index', String(i));
            item.className = isSelected
                ? 'w-full px-3 py-1.5 flex items-center gap-2.5 text-left bg-[#F0E9DF] dark:bg-[#45423E] text-[#2B2725] dark:text-[#F0E9E2] cursor-pointer'
                : 'w-full px-3 py-1.5 flex items-center gap-2.5 text-left text-[#2B2725] dark:text-[#F0E9E2] hover:bg-[#F0E9DF]/70 dark:hover:bg-[#45423E]/70 cursor-pointer';

            const emojiSpan = document.createElement('span');
            emojiSpan.className = 'emoji-font text-lg leading-none shrink-0';
            emojiSpan.textContent = rendered;
            item.appendChild(emojiSpan);

            const codeSpan = document.createElement('span');
            codeSpan.className = 'text-xs font-medium text-[#6E6560] dark:text-[#C9C0B9] truncate';
            codeSpan.textContent = `:${entry[1]}:`;
            item.appendChild(codeSpan);

            item.addEventListener('mousedown', (e) => {
                // Prevent textarea blur before selection completes
                e.preventDefault();
            });
            item.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                commitMatch(i);
            });

            frag.appendChild(item);
        }

        popoverEl.appendChild(frag);
        popoverEl.classList.remove('hidden');
        positionPopover();
    }

    async function evaluateInput(inputEl) {
        if (!inputEl || inputEl.disabled) {
            if (activeInputEl === inputEl) close();
            return;
        }

        const start = typeof inputEl.selectionStart === 'number' ? inputEl.selectionStart : (inputEl.value || '').length;
        const end = typeof inputEl.selectionEnd === 'number' ? inputEl.selectionEnd : start;
        if (start !== end) {
            if (activeInputEl === inputEl) close();
            return;
        }

        const trigger = findShortcodeTrigger(inputEl.value || '', start);
        if (!trigger) {
            if (activeInputEl === inputEl) close();
            return;
        }

        const myToken = ++searchToken;
        if (!catalog) {
            try {
                catalog = await loadCatalog();
            } catch {
                return;
            }
            if (myToken !== searchToken || inputEl.disabled) return;
        }

        const matches = searchEmojiEntries(catalog.entries, trigger.query, MAX_SUGGESTIONS);
        if (myToken !== searchToken) return;

        if (matches.length === 0) {
            close();
            return;
        }

        activeInputEl = inputEl;
        activeTriggerRange = { start: trigger.start, end: trigger.end };
        activeMatches = matches;
        selectedIndex = 0;
        isOpenState = true;
        renderMatches();
    }

    /**
     * Attaches :shortcode autocomplete listeners to a textarea element.
     * @param {HTMLTextAreaElement|null} inputEl
     * @returns {Function} Detach callback
     */
    function attach(inputEl) {
        if (!inputEl || attached.has(inputEl)) {
            return () => {};
        }

        const onInput = () => {
            evaluateInput(inputEl);
        };

        const onKeyDown = (e) => {
            if (!isOpenState || activeInputEl !== inputEl) return;

            if (e.key === 'ArrowDown') {
                e.preventDefault();
                selectedIndex = (selectedIndex + 1) % activeMatches.length;
                renderMatches();
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                selectedIndex = (selectedIndex - 1 + activeMatches.length) % activeMatches.length;
                renderMatches();
                return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                if (typeof e.stopImmediatePropagation === 'function') {
                    e.stopImmediatePropagation();
                } else if (typeof e.stopPropagation === 'function') {
                    e.stopPropagation();
                }
                commitMatch(selectedIndex);
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                if (typeof e.stopImmediatePropagation === 'function') {
                    e.stopImmediatePropagation();
                } else if (typeof e.stopPropagation === 'function') {
                    e.stopPropagation();
                }
                close();
            }
        };

        const onBlur = () => {
            if (activeInputEl === inputEl) {
                close();
            }
        };

        const onClick = () => {
            evaluateInput(inputEl);
        };

        // Capture phase on keydown ensures autocomplete claims Enter/Escape before composer or modal handlers
        inputEl.addEventListener('keydown', onKeyDown, true);
        inputEl.addEventListener('input', onInput);
        inputEl.addEventListener('blur', onBlur);
        inputEl.addEventListener('click', onClick);

        const detach = () => {
            if (activeInputEl === inputEl) close();
            inputEl.removeEventListener('keydown', onKeyDown, true);
            inputEl.removeEventListener('input', onInput);
            inputEl.removeEventListener('blur', onBlur);
            inputEl.removeEventListener('click', onClick);
            attached.delete(inputEl);
        };

        attached.set(inputEl, detach);
        return detach;
    }

    function destroy() {
        close();
        for (const detach of [...attached.values()]) {
            detach();
        }
        attached.clear();
        if (popoverEl && typeof popoverEl.remove === 'function') {
            popoverEl.remove();
        }
        popoverEl = null;
    }

    return {
        attach,
        close,
        resetForSwitch: close,
        isOpen: () => isOpenState,
        destroy
    };
}
