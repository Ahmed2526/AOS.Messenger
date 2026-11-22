/**
 * AOS Messenger - Voice Recording Studio Page Controller
 *
 * Implements audio memo duration timer simulation, animated waveform frequency bars,
 * pause/resume controls, and R2 upload flow preview.
 *
 * Invariants:
 * - Safe DOM manipulation exclusively.
 * - Zero HTML string injection into live DOM.
 */

(() => {
    'use strict';

    let seconds = 24;
    const timerEl = document.getElementById('recordingTimer');
    const pauseBtn = document.getElementById('pauseRecordBtn');
    const waveformBars = document.querySelectorAll('#liveWaveform span');
    let isPaused = false;

    // Timer tick simulation
    const interval = setInterval(function () {
        if (!isPaused && timerEl) {
            seconds++;
            const m = Math.floor(seconds / 60);
            const s = seconds % 60;
            timerEl.textContent = m + ':' + (s < 10 ? '0' : '') + s;

            // Waveform subtle flutter animation
            waveformBars.forEach(bar => {
                const randomH = Math.floor(Math.random() * 24) + 8;
                bar.style.height = randomH + 'px';
            });
        }
    }, 1000);

    if (pauseBtn) {
        pauseBtn.addEventListener('click', function () {
            isPaused = !isPaused;
            this.setAttribute('title', isPaused ? 'Resume Recording' : 'Pause Recording');
        });
    }

    const previewBtn = document.getElementById('voicePreviewBtn');
    if (previewBtn) {
        previewBtn.addEventListener('click', () => {
            window.alert('Voice preview mode: review waveform and playback prior to sending.');
        });
    }

    const uploadBtn = document.getElementById('voiceUploadBtn');
    if (uploadBtn) {
        uploadBtn.addEventListener('click', () => {
            window.alert('Uploading voice note to Cloudflare R2 object storage...');
        });
    }

    // Clean up interval on page hide / unload
    window.addEventListener('pagehide', () => {
        clearInterval(interval);
    });
})();
