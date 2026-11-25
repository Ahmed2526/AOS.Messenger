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
import { removeAvatar } from './api-client.js';
import {
    hydrateAvatarSlot,
    bindAvatarLightboxDelegation,
    updateUserAvatarAcrossDom
} from './avatar-loader.js';
import { createAvatarCropper } from './avatar-cropper.js';

syncThemeUI();

const heroAvatar = document.getElementById('profile-hero-avatar');
const uploadBtn = document.getElementById('profile-avatar-upload-btn');
const removeBtn = document.getElementById('profile-avatar-remove-btn');
const fileInput = document.getElementById('profile-avatar-file-input');
const statusEl = document.getElementById('profile-avatar-status');

function setStatus(message, isError = false) {
    if (!statusEl) return;
    if (!message) {
        statusEl.textContent = '';
        statusEl.classList.add('hidden');
        return;
    }
    statusEl.textContent = message;
    statusEl.className = isError
        ? 'relative z-10 mt-3 text-xs font-medium text-red-600 dark:text-red-400'
        : 'relative z-10 mt-3 text-xs font-medium text-[#6F9473] dark:text-[#82B386]';
}

if (heroAvatar) {
    hydrateAvatarSlot(heroAvatar);
    bindAvatarLightboxDelegation(document);
}

const currentUserId = heroAvatar ? (heroAvatar.getAttribute('data-avatar-user-id') || '') : '';
const currentDisplayName = heroAvatar ? (heroAvatar.getAttribute('data-avatar-name') || '') : '';

const cropper = createAvatarCropper({
    onUploaded: async (result) => {
        const version = result?.avatarVersion ?? result?.AvatarVersion ?? '';
        if (currentUserId && version) {
            await updateUserAvatarAcrossDom(currentUserId, version, currentDisplayName);
        }
        if (removeBtn) {
            removeBtn.classList.remove('hidden');
            removeBtn.classList.add('inline-flex');
        }
        setStatus('Profile photo updated.');
    },
    onError: (msg) => {
        setStatus(msg || 'Failed to update profile photo.', true);
    }
});

if (uploadBtn && fileInput) {
    uploadBtn.addEventListener('click', () => {
        setStatus('');
        fileInput.value = '';
        fileInput.click();
    });

    fileInput.addEventListener('change', async () => {
        const file = fileInput.files && fileInput.files[0];
        if (!file) return;
        await cropper.open(file);
    });
}

if (removeBtn) {
    removeBtn.addEventListener('click', async () => {
        setStatus('');
        removeBtn.disabled = true;
        try {
            await removeAvatar();
            if (currentUserId) {
                await updateUserAvatarAcrossDom(currentUserId, null, currentDisplayName);
            }
            removeBtn.classList.add('hidden');
            removeBtn.classList.remove('inline-flex');
            setStatus('Profile photo removed.');
        } catch (err) {
            setStatus(err?.message || 'Failed to remove profile photo.', true);
        } finally {
            removeBtn.disabled = false;
        }
    });
}
