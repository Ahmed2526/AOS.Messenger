/**
 * AOS Messenger - Modular Media Upload-on-Send & Attachments Tray
 *
 * Implements:
 * - Client allowlist & size pre-validation with clipboard extension fallback.
 * - Pending attachments tray (max 5 files) with blob: thumbnails & Clean Clear/Remove.
 * - Drag-and-drop overlay using a dragDepth counter + Ctrl+V clipboard paste (skipping image paste when text/plain is non-empty).
 * - Background per-conversation Upload-on-Send job queue (max 2 concurrent uploads, native <progress>, no credentials on R2 XHR PUT).
 * - Partial-failure Retry (reusing verified MediaIds under 90 minutes, invalidating expired/409 items with fresh IdempotencyKey).
 * - Cancel with best-effort server cleanup, optimistic bubble deduplication against ReceiveMessage, and complete wipe on switch/logout.
 */

import {
    initiateMediaUpload,
    completeMediaUpload,
    cancelMediaUpload,
    sendMessage
} from './api-client.js';

export const MAX_ATTACHMENTS = 5;
export const MAX_CONCURRENT_UPLOADS = 2;
export const VERIFIED_MEDIA_TTL_MS = 90 * 60 * 1000;

export const MAX_IMAGE_BYTES = 15 * 1024 * 1024;
export const MAX_VIDEO_BYTES = 70 * 1024 * 1024;
export const MAX_DOCUMENT_BYTES = 50 * 1024 * 1024;

