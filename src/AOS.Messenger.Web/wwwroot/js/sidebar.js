/**
 * AOS Messenger - Conversation Sidebar Controller
 *
 * Implements client-side conversation list search filtering by contact name and username handle.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 */

(() => {
    'use strict';

    function initSidebar() {
        const searchInput = document.getElementById('conversationSearchInput');
        if (searchInput) {
            searchInput.addEventListener('input', function (e) {
                const query = (e.target.value || '').toLowerCase().trim();
                const items = document.querySelectorAll('.conversation-item');
                items.forEach(item => {
                    const text = (item.getAttribute('data-search') || '').toLowerCase();
                    item.style.display = text.includes(query) ? '' : 'none';
                });
            });
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initSidebar);
    } else {
        initSidebar();
    }
})();
