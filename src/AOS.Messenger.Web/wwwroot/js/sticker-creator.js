/**
 * AOS Messenger - Custom Sticker Creator Modal (Static Cutout Canvas + Animated GIF/WebP Upload)
 *
 * Security & Concurrency Invariants:
 * - Accepts up to 15 MB local static images for client-side canvas cutout, enforcing <= 5 MB on exported Blob.
 * - Always completes <canvas>.toBlob() BEFORE calling /api/stickers/uploads/initiate so exact Content-Length
 *   and Content-Type match the R2 presigned PUT signature (Red-Team #18).
 * - Direct XHR PUT uses xhr.withCredentials = false (never sends cookies to R2).
 * - Deterministically closes ImageBitmap and revokes blob: URLs on close/cancel/logout (Red-Team #15).
 * - Zero innerHTML, zero inline event handlers.
 */

import {
    createStickerPack as apiCreateStickerPack,
    initiateStickerUpload as apiInitiateStickerUpload,
    completeStickerUpload as apiCompleteStickerUpload,
    cancelStickerUpload as apiCancelStickerUpload
} from './api-client.js';

const MAX_LOCAL_STATIC_BYTES = 15 * 1024 * 1024;
const MAX_UPLOAD_BYTES = 5 * 1024 * 1024;
const MAX_SOURCE_DIMENSION = 8192;
const CANVAS_SIZE = 512;

function normalizeId(val) {
    if (val === null || val === undefined) return '';
    if (typeof val === 'object' && val.value !== undefined) return String(val.value);
    return String(val);
}

/**
 * Inspects initial file bytes to determine if a GIF or WebP file is animated (>1 frame).
 * @param {File|Blob} file
 * @returns {Promise<boolean>}
 */
export async function detectAnimatedImage(file) {
    if (!file || typeof file.slice !== 'function') return false;
    const type = String(file.type || '').toLowerCase();
    const name = String(file.name || '').toLowerCase();
    const isGif = type === 'image/gif' || name.endsWith('.gif');
    const isWebp = type === 'image/webp' || name.endsWith('.webp');
    if (!isGif && !isWebp) return false;

    try {
        if (isWebp) {
            const buf = await file.slice(0, 65536).arrayBuffer();
            const bytes = new Uint8Array(buf);
            if (bytes.length < 16) return false;
            // Check RIFF....WEBP
            if (bytes[0] !== 0x52 || bytes[1] !== 0x49 || bytes[2] !== 0x46 || bytes[3] !== 0x46) return false;
            if (bytes[8] !== 0x57 || bytes[9] !== 0x45 || bytes[10] !== 0x42 || bytes[11] !== 0x50) return false;
            let offset = 12;
            let anmfCount = 0;
            while (offset + 8 <= bytes.length) {
                const c0 = bytes[offset];
                const c1 = bytes[offset + 1];
                const c2 = bytes[offset + 2];
                const c3 = bytes[offset + 3];
                const size = bytes[offset + 4] | (bytes[offset + 5] << 8) | (bytes[offset + 6] << 16) | (bytes[offset + 7] << 24);
                // VP8X animation flag (bit 1 of byte 0 of payload)
                if (c0 === 0x56 && c1 === 0x50 && c2 === 0x38 && c3 === 0x58 && offset + 9 <= bytes.length) {
                    if ((bytes[offset + 8] & 0x02) !== 0) return true;
                }
                if (c0 === 0x41 && c1 === 0x4E && c2 === 0x4D && c3 === 0x46) {
                    anmfCount++;
                    if (anmfCount > 1) return true;
                }
                if (size < 0) break;
                offset += 8 + size + (size & 1);
            }
            return anmfCount > 1;
        }

        if (isGif) {
            // Scan GIF up to 2 MB for >= 2 image descriptors (0x2C) or Graphic Control Extensions (0x21 0xF9)
            const buf = await file.slice(0, Math.min(file.size || 0, 2 * 1024 * 1024)).arrayBuffer();
            const bytes = new Uint8Array(buf);
            if (bytes.length < 14) return false;
            // GIF87a / GIF89a
            if (bytes[0] !== 0x47 || bytes[1] !== 0x49 || bytes[2] !== 0x46) return false;
            let pos = 13;
            const packed = bytes[10];
            if ((packed & 0x80) !== 0) {
                pos += 3 * (1 << ((packed & 0x07) + 1));
            }
            let frames = 0;
            while (pos < bytes.length) {
                const intro = bytes[pos++];
                if (intro === 0x3B) break;
                if (intro === 0x2C) {
                    frames++;
                    if (frames > 1) return true;
                    if (pos + 9 > bytes.length) break;
                    const imgPacked = bytes[pos + 8];
                    pos += 9;
                    if ((imgPacked & 0x80) !== 0) {
                        pos += 3 * (1 << ((imgPacked & 0x07) + 1));
                    }
                    pos++; // LZW min code size
                    while (pos < bytes.length) {
                        const sub = bytes[pos++];
                        if (sub === 0) break;
                        pos += sub;
                    }
                } else if (intro === 0x21) {
                    pos++; // label
                    while (pos < bytes.length) {
                        const sub = bytes[pos++];
                        if (sub === 0) break;
                        pos += sub;
                    }
                } else {
                    break;
                }
            }
            return frames > 1;
        }
    } catch {
        return isGif;
    }

    return false;
}

