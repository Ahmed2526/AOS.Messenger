/**
 * AOS Messenger - Profile Page Controller
 *
 * Appearance theme management is delegated to the central theme.js module.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 */

import { syncThemeUI } from './theme.js';

// Ensure profile appearance buttons are synchronized with the active theme
syncThemeUI();
