/**
 * AOS Messenger - WhatsApp-Style Emoji Picker Module
 *
 * Implements a lazy-loaded, full Unicode 15.1 emoji picker supporting:
 * - Recently Used + 8 standard WhatsApp categories
 * - Instant keyword/shortcode search
 * - Global 6-option Fitzpatrick skin-tone selector (persisted per userId in localStorage)
 * - Adaptive layout: floating popover on Desktop (>= 768px), full-width panel above composer on Mobile
 * - Stay-open multi-emoji insertion at caret without forcing the mobile soft keyboard over the picker
 * - Shared across both the main message composer and the Edit Message modal
 *
 * Security Invariants:
 * - Pure DOM construction via document.createElement and .textContent only.
 * - Zero external network requests (self-hosted emoji-data.js lazy import).
 */

import {
    applySkinTone,
    insertTextAtCaret,
    loadPreferredSkinTone,
    loadRecentEmojis,
    recordRecentEmoji,
    savePreferredSkinTone
} from './emoji-utils.js';

const MOBILE_BREAKPOINT_PX = 768;
const MAX_SEARCH_RESULTS = 160;
const FALLBACK_POPULAR_EMOJIS = ['😂', '❤️', '👍', '🔥', '🙏', '😍', '🎉', '😊', '😭', '🥰', '👏', '✨'];

let defaultCatalogPromise = null;

/**
 * Lazily loads the self-hosted Unicode 15.1 emoji dataset once per page session.
 * @returns {Promise<{ categories: Array<object>, skinTones: Array<object>, entries: Array<Array>, byCategory: Map<number, Array<Array>> }>}
 */
export async function loadDefaultEmojiCatalog() {
    if (!defaultCatalogPromise) {
        defaultCatalogPromise = import('./emoji-data.js').then((mod) => {
            const categories = mod.EMOJI_CATEGORIES || [];
            const skinTones = mod.SKIN_TONES || [];
            const entries = mod.EMOJI_ENTRIES || [];
            const byCategory = new Map();
            for (let i = 0; i < categories.length; i++) {
                byCategory.set(i, []);
            }
            for (let i = 0; i < entries.length; i++) {
                const entry = entries[i];
                const catIdx = entry[3];
                let bucket = byCategory.get(catIdx);
                if (!bucket) {
                    bucket = [];
                    byCategory.set(catIdx, bucket);
                }
                bucket.push(entry);
            }
            return { categories, skinTones, entries, byCategory };
        }).catch((err) => {
            defaultCatalogPromise = null;
            throw err;
        });
    }
    return defaultCatalogPromise;
}

/**
 * Scores and filters emoji entries for a search query.
 * @param {Array<Array>} entries
 * @param {string} rawQuery
 * @param {number} [limit]
 * @returns {Array<Array>}
 */
export function searchEmojiEntries(entries, rawQuery, limit = MAX_SEARCH_RESULTS) {
    if (!Array.isArray(entries) || entries.length === 0) return [];
    const query = String(rawQuery || '').trim().toLowerCase().replace(/^:+|:+$/g, '');
    if (!query) return [];

    const queryTokens = query.split(/[\s_-]+/).filter(Boolean);
    const scored = [];

    for (let i = 0; i < entries.length; i++) {
        const entry = entries[i];
        const shortcode = entry[1] || '';
        const keywords = entry[2] || '';

        let score = 0;
        if (shortcode === query) {
            score = 1000;
        } else if (shortcode.startsWith(query)) {
            score = 800;
        } else if (shortcode.includes(`_${query}`)) {
            score = 650;
        } else if (keywords.startsWith(query) || keywords.includes(` ${query}`)) {
            score = 550;
        } else if (shortcode.includes(query)) {
            score = 400;
        } else if (queryTokens.length > 1) {
            const combined = `${shortcode} ${keywords}`;
            let allMatch = true;
            for (let t = 0; t < queryTokens.length; t++) {
                if (!combined.includes(queryTokens[t])) {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) score = 300;
        } else if (keywords.includes(query)) {
            score = 200;
        }

        if (score > 0) {
            scored.push({ entry, score, index: i });
        }
    }

    scored.sort((a, b) => (b.score - a.score) || (a.index - b.index));
    const result = [];
    const count = Math.min(limit, scored.length);
    for (let i = 0; i < count; i++) {
        result.push(scored[i].entry);
    }
    return result;
}

function clearChildren(el) {
    if (!el) return;
    while (el.firstChild) {
        el.removeChild(el.firstChild);
    }
}

function isMobileViewport() {
    if (typeof window === 'undefined') return false;
    const vw = window.innerWidth || (typeof document !== 'undefined' && document.documentElement ? document.documentElement.clientWidth : 1024);
    return vw < MOBILE_BREAKPOINT_PX;
}

function shouldFocusTextareaAfterPick() {
    if (isMobileViewport()) return false;
    if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
        try {
            if (window.matchMedia('(pointer: coarse)').matches) {
                return false;
            }
        } catch {
            // Ignore matchMedia errors
        }
    }
    return true;
}

