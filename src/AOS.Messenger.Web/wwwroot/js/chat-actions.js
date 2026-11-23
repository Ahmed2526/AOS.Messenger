/**
 * AOS Messenger - Chat Actions Overflow Menu Controller
 *
 * Implements mobile conversation action dropdown menu toggling, outside-click dismissal,
 * and keyboard Escape accessibility.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively (classList, setAttribute).
 * - Zero HTML string injection into DOM.
 * - Zero inline styles.
 */

export function closeChatActionsMenu() {
    if (typeof document === 'undefined') return;
    const toggleBtn = document.querySelector('[data-chat-actions-toggle]');
    const menuContainer = document.querySelector('[data-chat-actions-menu]');
    if (menuContainer && !menuContainer.classList.contains('hidden')) {
        menuContainer.classList.add('hidden');
    }
    if (toggleBtn) {
        toggleBtn.setAttribute('aria-expanded', 'false');
    }
}

export function initChatActions() {
    const toggleBtn = document.querySelector('[data-chat-actions-toggle]');
    const menuContainer = document.querySelector('[data-chat-actions-menu]');

    if (!toggleBtn || !menuContainer) {
        return;
    }

    if (toggleBtn.dataset.chatActionsBound === 'true') {
        return;
    }
    toggleBtn.dataset.chatActionsBound = 'true';

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

    document.addEventListener('click', (e) => {
        if (!menuContainer.contains(e.target) && !toggleBtn.contains(e.target)) {
            closeMenu();
        }
    });

    window.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            closeMenu();
        }
    });
}

if (typeof document !== 'undefined') {
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initChatActions);
    } else {
        initChatActions();
    }
}
