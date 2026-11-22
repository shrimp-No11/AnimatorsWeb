(function () {
    'use strict';

    let modal = null;
    let content = null;
    let closeButton = null;
    let previousFocus = null;

    let ytLoadTimer = null;
    const ytVolumeTimers = [];
    let ytVolumePollTimer = null;
    let ytActiveIframe = null;
    let ytVolumeCaptureFrom = 0;

    const INITIAL_YOUTUBE_VOLUME = 30;

    function getSavedYouTubeVolume() {
        try {
            const raw = localStorage.getItem('yt_volume');
            if (raw !== null) {
                const value = Number(raw);
                if (Number.isFinite(value) && value >= 0 && value <= 100) return value;
            }
        } catch (e) { }
        return INITIAL_YOUTUBE_VOLUME;
    }

    function saveYouTubeVolume(volume) {
        try { localStorage.setItem('yt_volume', String(volume)); } catch (e) { }
    }

    // YouTube присылает infoDelivery с громкостью, если один раз отправить «listening».
    // Первые секунды игнорируем: там плеер ещё может вернуть свою сохранённую
    // громкость до того, как применится наша стартовая команда.
    window.addEventListener('message', event => {
        if (event.origin !== 'https://www.youtube-nocookie.com' &&
            event.origin !== 'https://www.youtube.com') return;
        if (!ytActiveIframe || event.source !== ytActiveIframe.contentWindow) return;
        if (performance.now() < ytVolumeCaptureFrom) return;

        let data;
        try {
            data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        } catch (e) { return; }
        if (!data || data.event !== 'infoDelivery' || !data.info) return;

        const volume = data.info.volume;
        if (typeof volume !== 'number' || volume < 0 || volume > 100) return;
        if (volume === getSavedYouTubeVolume()) return;

        saveYouTubeVolume(volume);
        netLog('yt-volume-saved', { volume: volume });
    });

    function netLog(kind, data) {
        try {
            if (window.NetworkLog) window.NetworkLog.record(kind, data);
        } catch (e) { }
    }

    function pauseCurrentMedia() {
        // У обычного YouTube-iframe нет внешнего API паузы — выключаем звук,
        // убрав источник, если пользователь ушёл по ссылке из заголовка.
        const iframe = content?.querySelector('iframe.media-yt-frame');
        if (iframe) {
            try { iframe.src = 'about:blank'; } catch (e) { }
        }

        // HTML5 <video> — через DOM
        const video = content?.querySelector('video');
        if (video && !video.paused) {
            try { video.pause(); } catch (e) { }
        }

        // Аудио, если когда-нибудь появится
        const audio = content?.querySelector('audio');
        if (audio && !audio.paused) {
            try { audio.pause(); } catch (e) { }
        }
    }

    // Тестовый вариант: обычный <iframe> без YouTube IFrame API — на один
    // внешний скрипт и одну инициализацию меньше.
    function enableYouTubeMessages(iframe) {
        if (!iframe || !iframe.contentWindow) return;
        try {
            iframe.contentWindow.postMessage(
                JSON.stringify({ event: 'listening', id: iframe.id }),
                'https://www.youtube-nocookie.com'
            );
            netLog('yt-listening', { id: iframe.id });
        } catch (e) { }
    }

    function sendYouTubeCommand(iframe, func, args) {
        if (!iframe || !iframe.contentWindow) return;
        try {
            iframe.contentWindow.postMessage(
                JSON.stringify({ event: 'command', func: func, args: args || [], id: iframe.id }),
                'https://www.youtube-nocookie.com'
            );
            netLog('yt-command', { func: func, args: args || [], id: iframe.id });
        } catch (e) {
            netLog('yt-command-error', { func: func, error: String(e) });
        }
    }

    function sendYouTubeVolume(iframe, volume) {
        sendYouTubeCommand(iframe, 'setVolume', [volume]);
    }

    // YouTube не даёт задать громкость URL-параметром, но принимает команду
    // через postMessage. Шлём её несколько раз, пока плеер инициализируется,
    // иначе ранняя команда теряется и остаётся громкость 100%.
    function scheduleInitialVolume(iframe) {
        ytVolumeTimers.forEach(clearTimeout);
        ytVolumeTimers.length = 0;
        clearInterval(ytVolumePollTimer);

        ytActiveIframe = iframe;
        ytVolumeCaptureFrom = performance.now() + 7000;
        const initialVolume = getSavedYouTubeVolume();
        let hasSaved = false;
        try { hasSaved = localStorage.getItem('yt_volume') !== null; } catch (e) { }
        netLog('yt-volume-initial', { volume: initialVolume, hasSaved: hasSaved });

        [300, 800, 1600, 2800, 4500, 6500].forEach(delay => {
            ytVolumeTimers.push(setTimeout(() => {
                if (iframe.isConnected) sendYouTubeVolume(iframe, initialVolume);
            }, delay));
        });

        // Если infoDelivery не придёт сам — периодически спрашиваем текущую громкость
        ytVolumePollTimer = setInterval(() => {
            if (ytActiveIframe && ytActiveIframe.isConnected)
                sendYouTubeCommand(ytActiveIframe, 'getVolume', []);
        }, 2000);
    }

    function createYouTubeIframe(container, videoId, poster) {
        clearTimeout(ytLoadTimer);
        clearInterval(ytVolumePollTimer);
        ytVolumeTimers.forEach(clearTimeout);
        ytVolumeTimers.length = 0;
        let loaded = false;
        const startedAt = performance.now();

        const iframe = document.createElement('iframe');
        iframe.className = 'media-yt-frame';
        iframe.title = 'YouTube-видео';
        iframe.id = `media-yt-frame-${videoId}`;

        // Важно: все атрибуты — ДО присваивания src. Иначе навигация начинается
        // без Referer, и YouTube возвращает «Error 153 / неправильные настройки плеера».
        iframe.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
        iframe.allow = 'accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share';
        iframe.allowFullscreen = true;
        iframe.src = `https://www.youtube-nocookie.com/embed/${encodeURIComponent(videoId)}` +
            `?autoplay=1&rel=0&playsinline=1&enablejsapi=1` +
            `&origin=${encodeURIComponent(window.location.origin)}`;

        netLog('yt-iframe-create', { videoId: videoId, src: iframe.src, href: window.location.href });

        iframe.addEventListener('load', () => {
            loaded = true;
            clearTimeout(ytLoadTimer);
            netLog('yt-iframe-load', {
                videoId: videoId,
                ms: Math.round(performance.now() - startedAt),
                src: iframe.src
            });
            // Включаем обратную связь от плеера, чтобы получать infoDelivery с громкостью
            enableYouTubeMessages(iframe);
            if (poster && poster.isConnected) poster.remove();
        });

        container.insertBefore(iframe, poster);
        scheduleInitialVolume(iframe);

        ytLoadTimer = setTimeout(() => {
            if (!loaded && container.isConnected) {
                netLog('yt-iframe-timeout', {
                    videoId: videoId,
                    ms: Math.round(performance.now() - startedAt),
                    src: iframe.src,
                    detail: 'iframe не вызвал load за 4s — probable ERR_CONNECTION_CLOSED / блокировка'
                });
                showYouTubeFallback(container, videoId, poster);
            }
        }, 4000);
    }

    function showYouTubeFallback(container, videoId, poster) {
        if (!container.isConnected) return;
        const host = poster && poster.isConnected ? poster : container;
        if (host.querySelector('.media-yt-fallback')) return;
        netLog('yt-fallback-shown', { videoId: videoId });

        const link = document.createElement('a');
        link.className = 'media-yt-fallback';
        link.href = `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}`;
        link.target = '_blank';
        link.rel = 'noopener';
        link.textContent = 'Открыть на YouTube ↗';
        host.appendChild(link);
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

    function createMediaBlock(tagName, src, options = {}) {
        const viewport = document.createElement('div');
        viewport.className = 'media-zoom-viewport';

        const element = document.createElement('video' === tagName ? 'video' : tagName);
        element.className = 'work-modal-media';
        element.src = src;

        if (tagName === 'video') {
            element.controls = true;
            element.autoplay = true;
            element.playsInline = true;
        }
        if (options.alt) element.alt = options.alt;

        viewport.appendChild(element);
        attachZoom(viewport, element);

        return { viewport, element };
    }

    function attachZoom(viewport, mediaEl) {
        const MIN = 1;
        const MAX = 5;
        let scale = 1;
        let tx = 0, ty = 0;
        let dragging = false;
        let dragStartX = 0, dragStartY = 0;
        let pinchStartDist = 0;
        let pinchStartScale = 1;

        const apply = () => {
            if (scale === 1) {
                mediaEl.style.transform = '';
                mediaEl.classList.remove('zoomed');
                tx = 0; ty = 0;
            } else {
                mediaEl.style.transform = `translate(${tx}px, ${ty}px) scale(${scale})`;
                mediaEl.classList.add('zoomed');
            }
        };

        const reset = () => { scale = 1; apply(); };

        // Колесо мыши
        viewport.addEventListener('wheel', (e) => {
            if (e.ctrlKey) return; // браузерный zoom
            e.preventDefault();
            const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
            const next = Math.min(MAX, Math.max(MIN, scale * factor));
            if (next === scale) return;
            scale = next;
            apply();
        }, { passive: false });

        // Double-click — сброс
        viewport.addEventListener('dblclick', (e) => {
            e.preventDefault();
            reset();
        });

        // Pointer drag (мышь)
        mediaEl.addEventListener('pointerdown', (e) => {
            if (scale <= 1) return;
            if (e.pointerType === 'touch') return;
            dragging = true;
            dragStartX = e.clientX - tx;
            dragStartY = e.clientY - ty;
            try { mediaEl.setPointerCapture(e.pointerId); } catch { }
            e.preventDefault();
        });

        mediaEl.addEventListener('pointermove', (e) => {
            if (!dragging) return;
            tx = e.clientX - dragStartX;
            ty = e.clientY - dragStartY;
            apply();
        });

        mediaEl.addEventListener('pointerup', (e) => {
            dragging = false;
            try { mediaEl.releasePointerCapture(e.pointerId); } catch { }
        });

        // Touch: pinch + drag
        viewport.addEventListener('touchstart', (e) => {
            if (e.touches.length === 2) {
                const t1 = e.touches[0], t2 = e.touches[1];
                pinchStartDist = Math.hypot(t2.clientX - t1.clientX, t2.clientY - t1.clientY);
                pinchStartScale = scale;
            } else if (e.touches.length === 1 && scale > 1) {
                const t = e.touches[0];
                dragStartX = t.clientX - tx;
                dragStartY = t.clientY - ty;
                dragging = true;
            }
        }, { passive: true });

        viewport.addEventListener('touchmove', (e) => {
            if (e.touches.length === 2 && pinchStartDist > 0) {
                e.preventDefault();
                const t1 = e.touches[0], t2 = e.touches[1];
                const dist = Math.hypot(t2.clientX - t1.clientX, t2.clientY - t1.clientY);
                scale = Math.min(MAX, Math.max(MIN, pinchStartScale * (dist / pinchStartDist)));
                apply();
            } else if (e.touches.length === 1 && dragging) {
                e.preventDefault();
                const t = e.touches[0];
                tx = t.clientX - dragStartX;
                ty = t.clientY - dragStartY;
                apply();
            }
        }, { passive: false });

        viewport.addEventListener('touchend', () => {
            dragging = false;
            pinchStartDist = 0;
        });
    }
    function addMedia(tagName, src, options = {}) {
        const block = createMediaBlock(tagName, src, options);
        content.appendChild(block.viewport);
        return block;
    }

    let galleryGeneration = 0;

    function openWork(workUrl, messageUrl, participantId, mediaType, meta) {
        ensureModal();
        content.replaceChildren();
        galleryGeneration++;   // отменяем все «висящие» loadGallery

        const header = renderHeader(meta);
        if (header) content.appendChild(header);

        const ytId = extractYouTubeId(workUrl);
        const pid = Number(participantId) || 0;

        if (ytId) {
            netLog('media-open-youtube', { videoId: ytId, workUrl: workUrl, href: window.location.href });

            const wrap = document.createElement('div');
            wrap.className = 'media-yt-wrap';
            content.appendChild(wrap);

            // Сразу показываем превью с YouTube, пока грузится iframe
            const poster = document.createElement('div');
            poster.className = 'media-yt-poster';
            poster.innerHTML =
                `<img src="https://i.ytimg.com/vi/${encodeURIComponent(ytId)}/hqdefault.jpg" alt="" />` +
                '<span class="media-yt-spinner" aria-hidden="true"></span>';
            wrap.appendChild(poster);

            const posterImg = poster.querySelector('img');
            const posterStart = performance.now();
            posterImg.addEventListener('load', () => netLog('yt-poster-load', {
                videoId: ytId, ms: Math.round(performance.now() - posterStart)
            }));
            posterImg.addEventListener('error', () => netLog('yt-poster-error', {
                videoId: ytId, ms: Math.round(performance.now() - posterStart)
            }));

            createYouTubeIframe(wrap, ytId, poster);
        } else if (pid > 0) {
            const proxyUrl = '/Media/StreamWork/' + pid;
            netLog('media-open-local', { url: proxyUrl, workUrl: workUrl });
            let pathname = '';
            try {
                pathname = new URL(workUrl || '', window.location.origin).pathname.toLowerCase();
            } catch { }

            const isVideo = /\.(mp4|webm|mov|m4v)$/.test(pathname);
            const isImage = /\.(gif|png|jpe?g|webp|avif)$/.test(pathname);

            if (isVideo) {
                addMedia('video', proxyUrl);
                loadGallery(pid, galleryGeneration);
            } else if (isImage) {
                addMedia('img', proxyUrl, { alt: 'Работа участника' });
                loadGallery(pid, galleryGeneration);
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

    async function loadGallery(participantId, generation) {
        try {
            const resp = await fetch(`/Media/AttachmentInfo/${participantId}`);
            if (!resp.ok) return;
            if (generation !== galleryGeneration) return;

            const info = await resp.json();
            if (!info || info.count <= 1) return;
            if (generation !== galleryGeneration) return;

            let current = info.primaryIndex ?? 0;
            if (current < 0 || current >= info.count) current = 0;

            content.querySelectorAll('.media-modal-gallery').forEach(g => g.remove());

            const gallery = document.createElement('div');
            gallery.className = 'media-modal-gallery';
            gallery.innerHTML = `
            <button type="button" class="media-nav-prev" aria-label="Предыдущее">←</button>
            <span class="media-counter"></span>
            <button type="button" class="media-nav-next" aria-label="Следующее">→</button>
        `;

            const prev = gallery.querySelector('.media-nav-prev');
            const next = gallery.querySelector('.media-nav-next');
            const counter = gallery.querySelector('.media-counter');

            const renderAt = (index) => {
                if (generation !== galleryGeneration) return;
                current = index;
                const item = info.items[index];
                if (!item) return;

                const url = `/Media/StreamAttachment/${participantId}/${index}`;

                const existing = content.querySelector('.media-zoom-viewport');
                if (existing) existing.remove();

                const block = createMediaBlock(
                    item.isVideo ? 'video' : 'img',
                    url,
                    item.isVideo ? {} : { alt: 'Работа участника' }
                );
                content.insertBefore(block.viewport, gallery);

                counter.textContent = `${index + 1} / ${info.count}`;
                prev.disabled = index <= 0;
                next.disabled = index >= info.count - 1;
            };

            prev.addEventListener('click', () => { if (current > 0) renderAt(current - 1); });
            next.addEventListener('click', () => { if (current < info.count - 1) renderAt(current + 1); });

            content.appendChild(gallery);
            renderAt(current);
        } catch { }
    }



    function closeModal() {
        if (!modal) return;

        clearTimeout(ytLoadTimer);
        ytVolumeTimers.forEach(clearTimeout);
        ytVolumeTimers.length = 0;
        clearInterval(ytVolumePollTimer);
        ytVolumePollTimer = null;
        ytActiveIframe = null;
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

    // Автоматически вешаем обработчик на все .js-open-work
    function attachWorkButtons() {
        document.querySelectorAll('.js-open-work').forEach(button => {
            // Защита от двойной навески
            if (button.dataset.workBtnBound === '1') return;
            button.dataset.workBtnBound = '1';

            button.addEventListener('click', () => {
                let meta = null;
                if (button.dataset.meta) {
                    try { meta = JSON.parse(button.dataset.meta); } catch (e) { meta = null; }
                }
                openWork(
                    button.dataset.workUrl || '',
                    button.dataset.messageUrl || '',
                    Number(button.dataset.participantId) || 0,
                    button.dataset.mediaType || '',
                    meta
                );
            });
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', attachWorkButtons);
    } else {
        attachWorkButtons();
    }

    // Для динамических карточек (модераторская — рисует DOM через JS)
    window.MediaModalAttach = attachWorkButtons;


    // Глобальный доступ
    window.MediaModal = { openWork, closeModal };
    // Для старых onclick в partial-вьюхах
    window.openWork = openWork;



    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[c]));
    }

    function renderHeader(meta) {
        if (!meta) return null;

        const authors = Array.isArray(meta.authors) ? meta.authors : [];
        const jamNumber = meta.jamNumber;
        const theme = meta.theme;

        if (authors.length === 0 && !jamNumber) return null;

        const parts = [];

        if (authors.length > 0) {
            const authorsHtml = authors
                .map(a => {
                    if (!a) return null;
                    const name = escapeHtml((a.name || '').trim());
                    const id = (a.discordId || '').trim();
                    if (!name && !id) return null;
                    if (id) {
                        return `<a href="/User/PublicProfile/${encodeURIComponent(id)}" target="_blank" rel="noopener">${name || 'Участник'}</a>`;
                    }
                    return name;
                })
                .filter(Boolean)
                .join(', ');
            if (authorsHtml) parts.push(authorsHtml);
        }

        if (jamNumber) {
            const jamText = theme
                ? `#${jamNumber} ${escapeHtml(theme)}`
                : `#${jamNumber}`;
            const eventType = String(meta.eventType || 'AniJam');
            const detailsUrl = `/Events/Details?type=${encodeURIComponent(eventType)}&number=${encodeURIComponent(jamNumber)}`;
            parts.push(`<a href="${detailsUrl}" target="_blank" rel="noopener">${jamText}</a>`);
        }

        if (parts.length === 0) return null;

        const header = document.createElement('div');
        header.className = 'media-modal-header';
        header.innerHTML = parts.join(' <span class="media-modal-sep">—</span> ');

        // ⬇⬇⬇ НОВОЕ: пауза при клике по ссылке в заголовке
        header.querySelectorAll('a').forEach(link => {
            link.addEventListener('click', () => {
                pauseCurrentMedia();
            });
        });

        return header;
    }

}
)();

