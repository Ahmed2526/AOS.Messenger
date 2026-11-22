/**
 * AOS Messenger - Mobile Chat View Controller
 *
 * Implements mobile-specific touch interactions, media action sheet toggling,
 * and backdrop dismiss handlers.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 */

(() => {
    'use strict';

    function initMobilePage() {
        const attachBtn = document.querySelector('footer button[aria-label="Attach media"]');
        const sheet = document.getElementById('mediaSheet');
        if (attachBtn && sheet) {
            attachBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                sheet.classList.toggle('hidden');
            });
            document.addEventListener('click', (e) => {
                if (!sheet.contains(e.target) && e.target !== attachBtn) {
                    sheet.classList.add('hidden');
                }
            });
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initMobilePage);
    } else {
        initMobilePage();
    }
})();
