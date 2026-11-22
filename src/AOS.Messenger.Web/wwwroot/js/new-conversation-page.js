/**
 * AOS Messenger - New Conversation Modal & User Discovery Page
 *
 * Implements debounced user search against /api/users/search, state transitions,
 * keyboard accessibility, and safe DOM rendering of user cards.
 *
 * Invariants:
 * - Safe DOM tree construction using document.createElement exclusively.
 * - Zero HTML string injection into live DOM.
 * - All text content bound strictly via textContent or createTextNode.
 */

import { getInitials } from './message-renderer.js';

(() => {
    'use strict';

    let debounceTimer = null;
    let currentAbortController = null;

    /**
     * Resolves a state container element by semantic [data-search-state] attribute or id.
     *
     * @param {string} s
     * @returns {HTMLElement|null}
     */
    function getStateElement(s) {
        return document.querySelector(`[data-search-state="${s}"]`) || document.getElementById('state-' + s);
    }

    /**
     * Switches between visual presentation states in the modal:
     * - 'results': Search matches container
     * - 'empty': Initial blank state
     * - 'loading': Skeleton pulse indicator
     * - 'no-results': Zero results warning
     *
     * @param {string} stateName
     */
    function switchState(stateName) {
        const states = ['results', 'empty', 'loading', 'no-results'];

        states.forEach(s => {
            const el = getStateElement(s);
            if (el) {
                if (s === stateName) {
                    el.classList.remove('hidden');
                    el.classList.add('flex');
                } else {
                    el.classList.add('hidden');
                    el.classList.remove('flex');
                }
            }
        });
    }

    /**
     * Renders user search results safely into the DOM without HTML string parsing.
     *
     * @param {Array} users
     * @param {HTMLElement} resultsContainer
     * @param {string} token
     */
    function renderUserResults(users, resultsContainer, token) {
        // Clear previous results safely
        while (resultsContainer.firstChild) {
            resultsContainer.removeChild(resultsContainer.firstChild);
        }

        const headerDiv = document.createElement('div');
        headerDiv.className = 'pt-space-xs flex items-center justify-between font-label-sm text-label-sm text-outline';
        const countSpan = document.createElement('span');
        countSpan.textContent = `Matches (${users.length})`;
        headerDiv.appendChild(countSpan);
        resultsContainer.appendChild(headerDiv);

        for (const user of users) {
            const initials = getInitials(user.displayName, user.userName);

            const card = document.createElement('div');
            card.className = 'group p-space-md rounded-xl bg-surface-container-lowest hover:bg-surface-container transition-all flex items-center justify-between cursor-pointer shadow-xs border border-transparent hover:border-[#E3DCD7] dark:hover:border-[#5B5752]';

            const infoContainer = document.createElement('div');
            infoContainer.className = 'flex items-center gap-space-md min-w-0';

            const avatarWrap = document.createElement('div');
            avatarWrap.className = 'relative flex-shrink-0';
            const avatar = document.createElement('div');
            avatar.className = 'w-12 h-12 rounded-full bg-secondary-container text-on-secondary-container font-headline-sm text-headline-sm flex items-center justify-center font-bold';
            avatar.textContent = initials;
            avatarWrap.appendChild(avatar);
            infoContainer.appendChild(avatarWrap);

            const textWrap = document.createElement('div');
            textWrap.className = 'min-w-0';

            const nameRow = document.createElement('div');
            nameRow.className = 'flex items-center gap-space-xs';
            const nameSpan = document.createElement('span');
            nameSpan.className = 'font-headline-sm text-headline-sm text-on-surface truncate group-hover:text-primary transition-colors';
            nameSpan.textContent = user.displayName || '';
            nameRow.appendChild(nameSpan);
            textWrap.appendChild(nameRow);

            const handleDiv = document.createElement('div');
            handleDiv.className = 'font-label-md text-label-md text-secondary';
            handleDiv.textContent = '@' + (user.userName || '');
            textWrap.appendChild(handleDiv);

            if (user.bio) {
                const bioDiv = document.createElement('div');
                bioDiv.className = 'font-body-sm text-body-sm text-outline truncate';
                bioDiv.textContent = user.bio;
                textWrap.appendChild(bioDiv);
            }
            infoContainer.appendChild(textWrap);
            card.appendChild(infoContainer);

            const form = document.createElement('form');
            form.method = 'post';
            form.action = '/Chat/Start';
            form.className = 'inline m-0 p-0';

            const tokenInput = document.createElement('input');
            tokenInput.type = 'hidden';
            tokenInput.name = '__RequestVerificationToken';
            tokenInput.value = token;
            form.appendChild(tokenInput);

            const targetInput = document.createElement('input');
            targetInput.type = 'hidden';
            targetInput.name = 'targetUserId';
            targetInput.value = user.id || '';
            form.appendChild(targetInput);

            const submitBtn = document.createElement('button');
            submitBtn.type = 'submit';
            submitBtn.className = 'flex-shrink-0 px-space-md py-2 rounded-xl bg-primary text-on-primary hover:bg-on-primary-container font-label-md text-label-md flex items-center gap-1.5 shadow-xs transition-all opacity-90 group-hover:opacity-100 group-hover:scale-[1.02]';

            const btnText = document.createElement('span');
            btnText.textContent = 'Start chat';
            submitBtn.appendChild(btnText);

            const btnIcon = document.createElement('span');
            btnIcon.className = 'material-symbols-outlined text-[16px]';
            btnIcon.textContent = 'arrow_forward';
            submitBtn.appendChild(btnIcon);

            form.appendChild(submitBtn);
            card.appendChild(form);

            resultsContainer.appendChild(card);
        }
    }

    /**
     * Executes the debounced search request against the authoritative discovery API.
     *
     * @param {string} query
     */
    async function performSearch(query) {
        const trimmed = (query || '').trim();
        if (!trimmed) {
            switchState('empty');
            return;
        }

        if (currentAbortController) {
            currentAbortController.abort();
        }
        currentAbortController = new AbortController();

        switchState('loading');

        try {
            const response = await fetch('/api/users/search?query=' + encodeURIComponent(trimmed), {
                signal: currentAbortController.signal,
                headers: {
                    'Accept': 'application/json'
                }
            });

            if (!response.ok) {
                const termEls = document.querySelectorAll('[data-no-results-query], #query-term');
                termEls.forEach(el => { el.textContent = trimmed; });
                switchState('no-results');
                return;
            }

            const users = await response.json();
            const resultsContainer = getStateElement('results');

            if (!users || users.length === 0) {
                const termEls = document.querySelectorAll('[data-no-results-query], #query-term');
                termEls.forEach(el => { el.textContent = trimmed; });
                switchState('no-results');
                return;
            }

            const token = document.querySelector('#antiforgery-token-container input[name="__RequestVerificationToken"], input[name="__RequestVerificationToken"]')?.value || '';
            if (resultsContainer) {
                renderUserResults(users, resultsContainer, token);
            }
            switchState('results');
        } catch (err) {
            if (err.name === 'AbortError') return;
            console.error('User search error:', err);
            const termEls = document.querySelectorAll('[data-no-results-query], #query-term');
            termEls.forEach(el => { el.textContent = trimmed; });
            switchState('no-results');
        }
    }

    const searchInput = document.getElementById('user-search-input');
    if (searchInput) {
        searchInput.addEventListener('input', (e) => {
            clearTimeout(debounceTimer);
            const val = e.target.value;
            debounceTimer = setTimeout(() => {
                performSearch(val);
            }, 300);
        });
    }

    const clearSearchBtn = document.querySelector('[data-clear-search], #clear-search-btn');
    if (clearSearchBtn) {
        clearSearchBtn.addEventListener('click', () => {
            clearTimeout(debounceTimer);
            if (searchInput) {
                searchInput.value = '';
                searchInput.focus();
            }
            switchState('empty');
        });
    }

    const clearQueryBtn = document.querySelector('[data-clear-query], #clear-query-btn');
    if (clearQueryBtn) {
        clearQueryBtn.addEventListener('click', () => {
            clearTimeout(debounceTimer);
            if (searchInput) {
                searchInput.value = '';
                searchInput.focus();
            }
            switchState('empty');
        });
    }

    // Dismiss modal on ESC
    window.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            const btn = document.querySelector('[data-modal-close], #modal-close-btn');
            if (btn) btn.click();
        }
    });
})();
