(function () {
    'use strict';

    let modal = null;
    let content = null;
    let closeButton = null;
    let previousFocus = null;

    let currentYouTubePlayer = null;
    let ytApiLoading = false;
    const ytReadyQueue = [];

    // ===== YouTube: громкость =====
    function getInitialVolume() {
        const saved = localStorage.getItem('yt_volume');
        if (saved !== null) {
            const n = parseInt(saved, 10);
            if (!isNaN(n) && n >= 0 && n <= 100) return n;
        }
        const fallback = window.MEDIA_MODAL_INITIAL_VOLUME;
        return typeof fallback === 'number' ? fallback : 30;
    }

    function saveCurrentVolume() {
        if (currentYouTubePlayer && typeof currentYouTubePlayer.getVolume === 'function') {
            try {
                const v = currentYouTubePlayer.getVolume();
                if (v > 0) localStorage.setItem('yt_volume', Math.round(v));
            } catch (e) { }
        }
    }

    function ensureYouTubeApi(callback) {
        if (window.YT && window.YT.Player) {
            callback();
            return;
        }
        ytReadyQueue.push(callback);
        if (ytApiLoading) return;
        ytApiLoading = true;

        window.onYouTubeIframeAPIReady = () => {
            while (ytReadyQueue.length) ytReadyQueue.shift()();
        };

        const tag = document.createElement('script');
        tag.src = 'https://www.youtube.com/iframe_api';
        document.head.appendChild(tag);
    }

    function createYouTubePlayer(container, videoId) {
        if (currentYouTubePlayer) {
            try { currentYouTubePlayer.destroy(); } catch (e) { }
            currentYouTubePlayer = null;
        }

        const startVolume = getInitialVolume();

        ensureYouTubeApi(() => {
            currentYouTubePlayer = new YT.Player(container, {
                width: '100%',
                height: '100%',
                videoId: videoId,
                playerVars: {
                    autoplay: 1,
                    enablejsapi: 1,
                    origin: window.location.origin,
                    rel: 0
                },
                events: {
                    onReady: (e) => {
                        try { e.target.setVolume(startVolume); } catch (ex) { }
                    }
                }
            });
        });
    }

    // ===== Модалка =====
    function ensureModal() {
        if (modal) return;

        modal = document.getElementById('media-modal');
        if (!modal) {
            modal = document.createElement('div');
            modal.id = 'media-modal';
            modal.setAttribute('role', 'dialog');
            modal.setAttribute('aria-modal', 'true');
            modal.setAttribute('aria-label', 'Просмотр работы');
            modal.innerHTML =
                '<button type="button" class="media-modal-close" aria-label="Закрыть">×</button>' +
                '<div class="media-modal-content"></div>';
            document.body.appendChild(modal);

            closeButton = modal.querySelector('.media-modal-close');
            closeButton.addEventListener('click', closeModal);

            modal.addEventListener('click', (e) => {
                if (e.target === modal) closeModal();
            });
        } else {
            closeButton = modal.querySelector('.media-modal-close');
        }

        content = modal.querySelector('.media-modal-content');
    }

    // ===== Утилиты =====
    function extractYouTubeId(url) {
        if (!url) return null;
        try {
            const parsed = new URL(url, window.location.origin);
            const host = parsed.hostname.toLowerCase();

            if (host === 'youtu.be' || host.endsWith('.youtu.be')) {
                const id = parsed.pathname.split('/').filter(Boolean)[0];
                return /^[a-zA-Z0-9_-]{11}$/.test(id) ? id : null;
            }

            if (
                host === 'youtube.com' || host.endsWith('.youtube.com') ||
                host === 'youtube-nocookie.com' || host.endsWith('.youtube-nocookie.com')
            ) {
                const id = parsed.searchParams.get('v')
                    || parsed.pathname.match(/^\/(?:embed|shorts|live|v)\/([a-zA-Z0-9_-]{11})/)?.[1];
                return id && /^[a-zA-Z0-9_-]{11}$/.test(id) ? id : null;
            }
        } catch { return null; }
        return null;
    }

    function addMedia(tagName, src, options = {}) {
        const element = document.createElement(tagName);
        element.className = 'work-modal-media';
        element.src = src;

        if (tagName === 'video') {
            element.controls = true;
            element.autoplay = true;
            element.playsInline = true;
        }

        if (options.alt) element.alt = options.alt;

        content.appendChild(element);
    }

    // ===== Публичные методы =====
    function openWork(workUrl, messageUrl, participantId, mediaType) {
        ensureModal();
        content.replaceChildren();

        const ytId = extractYouTubeId(workUrl);
        const pid = Number(participantId) || 0;

        if (ytId) {
            const wrap = document.createElement('div');
            wrap.className = 'media-yt-wrap';
            content.appendChild(wrap);
            createYouTubePlayer(wrap, ytId);
        } else if (pid > 0) {
            const proxyUrl = '/Media/StreamWork/' + pid;
            let pathname = '';
            try {
                pathname = new URL(workUrl || '', window.location.origin).pathname.toLowerCase();
            } catch { }

            if (/\.(mp4|webm|mov|m4v)$/.test(pathname)) {
                addMedia('video', proxyUrl);
            } else if (/\.(gif|png|jpe?g|webp|avif)$/.test(pathname)) {
                addMedia('img', proxyUrl, { alt: 'Работа участника' });
            } else if (messageUrl) {
                window.open(messageUrl, '_blank', 'noopener,noreferrer');
                return;
            } else if (workUrl) {
                window.open(workUrl, '_blank', 'noopener,noreferrer');
                return;
            } else {
                return;
            }
        } else if (messageUrl) {
            window.open(messageUrl, '_blank', 'noopener,noreferrer');
            return;
        } else if (workUrl) {
            window.open(workUrl, '_blank', 'noopener,noreferrer');
            return;
        } else {
            return;
        }

        previousFocus = document.activeElement;
        modal.classList.add('is-open');
        closeButton.focus();
    }

    function closeModal() {
        if (!modal) return;

        saveCurrentVolume();

        if (currentYouTubePlayer) {
            try { currentYouTubePlayer.destroy(); } catch (e) { }
            currentYouTubePlayer = null;
        }

        modal.classList.remove('is-open');
        content.replaceChildren();

        if (previousFocus && typeof previousFocus.focus === 'function') {
            previousFocus.focus();
            previousFocus = null;
        }
    }

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && modal && modal.classList.contains('is-open')) {
            closeModal();
        }
    });

    // Глобальный доступ
    window.MediaModal = { openWork, closeModal };
    // Для старых onclick в partial-вьюхах
    window.openWork = openWork;
})();