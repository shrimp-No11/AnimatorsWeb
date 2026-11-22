(function () {
    'use strict';

    // Лёгкий логгер сетевой активности браузера.
    // Включается: ?netlog=1 в адресе или NetworkLog.enable() в консоли.
    const MAX_ENTRIES = 300;
    const entries = [];
    let enabled = false;
    let panel = null;
    let panelBody = null;

    function isEnabledByUrl() {
        try {
            return new URLSearchParams(window.location.search).has('netlog') ||
                localStorage.getItem('media_netlog') === '1';
        } catch (e) { return false; }
    }

    function fmtTime(t) {
        const d = new Date();
        return d.toTimeString().slice(0, 8) + '.' + String(d.getMilliseconds()).padStart(3, '0');
    }

    function renderPanel() {
        if (!panelBody) return;
        const lines = entries.slice(-60).map(e => {
            const parts = [`[${e.time}]`, e.kind];
            if (e.url) parts.push(shortUrl(e.url));
            if (e.ms != null) parts.push(e.ms + 'ms');
            if (e.status != null) parts.push('status=' + e.status);
            if (e.error) parts.push(String(e.error));
            if (e.detail) parts.push(typeof e.detail === 'string' ? e.detail : JSON.stringify(e.detail));
            return parts.join(' ');
        });
        panelBody.textContent = lines.join('\n');
        panelBody.scrollTop = panelBody.scrollHeight;
    }

    function shortUrl(url) {
        try {
            const u = new URL(url, window.location.href);
            return u.host + u.pathname + u.search;
        } catch (e) { return String(url); }
    }

    function record(kind, data) {
        const entry = Object.assign({ time: fmtTime(), kind: kind }, data || {});
        entries.push(entry);
        if (entries.length > MAX_ENTRIES) entries.shift();

        if (enabled) {
            console.log('[NET]', kind, data || '');
            renderPanel();
        }
        return entry;
    }

    function ensurePanel() {
        if (panel || !enabled) return;
        panel = document.createElement('details');
        panel.id = 'netlog-panel';
        panel.style.cssText =
            'position:fixed;left:12px;bottom:12px;z-index:9999;max-width:min(780px,95vw);' +
            'background:rgba(10,10,16,.94);color:#d8ffe0;border:1px solid #3a7d4f;' +
            'border-radius:8px;font:12px/1.4 ui-monospace,monospace;padding:6px 10px;';
        panel.innerHTML = '<summary style="cursor:pointer;">Сеть: логи запросов</summary>';
        panelBody = document.createElement('pre');
        panelBody.style.cssText = 'margin:6px 0 0;max-height:280px;overflow:auto;white-space:pre-wrap;word-break:break-all;';
        panel.appendChild(panelBody);
        document.body.appendChild(panel);
        renderPanel();
    }

    function enable() {
        if (enabled) return;
        enabled = true;
        try { localStorage.setItem('media_netlog', '1'); } catch (e) { }
        ensurePanel();
        var conn = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
        record('logger-enabled', {
            href: window.location.href,
            connection: conn ? {
                effectiveType: conn.effectiveType,
                downlink: conn.downlink,
                rtt: conn.rtt,
                saveData: conn.saveData
            } : null
        });
    }

    function disable() {
        enabled = false;
        try { localStorage.removeItem('media_netlog'); } catch (e) { }
        if (panel) { panel.remove(); panel = null; panelBody = null; }
    }

    // === Наблюдаем за всеми ресурсами (script, img, iframe, fetch и т.д.) ===
    try {
        const observer = new PerformanceObserver(list => {
            for (const e of list.getEntries()) {
                if (!e.name || e.name.startsWith('data:')) continue;
                record('resource', {
                    url: e.name,
                    initiator: e.initiatorType,
                    ms: Math.round(e.duration),
                    start: Math.round(e.startTime),
                    size: e.transferSize || 0,
                    // responseEnd = 0 у кросс-доменных ресурсов без Timing-Allow-Origin
                    responseEnd: Math.round(e.responseEnd || 0),
                    encoded: e.encodedBodySize || 0
                });
            }
        });
        observer.observe({ type: 'resource', buffered: true });
    } catch (e) {
        record('observer-unavailable', { error: String(e) });
    }

    // === Ошибки загрузки тегов (img/script/iframe/link) и JS ===
    window.addEventListener('error', event => {
        const target = event.target;
        if (target && target !== window && target.tagName) {
            record('load-error', {
                tag: target.tagName,
                url: target.src || target.href || '',
                detail: 'ERR_CONNECTION_CLOSED / блокировка / таймаут (точная причина недоступна из JS)'
            });
            return;
        }
        record('js-error', {
            error: event.message,
            url: event.filename,
            detail: `line ${event.lineno}:${event.colno}`
        });
    }, true);

    window.addEventListener('unhandledrejection', event => {
        record('promise-rejection', { error: String(event.reason) });
    });

    window.NetworkLog = {
        entries: entries,
        record: record,
        enable: enable,
        disable: disable,
        clear: () => { entries.length = 0; renderPanel(); },
        dump: () => { console.table(entries); return entries.slice(); },
        get enabled() { return enabled; }
    };

    if (isEnabledByUrl()) enable();
    else record('logger-idle', { hint: '?netlog=1 или NetworkLog.enable()' });
})();