const ALLOWED_EXTENSION_SPECS = new Map([
    ['.jpg', { kind: 'image', canonicalMime: 'image/jpeg', maxBytes: MAX_IMAGE_BYTES, mimes: new Set(['image/jpeg', 'image/jpg']) }],
    ['.jpeg', { kind: 'image', canonicalMime: 'image/jpeg', maxBytes: MAX_IMAGE_BYTES, mimes: new Set(['image/jpeg', 'image/jpg']) }],
    ['.png', { kind: 'image', canonicalMime: 'image/png', maxBytes: MAX_IMAGE_BYTES, mimes: new Set(['image/png']) }],
    ['.webp', { kind: 'image', canonicalMime: 'image/webp', maxBytes: MAX_IMAGE_BYTES, mimes: new Set(['image/webp']) }],
    ['.gif', { kind: 'image', canonicalMime: 'image/gif', maxBytes: MAX_IMAGE_BYTES, mimes: new Set(['image/gif']) }],

    ['.mp4', { kind: 'video', canonicalMime: 'video/mp4', maxBytes: MAX_VIDEO_BYTES, mimes: new Set(['video/mp4']) }],
    ['.webm', { kind: 'video', canonicalMime: 'video/webm', maxBytes: MAX_VIDEO_BYTES, mimes: new Set(['video/webm']) }],
    ['.mov', { kind: 'video', canonicalMime: 'video/quicktime', maxBytes: MAX_VIDEO_BYTES, mimes: new Set(['video/quicktime']) }],

    ['.pdf', { kind: 'document', canonicalMime: 'application/pdf', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/pdf']) }],
    ['.doc', { kind: 'document', canonicalMime: 'application/msword', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/msword']) }],
    ['.docx', { kind: 'document', canonicalMime: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/vnd.openxmlformats-officedocument.wordprocessingml.document']) }],
    ['.xls', { kind: 'document', canonicalMime: 'application/vnd.ms-excel', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/vnd.ms-excel']) }],
    ['.xlsx', { kind: 'document', canonicalMime: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/vnd.openxmlformats-officedocument.spreadsheetml.sheet']) }],
    ['.ppt', { kind: 'document', canonicalMime: 'application/vnd.ms-powerpoint', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/vnd.ms-powerpoint']) }],
    ['.pptx', { kind: 'document', canonicalMime: 'application/vnd.openxmlformats-officedocument.presentationml.presentation', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/vnd.openxmlformats-officedocument.presentationml.presentation']) }],
    ['.txt', { kind: 'document', canonicalMime: 'text/plain', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['text/plain']) }],
    ['.csv', { kind: 'document', canonicalMime: 'text/csv', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['text/csv', 'application/vnd.ms-excel', 'text/x-csv', 'application/csv']) }],
    ['.zip', { kind: 'document', canonicalMime: 'application/zip', maxBytes: MAX_DOCUMENT_BYTES, mimes: new Set(['application/zip', 'application/x-zip-compressed', 'multipart/x-zip']) }]
]);

const FALLBACK_EXT_BY_MIME = new Map([
    ['image/jpeg', '.jpg'],
    ['image/jpg', '.jpg'],
    ['image/png', '.png'],
    ['image/webp', '.webp'],
    ['image/gif', '.gif'],
    ['video/mp4', '.mp4'],
    ['video/webm', '.webm'],
    ['video/quicktime', '.mov'],
    ['application/pdf', '.pdf'],
    ['application/msword', '.doc'],
    ['application/vnd.openxmlformats-officedocument.wordprocessingml.document', '.docx'],
    ['application/vnd.ms-excel', '.xls'],
    ['application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', '.xlsx'],
    ['application/vnd.ms-powerpoint', '.ppt'],
    ['application/vnd.openxmlformats-officedocument.presentationml.presentation', '.pptx'],
    ['text/plain', '.txt'],
    ['text/csv', '.csv'],
    ['application/zip', '.zip']
]);

function normalizeId(val) {
    if (!val) return '';
    return String(val).trim().toLowerCase();
}

function createUuid() {
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
        return crypto.randomUUID();
    }
    return `id-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

/**
 * Formats a byte count into human-readable B / KB / MB / GB text.
 * @param {number} bytes
 * @returns {string}
 */
export function formatFileSize(bytes) {
    const n = Number(bytes) || 0;
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1).replace(/\.0$/, '')} KB`;
    if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1).replace(/\.0$/, '')} MB`;
    return `${(n / (1024 * 1024 * 1024)).toFixed(2).replace(/\.00$/, '')} GB`;
}

/**
 * Validates a File before queuing in the pending tray, applying clipboard MIME extension
 * fallback and allowlist/size bounds.
 * @param {File|{ name?: string, type?: string, size?: number }} file
 * @returns {{ ok: boolean, error?: string, kind?: string, fileName?: string, contentType?: string, sizeBytes?: number }}
 */
export function validateSelectedFile(file) {
    if (!file || typeof file !== 'object') {
        return { ok: false, error: 'Invalid file selection.' };
    }

    const sizeBytes = Number(file.size ?? 0);
    if (!Number.isFinite(sizeBytes) || sizeBytes <= 0) {
        return { ok: false, error: 'Selected file cannot be empty (0 bytes).' };
    }

    const rawMime = String(file.type || '').split(';')[0].trim().toLowerCase();
    let rawName = String(file.name || '').trim();
    const lastDot = rawName.lastIndexOf('.');
    let ext = lastDot > 0 ? rawName.slice(lastDot).toLowerCase() : '';

    if (!ext || ext === '.') {
        const fallbackExt = FALLBACK_EXT_BY_MIME.get(rawMime);
        if (!fallbackExt) {
            return { ok: false, error: `Unsupported file type (${rawName || 'file'}).` };
        }
        ext = fallbackExt;
        const base = rawName ? rawName.replace(/\.+$/, '') : 'clipboard-attachment';
        rawName = `${base || 'clipboard-attachment'}${ext}`;
    }

    const spec = ALLOWED_EXTENSION_SPECS.get(ext);
    if (!spec) {
        return { ok: false, error: `File extension '${ext}' is not supported.` };
    }

    if (rawMime && rawMime !== 'application/octet-stream' && !spec.mimes.has(rawMime)) {
        return { ok: false, error: `MIME type '${rawMime}' does not match extension '${ext}'.` };
    }

    if (sizeBytes > spec.maxBytes) {
        return {
            ok: false,
            error: `File '${rawName}' exceeds maximum allowed size (${formatFileSize(spec.maxBytes)}).`
        };
    }

    return {
        ok: true,
        kind: spec.kind,
        fileName: rawName,
        contentType: spec.canonicalMime,
        sizeBytes
    };
}

function createBlobPreviewUrl(file, kind) {
    if (kind !== 'image' && kind !== 'video') return null;
    if (typeof URL !== 'undefined' && typeof URL.createObjectURL === 'function') {
        try {
            return URL.createObjectURL(file);
        } catch {
            return null;
        }
    }
    return null;
}

function revokeBlobUrl(url) {
    if (!url) return;
    if (typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
        try {
            URL.revokeObjectURL(url);
        } catch {}
    }
}

function clearElement(el) {
    if (!el) return;
    while (el.firstChild) {
        el.removeChild(el.firstChild);
    }
}

/**
 * Creates the per-conversation media uploader & attachments tray controller.
 * @param {{
 *   state: object,
 *   api?: object,
 *   createXhr?: Function|null,
 *   onError?: Function|null,
 *   onMessageSent?: Function|null,
 *   onUnauthorized?: Function|null,
 *   nowFn?: Function
 * }} options
 */
export function createMediaUploader({
    state,
    api = { initiateMediaUpload, completeMediaUpload, cancelMediaUpload, sendMessage },
    createXhr = null,
    onError = null,
    onMessageSent = null,
    onUnauthorized = null,
    nowFn = () => Date.now()
} = {}) {
    let locked = true;
    let menuOpen = false;
    let dragDepth = 0;
    let isTerminated = false;

    const trayItems = [];
    const jobsByConversation = new Map();
    const jobsById = new Map();
    const progressNodesByItemKey = new Map();

    function getAttachBtn() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('composer-attach-btn')
            : null;
    }

    function getAttachMenu() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('composer-attach-menu')
            : null;
    }

    function getMediaInput() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('media-file-input')
            : null;
    }

    function getDocumentInput() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('document-file-input')
            : null;
    }

    function getTrayEl() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('composer-attachments-tray')
            : null;
    }

    function getDropzoneEl() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('chat-dropzone-overlay')
            : null;
    }

    function getPendingContainerEl() {
        return typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('pending-uploads-container')
            : null;
    }

    function reportError(msg) {
        if (typeof onError === 'function' && msg) {
            onError(msg);
        }
    }

    function closeMenu() {
        menuOpen = false;
        const menu = getAttachMenu();
        const btn = getAttachBtn();
        if (menu && menu.classList) menu.classList.add('hidden');
        if (btn && typeof btn.setAttribute === 'function') btn.setAttribute('aria-expanded', 'false');
    }

    function toggleMenu() {
        if (locked || isTerminated) return;
        menuOpen = !menuOpen;
        const menu = getAttachMenu();
        const btn = getAttachBtn();
        if (menu && menu.classList) menu.classList.toggle('hidden', !menuOpen);
        if (btn && typeof btn.setAttribute === 'function') btn.setAttribute('aria-expanded', menuOpen ? 'true' : 'false');
    }

    function hideDropzone() {
        dragDepth = 0;
        const dz = getDropzoneEl();
        if (dz && dz.classList) dz.classList.add('hidden');
    }

    function renderTray() {
        const trayEl = getTrayEl();
        if (!trayEl || typeof document === 'undefined') return;

        clearElement(trayEl);
        if (trayItems.length === 0) {
            trayEl.classList.add('hidden');
            return;
        }

        trayEl.classList.remove('hidden');

        const headerRow = document.createElement('div');
        headerRow.className = 'flex items-center justify-between mb-1.5 text-[11px] text-[#6E6560] dark:text-[#C9C0B9]';

        const countSpan = document.createElement('span');
        countSpan.className = 'font-semibold';
        countSpan.textContent = `${trayItems.length} of ${MAX_ATTACHMENTS} attached`;
        headerRow.appendChild(countSpan);

        const clearBtn = document.createElement('button');
        clearBtn.type = 'button';
        clearBtn.setAttribute('data-tray-clear', 'true');
        clearBtn.className = 'text-xs font-semibold text-red-600 dark:text-red-400 hover:underline cursor-pointer';
        clearBtn.textContent = 'Clear all';
        headerRow.appendChild(clearBtn);

        trayEl.appendChild(headerRow);

        const strip = document.createElement('div');
        strip.className = 'flex items-center gap-2 overflow-x-auto pb-1';

        for (const item of trayItems) {
            const chip = document.createElement('div');
            chip.className = 'relative shrink-0 flex items-center gap-2 rounded-xl border border-[#E3DCD7] dark:border-[#514D48] bg-white dark:bg-[#3A3835] p-1.5 pr-7 max-w-[200px]';
            chip.setAttribute('data-tray-item-id', item.id);

            if (item.kind === 'image' && item.previewUrl) {
                const thumb = document.createElement('img');
                thumb.src = item.previewUrl;
                thumb.alt = item.fileName;
                thumb.className = 'w-10 h-10 rounded-lg object-cover shrink-0';
                chip.appendChild(thumb);
            } else {
                const iconBox = document.createElement('div');
                iconBox.className = 'w-10 h-10 rounded-lg bg-[#F0E9DF] dark:bg-[#45423E] flex items-center justify-center shrink-0 text-[#735761] dark:text-[#E1BDC9]';
                const icon = document.createElement('span');
                icon.className = 'material-symbols-outlined text-[20px]';
                icon.textContent = item.kind === 'video' ? 'movie' : 'description';
                iconBox.appendChild(icon);
                chip.appendChild(iconBox);
            }

            const metaCol = document.createElement('div');
            metaCol.className = 'min-w-0';

            const nameEl = document.createElement('div');
            nameEl.className = 'text-[11px] font-semibold text-[#2B2725] dark:text-[#F0E9E2] truncate';
            nameEl.textContent = item.fileName;
            metaCol.appendChild(nameEl);

            const sizeEl = document.createElement('div');
            sizeEl.className = 'text-[10px] text-[#6E6560] dark:text-[#C9C0B9]';
            sizeEl.textContent = formatFileSize(item.sizeBytes);
            metaCol.appendChild(sizeEl);

            chip.appendChild(metaCol);

            const removeBtn = document.createElement('button');
            removeBtn.type = 'button';
            removeBtn.setAttribute('data-tray-remove', item.id);
            removeBtn.setAttribute('aria-label', `Remove ${item.fileName}`);
            removeBtn.className = 'absolute top-1 right-1 w-5 h-5 rounded-full flex items-center justify-center text-[#6E6560] dark:text-[#C9C0B9] hover:bg-[#F0E9DF] dark:hover:bg-[#45423E] cursor-pointer';
            const closeIcon = document.createElement('span');
            closeIcon.className = 'material-symbols-outlined text-[14px]';
            closeIcon.textContent = 'close';
            removeBtn.appendChild(closeIcon);
            chip.appendChild(removeBtn);

            strip.appendChild(chip);
        }

        trayEl.appendChild(strip);
    }

    function addFiles(fileList) {
        if (locked || isTerminated || !fileList) return 0;
        const files = Array.from(fileList);
        if (files.length === 0) return 0;

        let addedCount = 0;
        for (const file of files) {
            if (trayItems.length >= MAX_ATTACHMENTS) {
                reportError(`You can attach up to ${MAX_ATTACHMENTS} files per message.`);
                break;
            }

            const check = validateSelectedFile(file);
            if (!check.ok) {
                reportError(check.error);
                continue;
            }

            trayItems.push({
                id: createUuid(),
                file,
                fileName: check.fileName,
                contentType: check.contentType,
                sizeBytes: check.sizeBytes,
                kind: check.kind,
                previewUrl: createBlobPreviewUrl(file, check.kind)
            });
            addedCount++;
        }

        if (addedCount > 0) {
            renderTray();
        }
        return addedCount;
    }

    function removeTrayItem(itemId) {
        const idx = trayItems.findIndex(x => x.id === itemId);
        if (idx < 0) return;
        const [removed] = trayItems.splice(idx, 1);
        revokeBlobUrl(removed?.previewUrl);
        renderTray();
    }

    function clearTray() {
        while (trayItems.length > 0) {
            const removed = trayItems.pop();
            revokeBlobUrl(removed?.previewUrl);
        }
        renderTray();
    }

    function resetTrayForSwitch() {
        closeMenu();
        hideDropzone();
        clearTray();
    }

    function setLocked(isLocked) {
        locked = !!isLocked;
        const btn = getAttachBtn();
        if (btn) btn.disabled = locked;
        if (locked) {
            closeMenu();
            hideDropzone();
        }
    }

    function hasPendingFiles() {
        return trayItems.length > 0;
    }

    function renderPendingJobsForConversation(convId) {
        const containerEl = getPendingContainerEl();
        if (!containerEl || typeof document === 'undefined') return;

        progressNodesByItemKey.clear();
        clearElement(containerEl);

        const normConvId = normalizeId(convId);
        if (!normConvId) return;

        const jobs = jobsByConversation.get(normConvId) || [];
        if (jobs.length === 0) return;

        for (const job of jobs) {
            const row = document.createElement('div');
            row.className = 'flex justify-end items-center message-bubble-row optimistic-upload-row';
            row.setAttribute('data-upload-job-id', job.id);

            const bubble = document.createElement('div');
            bubble.className = 'message-bubble max-w-[75%] min-w-[220px] rounded-2xl rounded-tr-xs px-4 py-2.5 bg-[#B3929D]/90 text-white shadow-2xs';

            const statusHeader = document.createElement('div');
            statusHeader.className = 'flex items-center justify-between gap-3 mb-1.5 text-[11px] font-semibold';
            const statusText = document.createElement('span');
            if (job.status === 'failed') {
                statusText.textContent = job.errorMessage || 'Upload failed';
            } else if (job.status === 'sending') {
                statusText.textContent = 'Sending message...';
            } else {
                statusText.textContent = `Uploading ${job.items.length} attachment${job.items.length > 1 ? 's' : ''}...`;
            }
            statusHeader.appendChild(statusText);

            const actionBtns = document.createElement('div');
            actionBtns.className = 'flex items-center gap-1.5 shrink-0';
            if (job.status === 'failed' && job.retryable) {
                const retryBtn = document.createElement('button');
                retryBtn.type = 'button';
                retryBtn.setAttribute('data-upload-retry', job.id);
                retryBtn.className = 'px-2 py-0.5 rounded bg-white/20 hover:bg-white/30 text-[10px] font-bold cursor-pointer';
                retryBtn.textContent = 'Retry';
                actionBtns.appendChild(retryBtn);
            }

            const cancelBtn = document.createElement('button');
            cancelBtn.type = 'button';
            cancelBtn.setAttribute('data-upload-cancel', job.id);
            cancelBtn.className = 'px-2 py-0.5 rounded bg-black/20 hover:bg-black/30 text-[10px] font-semibold cursor-pointer';
            cancelBtn.textContent = 'Cancel';
            actionBtns.appendChild(cancelBtn);

            statusHeader.appendChild(actionBtns);
            bubble.appendChild(statusHeader);

            const itemsCol = document.createElement('div');
            itemsCol.className = 'space-y-1.5';

            for (const item of job.items) {
                const itemCard = document.createElement('div');
                itemCard.className = 'rounded-xl bg-black/15 px-2.5 py-1.5 text-xs';

                const topLine = document.createElement('div');
                topLine.className = 'flex items-center justify-between gap-2';

                const nameSpan = document.createElement('span');
                nameSpan.className = 'truncate font-medium text-[11px]';
                nameSpan.textContent = item.fileName;
                topLine.appendChild(nameSpan);

                const stateSpan = document.createElement('span');
                stateSpan.className = 'text-[10px] opacity-85 shrink-0';
                stateSpan.textContent = item.status === 'verified'
                    ? 'Ready'
                    : item.status === 'completing'
                        ? 'Verifying...'
                        : item.status === 'failed'
                            ? 'Failed'
                            : formatFileSize(item.sizeBytes);
                topLine.appendChild(stateSpan);

                itemCard.appendChild(topLine);

                const progressEl = document.createElement('progress');
                progressEl.max = 100;
                progressEl.value = item.status === 'verified' || item.status === 'completing' ? 100 : (item.progress || 0);
                progressEl.className = 'w-full h-1.5 mt-1 rounded-full overflow-hidden accent-white';
                itemCard.appendChild(progressEl);
                progressNodesByItemKey.set(`${job.id}:${item.index}`, progressEl);

                itemsCol.appendChild(itemCard);
            }

            bubble.appendChild(itemsCol);

            if (job.caption) {
                const capDiv = document.createElement('div');
                capDiv.className = 'text-xs leading-relaxed whitespace-pre-wrap break-words mt-1.5';
                capDiv.textContent = job.caption;
                bubble.appendChild(capDiv);
            }

            row.appendChild(bubble);
            containerEl.appendChild(row);
        }
    }

    function clearRenderedPendingJobs() {
        progressNodesByItemKey.clear();
        clearElement(getPendingContainerEl());
    }

    function renderIfActive(job) {
        if (!isTerminated && normalizeId(state?.activeConversationId) === normalizeId(job.conversationId)) {
            renderPendingJobsForConversation(job.conversationId);
        }
    }

    function removeJobAndRevokeBlobs(job) {
        if (!job) return;
        jobsById.delete(job.id);
        const normConvId = normalizeId(job.conversationId);
        const list = jobsByConversation.get(normConvId);
        if (list) {
            const idx = list.indexOf(job);
            if (idx >= 0) list.splice(idx, 1);
            if (list.length === 0) {
                jobsByConversation.delete(normConvId);
            }
        }

        for (const item of job.items) {
            revokeBlobUrl(item.previewUrl);
            item.previewUrl = null;
        }

        renderIfActive(job);
    }

    function putFileWithXhr(job, item, uploadUrl, requiredContentType) {
        return new Promise((resolve, reject) => {
            const xhr = typeof createXhr === 'function' ? createXhr() : new XMLHttpRequest();
            item.xhr = xhr;

            if (xhr.upload && typeof xhr.upload === 'object') {
                xhr.upload.onprogress = (evt) => {
                    if (!evt || !evt.lengthComputable || evt.total <= 0) return;
                    const pct = Math.max(0, Math.min(100, Math.round((evt.loaded / evt.total) * 100)));
                    item.progress = pct;
                    const progNode = progressNodesByItemKey.get(`${job.id}:${item.index}`);
                    if (progNode) {
                        progNode.value = pct;
                    }
                };
            }

            xhr.onload = () => {
                item.xhr = null;
                if (xhr.status >= 200 && xhr.status < 300) {
                    item.progress = 100;
                    resolve();
                } else {
                    const err = new Error(`Upload failed (${xhr.status}).`);
                    err.status = xhr.status;
                    reject(err);
                }
            };

            xhr.onerror = () => {
                item.xhr = null;
                const err = new Error('Network error while uploading media.');
                err.status = 0;
                reject(err);
            };

            xhr.onabort = () => {
                item.xhr = null;
                const err = new Error('Upload cancelled.');
                err.name = 'AbortError';
                reject(err);
            };

            xhr.open('PUT', uploadUrl, true);
            xhr.withCredentials = false;
            if (requiredContentType && typeof xhr.setRequestHeader === 'function') {
                xhr.setRequestHeader('Content-Type', requiredContentType);
            }
            xhr.send(item.file);
        });
    }

    async function uploadSingleItem(job, item) {
        if (job.cancelled || isTerminated) return;

        let initiatedMediaId = null;
        try {
            item.status = 'initiating';
            item.errorMessage = '';
            item.progress = 0;
            item.abortController = typeof AbortController === 'function' ? new AbortController() : null;
            renderIfActive(job);

            const initResp = await api.initiateMediaUpload(
                job.conversationId,
                {
                    fileName: item.fileName,
                    contentType: item.contentType,
                    sizeBytes: item.sizeBytes
                },
                { signal: item.abortController?.signal }
            );
            initiatedMediaId = initResp?.mediaId ?? initResp?.MediaId ?? null;
            if (job.cancelled || isTerminated) {
                if (initiatedMediaId) {
                    Promise.resolve(api.cancelMediaUpload(job.conversationId, initiatedMediaId)).catch(() => {});
                }
                return;
            }
            const uploadUrl = initResp?.uploadUrl ?? initResp?.UploadUrl;
            const requiredMime = initResp?.requiredContentType ?? initResp?.RequiredContentType ?? item.contentType;
            item.mediaId = initiatedMediaId;

            if (!initiatedMediaId || !uploadUrl) {
                throw new Error('Invalid upload initialization response.');
            }

            item.status = 'uploading';
            renderIfActive(job);

            await putFileWithXhr(job, item, uploadUrl, requiredMime);
            if (job.cancelled || isTerminated) return;

            item.status = 'completing';
            item.progress = 100;
            item.abortController = typeof AbortController === 'function' ? new AbortController() : null;
            renderIfActive(job);

            await api.completeMediaUpload(
                job.conversationId,
                initiatedMediaId,
                { signal: item.abortController?.signal }
            );
            if (job.cancelled || isTerminated) return;

            item.status = 'verified';
            item.verifiedAtMs = nowFn();
            item.errorMessage = '';
            item.retryable = true;
            renderIfActive(job);
        } catch (err) {
            if (job.cancelled || isTerminated || err?.name === 'AbortError') {
                return;
            }

            const status = Number(err?.status || 0);
            if (status === 401 || err?.code === 'UNAUTHORIZED') {
                if (typeof onUnauthorized === 'function') onUnauthorized();
                return;
            }

            if (initiatedMediaId && item.status !== 'completing') {
                Promise.resolve(api.cancelMediaUpload(job.conversationId, initiatedMediaId)).catch(() => {});
            }

            const nonRetryable = status === 400 || status === 403 || status === 413;
            item.mediaId = null;
            item.verifiedAtMs = null;
            item.status = 'failed';
            item.retryable = !nonRetryable;
            item.errorMessage = err?.message || 'Upload failed.';
            renderIfActive(job);
        } finally {
            item.abortController = null;
            item.xhr = null;
        }
    }

    async function processJob(job) {
        if (!job || job.cancelled || isTerminated) return;

        const now = nowFn();
        let anyInvalidated = false;
        for (const item of job.items) {
            if (item.status === 'verified' && (!item.verifiedAtMs || (now - item.verifiedAtMs) >= VERIFIED_MEDIA_TTL_MS)) {
                item.status = 'queued';
                item.mediaId = null;
                item.verifiedAtMs = null;
                item.progress = 0;
                item.retryable = true;
                item.errorMessage = '';
                anyInvalidated = true;
            }
        }
        if (anyInvalidated) {
            job.idempotencyKey = createUuid();
        }

        const pendingItems = job.items.filter(it => it.status !== 'verified');
        let nextIdx = 0;

        const runWorker = async () => {
            while (!job.cancelled && !isTerminated) {
                const idx = nextIdx++;
                if (idx >= pendingItems.length) break;
                await uploadSingleItem(job, pendingItems[idx]);
            }
        };

        const workerCount = Math.min(MAX_CONCURRENT_UPLOADS, pendingItems.length);
        if (workerCount > 0) {
            const workers = [];
            for (let i = 0; i < workerCount; i++) {
                workers.push(runWorker());
            }
            await Promise.all(workers);
        }

        if (job.cancelled || isTerminated) return;

        const failedItems = job.items.filter(it => it.status !== 'verified');
        if (failedItems.length > 0) {
            job.status = 'failed';
            job.retryable = failedItems.every(it => it.retryable);
            job.errorMessage = failedItems[0].errorMessage || 'One or more attachments failed.';
            renderIfActive(job);
            return;
        }

        job.status = 'sending';
        job.sendController = typeof AbortController === 'function' ? new AbortController() : null;
        renderIfActive(job);

        try {
            const payload = {
                content: job.caption || null,
                idempotencyKey: job.idempotencyKey,
                replyToMessageId: job.replyToMessageId,
                mediaIds: job.items.map(it => it.mediaId)
            };

            const sentMessage = await api.sendMessage(job.conversationId, payload, { signal: job.sendController?.signal });
            if (job.cancelled || isTerminated) return;

            removeJobAndRevokeBlobs(job);
            if (typeof onMessageSent === 'function') {
                onMessageSent(job.conversationId, sentMessage);
            }
        } catch (err) {
            if (job.cancelled || isTerminated || err?.name === 'AbortError') {
                return;
            }

            const status = Number(err?.status || 0);
            if (status === 401 || err?.code === 'UNAUTHORIZED') {
                if (typeof onUnauthorized === 'function') onUnauthorized();
                return;
            }

            if (status === 409 || err?.code === 'CONFLICT') {
                for (const item of job.items) {
                    item.mediaId = null;
                    item.verifiedAtMs = null;
                    item.status = 'failed';
                    item.progress = 0;
                    item.retryable = true;
                    item.errorMessage = 'Attachment expired or conflicted.';
                }
                job.idempotencyKey = createUuid();
                job.status = 'failed';
                job.retryable = true;
                job.errorMessage = err?.message || 'Attachments expired or conflicted. Retry to re-upload.';
            } else {
                const nonRetryable = status === 400 || status === 403 || status === 413;
                job.status = 'failed';
                job.retryable = !nonRetryable;
                job.errorMessage = err?.message || 'Failed to send attachments.';
            }
            renderIfActive(job);
        } finally {
            job.sendController = null;
        }
    }

    function startUploadJob({ conversationId, caption = '', replyTo = null } = {}) {
        if (!conversationId || trayItems.length === 0 || isTerminated) return null;

        const selected = trayItems.splice(0, trayItems.length);
        renderTray();

        const job = {
            id: createUuid(),
            conversationId: String(conversationId),
            caption: caption ? String(caption).trim() : '',
            replyToMessageId: replyTo ? String(replyTo.id) : null,
            idempotencyKey: createUuid(),
            status: 'uploading',
            errorMessage: '',
            retryable: true,
            cancelled: false,
            sendController: null,
            items: selected.map((it, index) => ({
                index,
                file: it.file,
                fileName: it.fileName,
                contentType: it.contentType,
                sizeBytes: it.sizeBytes,
                kind: it.kind,
                previewUrl: it.previewUrl,
                status: 'queued',
                progress: 0,
                mediaId: null,
                verifiedAtMs: null,
                retryable: true,
                errorMessage: '',
                xhr: null,
                abortController: null
            }))
        };

        const normConvId = normalizeId(job.conversationId);
        if (!jobsByConversation.has(normConvId)) {
            jobsByConversation.set(normConvId, []);
        }
        jobsByConversation.get(normConvId).push(job);
        jobsById.set(job.id, job);

        renderIfActive(job);
        job.promise = processJob(job);
        return job;
    }

    function retryJob(jobId) {
        const job = jobsById.get(jobId);
        if (!job || job.cancelled || isTerminated) return null;
        if (job.status !== 'failed' || !job.retryable) return null;

        const now = nowFn();
        let anyInvalidated = false;
        for (const item of job.items) {
            const isVerifiedFresh = item.status === 'verified'
                && item.mediaId
                && item.verifiedAtMs
                && (now - item.verifiedAtMs) < VERIFIED_MEDIA_TTL_MS;

            if (isVerifiedFresh) {
                continue;
            }

            if (item.status === 'verified') {
                anyInvalidated = true;
            }
            if (item.status === 'verified' || item.retryable) {
                item.status = 'queued';
                item.mediaId = null;
                item.verifiedAtMs = null;
                item.progress = 0;
                item.errorMessage = '';
            }
        }

        if (anyInvalidated) {
            job.idempotencyKey = createUuid();
        }

        job.status = 'uploading';
        job.errorMessage = '';
        renderIfActive(job);
        job.promise = processJob(job);
        return job;
    }

    async function cancelJob(jobId) {
        const job = jobsById.get(jobId);
        if (!job) return;

        job.cancelled = true;
        if (job.sendController) {
            try { job.sendController.abort(); } catch {}
        }

        const cleanupCalls = [];
        for (const item of job.items) {
            if (item.xhr && typeof item.xhr.abort === 'function') {
                try { item.xhr.abort(); } catch {}
            }
            if (item.abortController) {
                try { item.abortController.abort(); } catch {}
            }
            if (item.mediaId) {
                cleanupCalls.push(Promise.resolve(api.cancelMediaUpload(job.conversationId, item.mediaId)).catch(() => {}));
            }
        }

        removeJobAndRevokeBlobs(job);
        await Promise.allSettled(cleanupCalls);
    }

    function reconcileIncomingMessage(convId, message) {
        if (!convId || !message) return false;
        const rawAttachments = message.attachments ?? message.Attachments;
        if (!Array.isArray(rawAttachments) || rawAttachments.length === 0) return false;

        const normConvId = normalizeId(convId);
        const jobs = jobsByConversation.get(normConvId);
        if (!jobs || jobs.length === 0) return false;

        const incomingMediaIds = new Set(
            rawAttachments
                .map(a => normalizeId(a?.id ?? a?.Id ?? a?.mediaId ?? a?.MediaId))
                .filter(Boolean)
        );
        if (incomingMediaIds.size === 0) return false;

        let removedAny = false;
        for (const job of [...jobs]) {
            const matches = job.items.some(it => it.mediaId && incomingMediaIds.has(normalizeId(it.mediaId)));
            if (matches) {
                job.cancelled = true;
                removeJobAndRevokeBlobs(job);
                removedAny = true;
            }
        }
        return removedAny;
    }

    function abortAllAndWipe() {
        isTerminated = true;
        closeMenu();
        hideDropzone();
        clearTray();

        for (const job of [...jobsById.values()]) {
            job.cancelled = true;
            if (job.sendController) {
                try { job.sendController.abort(); } catch {}
            }
            for (const item of job.items) {
                if (item.xhr && typeof item.xhr.abort === 'function') {
                    try { item.xhr.abort(); } catch {}
                }
                if (item.abortController) {
                    try { item.abortController.abort(); } catch {}
                }
                revokeBlobUrl(item.previewUrl);
                item.previewUrl = null;
            }
        }

        jobsById.clear();
        jobsByConversation.clear();
        clearRenderedPendingJobs();
    }

    const onDocumentClick = (e) => {
        if (!e.target || typeof e.target.closest !== 'function') return;

        const attachBtn = e.target.closest('#composer-attach-btn');
        if (attachBtn) {
            e.preventDefault();
            e.stopPropagation();
            toggleMenu();
            return;
        }

        const choiceBtn = e.target.closest('[data-attach-choice]');
        if (choiceBtn) {
            e.preventDefault();
            const choice = choiceBtn.getAttribute('data-attach-choice');
            closeMenu();
            const input = choice === 'document' ? getDocumentInput() : getMediaInput();
            if (input && typeof input.click === 'function' && !locked) {
                input.click();
            }
            return;
        }

        const removeTrayBtn = e.target.closest('[data-tray-remove]');
        if (removeTrayBtn) {
            e.preventDefault();
            removeTrayItem(removeTrayBtn.getAttribute('data-tray-remove'));
            return;
        }

        const clearTrayBtn = e.target.closest('[data-tray-clear]');
        if (clearTrayBtn) {
            e.preventDefault();
            clearTray();
            return;
        }

        const retryBtn = e.target.closest('[data-upload-retry]');
        if (retryBtn) {
            e.preventDefault();
            retryJob(retryBtn.getAttribute('data-upload-retry'));
            return;
        }

        const cancelBtn = e.target.closest('[data-upload-cancel]');
        if (cancelBtn) {
            e.preventDefault();
            cancelJob(cancelBtn.getAttribute('data-upload-cancel'));
            return;
        }

        if (menuOpen && !e.target.closest('#composer-attach-menu')) {
            closeMenu();
        }
    };

    const onFileInputChange = (e) => {
        const input = e.target;
        if (!input || !input.files) return;
        addFiles(input.files);
        input.value = '';
    };

    const hasDraggedFiles = (dt) => {
        if (!dt || !dt.types) return false;
        return Array.from(dt.types).includes('Files');
    };

    const onDragEnter = (e) => {
        if (locked || isTerminated || !hasDraggedFiles(e.dataTransfer)) return;
        e.preventDefault();
        dragDepth++;
        const dz = getDropzoneEl();
        if (dz && dz.classList) dz.classList.remove('hidden');
    };

    const onDragOver = (e) => {
        if (locked || isTerminated || !hasDraggedFiles(e.dataTransfer)) return;
        e.preventDefault();
    };

    const onDragLeave = (e) => {
        if (!hasDraggedFiles(e.dataTransfer)) return;
        dragDepth = Math.max(0, dragDepth - 1);
        if (dragDepth === 0) {
            hideDropzone();
        }
    };

    const onDrop = (e) => {
        if (!hasDraggedFiles(e.dataTransfer)) return;
        e.preventDefault();
        hideDropzone();
        if (!locked && !isTerminated && e.dataTransfer?.files?.length) {
            addFiles(e.dataTransfer.files);
        }
    };

    const onPaste = (e) => {
        if (locked || isTerminated) return;
        const inputEl = typeof document !== 'undefined' && typeof document.getElementById === 'function'
            ? document.getElementById('message-content-input')
            : null;
        if (!inputEl || e.target !== inputEl) return;

        const clipboard = e.clipboardData;
        if (!clipboard) return;

        const plainText = typeof clipboard.getData === 'function' ? clipboard.getData('text/plain') : '';
        if (plainText && plainText.length > 0) {
            return;
        }

        const pastedFiles = [];
        if (clipboard.files && clipboard.files.length > 0) {
            for (const f of clipboard.files) {
                pastedFiles.push(f);
            }
        } else if (clipboard.items && clipboard.items.length > 0) {
            for (const item of clipboard.items) {
                if (item && item.kind === 'file' && typeof item.getAsFile === 'function') {
                    const f = item.getAsFile();
                    if (f) pastedFiles.push(f);
                }
            }
        }

        if (pastedFiles.length > 0) {
            if (typeof e.preventDefault === 'function') e.preventDefault();
            addFiles(pastedFiles);
        }
    };

    function init() {
        if (typeof document === 'undefined') return;
        if (typeof document.addEventListener === 'function') {
            document.addEventListener('click', onDocumentClick);
            document.addEventListener('paste', onPaste);
        }

        const mediaIn = getMediaInput();
        if (mediaIn && typeof mediaIn.addEventListener === 'function') {
            mediaIn.addEventListener('change', onFileInputChange);
        }

        const docIn = getDocumentInput();
        if (docIn && typeof docIn.addEventListener === 'function') {
            docIn.addEventListener('change', onFileInputChange);
        }

        const activePane = typeof document.getElementById === 'function'
            ? document.getElementById('chat-active-pane')
            : null;
        if (activePane && typeof activePane.addEventListener === 'function') {
            activePane.addEventListener('dragenter', onDragEnter);
            activePane.addEventListener('dragover', onDragOver);
            activePane.addEventListener('dragleave', onDragLeave);
            activePane.addEventListener('drop', onDrop);
        }
    }

    function destroy() {
        abortAllAndWipe();
        if (typeof document === 'undefined') return;
        if (typeof document.removeEventListener === 'function') {
            document.removeEventListener('click', onDocumentClick);
            document.removeEventListener('paste', onPaste);
        }

        const mediaIn = getMediaInput();
        if (mediaIn && typeof mediaIn.removeEventListener === 'function') {
            mediaIn.removeEventListener('change', onFileInputChange);
        }

        const docIn = getDocumentInput();
        if (docIn && typeof docIn.removeEventListener === 'function') {
            docIn.removeEventListener('change', onFileInputChange);
        }

        const activePane = typeof document.getElementById === 'function'
            ? document.getElementById('chat-active-pane')
            : null;
        if (activePane && typeof activePane.removeEventListener === 'function') {
            activePane.removeEventListener('dragenter', onDragEnter);
            activePane.removeEventListener('dragover', onDragOver);
            activePane.removeEventListener('dragleave', onDragLeave);
            activePane.removeEventListener('drop', onDrop);
        }
    }

    return {
        init,
        destroy,
        setLocked,
        addFiles,
        removeTrayItem,
        clearTray,
        hasPendingFiles,
        getTrayItems: () => [...trayItems],
        startUploadJob,
        retryJob,
        cancelJob,
        reconcileIncomingMessage,
        renderPendingJobsForConversation,
        clearRenderedPendingJobs,
        resetTrayForSwitch,
        abortAllAndWipe,
        getJobsForConversation: (convId) => [...(jobsByConversation.get(normalizeId(convId)) || [])]
    };
}
