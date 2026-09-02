/*
 * Enhanced PDF Reader for Jellyfin web client.
 * Replaces the minimal built-in pdfPlayer with a full reader:
 * continuous scroll, zoom, fit modes, page jump, rotate, keyboard + touch nav,
 * and per-document reading position memory.
 */
(function () {
    'use strict';

    if (window.__enhancedPdfReaderLoaded) { return; }
    window.__enhancedPdfReaderLoaded = true;

    var BASE = '/EnhancedPdfReader';
    var LS = {
        zoom: 'epdfr:zoomMode',
        mode: 'epdfr:mode',
        get pagePrefix() { return 'epdfr:page:'; }
    };

    /* ---- reading position: per user, stored on the server ---- */
    function apiUserId() {
        try { return (window.ApiClient && window.ApiClient.getCurrentUserId()) || null; } catch (e) { return null; }
    }

    function cacheKey(itemId) { return LS.pagePrefix + (apiUserId() || 'anon') + ':' + itemId; }

    // pre-1.1 bookmarks had no user in the key, so every account on this browser shared them.
    // They cannot be attributed to anyone now, so drop them instead of letting them leak across accounts.
    function purgeLegacyKeys() {
        try {
            var doomed = [];
            for (var i = 0; i < localStorage.length; i++) {
                var k = localStorage.key(i);
                if (k && /^epdfr:page:[0-9a-f]{32}$/i.test(k)) { doomed.push(k); }
            }
            for (var j = 0; j < doomed.length; j++) { localStorage.removeItem(doomed[j]); }
            if (doomed.length) { console.log('[EnhancedPdfReader] descartadas ' + doomed.length + ' marcas antiguas compartidas entre cuentas'); }
        } catch (e) { /* ignore */ }
    }
    purgeLegacyKeys();

    function progressUrl(itemId) { return window.ApiClient.getUrl('EnhancedPdfReader/Progress/' + itemId); }

    function saveProgress(itemId, page, numPages) {
        // localStorage is only a per-user cache for when the server is unreachable
        try { localStorage.setItem(cacheKey(itemId), String(page)); } catch (e) { /* ignore */ }
        if (!itemId || !window.ApiClient) { return; }
        try {
            window.ApiClient.ajax({
                type: 'POST',
                url: progressUrl(itemId),
                contentType: 'application/json',
                data: JSON.stringify({ Page: page, NumPages: numPages || 0 }),
                dataType: 'json',
                headers: { accept: 'application/json' }
            }).catch(function () { /* offline: the cache above still has it */ });
        } catch (e) { /* ignore */ }
    }

    function loadProgress(itemId) {
        return new Promise(function (resolve) {
            if (!itemId) { resolve(0); return; }
            var cached = parseInt(localStorage.getItem(cacheKey(itemId)), 10) || 0;
            if (!window.ApiClient) { resolve(cached); return; }
            window.ApiClient.ajax({
                type: 'GET',
                url: progressUrl(itemId),
                dataType: 'json',
                headers: { accept: 'application/json' }
            }).then(function (p) {
                var page = (p && p.Page) ? p.Page : 0;
                if (!page && cached > 1) {
                    // this account read it offline: push what the cache kept
                    page = cached;
                    saveProgress(itemId, page, 0);
                }
                resolve(page);
            }).catch(function () { resolve(cached); });
        });
    }

    var pdfjsLibPromise = null;
    function loadPdfJs() {
        if (!pdfjsLibPromise) {
            pdfjsLibPromise = import(BASE + '/pdf.mjs').then(function (lib) {
                lib.GlobalWorkerOptions.workerSrc = BASE + '/pdf.worker.mjs';
                return lib;
            });
        }
        return pdfjsLibPromise;
    }

    var pageFlipPromise = null;
    function loadPageFlip() {
        if (!pageFlipPromise) {
            pageFlipPromise = new Promise(function (resolve, reject) {
                if (window.St && window.St.PageFlip) { resolve(window.St.PageFlip); return; }
                var s = document.createElement('script');
                s.src = BASE + '/page-flip.js';
                s.onload = function () {
                    if (window.St && window.St.PageFlip) { resolve(window.St.PageFlip); }
                    else { reject(new Error('StPageFlip no disponible')); }
                };
                s.onerror = function () { reject(new Error('No se pudo cargar page-flip.js')); };
                document.head.appendChild(s);
            });
        }
        return pageFlipPromise;
    }

    /* ---- capture the PDF download URL the built-in player is about to open ---- */
    var lastPdf = { url: null, itemId: null, ts: 0 };

    function remember(url) {
        if (typeof url !== 'string') {
            try { url = String(url); } catch (e) { return; }
        }
        var m = url.match(/\/Items\/([0-9a-fA-F-]{16,})\/Download/);
        if (m) {
            lastPdf = { url: url, itemId: m[1].replace(/-/g, ''), ts: Date.now() };
        }
    }

    var _fetch = window.fetch;
    window.fetch = function (input, init) {
        try { remember(typeof input === 'string' ? input : (input && input.url)); } catch (e) { /* ignore */ }
        return _fetch.apply(this, arguments);
    };
    var _open = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        try { remember(url); } catch (e) { /* ignore */ }
        return _open.apply(this, arguments);
    };

    /* ---- intercept the built-in player dialog ---- */
    var opening = false;

    function closeBuiltIn(node) {
        try {
            var exit = node.querySelector('.btnExit');
            if (exit) { exit.click(); return; }
        } catch (e) { /* ignore */ }
        try { node.dispatchEvent(new Event('close')); } catch (e) { /* ignore */ }
        if (node.parentNode) { node.parentNode.removeChild(node); }
    }

    function handleBuiltIn(dlg) {
        if (opening) { return; }
        opening = true;
        // hide the bare built-in player while we wait for its download URL
        var prevVis = dlg.style.visibility;
        dlg.style.visibility = 'hidden';
        var startTs = Date.now();
        var timer = setInterval(function () {
            var got = (lastPdf.url && lastPdf.ts >= startTs - 4000);
            if (got) {
                clearInterval(timer);
                var info = { url: lastPdf.url, itemId: lastPdf.itemId };
                closeBuiltIn(dlg);
                openReader(info.url, info.itemId);
            } else if (Date.now() - startTs > 6000) {
                clearInterval(timer);
                // give up: let the built-in player work normally
                dlg.style.visibility = prevVis || '';
                opening = false;
            }
        }, 100);
    }

    var mo = new MutationObserver(function (muts) {
        for (var i = 0; i < muts.length; i++) {
            var added = muts[i].addedNodes;
            for (var j = 0; j < added.length; j++) {
                var n = added[j];
                if (n.nodeType !== 1) { continue; }
                var dlg = n.id === 'pdfPlayer' ? n : (n.querySelector && n.querySelector('#pdfPlayer'));
                if (dlg) { handleBuiltIn(dlg); }
            }
        }
    });
    mo.observe(document.documentElement, { childList: true, subtree: true });

    /* ---- optional: a "Leer PDF" button on book detail pages ---- */
    function currentDetailItemId() {
        var h = window.location.hash || '';
        var m = h.match(/[?&]id=([0-9a-fA-F-]{16,})/);
        return m ? m[1].replace(/-/g, '') : null;
    }

    function maybeAddDetailButton() {
        try {
            var page = document.querySelector('.itemDetailPage:not(.hide)');
            if (!page || page.querySelector('.epdfrOpenBtn')) { return; }
            var id = currentDetailItemId();
            if (!id || !window.ApiClient) { return; }
            window.ApiClient.getItem(window.ApiClient.getCurrentUserId(), id).then(function (item) {
                if (!item || item.Type !== 'Book') { return; }
                var path = (item.Path || '').toLowerCase();
                if (path && !path.endsWith('.pdf')) { return; }
                var group = page.querySelector('.mainDetailButtons') || page.querySelector('.detailButtons');
                if (!group || group.querySelector('.epdfrOpenBtn')) { return; }
                var btn = document.createElement('button');
                btn.className = 'epdfrOpenBtn button-flat detailButton emby-button';
                btn.type = 'button';
                btn.title = 'Leer con el lector mejorado';
                btn.innerHTML = '<span class="material-icons detailButton-icon" aria-hidden="true">menu_book</span>';
                btn.addEventListener('click', function () {
                    var url = window.ApiClient.getItemDownloadUrl(id);
                    openReader(url, id);
                });
                group.appendChild(btn);
            }).catch(function () { /* ignore */ });
        } catch (e) { /* ignore */ }
    }
    setInterval(maybeAddDetailButton, 1500);

    /* ---------------------------------------------------------------- reader */
    var STYLE_ID = 'epdfr-style';
    function injectStyle() {
        if (document.getElementById(STYLE_ID)) { return; }
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent = [
            '#epdfr-root{position:fixed;inset:0;z-index:100000;background:#1c1c1c;display:flex;flex-direction:column;color:#eee;font-family:inherit}',
            '#epdfr-bar{display:flex;align-items:center;gap:.35rem;padding:.4rem .6rem;background:rgba(0,0,0,.72);backdrop-filter:blur(6px);flex-wrap:nowrap;overflow-x:auto;transition:transform .2s,opacity .2s}',
            '#epdfr-root.hidebar #epdfr-bar{transform:translateY(-100%);opacity:0;pointer-events:none}',
            '#epdfr-bar button{background:transparent;border:0;color:#eee;cursor:pointer;border-radius:6px;height:38px;min-width:38px;display:inline-flex;align-items:center;justify-content:center;padding:0 .4rem;font-size:.9rem}',
            '#epdfr-bar button:hover{background:rgba(255,255,255,.14)}',
            '#epdfr-bar .epdfr-sep{width:1px;height:24px;background:rgba(255,255,255,.2);margin:0 .25rem;flex:none}',
            '#epdfr-bar .epdfr-title{flex:1 1 auto;min-width:40px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:600;padding:0 .5rem}',
            '#epdfr-bar input.epdfr-pageinput{width:3.2rem;text-align:center;background:rgba(255,255,255,.1);border:1px solid rgba(255,255,255,.2);color:#fff;border-radius:6px;height:34px;font-size:.9rem}',
            '#epdfr-bar .epdfr-zoomlabel{min-width:3.4rem;text-align:center;font-variant-numeric:tabular-nums}',
            '#epdfr-scroll{flex:1;overflow:auto;overflow-x:auto;scroll-behavior:auto;-webkit-overflow-scrolling:touch;padding:14px 0;text-align:center}',
            '.epdfr-page{position:relative;margin:0 auto 14px;background:#fff;box-shadow:0 2px 12px rgba(0,0,0,.5);max-width:100%}',
            '.epdfr-page canvas{display:block;width:100%;height:100%}',
            '.epdfr-page .epdfr-pnum{position:absolute;right:6px;bottom:4px;background:rgba(0,0,0,.55);color:#fff;font-size:11px;padding:1px 6px;border-radius:8px}',
            '#epdfr-msg{margin:auto;padding:2rem;text-align:center;max-width:32rem;line-height:1.5}',
            '#epdfr-msg a{color:#4aa3ff}',
            '@media (max-width:600px){#epdfr-bar .epdfr-title{display:none}}',
            '#epdfr-flip{flex:1;display:none;align-items:center;justify-content:center;overflow:hidden;background:radial-gradient(ellipse at 50% 45%,#333 0%,#131313 100%)}',
            '#epdfr-root.mode-flip #epdfr-scroll{display:none}',
            '#epdfr-root.mode-flip #epdfr-flip{display:flex}',
            '#epdfr-root.mode-flip .epdfr-scrollonly{display:none!important}',
            '#epdfr-book{margin:auto}',
            '.epdfr-fpage{background:#fff;overflow:hidden}',
            '.epdfr-fpage canvas{display:block;width:100%;height:100%}',
            '.epdfr-fpage.--busy{background:#fff repeating-linear-gradient(45deg,#f6f6f6,#f6f6f6 12px,#eee 12px,#eee 24px)}',
            '#epdfr-flip .stf__parent{margin:auto;box-shadow:0 18px 50px rgba(0,0,0,.6)}',
            '#epdfr-fliphint{position:absolute;bottom:12px;left:0;right:0;text-align:center;font-size:12px;color:rgba(255,255,255,.45);pointer-events:none;transition:opacity .4s}'
        ].join('\n');
        document.head.appendChild(s);
    }

    function icon(name) { return '<span class="material-icons" aria-hidden="true" style="font-size:22px">' + name + '</span>'; }

    function openReader(url, itemId) {
        opening = true;
        injectStyle();
        var stale = document.getElementById('epdfr-root');
        if (stale && stale.parentNode) { stale.parentNode.removeChild(stale); }

        var root = document.createElement('div');
        root.id = 'epdfr-root';
        root.innerHTML =
            '<div id="epdfr-bar">' +
                '<button data-a="close" title="Cerrar (Esc)">' + icon('close') + '</button>' +
                '<span class="epdfr-sep"></span>' +
                '<button data-a="prev" title="Previous page (\u2190)">' + icon('keyboard_arrow_up') + '</button>' +
                '<input class="epdfr-pageinput" type="text" inputmode="numeric" value="1" title="Go to page">' +
                '<span class="epdfr-pagetotal" style="opacity:.8">/ ?</span>' +
                '<button data-a="next" title="Next page (\u2192)">' + icon('keyboard_arrow_down') + '</button>' +
                '<span class="epdfr-sep"></span>' +
                '<button data-a="mode" title="Modo scroll / modo libro (m)">' + icon('auto_stories') + '</button>' +
                '<span class="epdfr-sep epdfr-scrollonly"></span>' +
                '<button data-a="zoomout" class="epdfr-scrollonly" title="Reducir (-)">' + icon('zoom_out') + '</button>' +
                '<span class="epdfr-zoomlabel epdfr-scrollonly">100%</span>' +
                '<button data-a="zoomin" class="epdfr-scrollonly" title="Ampliar (+)">' + icon('zoom_in') + '</button>' +
                '<button data-a="fitwidth" class="epdfr-scrollonly" title="Ajustar al ancho (w)">' + icon('fit_screen') + '</button>' +
                '<button data-a="fitpage" class="epdfr-scrollonly" title="Fit page (p)">' + icon('crop_free') + '</button>' +
                '<button data-a="rotate" class="epdfr-scrollonly" title="Rotar (r)">' + icon('rotate_right') + '</button>' +
                '<span class="epdfr-sep"></span>' +
                '<span class="epdfr-title"></span>' +
                '<button data-a="download" title="Descargar">' + icon('download') + '</button>' +
            '</div>' +
            '<div id="epdfr-scroll"><div id="epdfr-msg">Cargando PDF…</div></div>' +
            '<div id="epdfr-flip"><div id="epdfr-book"></div><div id="epdfr-fliphint"></div></div>';
        document.body.appendChild(root);
        document.documentElement.style.overflow = 'hidden';

        var bar = root.querySelector('#epdfr-bar');
        var scroll = root.querySelector('#epdfr-scroll');
        var pageInput = root.querySelector('.epdfr-pageinput');
        var pageTotal = root.querySelector('.epdfr-pagetotal');
        var zoomLabel = root.querySelector('.epdfr-zoomlabel');
        var titleEl = root.querySelector('.epdfr-title');
        var flipWrap = root.querySelector('#epdfr-flip');
        var bookEl = root.querySelector('#epdfr-book');
        var hintEl = root.querySelector('#epdfr-fliphint');

        var state = {
            mode: (localStorage.getItem(LS.mode) === 'flip') ? 'flip' : 'scroll',
            flip: null,
            flipPages: [],
            flipRendered: {},
            flipBusy: false,
            doc: null,
            numPages: 0,
            rotation: 0,
            zoomMode: localStorage.getItem(LS.zoom) || 'fit-width', // 'fit-width' | 'fit-page' | number(%)
            scale: 1,
            baseSizes: [],       // {w,h} at scale 1, rotation 0
            pageEls: [],
            rendered: {},        // pageNum -> true
            renderTasks: {},
            current: 1,
            destroyed: false
        };

        function cleanup() {
            state.destroyed = true;
            try { savePage(true); } catch (e) { /* ignore */ }
            document.removeEventListener('visibilitychange', onVisibility);
            window.removeEventListener('pagehide', onVisibility);
            try { io.disconnect(); } catch (e) {}
            document.removeEventListener('keydown', onKey, true);
            window.removeEventListener('resize', onResize);
            document.documentElement.style.overflow = '';
            try { destroyFlip(); } catch (e) { /* ignore */ }
            if (root.parentNode) { root.parentNode.removeChild(root); }
            try { if (state.doc) { state.doc.destroy(); } } catch (e) {}
            opening = false;
        }

        var saveTimer = null;
        var lastSaved = 0;

        function flushPage() {
            if (!itemId || state.current === lastSaved) { return; }
            lastSaved = state.current;
            saveProgress(itemId, state.current, state.numPages);
        }

        function savePage(immediate) {
            if (!itemId) { return; }
            clearTimeout(saveTimer);
            if (immediate === true) { flushPage(); }
            else { saveTimer = setTimeout(flushPage, 1500); }
        }

        function onVisibility() {
            if (document.visibilityState === 'hidden') { savePage(true); }
        }
        document.addEventListener('visibilitychange', onVisibility);
        window.addEventListener('pagehide', onVisibility);

        function pageViewport(i) {
            // returns {w,h} for page index i (1-based) at current scale+rotation
            var b = state.baseSizes[i - 1];
            if (!b) { return { w: 600, h: 800 }; }
            var swap = (state.rotation % 180) !== 0;
            var w = (swap ? b.h : b.w) * state.scale;
            var h = (swap ? b.w : b.h) * state.scale;
            return { w: w, h: h };
        }

        function computeScale() {
            if (!state.baseSizes.length) { return; }
            var avail = scroll.clientWidth - 28;
            var availH = scroll.clientHeight - 28;
            var b0 = state.baseSizes[0];
            var swap = (state.rotation % 180) !== 0;
            var pw = swap ? b0.h : b0.w;
            var ph = swap ? b0.w : b0.h;
            if (state.zoomMode === 'fit-width') {
                state.scale = avail / pw;
            } else if (state.zoomMode === 'fit-page') {
                state.scale = Math.min(avail / pw, availH / ph);
            } else {
                var pct = parseFloat(state.zoomMode) || 100;
                state.scale = (pct / 100);
            }
            state.scale = Math.max(0.1, Math.min(state.scale, 8));
            zoomLabel.textContent = Math.round(state.scale * 100) + '%';
        }

        function layout() {
            computeScale();
            for (var i = 1; i <= state.numPages; i++) {
                var el = state.pageEls[i - 1];
                var vp = pageViewport(i);
                el.style.width = Math.round(vp.w) + 'px';
                el.style.height = Math.round(vp.h) + 'px';
                // force re-render at new scale for currently rendered pages
                if (state.rendered[i]) {
                    state.rendered[i] = false;
                    renderPage(i);
                }
            }
        }

        function renderPage(num) {
            if (state.destroyed || state.rendered[num]) { return; }
            var el = state.pageEls[num - 1];
            if (!el) { return; }
            state.rendered[num] = true;
            state.doc.getPage(num).then(function (page) {
                if (state.destroyed) { return; }
                var dpr = Math.min(window.devicePixelRatio || 1, 2);
                var vp = page.getViewport({ scale: state.scale * dpr, rotation: state.rotation });
                var canvas = el.querySelector('canvas') || document.createElement('canvas');
                canvas.width = Math.floor(vp.width);
                canvas.height = Math.floor(vp.height);
                if (!canvas.parentNode) { el.insertBefore(canvas, el.firstChild); }
                var task = page.render({ canvasContext: canvas.getContext('2d'), viewport: vp });
                state.renderTasks[num] = task;
                task.promise.catch(function () {}).then(function () { delete state.renderTasks[num]; });
            }).catch(function () { state.rendered[num] = false; });
        }

        function unrenderPage(num) {
            if (!state.rendered[num]) { return; }
            state.rendered[num] = false;
            if (state.renderTasks[num]) { try { state.renderTasks[num].cancel(); } catch (e) {} }
            var el = state.pageEls[num - 1];
            var c = el && el.querySelector('canvas');
            if (c) { c.width = c.height = 0; c.remove(); }
        }

        var io = new IntersectionObserver(function (entries) {
            entries.forEach(function (en) {
                var num = parseInt(en.target.dataset.page, 10);
                if (en.isIntersecting) { renderPage(num); }
            });
        }, { root: scroll, rootMargin: '1200px 0px' });

        function updateCurrentFromScroll() {
            var mid = scroll.scrollTop + scroll.clientHeight / 2;
            var acc = 0, cur = 1;
            for (var i = 1; i <= state.numPages; i++) {
                var el = state.pageEls[i - 1];
                var top = el.offsetTop, bottom = top + el.offsetHeight;
                if (mid >= top && mid < bottom) { cur = i; break; }
                if (mid >= bottom) { cur = i; }
            }
            if (cur !== state.current) {
                state.current = cur;
                if (document.activeElement !== pageInput) { pageInput.value = String(cur); }
                savePage();
            }
        }

        function goToPage(n, smooth) {
            n = Math.max(1, Math.min(state.numPages, n | 0));
            if (state.mode === 'flip') {
                if (!state.flip) { return; }
                var cur = state.current;
                try {
                    if (n === cur + 1) { state.flip.flipNext(); }
                    else if (n === cur - 1) { state.flip.flipPrev(); }
                    else { state.flip.turnToPage(n - 1); state.current = n; pageInput.value = String(n); savePage(); updateFlipWindow(n); }
                } catch (e) { /* ignore */ }
                return;
            }
            var el = state.pageEls[n - 1];
            if (el) {
                scroll.scrollTo({ top: el.offsetTop - 8, behavior: smooth ? 'smooth' : 'auto' });
                state.current = n;
                pageInput.value = String(n);
                savePage();
            }
        }

        function setZoom(mode) {
            state.zoomMode = mode;
            localStorage.setItem(LS.zoom, mode);
            var anchor = state.current;
            layout();
            goToPage(anchor, false);
        }

        function nudgeZoom(dir) {
            var cur = Math.round(state.scale * 100);
            var next = dir > 0 ? cur + 15 : cur - 15;
            setZoom(String(Math.max(20, Math.min(400, next))));
        }

        /* ------------------------------------------------ book / page-flip mode */

        function destroyFlip() {
            if (state.flip) {
                try { state.flip.destroy(); } catch (e) { /* ignore */ }
                state.flip = null;
            }
            state.flipPages = [];
            state.flipRendered = {};
            bookEl.innerHTML = '';
        }

        // Render one PDF page into its flip page element, sized to how big it is shown.
        function renderFlipPage(num) {
            if (state.destroyed || num < 1 || num > state.numPages) { return; }
            if (state.flipRendered[num]) { return; }
            var el = state.flipPages[num - 1];
            if (!el) { return; }
            state.flipRendered[num] = true;
            var cssW = el.clientWidth || 500;
            state.doc.getPage(num).then(function (page) {
                if (state.destroyed || !state.flipRendered[num]) { return; }
                var base = page.getViewport({ scale: 1 });
                var dpr = Math.min(window.devicePixelRatio || 1, 2);
                var scale = (cssW / base.width) * dpr;
                var vp = page.getViewport({ scale: scale });
                var canvas = el.querySelector('canvas') || document.createElement('canvas');
                canvas.width = Math.floor(vp.width);
                canvas.height = Math.floor(vp.height);
                if (!canvas.parentNode) { el.appendChild(canvas); }
                return page.render({ canvasContext: canvas.getContext('2d'), viewport: vp }).promise
                    .then(function () { el.classList.remove('--busy'); });
            }).catch(function () { state.flipRendered[num] = false; });
        }

        function releaseFlipPage(num) {
            if (!state.flipRendered[num]) { return; }
            state.flipRendered[num] = false;
            var el = state.flipPages[num - 1];
            var c = el && el.querySelector('canvas');
            if (c) { c.width = c.height = 0; c.remove(); }
            if (el) { el.classList.add('--busy'); }
        }

        // Keep a rendered window of pages around the current one; free the rest.
        function updateFlipWindow(center) {
            var span = 4;
            var lo = Math.max(1, center - span);
            var hi = Math.min(state.numPages, center + span);
            for (var i = lo; i <= hi; i++) { renderFlipPage(i); }
            for (var k in state.flipRendered) {
                var n = parseInt(k, 10);
                if (state.flipRendered[n] && (n < lo - 2 || n > hi + 2)) { releaseFlipPage(n); }
            }
        }

        function buildFlip(startPage) {
            if (!state.doc || state.destroyed) { return Promise.resolve(); }
            return loadPageFlip().then(function (PageFlip) {
                if (state.destroyed) { return; }
                destroyFlip();

                var b = state.baseSizes[0] || { w: 600, h: 800 };
                var aspect = b.h / b.w;

                // available area inside the flip container
                var availW = flipWrap.clientWidth - 24;
                var availH = flipWrap.clientHeight - 24;
                // a spread is two pages wide when the viewport is wide enough
                var spread = availW / availH > 1.15;
                var pageH = availH;
                var pageW = pageH / aspect;
                var totalW = spread ? pageW * 2 : pageW;
                if (totalW > availW) {
                    var k2 = availW / totalW;
                    pageW *= k2; pageH *= k2;
                }

                for (var i = 1; i <= state.numPages; i++) {
                    var el = document.createElement('div');
                    el.className = 'epdfr-fpage --busy';
                    el.dataset.density = 'soft';
                    el.dataset.page = String(i);
                    bookEl.appendChild(el);
                    state.flipPages[i - 1] = el;
                }

                var pf = new PageFlip(bookEl, {
                    width: Math.round(pageW),
                    height: Math.round(pageH),
                    size: 'fixed',
                    minWidth: 100,
                    maxWidth: 4000,
                    minHeight: 100,
                    maxHeight: 4000,
                    drawShadow: true,
                    flippingTime: 700,
                    maxShadowOpacity: 0.5,
                    showCover: true,
                    usePortrait: !spread,
                    mobileScrollSupport: false,
                    swipeDistance: 25,
                    showPageCorners: true,
                    useMouseEvents: true,
                    clickEventForward: false
                });
                state.flip = pf;
                pf.loadFromHTML(bookEl.querySelectorAll('.epdfr-fpage'));

                pf.on('flip', function (e) {
                    var idx = (typeof e.data === 'number') ? e.data : pf.getCurrentPageIndex();
                    state.current = Math.min(state.numPages, Math.max(1, idx + 1));
                    if (document.activeElement !== pageInput) { pageInput.value = String(state.current); }
                    savePage();
                    updateFlipWindow(state.current);
                    poke();
                });
                pf.on('changeState', function (e) { state.flipBusy = (e.data === 'flipping'); });

                var start = Math.min(state.numPages, Math.max(1, startPage || 1));
                updateFlipWindow(start);
                if (start > 1) { try { pf.turnToPage(start - 1); } catch (e) { /* ignore */ } }
                state.current = start;
                pageInput.value = String(start);

                hintEl.textContent = 'Drag the corner of the page, or use \u2190 \u2192';
                setTimeout(function () { hintEl.style.opacity = '0'; }, 4500);
            }).catch(function (err) {
                hintEl.textContent = 'No se pudo iniciar el modo libro: ' + (err && err.message ? err.message : err);
                state.mode = 'scroll';
                root.classList.remove('mode-flip');
                localStorage.setItem(LS.mode, 'scroll');
            });
        }

        function setMode(mode, opts) {
            if (mode === state.mode && !(opts && opts.force)) { return; }
            var keepPage = state.current;
            state.mode = mode;
            localStorage.setItem(LS.mode, mode);
            if (mode === 'flip') {
                root.classList.add('mode-flip');
                hintEl.style.opacity = '';
                buildFlip(keepPage);
            } else {
                root.classList.remove('mode-flip');
                destroyFlip();
                layout();
                goToPage(keepPage, false);
            }
        }

        /* toolbar auto-hide */
        var hideTimer = null;
        function poke() {
            root.classList.remove('hidebar');
            clearTimeout(hideTimer);
            hideTimer = setTimeout(function () { root.classList.add('hidebar'); }, 2800);
        }
        scroll.addEventListener('scroll', function () { updateCurrentFromScroll(); poke(); }, { passive: true });
        root.addEventListener('mousemove', poke);
        root.addEventListener('touchstart', poke, { passive: true });

        bar.addEventListener('click', function (e) {
            var btn = e.target.closest('button');
            if (!btn) { return; }
            var a = btn.dataset.a;
            if (a === 'close') { cleanup(); }
            else if (a === 'prev') { goToPage(state.current - 1, true); }
            else if (a === 'next') { goToPage(state.current + 1, true); }
            else if (a === 'mode') { setMode(state.mode === 'flip' ? 'scroll' : 'flip'); }
            else if (a === 'zoomin') { nudgeZoom(1); }
            else if (a === 'zoomout') { nudgeZoom(-1); }
            else if (a === 'fitwidth') { setZoom('fit-width'); }
            else if (a === 'fitpage') { setZoom('fit-page'); }
            else if (a === 'rotate') { state.rotation = (state.rotation + 90) % 360; layout(); goToPage(state.current, false); }
            else if (a === 'download') { window.open(url, '_blank'); }
        });

        pageInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); goToPage(parseInt(pageInput.value, 10) || 1, true); pageInput.blur(); }
        });
        pageInput.addEventListener('focus', function () { pageInput.select(); });

        function onKey(e) {
            if (!document.getElementById('epdfr-root')) { return; }
            if (e.target === pageInput) { return; }
            var k = e.key;
            if (k === 'Escape') { e.preventDefault(); cleanup(); }
            else if (k === 'ArrowRight' || k === 'ArrowDown' || k === 'PageDown' || k === ' ' || k === 'j') { e.preventDefault(); goToPage(state.current + 1, true); }
            else if (k === 'ArrowLeft' || k === 'ArrowUp' || k === 'PageUp' || k === 'k') { e.preventDefault(); goToPage(state.current - 1, true); }
            else if (k === 'Home') { e.preventDefault(); goToPage(1, true); }
            else if (k === 'End') { e.preventDefault(); goToPage(state.numPages, true); }
            else if (k === 'm') { e.preventDefault(); setMode(state.mode === 'flip' ? 'scroll' : 'flip'); }
            else if (state.mode === 'flip') { return; }
            else if (k === '+' || k === '=') { e.preventDefault(); nudgeZoom(1); }
            else if (k === '-' || k === '_') { e.preventDefault(); nudgeZoom(-1); }
            else if (k === 'w') { e.preventDefault(); setZoom('fit-width'); }
            else if (k === 'p') { e.preventDefault(); setZoom('fit-page'); }
            else if (k === 'r') { e.preventDefault(); state.rotation = (state.rotation + 90) % 360; layout(); goToPage(state.current, false); }
        }
        document.addEventListener('keydown', onKey, true);

        var resizeT = null;
        function onResize() {
            clearTimeout(resizeT);
            resizeT = setTimeout(function () {
                var a = state.current;
                if (state.mode === 'flip') { buildFlip(a); }
                else { layout(); goToPage(a, false); }
            }, 200);
        }
        window.addEventListener('resize', onResize);

        /* title */
        if (window.ApiClient && itemId) {
            try {
                window.ApiClient.getItem(window.ApiClient.getCurrentUserId(), itemId)
                    .then(function (it) { if (it && it.Name) { titleEl.textContent = it.Name; document.title = it.Name; } })
                    .catch(function () {});
            } catch (e) {}
        }

        /* load */
        var progressPromise = loadProgress(itemId);
        loadPdfJs().then(function (pdfjsLib) {
            return pdfjsLib.getDocument({ url: url, withCredentials: false, rangeChunkSize: 262144 }).promise;
        }).then(function (doc) {
            if (state.destroyed) { return; }
            state.doc = doc;
            state.numPages = doc.numPages;
            pageTotal.textContent = '/ ' + doc.numPages;
            scroll.innerHTML = '';
            var pending = [];
            for (var i = 1; i <= doc.numPages; i++) {
                (function (i) {
                    var el = document.createElement('div');
                    el.className = 'epdfr-page';
                    el.dataset.page = String(i);
                    el.innerHTML = '<span class="epdfr-pnum">' + i + '</span>';
                    scroll.appendChild(el);
                    state.pageEls[i - 1] = el;
                    io.observe(el);
                    pending.push(doc.getPage(i).then(function (p) {
                        var v = p.getViewport({ scale: 1, rotation: 0 });
                        state.baseSizes[i - 1] = { w: v.width, h: v.height };
                    }));
                })(i);
            }
            // we only need first few sizes to lay out; resolve when first page known
            return Promise.all(pending.slice(0, Math.min(pending.length, 8)));
        }).then(function () {
            if (state.destroyed) { return; }
            // fill any missing base sizes with the first known one so layout works before all pages measured
            var known = state.baseSizes.find(Boolean) || { w: 600, h: 800 };
            for (var i = 0; i < state.numPages; i++) { if (!state.baseSizes[i]) { state.baseSizes[i] = known; } }
            layout();
            return progressPromise.then(function (saved) {
                if (state.destroyed) { return; }
                var startAt = (saved && saved > 1 && saved <= state.numPages) ? saved : 1;
                lastSaved = startAt;
                var wanted = state.mode;
                state.mode = 'scroll';           // position the scroll view first
                goToPage(startAt, false);
                updateCurrentFromScroll();
                state.current = startAt;
                poke();
                if (wanted === 'flip') {
                    state.mode = 'flip';
                    root.classList.add('mode-flip');
                    buildFlip(startAt);
                }
            });
        }).then(function () {
            if (state.destroyed) { return; }
            var known = state.baseSizes.find(Boolean) || { w: 600, h: 800 };
            // lazily finish measuring the rest and relayout once
            var rest = [];
            for (var j = 1; j <= state.numPages; j++) {
                if (state.baseSizes[j - 1] === known && j > 8) {
                    (function (j) {
                        rest.push(state.doc.getPage(j).then(function (p) {
                            var v = p.getViewport({ scale: 1, rotation: 0 });
                            state.baseSizes[j - 1] = { w: v.width, h: v.height };
                            var el = state.pageEls[j - 1];
                            var vp = pageViewport(j);
                            el.style.width = Math.round(vp.w) + 'px';
                            el.style.height = Math.round(vp.h) + 'px';
                        }).catch(function () {}));
                    })(j);
                }
            }
        }).catch(function (err) {
            scroll.innerHTML = '<div id="epdfr-msg">No se pudo abrir el PDF en el lector.<br><br>' +
                '<a href="' + url + '" target="_blank" rel="noopener">Descargar el archivo</a>' +
                '<br><br><small>' + (err && err.message ? String(err.message) : '') + '</small></div>';
        });
    }

    var VERSION = '1.3.0';
    window.EnhancedPdfReader = { open: openReader, version: VERSION };
    console.log('[EnhancedPdfReader] loaded v' + VERSION + ' (server-side per-user reading position)');
})();
