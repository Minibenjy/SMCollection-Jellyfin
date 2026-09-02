/*
 * Mature Content for Jellyfin web client.
 *  - Topbar "M" switch that controls Jellyfin's native BlockedTags policy.
 *  - Admin-only front-end tagging: a button on the item detail page and
 *    two buttons in the multi-select toolbar to (un)mark content as mature.
 *
 * The glyph is a custom inline-SVG "M" monogram (no icon font), drawn to sit
 * cleanly next to Jellyfin's own header icons. State is shown by colour only:
 * quiet when mature is hidden, red with a soft glow when it is visible.
 */
(function () {
    'use strict';

    if (window.__matureContentLoaded) { return; }
    window.__matureContentLoaded = true;

    var ROOT_ID = 'mc-toggle-root';
    var STYLE_ID = 'mc-toggle-style';
    var DETAIL_ID = 'mc-detail-btn';
    var SEL_MARK_ID = 'mc-sel-mark';
    var SEL_UNMARK_ID = 'mc-sel-unmark';

    var state = null;      // { CanToggle, MatureVisible, MatureTags }
    var isAdmin = false;
    var adminChecked = false;
    var busy = false;

    // ---------------------------------------------------------------- glyphs

    // Elegant geometric "M", even stroke weight, rounded joins.
    var M_PATH = 'M6 17V7.6c0-.7.83-1.05 1.32-.55L12 11.8l4.68-4.75c.49-.5 1.32-.15 1.32.55V17';
    // Compact "M" that leaves room for a corner badge.
    var M_PATH_SM = 'M3 16.4V8.2c0-.63.75-.95 1.19-.5L8 11.5l3.81-3.8c.44-.45 1.19-.13 1.19.5v8.2';

    function topGlyph() {
        return '<svg class="mc-glyph" viewBox="0 0 24 24" aria-hidden="true" fill="none">'
            + '<rect x="2.6" y="2.6" width="18.8" height="18.8" rx="5.6" stroke="currentColor" stroke-width="1.4" class="mc-ring"/>'
            + '<path d="' + M_PATH + '" stroke="currentColor" stroke-width="2.15" stroke-linecap="round" stroke-linejoin="round"/>'
            + '</svg>';
    }

    function badgeGlyph(kind) {
        var inner = kind === 'add'
            ? '<path d="M17.6 5.1v5M15.1 7.6h5" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/>'
            : '<path d="M15.1 7.6h5" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/>';
        return '<svg class="mc-glyph" viewBox="0 0 24 24" aria-hidden="true" fill="none">'
            + '<path d="' + M_PATH_SM + '" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
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

    function ensureAdmin() {
        if (adminChecked) { return Promise.resolve(isAdmin); }
        if (!window.ApiClient || !window.ApiClient.getCurrentUser) { return Promise.resolve(false); }
        return window.ApiClient.getCurrentUser().then(function (user) {
            adminChecked = true;
            isAdmin = !!(user && user.Policy && user.Policy.IsAdministrator);
            return isAdmin;
        }).catch(function () { adminChecked = true; return false; });
    }

    // ---------------------------------------------------------------- styles

    function injectStyle() {
        if (document.getElementById(STYLE_ID)) { return; }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = [
            '.mc-glyph{width:1.6rem;height:1.6rem;display:block;color:inherit;overflow:visible;',
                'transition:color .18s ease,filter .18s ease,opacity .18s ease}',
            '.mc-glyph .mc-ring{opacity:.28;transition:opacity .18s ease,stroke .18s ease}',

            /* topbar switch — matches the size/rhythm of the native header buttons */
            '#mc-toggle-root{display:inline-flex;align-items:center;height:100%}',
            '#mc-toggle-root.mc-floating{position:fixed;top:7px;right:96px;height:auto;z-index:1300}',
            '.mc-topbtn{width:2.4em;height:2.4em;display:inline-flex;align-items:center;justify-content:center;',
                'border:0;background:transparent;cursor:pointer;color:inherit;padding:0;border-radius:50%}',
            '.mc-topbtn:hover{background:rgba(255,255,255,.08)}',
            '.mc-topbtn .mc-glyph{width:1.6rem;height:1.6rem}',
            '.mc-topbtn[aria-checked="false"] .mc-glyph{opacity:.62}',
            '.mc-topbtn[aria-checked="true"] .mc-glyph{color:#ff3131;opacity:1;',
                'filter:drop-shadow(0 0 4px rgba(255,49,49,.55)) drop-shadow(0 0 11px rgba(255,49,49,.4))}',
            '.mc-topbtn[aria-checked="true"] .mc-ring{opacity:.5}',
            '.mc-topbtn:disabled{opacity:.5;cursor:default}',
            '@media (max-width:640px){#mc-toggle-root.mc-floating{right:60px;top:6px}}',

            /* detail-page button — reuses the native .detailButton shell */
            '#mc-detail-btn .detailButton-icon{display:inline-flex;align-items:center;justify-content:center}',
            '#mc-detail-btn .mc-glyph{width:2.2rem;height:2.2rem}',
            '#mc-detail-btn.mc-on .mc-glyph{color:#ff3131;',
                'filter:drop-shadow(0 0 4px rgba(255,49,49,.55)) drop-shadow(0 0 11px rgba(255,49,49,.38))}',
            '#mc-detail-btn.mc-on .mc-ring{opacity:.5}',

            /* multi-select toolbar buttons */
            '.selectionCommandsPanel .mc-sel-btn{color:#fff}',
            '.selectionCommandsPanel .mc-sel-btn .mc-glyph{width:1.6rem;height:1.6rem}',
            '.selectionCommandsPanel .mc-sel-btn.mc-mark .mc-glyph{color:#ff6b6b}',
            '.selectionCommandsPanel .mc-sel-btn.mc-mark:hover .mc-glyph{color:#ff3131;',
                'filter:drop-shadow(0 0 4px rgba(255,49,49,.6)) drop-shadow(0 0 11px rgba(255,49,49,.4))}',
            '.selectionCommandsPanel .mc-sel-btn.mc-unmark:hover .mc-glyph{filter:drop-shadow(0 0 4px rgba(255,255,255,.4))}'
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
        if (!state || !state.CanToggle) {
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
            root.innerHTML = '<button type="button" class="mc-topbtn" role="switch">' + topGlyph() + '</button>';
            root.querySelector('button').addEventListener('click', onToggle);
        }

        if (slot && root.parentNode !== slot) {
            root.classList.remove('mc-floating');
            slot.insertBefore(root, slot.firstChild);
        } else if (!slot && root.parentNode !== document.body) {
            root.classList.add('mc-floating');
            document.body.appendChild(root);
        }

        var button = root.querySelector('button');
        var on = !!state.MatureVisible;
        button.setAttribute('aria-checked', on ? 'true' : 'false');
        button.setAttribute('aria-label', on ? 'Mature content visible' : 'Mature content hidden');
        button.disabled = busy;
        button.title = on
            ? 'Mature content visible \u2014 click to hide it'
            : 'Mature content hidden \u2014 click to show it';
    }

    function onToggle() {
        if (!state || !state.CanToggle || busy) { return; }
        busy = true;
        renderToggle();
        request('MatureContent/State', { method: 'POST', body: { MatureVisible: !state.MatureVisible } })
            .then(function (s) {
                state = s;
                busy = false;
                renderToggle();
                setTimeout(function () { window.location.reload(); }, 250);
            })
            .catch(function () { busy = false; refreshState(); });
    }

    function refreshState() {
        request('MatureContent/State')
            .then(function (s) { state = s; renderToggle(); })
            .catch(function () { state = null; renderToggle(); });
    }

    // ---------------------------------------------------------------- shared: mark items

    function markItems(ids, isMature) {
        if (!ids.length) { return Promise.resolve(); }
        var done = 0;
        return Promise.all(ids.map(function (id) {
            return request('MatureContent/Items/' + id, { method: 'POST', body: { IsMature: isMature } })
                .then(function () { done++; })
                .catch(function () { /* keep going */ });
        })).then(function () {
            toast((isMature ? 'Marcado como mature: ' : 'Desmarcado: ') + done + '/' + ids.length);
        });
    }

    // ---------------------------------------------------------------- detail page button

    function detailItemId() {
        var h = window.location.hash || '';
        var m = h.match(/[?&]id=([a-f0-9-]{32,36})/i);
        return m ? m[1].replace(/-/g, '') : null;
    }

    function renderDetailButton() {
        if (!isAdmin) { return; }
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
                    '<span class="detailButton-icon">' + topGlyph() + '</span>' +
                    '<div class="detailButton-text">Mature</div>' +
                '</div>';
            existing.addEventListener('click', function () {
                var on = existing.classList.contains('mc-on');
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

        if (existing.getAttribute('data-mc-for') !== id) {
            existing.setAttribute('data-mc-for', id);
            request('MatureContent/Items/' + id)
                .then(function (s) { setDetailButtonState(existing, !!(s && s.IsMature)); })
                .catch(function () { /* ignore */ });
        }
    }

    function setDetailButtonState(btn, on) {
        btn.classList.toggle('mc-on', on);
        var text = btn.querySelector('.detailButton-text');
        if (text) { text.textContent = on ? 'Mature ✓' : 'Mature'; }
        btn.title = on ? 'Remove the mature mark' : 'Mark as mature';
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
        b.className = 'mc-sel-btn autoSize' + (extraClass ? ' ' + extraClass : '');
        b.title = label;
        b.setAttribute('aria-label', label);
        b.innerHTML = glyphHtml;
        return b;
    }

    function renderSelectionButtons() {
        var panel = document.querySelector('.selectionCommandsPanel');
        if (!panel || !isAdmin) { return; }

        var anchor = panel.querySelector('.btnSelectionPanelOptions');
        if (!document.getElementById(SEL_MARK_ID)) {
            var mark = makeSelBtn(SEL_MARK_ID, badgeGlyph('add'), 'Mark selection as mature', 'mc-mark');
            mark.addEventListener('click', function () {
                var ids = selectedItemIds();
                if (!ids.length) { toast('Nada seleccionado'); return; }
                markItems(ids, true);
            });
            panel.insertBefore(mark, anchor || null);
        }
        if (!document.getElementById(SEL_UNMARK_ID)) {
            var unmark = makeSelBtn(SEL_UNMARK_ID, badgeGlyph('remove'), 'Remove the mature mark from the selection', 'mc-unmark');
            unmark.addEventListener('click', function () {
                var ids = selectedItemIds();
                if (!ids.length) { toast('Nada seleccionado'); return; }
                markItems(ids, false);
            });
            panel.insertBefore(unmark, anchor || null);
        }
    }

    // ---------------------------------------------------------------- loop

    function tickFast() {
        injectStyle();
        renderToggle();
        if (isAdmin) {
            renderDetailButton();
            renderSelectionButtons();
        }
    }

    function boot() {
        injectStyle();
        refreshState();
        ensureAdmin().then(function () { tickFast(); });
    }

    document.addEventListener('viewshow', function () { refreshState(); tickFast(); });
    document.addEventListener('pageshow', tickFast);
    setInterval(tickFast, 1000);
    setTimeout(boot, 800);
    setTimeout(boot, 2500);
})();
