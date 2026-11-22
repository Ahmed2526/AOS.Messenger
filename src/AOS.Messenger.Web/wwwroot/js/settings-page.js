/**
 * AOS Messenger - Settings Page Controller
 *
 * Implements real-time section filtering search.
 * Appearance theme management is delegated to the central theme.js module.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 */

import { syncThemeUI } from './theme.js';

// 1. Live in-page settings search filter
const searchInput = document.getElementById('settingsSearch');
if (searchInput) {
    searchInput.addEventListener('input', (e) => {
        const query = (e.target.value || '').toLowerCase().trim();
        const sections = document.querySelectorAll('main section');

        sections.forEach(section => {
            const text = (section.textContent || '').toLowerCase();
            if (text.includes(query)) {
                section.style.display = 'flex';
            } else {
                section.style.display = 'none';
            }
        });
    });
}

// Ensure theme toggle controls and checkmarks are synchronized
syncThemeUI();
