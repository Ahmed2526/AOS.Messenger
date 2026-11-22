/**
 * AOS Messenger - Initial Theme Setter (Pre-Paint)
 *
 * Runs synchronously in <head> before rendering to prevent theme flash (FOUC).
 * Invariants:
 * - Zero inline JS (external script file strictly compliant with script-src 'self').
 * - Zero dependencies.
 * - Safe DOM manipulation (only toggles class on documentElement).
 */
(function () {
    'use strict';
    try {
        var storageKey = 'aos-messenger-theme';
        var savedTheme = localStorage.getItem(storageKey);
        var prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
        var isDark = savedTheme === 'dark' || ((!savedTheme || savedTheme === 'system') && prefersDark);
        if (isDark) {
            document.documentElement.classList.add('dark');
        } else {
            document.documentElement.classList.remove('dark');
        }
    } catch (_) {
        // Safe fallback for restricted browsing contexts (e.g., storage access denied)
    }
})();
