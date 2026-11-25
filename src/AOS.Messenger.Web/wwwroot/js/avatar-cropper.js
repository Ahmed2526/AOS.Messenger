/**
 * AOS Messenger - Native Canvas Circular Avatar Cropper Modal
 *
 * Security & Architecture Invariants:
 * - Zero external dependencies; pure vanilla ES module using native HTML5 <canvas>.
 * - Accepts local files up to 15 MB, validates decoded dimensions (<= 8192px, <= 33 MP),
 *   applies EXIF orientation via createImageBitmap({ imageOrientation: 'from-image' }),
 *   and exports a <= 1024px square Blob BEFORE calling /initiate so Content-Length and
 *   Content-Type match R2 presigned PUT signatures 100%.
 * - Always closes bitmaps and revokes blob: URLs on close/cancel/destroy/error.
 * - Lazily mounts single-instance #avatar-cropper-modal (prevents duplicate DOM IDs).
 */

import { initiateAvatarUpload, completeAvatarUpload } from './api-client.js';

export const MAX_LOCAL_AVATAR_SOURCE_BYTES = 15 * 1024 * 1024;
export const MAX_RAW_AVATAR_UPLOAD_BYTES = 5 * 1024 * 1024;
const MAX_IMAGE_DIMENSION = 8192;
const MAX_IMAGE_PIXELS = 33_554_432;
const MAX_FULL_AVATAR_SIDE = 1024;
const PREVIEW_SIZE = 280;

const ALLOWED_SOURCE_MIMES = new Set(['image/jpeg', 'image/jpg', 'image/png', 'image/webp']);
const ALLOWED_EXTENSIONS = new Set(['.jpg', '.jpeg', '.png', '.webp']);

function getFileExtension(name) {
    const clean = String(name || '').trim();
    const dotIdx = clean.lastIndexOf('.');
    return dotIdx >= 0 ? clean.slice(dotIdx).toLowerCase() : '';
}

function defaultFileNameForMime(mime) {
    const norm = String(mime || '').toLowerCase();
    if (norm === 'image/png') return 'avatar.png';
    if (norm === 'image/jpeg' || norm === 'image/jpg') return 'avatar.jpg';
    return 'avatar.webp';
}

/**
 * Validates a locally selected profile photo before decoding onto canvas.
 * @param {File|Blob|{ name?: string, type?: string, size?: number }|null|undefined} file
 * @returns {{ ok: boolean, error?: string }}
 */
export function validateAvatarSourceFile(file) {
    if (!file || typeof file !== 'object') {
        return { ok: false, error: 'Please select an image file.' };
    }

    const size = Number(file.size ?? 0);
    if (!Number.isFinite(size) || size <= 0) {
        return { ok: false, error: 'Selected image file is empty.' };
    }
    if (size > MAX_LOCAL_AVATAR_SOURCE_BYTES) {
        return { ok: false, error: 'Please select an image smaller than 15 MB.' };
    }

    const type = String(file.type || '').trim().toLowerCase();
    const ext = getFileExtension(file.name);

    if (type === 'image/svg+xml' || ext === '.svg' || type === 'image/gif' || ext === '.gif') {
        return { ok: false, error: 'Profile photos must be static JPEG, PNG, or WebP images.' };
    }

    if (type && !ALLOWED_SOURCE_MIMES.has(type)) {
        return { ok: false, error: 'Unsupported image format. Choose JPEG, PNG, or WebP.' };
    }

    if (!type && !ALLOWED_EXTENSIONS.has(ext)) {
        return { ok: false, error: 'Unsupported file type. Choose a JPEG, PNG, or WebP image.' };
    }

    return { ok: true };
}

function getCropperElements() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return {};
    }
    return {
        modal: document.getElementById('avatar-cropper-modal'),
        canvas: document.getElementById('avatar-cropper-canvas'),
        zoomSlider: document.getElementById('avatar-cropper-zoom'),
        errorEl: document.getElementById('avatar-cropper-error'),
        progressWrap: document.getElementById('avatar-cropper-progress-wrap'),
        progressBar: document.getElementById('avatar-cropper-progress-bar'),
        progressLabel: document.getElementById('avatar-cropper-progress-label'),
        saveBtn: document.getElementById('avatar-cropper-save-btn'),
        cancelBtn: document.getElementById('avatar-cropper-cancel-btn'),
        closeBtn: document.getElementById('avatar-cropper-close-btn')
    };
}

