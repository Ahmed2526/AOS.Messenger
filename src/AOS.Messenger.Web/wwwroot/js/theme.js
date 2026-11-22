/**
 * AOS Messenger - Appearance & Theme ES Module
 *
 * Handles client-side theme selection (Light / Dark / System), DOM class toggling,
 * persistence in localStorage, and synchronization of theme icons, aria-pressed attributes,
 * and page controls across desktop, mobile, settings, and profile views.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively (classList, setAttribute, textContent).
 * - Zero HTML string injection into DOM.
 * - Strictly compliant with Content-Security-Policy (script-src 'self').
 */

const THEME_STORAGE_KEY = 'aos-messenger-theme';

/**
 * Returns the currently stored theme preference ('light', 'dark', or 'system').
 * Defaults to 'system' if not explicitly stored.
 */
export function getStoredTheme() {
    try {
        const saved = localStorage.getItem(THEME_STORAGE_KEY);
        if (saved === 'light' || saved === 'dark' || saved === 'system') {
            return saved;
        }
    } catch (_) {
        // Fallback for restricted browsing contexts
    }
    return 'system';
}

/**
 * Returns true if the dark theme class is currently active on the root <html> element.
 */
export function isDarkModeActive() {
    return document.documentElement.classList.contains('dark');
}

/**
 * Updates visual icons, aria-pressed attributes, and page-specific theme controls.
 */
export function syncThemeUI() {
    const currentTheme = getStoredTheme();
    const isDark = isDarkModeActive();

    // 1. Update theme toggle icons (Header / Desktop / Mobile)
    const iconElements = document.querySelectorAll('#themeToggleIcon, [data-theme-icon]');
    iconElements.forEach(icon => {
        icon.textContent = isDark ? 'light_mode' : 'dark_mode';
    });

    // 2. Update aria-pressed attributes on all data-theme-toggle elements
    const toggleButtons = document.querySelectorAll('[data-theme-toggle], #themeToggleBtn');
    toggleButtons.forEach(btn => {
        const target = btn.getAttribute('data-theme-toggle');
        if (!target || target === 'toggle') {
            btn.setAttribute('aria-pressed', isDark ? 'true' : 'false');
        } else {
            btn.setAttribute('aria-pressed', currentTheme === target ? 'true' : 'false');
        }
    });

    // 3. Sync Settings View controls (if present in DOM)
    const lightSettingBtn = document.getElementById('theme-btn-light');
    const darkSettingBtn = document.getElementById('theme-btn-dark');
    const systemSettingBtn = document.getElementById('theme-btn-system');
    const checkLight = document.getElementById('theme-check-light');
    const checkDark = document.getElementById('theme-check-dark');
    const checkSystem = document.getElementById('theme-check-system');

    if (lightSettingBtn && darkSettingBtn && systemSettingBtn) {
        [lightSettingBtn, darkSettingBtn, systemSettingBtn].forEach(btn => {
            btn.classList.remove('bg-surface-container-high', 'shadow-xs');
            btn.classList.add('bg-surface-container-low');
        });
        [checkLight, checkDark, checkSystem].forEach(chk => {
            if (chk) chk.classList.add('hidden');
        });

        if (currentTheme === 'light') {
            lightSettingBtn.classList.add('bg-surface-container-high', 'shadow-xs');
            lightSettingBtn.classList.remove('bg-surface-container-low');
            if (checkLight) checkLight.classList.remove('hidden');
        } else if (currentTheme === 'dark') {
            darkSettingBtn.classList.add('bg-surface-container-high', 'shadow-xs');
            darkSettingBtn.classList.remove('bg-surface-container-low');
            if (checkDark) checkDark.classList.remove('hidden');
        } else {
            systemSettingBtn.classList.add('bg-surface-container-high', 'shadow-xs');
            systemSettingBtn.classList.remove('bg-surface-container-low');
            if (checkSystem) checkSystem.classList.remove('hidden');
        }
    }

    // 4. Sync Profile View controls (if present in DOM)
    const profileLightBtn = document.getElementById('profileThemeLightBtn');
    const profileDarkBtn = document.getElementById('profileThemeDarkBtn');
    const profileSystemBtn = document.getElementById('profileThemeSystemBtn');

    if (profileLightBtn && profileDarkBtn && profileSystemBtn) {
        const activeClass = 'px-3 py-1 rounded-md text-xs font-semibold bg-[#FFFFFF] dark:bg-[#45433F] text-[#2B2725] dark:text-[#EEE7DF] shadow-xs cursor-pointer';
        const inactiveClass = 'px-3 py-1 rounded-md text-xs font-medium text-[#6E6560] dark:text-[#C8C0BA] hover:text-[#2B2725] dark:hover:text-[#EEE7DF] cursor-pointer';

        profileLightBtn.className = currentTheme === 'light' ? activeClass : inactiveClass;
        profileDarkBtn.className = currentTheme === 'dark' ? activeClass : inactiveClass;
        profileSystemBtn.className = currentTheme === 'system' ? activeClass : inactiveClass;
    }

    // 5. Broadcast theme change event for any interested listeners
    try {
        window.dispatchEvent(new CustomEvent('themechanged', {
            detail: { theme: currentTheme, isDark }
        }));
    } catch (_) {
        // Fallback for older environments
    }
}

/**
 * Applies a given theme ('light', 'dark', 'system', or 'toggle').
 */
export function applyTheme(mode) {
    let target = mode;

    if (!target || target === 'toggle') {
        target = isDarkModeActive() ? 'light' : 'dark';
    }

    if (target === 'dark') {
        document.documentElement.classList.add('dark');
        try {
            localStorage.setItem(THEME_STORAGE_KEY, 'dark');
        } catch (_) {}
    } else if (target === 'light') {
        document.documentElement.classList.remove('dark');
        try {
            localStorage.setItem(THEME_STORAGE_KEY, 'light');
        } catch (_) {}
    } else if (target === 'system') {
        try {
            localStorage.setItem(THEME_STORAGE_KEY, 'system');
        } catch (_) {}

        const prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
        if (prefersDark) {
            document.documentElement.classList.add('dark');
        } else {
            document.documentElement.classList.remove('dark');
        }
    }

    syncThemeUI();
}

/**
 * Initializes listeners on all [data-theme-toggle] buttons and the OS media query.
 */
export function initTheme() {
    // Listen to all buttons with data-theme-toggle
    const buttons = document.querySelectorAll('[data-theme-toggle], #themeToggleBtn');
    buttons.forEach(btn => {
        // Prevent duplicate listener attachments
        if (btn.dataset.themeBound === 'true') return;
        btn.dataset.themeBound = 'true';

        btn.addEventListener('click', (e) => {
            e.preventDefault();
            const target = btn.getAttribute('data-theme-toggle') || 'toggle';
            applyTheme(target);
        });
    });

    // Listen for OS system theme changes
    if (window.matchMedia) {
        try {
            const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
            mediaQuery.addEventListener('change', () => {
                const stored = getStoredTheme();
                if (stored === 'system') {
                    applyTheme('system');
                }
            });
        } catch (_) {}
    }

    // Sync initial UI state with the already applied document theme
    syncThemeUI();
}

// Auto-initialize when the DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initTheme);
} else {
    initTheme();
}
