/**
 * AOS Messenger - Archived Conversations Page Controller
 *
 * Implements real-time client-side search filtering across archived conversations.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 */

import { hydrateAllAvatars } from './avatar-loader.js';

(() => {
    'use strict';

    const archivedList = document.getElementById('archivedList');
    if (archivedList) {
        hydrateAllAvatars(archivedList);
    }

    const searchInput = document.getElementById('archivedSearchInput');
    if (searchInput) {
        searchInput.addEventListener('input', function (e) {
            const query = (e.target.value || '').toLowerCase().trim();
            const items = document.querySelectorAll('#archivedList > div.group');
            items.forEach(item => {
                const text = (item.textContent || '').toLowerCase();
                item.style.display = text.includes(query) ? '' : 'none';
            });
        });
    }
})();