function ensureAvatarCropperModal() {
    if (typeof document === 'undefined' || typeof document.getElementById !== 'function') {
        return null;
    }

    const existing = document.getElementById('avatar-cropper-modal');
    if (existing) {
        return existing;
    }
    if (typeof document.createElement !== 'function' || !document.body) {
        return null;
    }

    const modal = document.createElement('div');
    modal.id = 'avatar-cropper-modal';
    modal.setAttribute('role', 'dialog');
    modal.setAttribute('aria-modal', 'true');
    modal.setAttribute('aria-labelledby', 'avatar-cropper-title');
    modal.className = 'hidden fixed inset-0 z-[70] flex items-center justify-center bg-black/65 p-4 backdrop-blur-xs';

    const card = document.createElement('div');
    card.className = 'bg-white dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] rounded-2xl shadow-2xl w-full max-w-md p-5 flex flex-col gap-4';

    const header = document.createElement('div');
    header.className = 'flex items-center justify-between gap-3';
    const title = document.createElement('h3');
    title.id = 'avatar-cropper-title';
    title.className = 'text-sm font-bold text-[#2B2725] dark:text-[#F0E9E2]';
    title.textContent = 'Adjust Profile Photo';
    header.appendChild(title);

    const closeBtn = document.createElement('button');
    closeBtn.id = 'avatar-cropper-close-btn';
    closeBtn.type = 'button';
    closeBtn.className = 'w-8 h-8 rounded-lg flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:text-[#2B2725] dark:hover:text-[#F0E9E2] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] transition-colors cursor-pointer';
    closeBtn.setAttribute('aria-label', 'Close photo cropper');
    const closeIcon = document.createElement('span');
    closeIcon.className = 'material-symbols-outlined text-[18px]';
    closeIcon.textContent = 'close';
    closeBtn.appendChild(closeIcon);
    header.appendChild(closeBtn);
    card.appendChild(header);

    const canvasWrap = document.createElement('div');
    canvasWrap.className = 'flex flex-col items-center gap-2';
    const canvas = document.createElement('canvas');
    canvas.id = 'avatar-cropper-canvas';
    canvas.width = PREVIEW_SIZE;
    canvas.height = PREVIEW_SIZE;
    canvas.setAttribute('tabindex', '0');
    canvas.setAttribute('aria-label', 'Drag photo to reposition circular crop; use arrow keys or slider to adjust');
    canvas.className = 'w-[280px] h-[280px] rounded-xl bg-[#2B2725] touch-none cursor-grab active:cursor-grabbing border border-[#E3DCD7] dark:border-[#514D48] select-none';
    canvasWrap.appendChild(canvas);

    const hint = document.createElement('p');
    hint.className = 'text-[11px] text-[#6E6560] dark:text-[#C9C0B9]';
    hint.textContent = 'Drag to reposition • Scroll or slide to zoom';
    canvasWrap.appendChild(hint);
    card.appendChild(canvasWrap);

    const zoomRow = document.createElement('div');
    zoomRow.className = 'flex items-center gap-3 px-1';
    const zoomOutIcon = document.createElement('span');
    zoomOutIcon.className = 'material-symbols-outlined text-[18px] text-[#6E6560] dark:text-[#C9C0B9]';
    zoomOutIcon.textContent = 'zoom_out';
    zoomRow.appendChild(zoomOutIcon);

    const zoomSlider = document.createElement('input');
    zoomSlider.id = 'avatar-cropper-zoom';
    zoomSlider.type = 'range';
    zoomSlider.min = '1';
    zoomSlider.max = '3';
    zoomSlider.step = '0.02';
    zoomSlider.value = '1';
    zoomSlider.setAttribute('aria-label', 'Zoom profile photo');
    zoomSlider.className = 'flex-1 accent-[#B3929D] cursor-pointer';
    zoomRow.appendChild(zoomSlider);

    const zoomInIcon = document.createElement('span');
    zoomInIcon.className = 'material-symbols-outlined text-[18px] text-[#6E6560] dark:text-[#C9C0B9]';
    zoomInIcon.textContent = 'zoom_in';
    zoomRow.appendChild(zoomInIcon);
    card.appendChild(zoomRow);

    const errorEl = document.createElement('p');
    errorEl.id = 'avatar-cropper-error';
    errorEl.setAttribute('role', 'alert');
    errorEl.className = 'hidden text-xs text-red-600 dark:text-red-400 font-medium';
    card.appendChild(errorEl);

    const progressWrap = document.createElement('div');
    progressWrap.id = 'avatar-cropper-progress-wrap';
    progressWrap.className = 'hidden flex flex-col gap-1';
    const progressLabel = document.createElement('span');
    progressLabel.id = 'avatar-cropper-progress-label';
    progressLabel.className = 'text-[11px] text-[#6E6560] dark:text-[#C9C0B9] font-medium';
    progressLabel.textContent = 'Uploading...';
    progressWrap.appendChild(progressLabel);
    const track = document.createElement('div');
    track.className = 'w-full h-1.5 rounded-full bg-[#E3DCD7] dark:bg-[#514D48] overflow-hidden';
    const bar = document.createElement('div');
    bar.id = 'avatar-cropper-progress-bar';
    bar.className = 'h-full w-0 bg-[#B3929D] transition-all';
    track.appendChild(bar);
    progressWrap.appendChild(track);
    card.appendChild(progressWrap);

    const footer = document.createElement('div');
    footer.className = 'flex items-center justify-end gap-2 pt-1';
    const cancelBtn = document.createElement('button');
    cancelBtn.id = 'avatar-cropper-cancel-btn';
    cancelBtn.type = 'button';
    cancelBtn.className = 'px-3.5 py-1.5 rounded-xl border border-[#E3DCD7] dark:border-[#514D48] text-xs font-semibold text-[#6E6560] dark:text-[#C9C0B9] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] transition-colors cursor-pointer';
    cancelBtn.textContent = 'Cancel';
    footer.appendChild(cancelBtn);

    const saveBtn = document.createElement('button');
    saveBtn.id = 'avatar-cropper-save-btn';
    saveBtn.type = 'button';
    saveBtn.className = 'px-4 py-1.5 rounded-xl bg-[#B3929D] text-white text-xs font-bold hover:opacity-90 active:scale-95 transition-all shadow-2xs cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed';
    saveBtn.textContent = 'Save Photo';
    footer.appendChild(saveBtn);

    card.appendChild(footer);
    modal.appendChild(card);
    document.body.appendChild(modal);
    return modal;
}

