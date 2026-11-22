/**
 * AOS Messenger - Dark Theme Empty State Page Controller
 *
 * Ensures dark theme styles and header icon state are applied when viewing Dark Theme preview.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 */

(() => {
    'use strict';

    if (!document.documentElement.classList.contains('dark')) {
        document.documentElement.classList.add('dark');
        const icon = document.getElementById('themeToggleIcon');
        if (icon) {
            icon.textContent = 'light_mode';
        }
    }
})();