/**
 * Creates a single reusable WhatsApp-style emoji picker controller for the chat shell.
 * @param {{
 *   getUserId?: Function,
 *   loadCatalog?: Function,
 *   onEmojiInserted?: Function
 * }} [options]
 * @returns {object}
 */
export function createEmojiPicker(options = {}) {
    const getUserId = typeof options.getUserId === 'function' ? options.getUserId : () => null;
    const loadCatalog = typeof options.loadCatalog === 'function' ? options.loadCatalog : loadDefaultEmojiCatalog;
    const onEmojiInserted = typeof options.onEmojiInserted === 'function' ? options.onEmojiInserted : null;

    let catalog = null;
    let isOpenState = false;
    let activeTriggerEl = null;
    let activeTargetEl = null;
    let activeSurface = 'composer';
    let activeCategoryKey = 'smileys';
    let searchQuery = '';
    let selectedSkinTone = '';
    let skinToneMenuOpen = false;
    let openRequestToken = 0;

    // DOM refs created lazily once
    let panelEl = null;
    let searchInputEl = null;
    let clearSearchBtn = null;
    let skinToneToggleBtn = null;
    let skinToneSwatchDot = null;
    let skinToneMenuEl = null;
    let tabsBarEl = null;
    let gridContainerEl = null;
    let statusHeadingEl = null;
    let globalListenersBound = false;

    function getViewportSize() {
        const vw = (typeof window !== 'undefined' && window.innerWidth) ||
            (typeof document !== 'undefined' && document.documentElement && document.documentElement.clientWidth) ||
            1024;
        const vh = (typeof window !== 'undefined' && window.innerHeight) ||
            (typeof document !== 'undefined' && document.documentElement && document.documentElement.clientHeight) ||
            768;
        return { vw, vh };
    }

    function updateTriggerAria(triggerEl, expanded) {
        if (!triggerEl || typeof triggerEl.setAttribute !== 'function') return;
        triggerEl.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        if (triggerEl.classList) {
            if (expanded) {
                triggerEl.classList.add('text-[#B3929D]');
            } else {
                triggerEl.classList.remove('text-[#B3929D]');
            }
        }
    }

    function getSkinToneSwatchColor(modifier) {
        if (catalog && Array.isArray(catalog.skinTones)) {
            const match = catalog.skinTones.find((t) => t.modifier === modifier);
            if (match && match.swatch) return match.swatch;
        }
        return '#F8D147';
    }

    function syncSkinToneButton() {
        if (skinToneSwatchDot && skinToneSwatchDot.style) {
            skinToneSwatchDot.style.backgroundColor = getSkinToneSwatchColor(selectedSkinTone);
        }
        if (skinToneToggleBtn) {
            skinToneToggleBtn.setAttribute('aria-expanded', skinToneMenuOpen ? 'true' : 'false');
        }
        if (skinToneMenuEl && skinToneMenuEl.classList) {
            if (skinToneMenuOpen) {
                skinToneMenuEl.classList.remove('hidden');
            } else {
                skinToneMenuEl.classList.add('hidden');
            }
        }
    }

    function renderSkinToneMenu() {
        if (!skinToneMenuEl || !catalog) return;
        clearChildren(skinToneMenuEl);

        for (let i = 0; i < catalog.skinTones.length; i++) {
            const tone = catalog.skinTones[i];
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.setAttribute('data-skin-tone', tone.modifier);
            btn.setAttribute('aria-label', tone.label);
            btn.setAttribute('aria-pressed', selectedSkinTone === tone.modifier ? 'true' : 'false');
            btn.title = tone.label;

            const isSelected = selectedSkinTone === tone.modifier;
            btn.className = `w-7 h-7 rounded-full flex items-center justify-center transition-transform hover:scale-110 cursor-pointer ${isSelected ? 'ring-2 ring-[#B3929D] ring-offset-1 ring-offset-white dark:ring-offset-[#3A3835]' : ''}`;

            const dot = document.createElement('span');
            dot.className = 'w-4 h-4 rounded-full border border-black/15 inline-block';
            if (dot.style) dot.style.backgroundColor = tone.swatch;
            btn.appendChild(dot);

            btn.addEventListener('mousedown', (e) => e.preventDefault());
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                selectedSkinTone = tone.modifier;
                savePreferredSkinTone(getUserId(), selectedSkinTone);
                skinToneMenuOpen = false;
                syncSkinToneButton();
                renderSkinToneMenu();
                renderGrid();
            });

            skinToneMenuEl.appendChild(btn);
        }
    }

    function renderCategoryTabs() {
        if (!tabsBarEl || !catalog) return;
        clearChildren(tabsBarEl);

        const allTabs = [
            { id: 'recents', label: 'Recently used', icon: 'schedule' },
            ...catalog.categories
        ];

        for (let i = 0; i < allTabs.length; i++) {
            const tab = allTabs[i];
            const isActive = !searchQuery && activeCategoryKey === tab.id;

            const btn = document.createElement('button');
            btn.type = 'button';
            btn.setAttribute('role', 'tab');
            btn.setAttribute('aria-selected', isActive ? 'true' : 'false');
            btn.setAttribute('aria-label', tab.label);
            btn.setAttribute('data-emoji-tab', tab.id);
            btn.title = tab.label;
            btn.className = isActive
                ? 'flex-1 h-9 flex items-center justify-center border-b-2 border-[#B3929D] text-[#B3929D] transition-colors cursor-pointer'
                : 'flex-1 h-9 flex items-center justify-center border-b-2 border-transparent text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#2B2725] dark:hover:text-[#F0E9E2] hover:bg-[#F0E9DF]/50 dark:hover:bg-[#45423E]/50 transition-colors cursor-pointer';

            const icon = document.createElement('span');
            icon.className = 'material-symbols-outlined text-[18px]';
            icon.textContent = tab.icon;
            btn.appendChild(icon);

            btn.addEventListener('mousedown', (e) => e.preventDefault());
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                activeCategoryKey = tab.id;
                if (searchQuery) {
                    searchQuery = '';
                    if (searchInputEl) searchInputEl.value = '';
                    if (clearSearchBtn && clearSearchBtn.classList) clearSearchBtn.classList.add('hidden');
                }
                skinToneMenuOpen = false;
                syncSkinToneButton();
                renderCategoryTabs();
                renderGrid();
                if (gridContainerEl) gridContainerEl.scrollTop = 0;
            });

            tabsBarEl.appendChild(btn);
        }
    }

    function createEmojiCellButton(renderedEmoji, label) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.setAttribute('data-emoji-pick', renderedEmoji);
        btn.setAttribute('aria-label', label || renderedEmoji);
        btn.title = label || renderedEmoji;
        btn.className = 'emoji-font w-full h-10 rounded-lg flex items-center justify-center text-[22px] leading-none hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] active:scale-90 transition-transform cursor-pointer select-none';
        btn.textContent = renderedEmoji;

        btn.addEventListener('mousedown', (e) => {
            // Keep textarea selection/caret intact when clicking emoji buttons
            e.preventDefault();
        });
        btn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (!activeTargetEl || activeTargetEl.disabled) return;

            const inserted = insertTextAtCaret(activeTargetEl, renderedEmoji, {
                focus: shouldFocusTextareaAfterPick()
            });

            if (inserted) {
                recordRecentEmoji(getUserId(), renderedEmoji);
                if (onEmojiInserted) {
                    onEmojiInserted(renderedEmoji, activeTargetEl, activeSurface);
                }
            }
        });

        return btn;
    }

    function renderGrid() {
        if (!gridContainerEl || !statusHeadingEl) return;
        clearChildren(gridContainerEl);

        if (!catalog) {
            statusHeadingEl.textContent = 'Loading emojis...';
            return;
        }

        const frag = document.createDocumentFragment();
        const trimmedSearch = searchQuery.trim();

        if (trimmedSearch) {
            const matches = searchEmojiEntries(catalog.entries, trimmedSearch, MAX_SEARCH_RESULTS);
            statusHeadingEl.textContent = matches.length > 0
                ? `Search results (${matches.length})`
                : 'Search results';

            if (matches.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'col-span-8 py-10 text-center text-xs text-[#6E6560] dark:text-[#C9C0B9]';
                empty.textContent = 'No emojis match your search.';
                frag.appendChild(empty);
            } else {
                for (let i = 0; i < matches.length; i++) {
                    const entry = matches[i];
                    const rendered = applySkinTone(entry[0], selectedSkinTone, entry[4] === 1);
                    frag.appendChild(createEmojiCellButton(rendered, `:${entry[1]}:`));
                }
            }

            gridContainerEl.appendChild(frag);
            return;
        }

        if (activeCategoryKey === 'recents') {
            const recents = loadRecentEmojis(getUserId());
            if (recents.length === 0) {
                statusHeadingEl.textContent = 'Frequently used';
                const hint = document.createElement('div');
                hint.className = 'col-span-8 px-2 pb-2 text-[11px] text-[#6E6560] dark:text-[#C9C0B9]';
                hint.textContent = 'No recent emojis yet — quick picks:';
                frag.appendChild(hint);

                for (let i = 0; i < FALLBACK_POPULAR_EMOJIS.length; i++) {
                    const emoji = FALLBACK_POPULAR_EMOJIS[i];
                    frag.appendChild(createEmojiCellButton(emoji, emoji));
                }
            } else {
                statusHeadingEl.textContent = 'Recently used';
                for (let i = 0; i < recents.length; i++) {
                    frag.appendChild(createEmojiCellButton(recents[i], recents[i]));
                }
            }

            gridContainerEl.appendChild(frag);
            return;
        }

        const catIndex = catalog.categories.findIndex((c) => c.id === activeCategoryKey);
        const safeIndex = catIndex >= 0 ? catIndex : 0;
        const catMeta = catalog.categories[safeIndex];
        const bucket = catalog.byCategory.get(safeIndex) || [];
        statusHeadingEl.textContent = catMeta ? catMeta.label : 'Emojis';

        for (let i = 0; i < bucket.length; i++) {
            const entry = bucket[i];
            const rendered = applySkinTone(entry[0], selectedSkinTone, entry[4] === 1);
            frag.appendChild(createEmojiCellButton(rendered, `:${entry[1]}:`));
        }

        gridContainerEl.appendChild(frag);
    }

    function ensurePanelBuilt() {
        if (panelEl || typeof document === 'undefined' || !document.body) return panelEl;

        const existing = typeof document.getElementById === 'function'
            ? document.getElementById('emoji-picker-panel')
            : null;
        if (existing && typeof existing.remove === 'function') {
            existing.remove();
        }

        panelEl = document.createElement('div');
        panelEl.id = 'emoji-picker-panel';
        panelEl.setAttribute('role', 'dialog');
        panelEl.setAttribute('aria-label', 'Emoji picker');
        panelEl.setAttribute('tabindex', '-1');
        panelEl.className = 'hidden fixed z-[60] bg-[#FFFFFF] dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] shadow-xl text-[#2B2725] dark:text-[#F0E9E2] flex flex-col overflow-hidden select-none';

        // Header: Search input + Skin tone button
        const headerEl = document.createElement('div');
        headerEl.className = 'p-2.5 pb-2 border-b border-[#E3DCD7] dark:border-[#514D48] flex flex-col gap-2 shrink-0';

        const searchRow = document.createElement('div');
        searchRow.className = 'flex items-center gap-2';

        const searchWrap = document.createElement('div');
        searchWrap.className = 'flex-1 flex items-center gap-1.5 bg-[#F8F6F3] dark:bg-[#302F2C] border border-[#E3DCD7] dark:border-[#514D48] rounded-xl px-2.5 py-1.5 focus-within:border-[#B3929D] focus-within:ring-1 focus-within:ring-[#B3929D]';

        const searchIcon = document.createElement('span');
        searchIcon.className = 'material-symbols-outlined text-[16px] text-[#6E6560] dark:text-[#C9C0B9] shrink-0';
        searchIcon.textContent = 'search';
        searchWrap.appendChild(searchIcon);

        searchInputEl = document.createElement('input');
        searchInputEl.id = 'emoji-picker-search';
        searchInputEl.type = 'search';
        searchInputEl.placeholder = 'Search emojis...';
        searchInputEl.setAttribute('aria-label', 'Search emojis');
        searchInputEl.autocomplete = 'off';
        searchInputEl.className = 'w-full bg-transparent text-xs text-[#2B2725] dark:text-[#F0E9E2] placeholder-[#6E6560] dark:placeholder-[#C9C0B9] focus:outline-none min-w-0';
        searchInputEl.addEventListener('input', () => {
            searchQuery = searchInputEl.value || '';
            if (clearSearchBtn && clearSearchBtn.classList) {
                if (searchQuery) clearSearchBtn.classList.remove('hidden');
                else clearSearchBtn.classList.add('hidden');
            }
            renderCategoryTabs();
            renderGrid();
            if (gridContainerEl) gridContainerEl.scrollTop = 0;
        });
        searchWrap.appendChild(searchInputEl);

        clearSearchBtn = document.createElement('button');
        clearSearchBtn.type = 'button';
        clearSearchBtn.setAttribute('aria-label', 'Clear emoji search');
        clearSearchBtn.className = 'hidden w-5 h-5 rounded-full flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#2B2725] dark:hover:text-[#F0E9E2] cursor-pointer shrink-0';
        const clearIcon = document.createElement('span');
        clearIcon.className = 'material-symbols-outlined text-[14px]';
        clearIcon.textContent = 'close';
        clearSearchBtn.appendChild(clearIcon);
        clearSearchBtn.addEventListener('mousedown', (e) => e.preventDefault());
        clearSearchBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            searchQuery = '';
            if (searchInputEl) searchInputEl.value = '';
            clearSearchBtn.classList.add('hidden');
            renderCategoryTabs();
            renderGrid();
        });
        searchWrap.appendChild(clearSearchBtn);
        searchRow.appendChild(searchWrap);

        // Skin tone toggle button
        skinToneToggleBtn = document.createElement('button');
        skinToneToggleBtn.type = 'button';
        skinToneToggleBtn.setAttribute('data-emoji-skintone-toggle', 'true');
        skinToneToggleBtn.setAttribute('aria-label', 'Select default skin tone');
        skinToneToggleBtn.setAttribute('aria-expanded', 'false');
        skinToneToggleBtn.title = 'Select skin tone';
        skinToneToggleBtn.className = 'w-8 h-8 rounded-xl border border-[#E3DCD7] dark:border-[#514D48] bg-[#F8F6F3] dark:bg-[#302F2C] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] flex items-center justify-center shrink-0 cursor-pointer transition-colors';

        skinToneSwatchDot = document.createElement('span');
        skinToneSwatchDot.className = 'w-4 h-4 rounded-full border border-black/20 inline-block';
        if (skinToneSwatchDot.style) skinToneSwatchDot.style.backgroundColor = '#F8D147';
        skinToneToggleBtn.appendChild(skinToneSwatchDot);

        skinToneToggleBtn.addEventListener('mousedown', (e) => e.preventDefault());
        skinToneToggleBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            skinToneMenuOpen = !skinToneMenuOpen;
            syncSkinToneButton();
        });
        searchRow.appendChild(skinToneToggleBtn);
        headerEl.appendChild(searchRow);

        // Skin tone swatch bar (expandable)
        skinToneMenuEl = document.createElement('div');
        skinToneMenuEl.setAttribute('role', 'group');
        skinToneMenuEl.setAttribute('aria-label', 'Skin tone options');
        skinToneMenuEl.className = 'hidden flex items-center justify-between px-1.5 py-1 rounded-xl bg-[#F8F6F3] dark:bg-[#302F2C] border border-[#E3DCD7] dark:border-[#514D48]';
        headerEl.appendChild(skinToneMenuEl);

        panelEl.appendChild(headerEl);

        // Category tab strip
        tabsBarEl = document.createElement('div');
        tabsBarEl.setAttribute('role', 'tablist');
        tabsBarEl.setAttribute('aria-label', 'Emoji categories');
        tabsBarEl.className = 'flex items-center border-b border-[#E3DCD7] dark:border-[#514D48] bg-[#F8F6F3]/60 dark:bg-[#302F2C]/60 px-1 shrink-0';
        panelEl.appendChild(tabsBarEl);

        // Active section label
        statusHeadingEl = document.createElement('div');
        statusHeadingEl.className = 'px-3 pt-2 pb-1 text-[11px] font-semibold uppercase tracking-wider text-[#6E6560] dark:text-[#C9C0B9] shrink-0';
        statusHeadingEl.textContent = 'Emojis';
        panelEl.appendChild(statusHeadingEl);

        // Scrollable 8-column grid
        gridContainerEl = document.createElement('div');
        gridContainerEl.setAttribute('data-emoji-grid', 'true');
        gridContainerEl.className = 'grid grid-cols-8 gap-0.5 px-2 pb-2.5 flex-1 min-h-0 overflow-y-auto custom-scroll content-start';
        panelEl.appendChild(gridContainerEl);

        document.body.appendChild(panelEl);
        return panelEl;
    }

    function positionPanel() {
        if (!panelEl || !activeTriggerEl || !isOpenState) return;
        const { vw, vh } = getViewportSize();
        const mobile = vw < MOBILE_BREAKPOINT_PX;

        if (mobile && activeSurface === 'composer') {
            const composerEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
                ? document.getElementById('active-conversation-composer')
                : null;
            const composerRect = composerEl && typeof composerEl.getBoundingClientRect === 'function'
                ? composerEl.getBoundingClientRect()
                : null;
            const bottomOffset = composerRect ? Math.max(0, Math.round(vh - composerRect.top)) : 64;
            const maxPanelHeight = Math.max(220, Math.min(320, Math.floor(vh - bottomOffset - 16)));

            panelEl.className = 'fixed z-[60] bg-[#FFFFFF] dark:bg-[#3A3835] border-t border-x-0 border-b-0 border-[#E3DCD7] dark:border-[#514D48] rounded-t-2xl shadow-xl text-[#2B2725] dark:text-[#F0E9E2] flex flex-col overflow-hidden select-none';
            if (panelEl.style) {
                panelEl.style.left = '0px';
                panelEl.style.right = '0px';
                panelEl.style.top = 'auto';
                panelEl.style.bottom = `${bottomOffset}px`;
                panelEl.style.width = '100%';
                panelEl.style.height = `${maxPanelHeight}px`;
            }
            return;
        }

        const margin = 8;
        const panelWidth = Math.min(352, Math.max(260, vw - margin * 2));
        const panelHeight = Math.min(392, Math.max(240, vh - margin * 2));

        panelEl.className = 'fixed z-[60] bg-[#FFFFFF] dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] rounded-2xl shadow-xl text-[#2B2725] dark:text-[#F0E9E2] flex flex-col overflow-hidden select-none';

        const triggerRect = typeof activeTriggerEl.getBoundingClientRect === 'function'
            ? activeTriggerEl.getBoundingClientRect()
            : { left: margin, right: margin + 32, top: vh - 80, bottom: vh - 48, width: 32, height: 32 };

        let left = triggerRect.right - panelWidth;
        const maxLeft = Math.max(margin, vw - panelWidth - margin);
        left = Math.max(margin, Math.min(left, maxLeft));

        let top = triggerRect.top - panelHeight - 8;
        if (top < margin) {
            const belowTop = triggerRect.bottom + 8;
            if (belowTop + panelHeight <= vh - margin) {
                top = belowTop;
            } else {
                top = Math.max(margin, Math.min(top, vh - panelHeight - margin));
            }
        }

        if (panelEl.style) {
            panelEl.style.left = `${Math.round(left)}px`;
            panelEl.style.right = 'auto';
            panelEl.style.top = `${Math.round(top)}px`;
            panelEl.style.bottom = 'auto';
            panelEl.style.width = `${Math.round(panelWidth)}px`;
            panelEl.style.height = `${Math.round(panelHeight)}px`;
        }
    }

    function close() {
        openRequestToken++;
        if (activeTriggerEl) {
            updateTriggerAria(activeTriggerEl, false);
        }
        isOpenState = false;
        skinToneMenuOpen = false;
        activeTriggerEl = null;
        activeTargetEl = null;
        if (panelEl && panelEl.classList) {
            panelEl.classList.add('hidden');
        }
        syncSkinToneButton();
    }

    const onOutsidePointerDown = (e) => {
        if (!isOpenState || !panelEl) return;
        const target = e.target;
        if (panelEl.contains(target)) return;
        if (activeTriggerEl && (target === activeTriggerEl || (typeof activeTriggerEl.contains === 'function' && activeTriggerEl.contains(target)))) {
            return;
        }
        close();
    };

    const onDocumentKeyDown = (e) => {
        if (!isOpenState) return;
        if (e.key === 'Escape') {
            e.preventDefault();
            if (typeof e.stopImmediatePropagation === 'function') {
                e.stopImmediatePropagation();
            } else if (typeof e.stopPropagation === 'function') {
                e.stopPropagation();
            }
            const prevTrigger = activeTriggerEl;
            close();
            if (prevTrigger && typeof prevTrigger.focus === 'function') {
                prevTrigger.focus();
            }
        }
    };

    const onViewportResize = () => {
        if (isOpenState) {
            positionPanel();
        }
    };

    function bindGlobalListeners() {
        if (globalListenersBound || typeof document === 'undefined') return;
        // Capture-phase keydown so Escape closes emoji picker before closing Edit Message modal
        document.addEventListener('keydown', onDocumentKeyDown, true);
        document.addEventListener('mousedown', onOutsidePointerDown);
        if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
            window.addEventListener('resize', onViewportResize);
        }
        globalListenersBound = true;
    }

    function unbindGlobalListeners() {
        if (!globalListenersBound || typeof document === 'undefined') return;
        document.removeEventListener('keydown', onDocumentKeyDown, true);
        document.removeEventListener('mousedown', onOutsidePointerDown);
        if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
            window.removeEventListener('resize', onViewportResize);
        }
        globalListenersBound = false;
    }

    /**
     * Opens or toggles the emoji picker anchored to triggerEl and inserting into targetEl.
     * @param {HTMLElement} triggerEl
     * @param {HTMLTextAreaElement} targetEl
     * @param {{ surface?: 'composer'|'edit-modal' }} [openOptions]
     * @returns {Promise<boolean>} True if now open
     */
    async function toggle(triggerEl, targetEl, openOptions = {}) {
        if (!triggerEl || !targetEl || targetEl.disabled) {
            close();
            return false;
        }

        if (isOpenState && activeTriggerEl === triggerEl && activeTargetEl === targetEl) {
            close();
            return false;
        }

        if (activeTriggerEl && activeTriggerEl !== triggerEl) {
            updateTriggerAria(activeTriggerEl, false);
        }

        const myToken = ++openRequestToken;
        activeTriggerEl = triggerEl;
        activeTargetEl = targetEl;
        activeSurface = openOptions.surface || 'composer';
        isOpenState = true;
        skinToneMenuOpen = false;
        searchQuery = '';

        selectedSkinTone = loadPreferredSkinTone(getUserId());
        const recents = loadRecentEmojis(getUserId());
        activeCategoryKey = recents.length > 0 ? 'recents' : 'smileys';

        ensurePanelBuilt();
        bindGlobalListeners();
        if (searchInputEl) searchInputEl.value = '';
        if (clearSearchBtn && clearSearchBtn.classList) clearSearchBtn.classList.add('hidden');

        updateTriggerAria(triggerEl, true);
        if (panelEl && panelEl.classList) {
            panelEl.classList.remove('hidden');
        }
        positionPanel();
        renderGrid();

        try {
            const loaded = await loadCatalog();
            if (myToken !== openRequestToken || !isOpenState) {
                return false;
            }
            catalog = loaded;
            syncSkinToneButton();
            renderSkinToneMenu();
            renderCategoryTabs();
            renderGrid();
            positionPanel();
            return true;
        } catch (err) {
            console.warn('[EmojiPicker] Failed to load emoji catalog:', err);
            if (myToken === openRequestToken && statusHeadingEl) {
                statusHeadingEl.textContent = 'Could not load emojis.';
            }
            return false;
        }
    }

    function destroy() {
        close();
        unbindGlobalListeners();
        if (panelEl && typeof panelEl.remove === 'function') {
            panelEl.remove();
        }
        panelEl = null;
        searchInputEl = null;
        clearSearchBtn = null;
        skinToneToggleBtn = null;
        skinToneSwatchDot = null;
        skinToneMenuEl = null;
        tabsBarEl = null;
        gridContainerEl = null;
        statusHeadingEl = null;
    }

    return {
        toggle,
        close,
        resetForSwitch: close,
        isOpen: () => isOpenState,
        reposition: positionPanel,
        destroy
    };
}