/**
 * Creates the interactive circular profile photo cropper & R2 upload controller.
 * @param {{
 *   onUploaded?: Function|null,
 *   onError?: Function|null,
 *   api?: object|null,
 *   createXhr?: Function|null
 * }} [options]
 */
export function createAvatarCropper({ onUploaded = null, onError = null, api = null, createXhr = null } = {}) {
    const resolvedApi = api || { initiateAvatarUpload, completeAvatarUpload };
    const xhrFactory = createXhr || (() => new XMLHttpRequest());

    let activeBitmap = null;
    let activeImgElement = null;
    let activeObjectUrl = null;
    let sourceWidth = 0;
    let sourceHeight = 0;
    let zoom = 1;
    let panX = 0;
    let panY = 0;
    let isDragging = false;
    let dragStartX = 0;
    let dragStartY = 0;
    let dragBasePanX = 0;
    let dragBasePanY = 0;
    let isUploading = false;
    let isOpenState = false;
    let openGeneration = 0;
    let activeXhr = null;
    let uploadAbortController = null;
    let lastFocusedElement = null;
    let listenersBound = false;
    let uiListenerCleanups = [];

    function disposeSourceMedia() {
        if (activeBitmap && typeof activeBitmap.close === 'function') {
            try {
                activeBitmap.close();
            } catch {}
        }
        activeBitmap = null;
        activeImgElement = null;
        if (activeObjectUrl && typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
            try {
                URL.revokeObjectURL(activeObjectUrl);
            } catch {}
        }
        activeObjectUrl = null;
        sourceWidth = 0;
        sourceHeight = 0;
    }

    function getDrawMetrics(canvasSize) {
        if (!sourceWidth || !sourceHeight) return null;
        const baseScale = canvasSize / Math.min(sourceWidth, sourceHeight);
        const totalScale = baseScale * zoom;
        const drawW = sourceWidth * totalScale;
        const drawH = sourceHeight * totalScale;
        const maxPanX = Math.max(0, (drawW - canvasSize) / 2);
        const maxPanY = Math.max(0, (drawH - canvasSize) / 2);
        return { totalScale, drawW, drawH, maxPanX, maxPanY };
    }

    function clampPan() {
        const metrics = getDrawMetrics(PREVIEW_SIZE);
        if (!metrics) return;
        panX = Math.max(-metrics.maxPanX, Math.min(metrics.maxPanX, panX));
        panY = Math.max(-metrics.maxPanY, Math.min(metrics.maxPanY, panY));
    }

    function drawPreview() {
        const { canvas } = getCropperElements();
        const drawable = activeBitmap || activeImgElement;
        if (!canvas || typeof canvas.getContext !== 'function' || !drawable) return;

        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        clampPan();
        const metrics = getDrawMetrics(PREVIEW_SIZE);
        if (!metrics) return;

        const drawX = (PREVIEW_SIZE - metrics.drawW) / 2 + panX;
        const drawY = (PREVIEW_SIZE - metrics.drawH) / 2 + panY;

        ctx.clearRect(0, 0, PREVIEW_SIZE, PREVIEW_SIZE);
        ctx.fillStyle = '#2B2725';
        ctx.fillRect(0, 0, PREVIEW_SIZE, PREVIEW_SIZE);
        ctx.drawImage(drawable, drawX, drawY, metrics.drawW, metrics.drawH);

        // Darken corners outside circular crop mask using even-odd fill rule
        const center = PREVIEW_SIZE / 2;
        const radius = (PREVIEW_SIZE / 2) - 8;
        ctx.save();
        ctx.fillStyle = 'rgba(0, 0, 0, 0.55)';
        ctx.beginPath();
        ctx.rect(0, 0, PREVIEW_SIZE, PREVIEW_SIZE);
        ctx.arc(center, center, radius, 0, Math.PI * 2, true);
        ctx.fill('evenodd');

        ctx.strokeStyle = 'rgba(255, 255, 255, 0.85)';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(center, center, radius, 0, Math.PI * 2);
        ctx.stroke();
        ctx.restore();
    }

    function setError(message) {
        const { errorEl } = getCropperElements();
        if (!errorEl) return;
        if (message) {
            errorEl.textContent = message;
            if (errorEl.classList) errorEl.classList.remove('hidden');
        } else {
            errorEl.textContent = '';
            if (errorEl.classList) errorEl.classList.add('hidden');
        }
    }

    function setUploadingUi(uploading, percent = 0, labelText = 'Uploading...') {
        isUploading = uploading;
        const { saveBtn, cancelBtn, zoomSlider, progressWrap, progressBar, progressLabel } = getCropperElements();
        if (saveBtn) saveBtn.disabled = uploading;
        if (cancelBtn) cancelBtn.disabled = false;
        if (zoomSlider) zoomSlider.disabled = uploading;
        if (progressWrap && progressWrap.classList) {
            progressWrap.classList.toggle('hidden', !uploading);
        }
        if (progressLabel) progressLabel.textContent = labelText;
        if (progressBar && progressBar.style) {
            progressBar.style.width = `${Math.max(0, Math.min(100, percent))}%`;
        }
    }

    function close() {
        openGeneration++;
        isOpenState = false;
        if (uploadAbortController && typeof uploadAbortController.abort === 'function') {
            try {
                uploadAbortController.abort();
            } catch {}
        }
        uploadAbortController = null;
        if (activeXhr && typeof activeXhr.abort === 'function') {
            try {
                activeXhr.abort();
            } catch {}
        }
        activeXhr = null;
        isDragging = false;
        setUploadingUi(false, 0);
        setError('');
        disposeSourceMedia();

        const { modal } = getCropperElements();
        if (modal && modal.classList) modal.classList.add('hidden');

        const restore = lastFocusedElement;
        lastFocusedElement = null;
        if (restore && typeof restore.focus === 'function' && restore.isConnected !== false) {
            restore.focus();
        }
    }

    async function loadDrawable(file, expectedGen) {
        disposeSourceMedia();
        if (typeof createImageBitmap === 'function') {
            try {
                const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
                if (expectedGen !== openGeneration || !isOpenState) {
                    if (typeof bitmap.close === 'function') {
                        try { bitmap.close(); } catch {}
                    }
                    return false;
                }
                activeBitmap = bitmap;
                sourceWidth = bitmap.width;
                sourceHeight = bitmap.height;
                return true;
            } catch {
                if (expectedGen !== openGeneration || !isOpenState) {
                    return false;
                }
                // Fall back to ObjectURL + Image
            }
        }

        if (typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function' || typeof Image === 'undefined') {
            throw new Error('Browser image decoding is unavailable.');
        }

        const objectUrl = URL.createObjectURL(file);
        activeObjectUrl = objectUrl;
        const img = new Image();
        try {
            await new Promise((resolve, reject) => {
                img.onload = () => resolve();
                img.onerror = () => reject(new Error('Could not decode the selected image.'));
                img.src = objectUrl;
            });
        } catch (err) {
            img.onload = null;
            img.onerror = null;
            if (typeof img.removeAttribute === 'function') img.removeAttribute('src');
            if (expectedGen !== openGeneration || !isOpenState) {
                return false;
            }
            throw err;
        }
        if (expectedGen !== openGeneration || !isOpenState) {
            img.onload = null;
            img.onerror = null;
            if (typeof img.removeAttribute === 'function') img.removeAttribute('src');
            return false;
        }
        activeImgElement = img;
        sourceWidth = img.naturalWidth || img.width;
        sourceHeight = img.naturalHeight || img.height;
        return true;
    }

    async function exportCroppedBlob() {
        const drawable = activeBitmap || activeImgElement;
        const metrics = getDrawMetrics(PREVIEW_SIZE);
        if (!drawable || !metrics || !sourceWidth || !sourceHeight) {
            throw new Error('No image loaded for cropping.');
        }

        const drawX = (PREVIEW_SIZE - metrics.drawW) / 2 + panX;
        const drawY = (PREVIEW_SIZE - metrics.drawH) / 2 + panY;

        const srcX = Math.max(0, (-drawX) / metrics.totalScale);
        const srcY = Math.max(0, (-drawY) / metrics.totalScale);
        const srcSideRaw = PREVIEW_SIZE / metrics.totalScale;
        const srcSide = Math.max(1, Math.min(srcSideRaw, sourceWidth - srcX, sourceHeight - srcY));

        const targetSide = Math.max(1, Math.min(MAX_FULL_AVATAR_SIDE, Math.round(srcSide)));
        const outCanvas = document.createElement('canvas');
        outCanvas.width = targetSide;
        outCanvas.height = targetSide;
        const outCtx = outCanvas.getContext('2d');
        if (!outCtx) {
            throw new Error('Could not initialize image encoder.');
        }

        outCtx.drawImage(drawable, srcX, srcY, srcSide, srcSide, 0, 0, targetSide, targetSide);

        const toBlobPromise = (mime, quality) => new Promise((resolve) => {
            outCanvas.toBlob((blob) => resolve(blob), mime, quality);
        });

        let blob = await toBlobPromise('image/webp', 0.90);
        if (!blob || blob.size <= 0) {
            blob = await toBlobPromise('image/jpeg', 0.90);
        }
        if (!blob || blob.size <= 0) {
            throw new Error('Failed to encode cropped profile photo.');
        }
        if (blob.size > MAX_RAW_AVATAR_UPLOAD_BYTES) {
            blob = await toBlobPromise(blob.type || 'image/jpeg', 0.72);
            if (!blob || blob.size <= 0 || blob.size > MAX_RAW_AVATAR_UPLOAD_BYTES) {
                throw new Error('Cropped image is still larger than 5 MB. Try zooming or choosing a smaller image.');
            }
        }

        return blob;
    }

    function uploadBlobToPresignedUrl(uploadUrl, blob, contentType, signal) {
        if (signal && signal.aborted) {
            return Promise.reject(new Error('Upload cancelled.'));
        }
        return new Promise((resolve, reject) => {
            const xhr = xhrFactory();
            activeXhr = xhr;
            const onAbort = () => {
                try {
                    if (typeof xhr.abort === 'function') xhr.abort();
                } catch {}
            };
            const detachAbort = () => {
                if (signal && typeof signal.removeEventListener === 'function') {
                    signal.removeEventListener('abort', onAbort);
                }
            };
            if (signal && typeof signal.addEventListener === 'function') {
                signal.addEventListener('abort', onAbort, { once: true });
            }

            xhr.open('PUT', uploadUrl);
            xhr.withCredentials = false;
            xhr.setRequestHeader('Content-Type', contentType);

            if (xhr.upload) {
                xhr.upload.onprogress = (evt) => {
                    if (evt && evt.lengthComputable && evt.total > 0) {
                        const pct = Math.round((evt.loaded / evt.total) * 100);
                        setUploadingUi(true, pct, `Uploading ${pct}%...`);
                    }
                };
            }

            xhr.onload = () => {
                detachAbort();
                if (activeXhr === xhr) activeXhr = null;
                if (xhr.status >= 200 && xhr.status < 300) {
                    resolve();
                } else {
                    reject(new Error(`Upload failed (${xhr.status}).`));
                }
            };
            xhr.onerror = () => {
                detachAbort();
                if (activeXhr === xhr) activeXhr = null;
                reject(new Error('Network error while uploading photo.'));
            };
            xhr.onabort = () => {
                detachAbort();
                if (activeXhr === xhr) activeXhr = null;
                reject(new Error('Upload cancelled.'));
            };
            xhr.send(blob);
        });
    }

    async function handleSave() {
        if (isUploading || !isOpenState) return;
        const myGen = openGeneration;
        const abortController = typeof AbortController === 'function' ? new AbortController() : null;
        uploadAbortController = abortController;
        const signal = abortController ? abortController.signal : undefined;
        const isCancelled = () => myGen !== openGeneration || !isOpenState || Boolean(signal && signal.aborted);

        setError('');

        try {
            setUploadingUi(true, 10, 'Preparing photo...');
            const blob = await exportCroppedBlob();
            if (isCancelled()) return;

            const actualMime = String(blob.type || 'image/webp').toLowerCase();
            const fileName = defaultFileNameForMime(actualMime);

            setUploadingUi(true, 25, 'Requesting upload slot...');
            const init = await resolvedApi.initiateAvatarUpload({
                fileName,
                contentType: actualMime,
                sizeBytes: blob.size
            }, { signal });
            if (isCancelled()) return;

            const uploadId = init?.uploadId ?? init?.UploadId;
            const uploadUrl = init?.uploadUrl ?? init?.UploadUrl;
            const requiredMime = init?.requiredContentType ?? init?.RequiredContentType ?? actualMime;
            if (!uploadId || !uploadUrl) {
                throw new Error('Upload initialization returned an invalid response.');
            }

            await uploadBlobToPresignedUrl(uploadUrl, blob, requiredMime, signal);
            if (isCancelled()) return;

            setUploadingUi(true, 95, 'Processing photo...');
            const result = await resolvedApi.completeAvatarUpload(uploadId, { signal });
            if (isCancelled()) return;

            close();
            if (typeof onUploaded === 'function') {
                await onUploaded(result);
            }
        } catch (err) {
            if ((err && (err.name === 'AbortError' || err.message === 'Upload cancelled.')) || isCancelled()) {
                return;
            }
            setUploadingUi(false, 0);
            const message = err?.message || 'Failed to upload profile photo.';
            setError(message);
            if (typeof onError === 'function') {
                onError(message);
            }
        } finally {
            if (uploadAbortController === abortController) {
                uploadAbortController = null;
            }
            if (myGen === openGeneration) {
                setUploadingUi(false, 0);
            }
        }
    }

    function bindUiListeners() {
        if (listenersBound || typeof document === 'undefined') return;
        listenersBound = true;

        const addTrackedListener = (target, type, handler, opts) => {
            if (!target || typeof target.addEventListener !== 'function') return;
            target.addEventListener(type, handler, opts);
            uiListenerCleanups.push(() => {
                if (typeof target.removeEventListener === 'function') {
                    target.removeEventListener(type, handler, opts);
                }
            });
        };

        const { modal, canvas, zoomSlider, saveBtn, cancelBtn, closeBtn } = getCropperElements();
        addTrackedListener(closeBtn, 'click', close);
        addTrackedListener(cancelBtn, 'click', close);
        addTrackedListener(saveBtn, 'click', handleSave);
        addTrackedListener(modal, 'click', (e) => {
            if (e.target === modal && !isUploading) {
                close();
            }
        });

        addTrackedListener(zoomSlider, 'input', () => {
            zoom = Math.max(1, Math.min(3, Number(zoomSlider.value) || 1));
            drawPreview();
        });

        if (canvas) {
            addTrackedListener(canvas, 'pointerdown', (e) => {
                if (isUploading) return;
                isDragging = true;
                dragStartX = e.clientX;
                dragStartY = e.clientY;
                dragBasePanX = panX;
                dragBasePanY = panY;
                if (typeof canvas.setPointerCapture === 'function' && e.pointerId !== undefined) {
                    try { canvas.setPointerCapture(e.pointerId); } catch {}
                }
            });

            addTrackedListener(canvas, 'pointermove', (e) => {
                if (!isDragging) return;
                panX = dragBasePanX + (e.clientX - dragStartX);
                panY = dragBasePanY + (e.clientY - dragStartY);
                drawPreview();
            });

            const endDrag = () => { isDragging = false; };
            addTrackedListener(canvas, 'pointerup', endDrag);
            addTrackedListener(canvas, 'pointercancel', endDrag);

            addTrackedListener(canvas, 'wheel', (e) => {
                if (isUploading) return;
                e.preventDefault();
                const delta = e.deltaY < 0 ? 0.08 : -0.08;
                zoom = Math.max(1, Math.min(3, Number((zoom + delta).toFixed(2))));
                if (zoomSlider) zoomSlider.value = String(zoom);
                drawPreview();
            }, { passive: false });

            addTrackedListener(canvas, 'keydown', (e) => {
                if (isUploading) return;
                const step = 10;
                if (e.key === 'ArrowLeft') { e.preventDefault(); panX -= step; drawPreview(); }
                else if (e.key === 'ArrowRight') { e.preventDefault(); panX += step; drawPreview(); }
                else if (e.key === 'ArrowUp') { e.preventDefault(); panY -= step; drawPreview(); }
                else if (e.key === 'ArrowDown') { e.preventDefault(); panY += step; drawPreview(); }
            });
        }

        addTrackedListener(document, 'keydown', (e) => {
            const { modal: m } = getCropperElements();
            if (!m || !m.classList || m.classList.contains('hidden')) return;
            if (e.key === 'Escape') {
                e.preventDefault();
                close();
            }
        });
    }

    function unbindUiListeners() {
        while (uiListenerCleanups.length > 0) {
            const cleanup = uiListenerCleanups.pop();
            try { cleanup(); } catch {}
        }
        listenersBound = false;
    }

    async function open(file) {
        const validation = validateAvatarSourceFile(file);
        if (!validation.ok) {
            if (typeof onError === 'function') onError(validation.error);
            return false;
        }

        const modal = ensureAvatarCropperModal();
        if (!modal) return false;
        bindUiListeners();

        const myGen = ++openGeneration;
        if (uploadAbortController && typeof uploadAbortController.abort === 'function') {
            try { uploadAbortController.abort(); } catch {}
        }
        uploadAbortController = null;
        if (activeXhr && typeof activeXhr.abort === 'function') {
            try { activeXhr.abort(); } catch {}
        }
        activeXhr = null;
        isDragging = false;
        isOpenState = true;

        lastFocusedElement = document.activeElement || null;
        setError('');
        setUploadingUi(false, 0);
        zoom = 1;
        panX = 0;
        panY = 0;

        try {
            const loaded = await loadDrawable(file, myGen);
            if (!loaded || myGen !== openGeneration || !isOpenState) {
                return false;
            }
            if (!sourceWidth || !sourceHeight || sourceWidth > MAX_IMAGE_DIMENSION || sourceHeight > MAX_IMAGE_DIMENSION ||
                (sourceWidth * sourceHeight) > MAX_IMAGE_PIXELS) {
                disposeSourceMedia();
                isOpenState = false;
                const msg = 'Image dimensions are too large. Maximum supported resolution is 8192x8192.';
                if (typeof onError === 'function') onError(msg);
                return false;
            }
        } catch (err) {
            if (myGen !== openGeneration || !isOpenState) {
                return false;
            }
            disposeSourceMedia();
            isOpenState = false;
            const msg = err?.message || 'Could not load selected image.';
            if (typeof onError === 'function') onError(msg);
            return false;
        }

        if (myGen !== openGeneration || !isOpenState) {
            return false;
        }

        const { zoomSlider, saveBtn } = getCropperElements();
        if (zoomSlider) zoomSlider.value = '1';
        modal.classList.remove('hidden');
        drawPreview();

        if (saveBtn && typeof saveBtn.focus === 'function') {
            saveBtn.focus();
        }
        return true;
    }

    return {
        open,
        close,
        isOpen: () => isOpenState,
        destroy: () => {
            close();
            unbindUiListeners();
        }
    };
}
