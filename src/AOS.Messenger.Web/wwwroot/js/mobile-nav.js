/**
 * AOS Messenger - Mobile Navigation Controller
 *
 * Implements mobile navigation drawer / dropdown toggling, outside-click dismissal,
 * and keyboard Escape accessibility.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively (classList, setAttribute).
 * - Zero HTML string injection into DOM.
 * - Zero inline styles.
 */

export function initMobileNav() {
    const toggleBtn = document.querySelector('[data-mobile-nav-toggle]');
    const menuContainer = document.querySelector('[data-mobile-nav-menu]');

    if (!toggleBtn || !menuContainer) {
        return;
    }

    // Prevent duplicate binding
    if (toggleBtn.dataset.mobileNavBound === 'true') {
        return;
    }
    toggleBtn.dataset.mobileNavBound = 'true';

    function openMenu() {
        menuContainer.classList.remove('hidden');
        toggleBtn.setAttribute('aria-expanded', 'true');
    }

    function closeMenu() {
        if (!menuContainer.classList.contains('hidden')) {
            menuContainer.classList.add('hidden');
            toggleBtn.setAttribute('aria-expanded', 'false');
        }
    }

    function toggleMenu() {
        const isExpanded = toggleBtn.getAttribute('aria-expanded') === 'true';
        if (isExpanded) {
            closeMenu();
        } else {
            openMenu();
        }
    }

    toggleBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        toggleMenu();
    });

    // Close when clicking outside both the toggle button and the menu container
    document.addEventListener('click', (e) => {
        if (!menuContainer.contains(e.target) && !toggleBtn.contains(e.target)) {
            closeMenu();
        }
    });

    // Close on Escape key press
    window.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            closeMenu();
        }
    });
}

if (typeof document !== 'undefined') {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initMobileNav);
    } else {
        initMobileNav();
    }
}
