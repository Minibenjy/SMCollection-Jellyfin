/*
 * Kids Mode for Jellyfin web client.
 *  - Topbar "K" switch: enters/leaves a per-account allow-list view.
 *  - Admin & per-user front-end curation: a button on the item detail page and
 *    two buttons in the multi-select toolbar to add/remove content from kids.
 *
 * The glyph is a custom inline-SVG "K" monogram (no icon font). State is shown
 * by colour: quiet when kids mode is off, green with a soft glow when it is on.
 * Only shown to accounts for which an administrator has enabled kids mode.
 */
(function () {
    'use strict';

    if (window.__kidsModeLoaded) { return; }
    window.__kidsModeLoaded = true;

    var ROOT_ID = 'kd-toggle-root';
    var STYLE_ID = 'kd-toggle-style';
    var DETAIL_ID = 'kd-detail-btn';
    var SEL_ADD_ID = 'kd-sel-add';
    var SEL_DEL_ID = 'kd-sel-del';

    var state = null;      // { Enabled, Active, IsAdministrator }
    var busy = false;

    // ---------------------------------------------------------------- glyphs

    // Geometric "K": the leg springs from the arm rather than from the stem,
    // which reads cleaner at 24px than the classic two-arms-from-a-point.
    var K_STEM = 'M9.1 7.6v8.8';
    var K_ARMS = 'M15.1 7.6l-6 4.7M11.5 10.5l3.8 5.9';

    function kGlyph() {
        return '<svg class="kd-glyph" viewBox="0 0 24 24" aria-hidden="true" fill="none">'
            + '<rect x="3" y="3" width="18" height="18" rx="6" fill="currentColor" class="kd-tile"/>'
            + '<rect x="3" y="3" width="18" height="18" rx="6" stroke="currentColor" stroke-width="1.5" class="kd-ring"/>'
            + '<path d="' + K_STEM + '" stroke="currentColor" stroke-width="2.1" stroke-linecap="round"/>'
            + '<path d="' + K_ARMS + '" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"/>'
            + '</svg>';
    }

    function kBadgeGlyph(kind) {
        var inner = kind === 'add'
            ? '<path d="M17.6 5.1v5M15.1 7.6h5" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/>'
            : '<path d="M15.1 7.6h5" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/>';
        return '<svg class="kd-glyph" viewBox="0 0 24 24" aria-hidden="true" fill="none">'
            + '<path d="M4 7.2v9.6" stroke="currentColor" stroke-width="2" stroke-linecap="round"/>'
            + '<path d="M9.4 7.2L4 11.8M6 10.1l3.6 6.7" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
            + '<circle cx="17.6" cy="7.6" r="4.6" stroke="currentColor" stroke-width="1.7"/>'
            + inner
            + '</svg>';
    }

    // ---------------------------------------------------------------- helpers

    function apiUrl(path) {
        if (window.ApiClient && window.ApiClient.getUrl) { return window.ApiClient.getUrl(path); }
        return '/' + path.replace(/^\/+/, '');
    }

    function request(path, options) {
        options = options || {};
        if (window.ApiClient && window.ApiClient.ajax) {
            return window.ApiClient.ajax({
                type: options.method || 'GET',
                url: apiUrl(path),
                data: options.body ? JSON.stringify(options.body) : null,
                contentType: 'application/json',
                dataType: 'json',
                headers: { accept: 'application/json' }
            });
        }
        return fetch(apiUrl(path), {
            method: options.method || 'GET',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: options.body ? JSON.stringify(options.body) : undefined
        }).then(function (r) {
            if (!r.ok) { throw new Error('HTTP ' + r.status); }
            if (r.status === 204) { return null; }
            return r.json();
        });
    }

    function toast(text) {
        if (window.Dashboard && window.Dashboard.alert) {
            try { window.Dashboard.alert({ message: text }); return; } catch (e) { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------- styles

    function injectStyle() {
        if (document.getElementById(STYLE_ID)) { return; }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = [
            '.kd-glyph{width:1.6rem;height:1.6rem;display:block;color:inherit;overflow:visible;',
                'transition:color .18s ease,filter .18s ease,opacity .18s ease}',
            '.kd-glyph .kd-ring{opacity:.3;transition:opacity .18s ease}',
            /* The tile fills in when kids mode is on: state reads from the shape,
               not only from a glow, and stays legible on light artwork. */
            '.kd-glyph .kd-tile{opacity:0;transition:opacity .18s ease}',

            '#kd-toggle-root{display:inline-flex;align-items:center;height:100%}',
            '#kd-toggle-root.kd-floating{position:fixed;top:7px;right:140px;height:auto;z-index:1300}',
            '.kd-topbtn{width:2.4em;height:2.4em;display:inline-flex;align-items:center;justify-content:center;',
                'border:0;background:transparent;cursor:pointer;color:inherit;padding:0;border-radius:50%}',
            '.kd-topbtn:hover{background:transparent}',
            '.kd-topbtn:hover .kd-tile{opacity:.1}',
            '.kd-topbtn:focus-visible{outline:2px solid #37d67a;outline-offset:1px}',
            '.kd-topbtn .kd-glyph{width:1.6rem;height:1.6rem}',
            '.kd-topbtn[aria-checked="false"] .kd-glyph{opacity:.62}',
            '.kd-topbtn[aria-checked="true"] .kd-glyph{color:#37d67a;opacity:1;',
                'filter:drop-shadow(0 0 6px rgba(55,214,122,.45))}',
            '.kd-topbtn[aria-checked="true"] .kd-ring{opacity:.7}',
            '.kd-topbtn[aria-checked="true"] .kd-tile{opacity:.18}',
            '.kd-topbtn:disabled{opacity:.5;cursor:default}',
            '@media (max-width:640px){#kd-toggle-root.kd-floating{right:96px;top:6px}}',

            '#kd-detail-btn .detailButton-icon{display:inline-flex;align-items:center;justify-content:center}',
            '#kd-detail-btn .kd-glyph{width:2.2rem;height:2.2rem}',
            '#kd-detail-btn.kd-on .kd-glyph{color:#37d67a;',
                'filter:drop-shadow(0 0 6px rgba(55,214,122,.45))}',
            '#kd-detail-btn.kd-on .kd-ring{opacity:.7}',
            '#kd-detail-btn.kd-on .kd-tile{opacity:.18}',

            '.selectionCommandsPanel .kd-sel-btn{color:#fff}',
            '.selectionCommandsPanel .kd-sel-btn .kd-glyph{width:1.6rem;height:1.6rem}',
            '.selectionCommandsPanel .kd-sel-btn.kd-add .kd-glyph{color:#5ee29a}',
            '.selectionCommandsPanel .kd-sel-btn.kd-add:hover .kd-glyph{color:#37d67a;',
                'filter:drop-shadow(0 0 4px rgba(55,214,122,.6)) drop-shadow(0 0 11px rgba(55,214,122,.4))}',
            '.selectionCommandsPanel .kd-sel-btn.kd-del:hover .kd-glyph{filter:drop-shadow(0 0 4px rgba(255,255,255,.4))}'
        ].join('');
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------- topbar switch

    function findHeaderSlot() {
        return document.querySelector('.skinHeader .headerRight')
            || document.querySelector('.headerRight')
            || document.querySelector('.headerButtons')
            || document.querySelector('.skinHeader');
    }

    function renderToggle() {
        if (!state || !state.Enabled) {
            var old = document.getElementById(ROOT_ID);
            if (old && old.parentNode) { old.parentNode.removeChild(old); }
            return;
        }

        injectStyle();
        var slot = findHeaderSlot();
        var root = document.getElementById(ROOT_ID);
        if (!root) {
            root = document.createElement('div');
            root.id = ROOT_ID;
            root.innerHTML = '<button type="button" class="kd-topbtn" role="switch">' + kGlyph() + '</button>';
            root.querySelector('button').addEventListener('click', onToggle);
        }

        if (slot && root.parentNode !== slot) {
            root.classList.remove('kd-floating');
            slot.insertBefore(root, slot.firstChild);
        } else if (!slot && root.parentNode !== document.body) {
            root.classList.add('kd-floating');
            document.body.appendChild(root);
        }

        var button = root.querySelector('button');
        var on = !!state.Active;
        button.setAttribute('aria-checked', on ? 'true' : 'false');
        button.setAttribute('aria-label', on ? 'Modo kids activo' : 'Modo kids inactivo');
        button.disabled = busy;
        button.title = on ? 'Modo kids activo — pulsa para salir' : 'Modo kids inactivo — pulsa para entrar';
    }

    function onToggle() {
        if (!state || !state.Enabled || busy) { return; }
        busy = true;
        renderToggle();
        request('KidsMode/State', { method: 'POST', body: { Active: !state.Active } })
            .then(function (s) {
                state = s;
                busy = false;
                renderToggle();
                setTimeout(function () { window.location.reload(); }, 250);
            })
            .catch(function () { busy = false; refreshState(); });
    }

    function refreshState() {
        request('KidsMode/State')
            .then(function (s) { state = s; renderToggle(); })
            .catch(function () { state = null; renderToggle(); });
    }

    // ---------------------------------------------------------------- shared

    function markItems(ids, inKids) {
        if (!ids.length) { return Promise.resolve(); }
        var done = 0;
        return Promise.all(ids.map(function (id) {
            return request('KidsMode/Items/' + id, { method: 'POST', body: { InKids: inKids } })
                .then(function () { done++; })
                .catch(function () { /* keep going */ });
        })).then(function () {
            toast((inKids ? 'Added to kids: ' : 'Removed from kids: ') + done + '/' + ids.length);
        });
    }

    // ---------------------------------------------------------------- detail page button

    function detailItemId() {
        var h = window.location.hash || '';
        var m = h.match(/[?&]id=([a-f0-9-]{32,36})/i);
        return m ? m[1].replace(/-/g, '') : null;
    }

    function renderDetailButton() {
        if (!state || !state.Enabled) {
            var gone = document.getElementById(DETAIL_ID);
            if (gone && gone.parentNode) { gone.parentNode.removeChild(gone); }
            return;
        }

        var page = document.querySelector('.itemDetailPage:not(.hide), #itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage, #itemDetailPage');
        var id = detailItemId();
        var existing = document.getElementById(DETAIL_ID);

        if (!page || !id) {
            if (existing && existing.parentNode) { existing.parentNode.removeChild(existing); }
            return;
        }

        var container = page.querySelector('.mainDetailButtons');
        if (!container) { return; }

        if (!existing) {
            existing = document.createElement('button');
            existing.id = DETAIL_ID;
            existing.type = 'button';
            existing.setAttribute('is', 'emby-button');
            existing.className = 'button-flat detailButton emby-button';
            existing.innerHTML =
                '<div class="detailButton-content">' +
                    '<span class="detailButton-icon">' + kGlyph() + '</span>' +
                    '<div class="detailButton-text">Kids</div>' +
                '</div>';
            existing.addEventListener('click', function () {
                var on = existing.classList.contains('kd-on');
                existing.disabled = true;
                markItems([id], !on).then(function () {
                    existing.disabled = false;
                    setDetailButtonState(existing, !on);
                });
            });
            container.appendChild(existing);
        } else if (existing.parentNode !== container) {
            container.appendChild(existing);
        }

        if (existing.getAttribute('data-kd-for') !== id) {
            existing.setAttribute('data-kd-for', id);
            request('KidsMode/Items/' + id)
                .then(function (s) { setDetailButtonState(existing, !!(s && s.InKids)); })
                .catch(function () { /* ignore */ });
        }
    }

    function setDetailButtonState(btn, on) {
        btn.classList.toggle('kd-on', on);
        var text = btn.querySelector('.detailButton-text');
        if (text) { text.textContent = on ? 'Kids ✓' : 'Kids'; }
        btn.title = on
            ? (state && state.IsAdministrator ? 'Remove from the global kids list' : 'Remove from my kids list')
            : (state && state.IsAdministrator ? 'Add to the global kids list' : 'Add to my kids list');
    }

    // ---------------------------------------------------------------- multi-select toolbar

    function selectedItemIds() {
        var ids = [];
        document.querySelectorAll('.card, .listItem').forEach(function (el) {
            var chk = el.querySelector('.chkItemSelect input, input.chkItemSelect');
            var checked = (chk && chk.checked) || el.classList.contains('selected');
            if (!checked) { return; }
            var id = el.getAttribute('data-id')
                || (el.querySelector('[data-id]') && el.querySelector('[data-id]').getAttribute('data-id'));
            if (id) { ids.push(id.replace(/-/g, '')); }
        });
        return Array.from(new Set(ids));
    }

    function makeSelBtn(id, glyphHtml, label, extraClass) {
        var b = document.createElement('button');
        b.id = id;
        b.setAttribute('is', 'paper-icon-button-light');
        b.className = 'kd-sel-btn autoSize' + (extraClass ? ' ' + extraClass : '');
        b.title = label;
        b.setAttribute('aria-label', label);
        b.innerHTML = glyphHtml;
        return b;
    }

    function renderSelectionButtons() {
        var panel = document.querySelector('.selectionCommandsPanel');
        if (!panel || !state || !state.Enabled) { return; }

        var anchor = panel.querySelector('.btnSelectionPanelOptions');
        var scope = state.IsAdministrator ? ' (lista global)' : ' (mi lista)';
        if (!document.getElementById(SEL_ADD_ID)) {
            var add = makeSelBtn(SEL_ADD_ID, kBadgeGlyph('add'), 'Add selection to kids' + scope, 'kd-add');
            add.addEventListener('click', function () {
                var ids = selectedItemIds();
                if (!ids.length) { toast('Nada seleccionado'); return; }
                markItems(ids, true);
            });
            panel.insertBefore(add, anchor || null);
        }
        if (!document.getElementById(SEL_DEL_ID)) {
            var del = makeSelBtn(SEL_DEL_ID, kBadgeGlyph('remove'), 'Remove selection from kids' + scope, 'kd-del');
            del.addEventListener('click', function () {
                var ids = selectedItemIds();
                if (!ids.length) { toast('Nada seleccionado'); return; }
                markItems(ids, false);
            });
            panel.insertBefore(del, anchor || null);
        }
    }

    // ---------------------------------------------------------------- loop

    function tickFast() {
        injectStyle();
        renderToggle();
        renderDetailButton();
        renderSelectionButtons();
    }

    function boot() {
        injectStyle();
        refreshState();
        setTimeout(tickFast, 200);
    }

    document.addEventListener('viewshow', function () { refreshState(); tickFast(); });
    document.addEventListener('pageshow', tickFast);
    setInterval(tickFast, 1000);
    setTimeout(boot, 800);
    setTimeout(boot, 2500);
})();