function uploadBlobWithXhr(url, blob, contentType, onProgress, onAbortHandle) {
    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        if (typeof onAbortHandle === 'function') {
            onAbortHandle(xhr);
        }
        xhr.open('PUT', url, true);
        xhr.withCredentials = false;
        xhr.setRequestHeader('Content-Type', contentType);
        if (xhr.upload && typeof onProgress === 'function') {
            xhr.upload.addEventListener('progress', (ev) => {
                if (ev.lengthComputable && ev.total > 0) {
                    onProgress(Math.min(100, Math.round((ev.loaded / ev.total) * 100)));
                }
            });
        }
        xhr.addEventListener('load', () => {
            if (xhr.status >= 200 && xhr.status < 300) {
                resolve();
            } else {
                reject(new Error(`Sticker upload failed (${xhr.status}).`));
            }
        });
        xhr.addEventListener('error', () => reject(new Error('Network error while uploading sticker.')));
        xhr.addEventListener('abort', () => reject(new Error('Upload cancelled.')));
        xhr.send(blob);
    });
}

/**
 * Creates the custom sticker creator modal controller bound to #sticker-creator-modal.
 * @param {{
 *   onBeforeOpen?: Function,
 *   onCreated?: Function,
 *   createPackFn?: Function,
 *   initiateUploadFn?: Function,
 *   completeUploadFn?: Function,
 *   cancelUploadFn?: Function,
 *   uploadPutFn?: Function
 * }} [options]
 */
export function createStickerCreator(options = {}) {
    const onBeforeOpen = typeof options.onBeforeOpen === 'function' ? options.onBeforeOpen : null;
    const defaultOnCreated = typeof options.onCreated === 'function' ? options.onCreated : null;
    const createPackFn = typeof options.createPackFn === 'function' ? options.createPackFn : apiCreateStickerPack;
    const initiateUploadFn = typeof options.initiateUploadFn === 'function' ? options.initiateUploadFn : apiInitiateStickerUpload;
    const completeUploadFn = typeof options.completeUploadFn === 'function' ? options.completeUploadFn : apiCompleteStickerUpload;
    const cancelUploadFn = typeof options.cancelUploadFn === 'function' ? options.cancelUploadFn : apiCancelStickerUpload;
    const uploadPutFn = typeof options.uploadPutFn === 'function' ? options.uploadPutFn : uploadBlobWithXhr;

    let isOpenState = false;
    let isSubmitting = false;
    let openGeneration = 0;
    let fileSelectionToken = 0;
    let activeXhr = null;
    let submitAbortController = null;
    let selectedFile = null;
    let isAnimatedFile = false;
    let animPreviewObjectUrl = null;
    let sourceBitmap = null;
    let sourceWidth = 0;
    let sourceHeight = 0;
    let shapeMode = 'square'; // 'square' | 'rounded' | 'circle'
    let zoom = 1;
    let panX = 0;
    let panY = 0;
    let isDragging = false;
    let activeDragPointerId = null;
    let dragStartX = 0;
    let dragStartY = 0;
    let dragBasePanX = 0;
    let dragBasePanY = 0;
    let customPacks = [];
    let activeOnCreatedCallback = null;
    let dropzoneDragDepth = 0;
    let listenersBound = false;

    function abortActiveOperations() {
        if (submitAbortController && typeof submitAbortController.abort === 'function') {
            try {
                submitAbortController.abort();
            } catch {}
        }
        submitAbortController = null;
        if (activeXhr && typeof activeXhr.abort === 'function') {
            try {
                activeXhr.abort();
            } catch {}
        }
        activeXhr = null;
    }

    function els() {
        if (typeof document === 'undefined' || typeof document.getElementById !== 'function') return {};
        return {
            modal: document.getElementById('sticker-creator-modal'),
            closeBtn: document.getElementById('sticker-creator-close-btn'),
            cancelBtn: document.getElementById('sticker-creator-cancel-btn'),
            dropzone: document.getElementById('sticker-creator-dropzone'),
            fileInput: document.getElementById('sticker-creator-file-input'),
            pickBtn: document.getElementById('sticker-creator-pick-btn'),
            previewArea: document.getElementById('sticker-creator-preview-area'),
            canvas: document.getElementById('sticker-creator-canvas'),
            animImg: document.getElementById('sticker-creator-anim-preview'),
            animBadge: document.getElementById('sticker-creator-anim-badge'),
            staticControls: document.getElementById('sticker-creator-static-controls'),
            zoomSlider: document.getElementById('sticker-creator-zoom'),
            changeFileBtn: document.getElementById('sticker-creator-change-file-btn'),
            packSelect: document.getElementById('sticker-creator-pack-select'),
            newPackToggleBtn: document.getElementById('sticker-creator-new-pack-btn'),
            newPackRow: document.getElementById('sticker-creator-new-pack-row'),
            newPackInput: document.getElementById('sticker-creator-new-pack-input'),
            newPackSaveBtn: document.getElementById('sticker-creator-new-pack-save-btn'),
            tagsInput: document.getElementById('sticker-creator-tags-input'),
            progressWrap: document.getElementById('sticker-creator-progress-wrap'),
            progressBar: document.getElementById('sticker-creator-progress-bar'),
            progressLabel: document.getElementById('sticker-creator-progress-label'),
            errorEl: document.getElementById('sticker-creator-error'),
            submitBtn: document.getElementById('sticker-creator-submit-btn')
        };
    }

    function setError(message) {
        const { errorEl } = els();
        if (!errorEl) return;
        if (message) {
            errorEl.textContent = message;
            errorEl.classList.remove('hidden');
        } else {
            errorEl.textContent = '';
            errorEl.classList.add('hidden');
        }
    }

    function setProgress(percent, label) {
        const { progressWrap, progressBar, progressLabel } = els();
        if (!progressWrap) return;
        if (percent === null || percent === undefined) {
            progressWrap.classList.add('hidden');
            if (progressBar && progressBar.style) progressBar.style.width = '0%';
            if (progressLabel) progressLabel.textContent = '';
            return;
        }
        progressWrap.classList.remove('hidden');
        if (progressBar && progressBar.style) {
            progressBar.style.width = `${Math.max(0, Math.min(100, percent))}%`;
        }
        if (progressLabel) {
            progressLabel.textContent = label || `${percent}%`;
        }
    }

    function releaseMediaResources() {
        if (sourceBitmap && typeof sourceBitmap.close === 'function') {
            try {
                sourceBitmap.close();
            } catch {}
        }
        sourceBitmap = null;
        sourceWidth = 0;
        sourceHeight = 0;

        if (animPreviewObjectUrl && typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
            try {
                URL.revokeObjectURL(animPreviewObjectUrl);
            } catch {}
        }
        animPreviewObjectUrl = null;

        const { animImg, canvas } = els();
        if (animImg && typeof animImg.removeAttribute === 'function') {
            animImg.removeAttribute('src');
            animImg.classList.add('hidden');
        }
        if (canvas && typeof canvas.getContext === 'function') {
            const ctx = canvas.getContext('2d');
            if (ctx) ctx.clearRect(0, 0, CANVAS_SIZE, CANVAS_SIZE);
        }
    }

    function renderPackOptions(selectedPackId = '') {
        const { packSelect } = els();
        if (!packSelect) return;
        while (packSelect.firstChild) {
            packSelect.removeChild(packSelect.firstChild);
        }

        if (!Array.isArray(customPacks) || customPacks.length === 0) {
            const opt = document.createElement('option');
            opt.value = '';
            opt.textContent = 'My Stickers (default)';
            packSelect.appendChild(opt);
            return;
        }

        for (let i = 0; i < customPacks.length; i++) {
            const p = customPacks[i];
            const opt = document.createElement('option');
            opt.value = p.id;
            opt.textContent = p.name;
            packSelect.appendChild(opt);
        }

        if (selectedPackId) {
            packSelect.value = selectedPackId;
        }
    }

    function updateShapeButtonsUi() {
        const { modal } = els();
        if (!modal || typeof modal.querySelectorAll !== 'function') return;
        const buttons = modal.querySelectorAll('[data-sticker-shape]');
        for (let i = 0; i < buttons.length; i++) {
            const btn = buttons[i];
            const active = btn.getAttribute('data-sticker-shape') === shapeMode;
            btn.setAttribute('aria-pressed', active ? 'true' : 'false');
            btn.className = active
                ? 'px-2.5 py-1 rounded-lg text-[11px] font-semibold bg-[#B3929D] text-white cursor-pointer transition-colors'
                : 'px-2.5 py-1 rounded-lg text-[11px] font-medium border border-[#E3DCD7] dark:border-[#514D48] text-[#6E6560] dark:text-[#C9C0B9] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] cursor-pointer transition-colors';
        }
    }

    function drawStaticPreview() {
        const { canvas } = els();
        if (!canvas || !sourceBitmap || typeof canvas.getContext !== 'function') return;
        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        ctx.clearRect(0, 0, CANVAS_SIZE, CANVAS_SIZE);
        ctx.save();

        if (shapeMode === 'circle') {
            ctx.beginPath();
            ctx.arc(CANVAS_SIZE / 2, CANVAS_SIZE / 2, CANVAS_SIZE / 2 - 4, 0, Math.PI * 2);
            ctx.closePath();
            ctx.clip();
        } else if (shapeMode === 'rounded') {
            const r = 84;
            const pad = 4;
            const s = CANVAS_SIZE - pad * 2;
            ctx.beginPath();
            ctx.moveTo(pad + r, pad);
            ctx.lineTo(pad + s - r, pad);
            ctx.quadraticCurveTo(pad + s, pad, pad + s, pad + r);
            ctx.lineTo(pad + s, pad + s - r);
            ctx.quadraticCurveTo(pad + s, pad + s, pad + s - r, pad + s);
            ctx.lineTo(pad + r, pad + s);
            ctx.quadraticCurveTo(pad, pad + s, pad, pad + s - r);
            ctx.lineTo(pad, pad + r);
            ctx.quadraticCurveTo(pad, pad, pad + r, pad);
            ctx.closePath();
            ctx.clip();
        }

        const useCover = shapeMode !== 'square';
        const baseScale = useCover
            ? Math.max(CANVAS_SIZE / sourceWidth, CANVAS_SIZE / sourceHeight)
            : Math.min(CANVAS_SIZE / sourceWidth, CANVAS_SIZE / sourceHeight);

        const drawW = sourceWidth * baseScale * zoom;
        const drawH = sourceHeight * baseScale * zoom;
        const maxPanX = Math.max(0, (drawW - CANVAS_SIZE) / 2);
        const maxPanY = Math.max(0, (drawH - CANVAS_SIZE) / 2);
        panX = Math.max(-maxPanX, Math.min(maxPanX, panX));
        panY = Math.max(-maxPanY, Math.min(maxPanY, panY));

        const x = (CANVAS_SIZE - drawW) / 2 + panX;
        const y = (CANVAS_SIZE - drawH) / 2 + panY;
        ctx.drawImage(sourceBitmap, x, y, drawW, drawH);
        ctx.restore();
    }

    async function handleFileSelected(file) {
        setError('');
        if (!file || !isOpenState || isSubmitting) return;

        selectedFile = null;
        releaseMediaResources();
        const ui = els();
        if (ui.submitBtn) ui.submitBtn.disabled = true;
        if (ui.previewArea) ui.previewArea.classList.add('hidden');
        if (ui.dropzone) ui.dropzone.classList.remove('hidden');

        const myGen = openGeneration;
        const myFileToken = ++fileSelectionToken;
        const isStale = () => myGen !== openGeneration || myFileToken !== fileSelectionToken || !isOpenState;

        const name = String(file.name || '').toLowerCase();
        const type = String(file.type || '').toLowerCase();
        const validExt = /\.(webp|png|jpe?g|gif)$/i.test(name);
        const validMime = ['image/webp', 'image/png', 'image/jpeg', 'image/jpg', 'image/gif'].includes(type);
        if (!validExt && !validMime) {
            setError('Please select a PNG, JPEG, WebP, or GIF image.');
            return;
        }

        const animated = await detectAnimatedImage(file);
        if (isStale()) return;

        if (animated && file.size > MAX_UPLOAD_BYTES) {
            setError('Animated stickers must be 5 MB or smaller.');
            return;
        }
        if (!animated && file.size > MAX_LOCAL_STATIC_BYTES) {
            setError('Selected image is too large (maximum 15 MB before resizing).');
            return;
        }

        isAnimatedFile = animated;
        shapeMode = 'square';
        zoom = 1;
        panX = 0;
        panY = 0;

        const {
            dropzone,
            previewArea,
            canvas,
            animImg,
            animBadge,
            staticControls,
            zoomSlider,
            submitBtn
        } = els();

        if (zoomSlider) zoomSlider.value = '1';
        updateShapeButtonsUi();

        if (animated) {
            if (typeof URL !== 'undefined' && typeof URL.createObjectURL === 'function') {
                animPreviewObjectUrl = URL.createObjectURL(file);
            }
            if (canvas) canvas.classList.add('hidden');
            if (staticControls) staticControls.classList.add('hidden');
            if (animBadge) animBadge.classList.remove('hidden');
            if (animImg) {
                if (animPreviewObjectUrl) {
                    animImg.src = animPreviewObjectUrl;
                } else if (typeof animImg.removeAttribute === 'function') {
                    animImg.removeAttribute('src');
                }
                animImg.alt = file.name || 'Animated sticker preview';
                animImg.classList.remove('hidden');
            }
            if (dropzone) dropzone.classList.add('hidden');
            if (previewArea) previewArea.classList.remove('hidden');
            try {
                if (!animPreviewObjectUrl) throw new Error('Image preview is unavailable.');
                if (typeof animImg?.decode === 'function') await animImg.decode();
                if (isStale()) return;
                selectedFile = file;
                if (submitBtn) submitBtn.disabled = false;
            } catch (err) {
                if (!isStale()) {
                    releaseMediaResources();
                    if (previewArea) previewArea.classList.add('hidden');
                    if (dropzone) dropzone.classList.remove('hidden');
                    setError(err?.message || 'Could not decode the selected image.');
                }
            }
            return;
        }

        try {
            let bmp;
            if (typeof createImageBitmap === 'function') {
                bmp = await createImageBitmap(file, { imageOrientation: 'from-image' });
            } else {
                throw new Error('Image preview is not supported in this browser.');
            }

            if (isStale()) {
                if (bmp && typeof bmp.close === 'function') {
                    try { bmp.close(); } catch {}
                }
                return;
            }

            if (bmp.width > MAX_SOURCE_DIMENSION || bmp.height > MAX_SOURCE_DIMENSION) {
                if (typeof bmp.close === 'function') bmp.close();
                selectedFile = null;
                setError(`Image dimensions exceed the maximum allowed ${MAX_SOURCE_DIMENSION}x${MAX_SOURCE_DIMENSION} pixels.`);
                return;
            }

            sourceBitmap = bmp;
            sourceWidth = bmp.width;
            sourceHeight = bmp.height;

            if (animImg) animImg.classList.add('hidden');
            if (animBadge) animBadge.classList.add('hidden');
            if (canvas) canvas.classList.remove('hidden');
            if (staticControls) staticControls.classList.remove('hidden');
            if (dropzone) dropzone.classList.add('hidden');
            if (previewArea) previewArea.classList.remove('hidden');
            drawStaticPreview();
            selectedFile = file;
            if (submitBtn) submitBtn.disabled = false;
        } catch (err) {
            if (!isStale()) {
                selectedFile = null;
                releaseMediaResources();
                setError(err?.message || 'Could not decode the selected image.');
            }
        }
    }

    function exportCanvasBlob() {
        const { canvas } = els();
        return new Promise((resolve, reject) => {
            if (!canvas || typeof canvas.toBlob !== 'function') {
                reject(new Error('Canvas export is unavailable.'));
                return;
            }
            canvas.toBlob((blob) => {
                if (blob && blob.size > 0) {
                    resolve(blob);
                    return;
                }
                canvas.toBlob((pngBlob) => {
                    if (pngBlob && pngBlob.size > 0) resolve(pngBlob);
                    else reject(new Error('Could not export sticker image.'));
                }, 'image/png');
            }, 'image/webp', 0.9);
        });
    }

    async function handleCreatePackInline() {
        const { newPackInput, newPackRow, newPackSaveBtn } = els();
        if (!newPackInput || !isOpenState) return;
        const myGen = openGeneration;
        const name = (newPackInput.value || '').trim();
        if (!name) {
            setError('Enter a name for the new sticker pack.');
            return;
        }
        setError('');
        if (newPackSaveBtn) newPackSaveBtn.disabled = true;
        try {
            const created = await createPackFn(name);
            if (myGen !== openGeneration || !isOpenState) return;
            const id = normalizeId(created?.id ?? created?.Id);
            const packName = created?.name ?? created?.Name ?? name;
            if (id) {
                customPacks = [...customPacks, { id, name: packName }];
                renderPackOptions(id);
            }
            newPackInput.value = '';
            if (newPackRow) newPackRow.classList.add('hidden');
        } catch (err) {
            if (myGen === openGeneration && isOpenState) {
                setError(err?.message || 'Could not create sticker pack.');
            }
        } finally {
            if (newPackSaveBtn) newPackSaveBtn.disabled = false;
        }
    }

    async function handleSubmit() {
        if (isSubmitting || !selectedFile || !isOpenState) return;

        const { packSelect, tagsInput, submitBtn, cancelBtn } = els();
        const myGen = openGeneration;
        const myAbortController = typeof AbortController === 'function' ? new AbortController() : null;
        submitAbortController = myAbortController;
        const signal = myAbortController ? myAbortController.signal : undefined;
        const isCancelled = () => myGen !== openGeneration || !isOpenState || Boolean(signal && signal.aborted);
        const capturedCallback = activeOnCreatedCallback || defaultOnCreated;

        isSubmitting = true;
        setError('');
        if (submitBtn) submitBtn.disabled = true;
        if (cancelBtn) cancelBtn.disabled = false;

        let initiatedStickerId = null;
        try {
            let uploadBlob;
            let uploadMime;
            let uploadFileName;

            if (isAnimatedFile) {
                uploadBlob = selectedFile;
                uploadMime = selectedFile.type || (String(selectedFile.name).toLowerCase().endsWith('.webp') ? 'image/webp' : 'image/gif');
                uploadFileName = selectedFile.name || (uploadMime === 'image/webp' ? 'sticker.webp' : 'sticker.gif');
            } else {
                setProgress(10, 'Preparing 512×512 sticker...');
                uploadBlob = await exportCanvasBlob();
                if (isCancelled()) return;
                uploadMime = uploadBlob.type || 'image/webp';
                uploadFileName = uploadMime === 'image/png' ? 'sticker.png' : 'sticker.webp';
            }

            if (uploadBlob.size <= 0 || uploadBlob.size > MAX_UPLOAD_BYTES) {
                throw new Error('Sticker exceeds the 5 MB upload limit.');
            }

            const selectedPackVal = packSelect ? (packSelect.value || '') : '';
            const emojiTags = tagsInput ? (tagsInput.value || '').trim() : '';

            setProgress(25, 'Starting upload...');
            const initResp = await initiateUploadFn({
                packId: selectedPackVal || null,
                fileName: uploadFileName,
                contentType: uploadMime,
                sizeBytes: uploadBlob.size,
                emojiTags: emojiTags || null
            }, { signal });

            initiatedStickerId = normalizeId(initResp?.stickerId ?? initResp?.StickerId);
            if (isCancelled()) {
                throw Object.assign(new Error('Upload cancelled.'), { name: 'AbortError' });
            }

            const uploadUrl = initResp?.uploadUrl ?? initResp?.UploadUrl;
            const requiredMime = initResp?.requiredContentType ?? initResp?.RequiredContentType ?? uploadMime;

            if (!initiatedStickerId || !uploadUrl) {
                throw new Error('Invalid upload response from server.');
            }

            setProgress(35, 'Uploading to storage...');
            await uploadPutFn(
                uploadUrl,
                uploadBlob,
                requiredMime,
                (pct) => {
                    if (isCancelled()) return;
                    const scaled = 35 + Math.round(pct * 0.45);
                    setProgress(scaled, `Uploading... ${pct}%`);
                },
                (xhr) => {
                    if (isCancelled()) {
                        if (xhr && typeof xhr.abort === 'function') {
                            try { xhr.abort(); } catch {}
                        }
                        return;
                    }
                    activeXhr = xhr;
                }
            );
            activeXhr = null;
            if (isCancelled()) {
                throw Object.assign(new Error('Upload cancelled.'), { name: 'AbortError' });
            }

            setProgress(85, 'Normalizing to 512×512 WebP...');
            const completed = await completeUploadFn(initiatedStickerId, { signal });
            if (isCancelled()) {
                throw Object.assign(new Error('Upload cancelled.'), { name: 'AbortError' });
            }
            initiatedStickerId = null;

            setProgress(100, 'Sticker added!');
            close();
            if (capturedCallback) {
                capturedCallback(completed);
            }
        } catch (err) {
            activeXhr = null;
            if (initiatedStickerId) {
                try {
                    await cancelUploadFn(initiatedStickerId);
                } catch {}
            }
            if (!isCancelled()) {
                setProgress(null);
                setError(err?.message || 'Could not create sticker.');
            }
        } finally {
            if (submitAbortController === myAbortController) {
                submitAbortController = null;
            }
            if (myGen === openGeneration) {
                isSubmitting = false;
                const { submitBtn: currentSubmitBtn } = els();
                if (currentSubmitBtn) {
                    currentSubmitBtn.disabled = !selectedFile;
                }
            }
        }
    }

    function normalizeEventTarget(target) {
        if (!target) return null;
        return target.nodeType === 3 ? target.parentNode : target;
    }

    function isFileDragEvent(e) {
        const dt = e && e.dataTransfer;
        if (!dt) return false;
        if (dt.types) {
            try {
                return Array.from(dt.types).includes('Files');
            } catch {
                return Boolean(dt.files && dt.files.length > 0);
            }
        }
        return Boolean(dt.files && dt.files.length > 0);
    }

    function isModalDragTarget(modal, target) {
        if (!modal || !target) return false;
        return target === modal || (typeof modal.contains === 'function' && modal.contains(target));
    }

    function isVisibleDropzoneTarget(dropzone, target) {
        if (!dropzone || !target) return false;
        if (dropzone.classList && dropzone.classList.contains('hidden')) return false;
        return target === dropzone || (typeof dropzone.contains === 'function' && dropzone.contains(target));
    }

    function setDropzoneDragActive(dropzone, active) {
        if (!dropzone || !dropzone.classList) return;
        if (active) {
            dropzone.classList.remove('border-[#E3DCD7]', 'dark:border-[#514D48]', 'bg-[#F8F6F3]/80', 'dark:bg-[#302F2C]/60');
            dropzone.classList.add('border-[#B3929D]', 'bg-[#F0E9DF]/80', 'dark:bg-[#45423E]/70');
        } else {
            dropzone.classList.remove('border-[#B3929D]', 'bg-[#F0E9DF]/80', 'dark:bg-[#45423E]/70');
            dropzone.classList.add('border-[#E3DCD7]', 'dark:border-[#514D48]', 'bg-[#F8F6F3]/80', 'dark:bg-[#302F2C]/60');
        }
    }

    function resetFormUi() {
        fileSelectionToken++;
        selectedFile = null;
        isAnimatedFile = false;
        shapeMode = 'square';
        zoom = 1;
        panX = 0;
        panY = 0;
        isDragging = false;
        activeDragPointerId = null;
        dropzoneDragDepth = 0;
        releaseMediaResources();

        const {
            dropzone,
            previewArea,
            fileInput,
            tagsInput,
            newPackRow,
            newPackInput,
            zoomSlider,
            submitBtn
        } = els();

        if (fileInput) fileInput.value = '';
        if (tagsInput) tagsInput.value = '';
        if (newPackInput) newPackInput.value = '';
        if (newPackRow) newPackRow.classList.add('hidden');
        if (zoomSlider) zoomSlider.value = '1';
        setDropzoneDragActive(dropzone, false);
        if (dropzone) dropzone.classList.remove('hidden');
        if (previewArea) previewArea.classList.add('hidden');
        if (submitBtn) submitBtn.disabled = true;
        setError('');
        setProgress(null);
        updateShapeButtonsUi();
    }

    function open(openOpts = {}) {
        const { modal } = els();
        if (!modal) return;

        bindListeners();
        abortActiveOperations();
        openGeneration++;
        fileSelectionToken++;

        if (onBeforeOpen) {
            onBeforeOpen();
        }

        customPacks = Array.isArray(openOpts.customPacks) ? openOpts.customPacks.slice() : [];
        activeOnCreatedCallback = typeof openOpts.onStickerCreated === 'function' ? openOpts.onStickerCreated : null;
        resetFormUi();
        renderPackOptions();

        isOpenState = true;
        modal.classList.remove('hidden');
    }

    function close() {
        openGeneration++;
        fileSelectionToken++;
        abortActiveOperations();
        if (activeDragPointerId !== null) {
            const { canvas } = els();
            if (canvas && typeof canvas.releasePointerCapture === 'function') {
                try { canvas.releasePointerCapture(activeDragPointerId); } catch {}
            }
        }
        isDragging = false;
        activeDragPointerId = null;
        isOpenState = false;
        isSubmitting = false;
        activeOnCreatedCallback = null;
        resetFormUi();
        const { modal } = els();
        if (modal && modal.classList) {
            modal.classList.add('hidden');
        }
    }

    const onClick = (e) => {
        const { modal, fileInput, newPackRow, newPackInput } = els();
        if (!modal || !e.target) return;

        if (e.target === modal) {
            close();
            return;
        }
        if (typeof e.target.closest !== 'function') return;

        if (e.target.closest('#sticker-creator-close-btn') || e.target.closest('#sticker-creator-cancel-btn')) {
            e.preventDefault();
            close();
            return;
        }
        if (e.target.closest('#sticker-creator-pick-btn') || e.target.closest('#sticker-creator-change-file-btn')) {
            e.preventDefault();
            if (fileInput && typeof fileInput.click === 'function') {
                fileInput.click();
            }
            return;
        }
        const shapeBtn = e.target.closest('[data-sticker-shape]');
        if (shapeBtn && modal.contains(shapeBtn)) {
            e.preventDefault();
            shapeMode = shapeBtn.getAttribute('data-sticker-shape') || 'square';
            panX = 0;
            panY = 0;
            updateShapeButtonsUi();
            drawStaticPreview();
            return;
        }
        if (e.target.closest('#sticker-creator-new-pack-btn')) {
            e.preventDefault();
            if (newPackRow) {
                newPackRow.classList.toggle('hidden');
                if (!newPackRow.classList.contains('hidden') && newPackInput && typeof newPackInput.focus === 'function') {
                    newPackInput.focus();
                }
            }
            return;
        }
        if (e.target.closest('#sticker-creator-new-pack-save-btn')) {
            e.preventDefault();
            handleCreatePackInline();
            return;
        }
        if (e.target.closest('#sticker-creator-submit-btn')) {
            e.preventDefault();
            handleSubmit();
        }
    };

    const onChange = (e) => {
        const { fileInput } = els();
        if (fileInput && e.target === fileInput && fileInput.files && fileInput.files[0]) {
            handleFileSelected(fileInput.files[0]);
        }
    };

    const onInput = (e) => {
        const { zoomSlider } = els();
        if (zoomSlider && e.target === zoomSlider) {
            zoom = Math.max(1, Math.min(3, Number(zoomSlider.value) || 1));
            drawStaticPreview();
        }
    };

    const onKeyDown = (e) => {
        if (isOpenState && e.key === 'Escape') {
            e.preventDefault();
            close();
        }
    };

    const onDragEnter = (e) => {
        if (!isOpenState || !isFileDragEvent(e)) return;
        const { modal, dropzone } = els();
        const target = normalizeEventTarget(e.target);
        if (!isModalDragTarget(modal, target)) return;
        if (typeof e.preventDefault === 'function') e.preventDefault();
        if (typeof e.stopPropagation === 'function') e.stopPropagation();
        if (!isSubmitting && isVisibleDropzoneTarget(dropzone, target)) {
            dropzoneDragDepth++;
            setDropzoneDragActive(dropzone, true);
        }
    };

    const onDragOver = (e) => {
        if (!isOpenState || !isFileDragEvent(e)) return;
        const { modal, dropzone } = els();
        const target = normalizeEventTarget(e.target);
        if (!isModalDragTarget(modal, target)) return;
        if (typeof e.preventDefault === 'function') e.preventDefault();
        if (typeof e.stopPropagation === 'function') e.stopPropagation();
        if (e.dataTransfer) {
            try {
                e.dataTransfer.dropEffect = (!isSubmitting && isVisibleDropzoneTarget(dropzone, target)) ? 'copy' : 'none';
            } catch {}
        }
    };

    const onDragLeave = (e) => {
        if (!isOpenState || !isFileDragEvent(e)) return;
        const { modal, dropzone } = els();
        const target = normalizeEventTarget(e.target);
        if (!isModalDragTarget(modal, target)) return;
        if (typeof e.stopPropagation === 'function') e.stopPropagation();
        if (isVisibleDropzoneTarget(dropzone, target)) {
            dropzoneDragDepth = Math.max(0, dropzoneDragDepth - 1);
            if (dropzoneDragDepth === 0) {
                setDropzoneDragActive(dropzone, false);
            }
        }
    };

    const onDrop = (e) => {
        if (!isOpenState || !isFileDragEvent(e)) return;
        const { modal, dropzone } = els();
        const target = normalizeEventTarget(e.target);
        if (!isModalDragTarget(modal, target)) return;
        if (typeof e.preventDefault === 'function') e.preventDefault();
        if (typeof e.stopPropagation === 'function') e.stopPropagation();
        dropzoneDragDepth = 0;
        setDropzoneDragActive(dropzone, false);
        if (!isSubmitting && isVisibleDropzoneTarget(dropzone, target)) {
            const file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
            if (file) handleFileSelected(file);
        }
    };

    const onCanvasPointerDown = (e) => {
        const { canvas } = els();
        if (!isOpenState || isSubmitting || !canvas || e.target !== canvas || !sourceBitmap) return;
        isDragging = true;
        activeDragPointerId = e.pointerId !== undefined ? e.pointerId : null;
        dragStartX = e.clientX;
        dragStartY = e.clientY;
        dragBasePanX = panX;
        dragBasePanY = panY;
        if (typeof canvas.setPointerCapture === 'function' && e.pointerId !== undefined) {
            try { canvas.setPointerCapture(e.pointerId); } catch {}
        }
    };

    const onCanvasPointerMove = (e) => {
        if (!isOpenState || !isDragging || !sourceBitmap) return;
        if (activeDragPointerId !== null && e.pointerId !== undefined && e.pointerId !== activeDragPointerId) return;
        const { canvas } = els();
        const rect = canvas && typeof canvas.getBoundingClientRect === 'function'
            ? canvas.getBoundingClientRect()
            : { width: 220 };
        const scale = rect.width > 0 ? (CANVAS_SIZE / rect.width) : 2;
        panX = dragBasePanX + (e.clientX - dragStartX) * scale;
        panY = dragBasePanY + (e.clientY - dragStartY) * scale;
        drawStaticPreview();
    };

    const onCanvasPointerUp = (e) => {
        if (!isDragging) return;
        if (e && activeDragPointerId !== null && e.pointerId !== undefined && e.pointerId !== activeDragPointerId) return;
        const { canvas } = els();
        if (canvas && typeof canvas.releasePointerCapture === 'function' && activeDragPointerId !== null) {
            try { canvas.releasePointerCapture(activeDragPointerId); } catch {}
        }
        isDragging = false;
        activeDragPointerId = null;
    };

    function bindListeners() {
        if (listenersBound || typeof document === 'undefined') return;
        document.addEventListener('click', onClick);
        document.addEventListener('change', onChange);
        document.addEventListener('input', onInput);
        document.addEventListener('keydown', onKeyDown);
        // Capture phase intercepts modal drag/drop before it bubbles to #chat-active-pane
        document.addEventListener('dragenter', onDragEnter, true);
        document.addEventListener('dragover', onDragOver, true);
        document.addEventListener('dragleave', onDragLeave, true);
        document.addEventListener('drop', onDrop, true);
        document.addEventListener('pointerdown', onCanvasPointerDown);
        document.addEventListener('pointermove', onCanvasPointerMove);
        document.addEventListener('pointerup', onCanvasPointerUp);
        document.addEventListener('pointercancel', onCanvasPointerUp);
        listenersBound = true;
    }

    function unbindListeners() {
        if (!listenersBound || typeof document === 'undefined') return;
        document.removeEventListener('click', onClick);
        document.removeEventListener('change', onChange);
        document.removeEventListener('input', onInput);
        document.removeEventListener('keydown', onKeyDown);
        document.removeEventListener('dragenter', onDragEnter, true);
        document.removeEventListener('dragover', onDragOver, true);
        document.removeEventListener('dragleave', onDragLeave, true);
        document.removeEventListener('drop', onDrop, true);
        document.removeEventListener('pointerdown', onCanvasPointerDown);
        document.removeEventListener('pointermove', onCanvasPointerMove);
        document.removeEventListener('pointerup', onCanvasPointerUp);
        document.removeEventListener('pointercancel', onCanvasPointerUp);
        listenersBound = false;
    }

    bindListeners();

    return {
        open,
        close,
        isOpen: () => isOpenState,
        abortAndWipe: close,
        destroy: () => {
            close();
            unbindListeners();
        }
    };
}
