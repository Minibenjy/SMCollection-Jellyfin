/*
 * Artwork Refresher for the Jellyfin web client.
 *
 * Adds "Refresh artwork" to the three-dots menu of an item card (and of the item page). It is the
 * only front-end part of the plugin and it is kept apart on purpose: everything that depends on
 * the markup of jellyfin-web lives in this one file, in four small steps:
 *   remember which item the user opened a menu for  ->  notice the menu when it appears  ->
 *   add one entry  ->  call POST ArtworkRefresher/Item/{id}.
 * If a jellyfin-web update changes the markup the entry simply does not appear; nothing else breaks.
 */
(function () {
    'use strict';

    if (window.__artworkRefresherLoaded) { return; }
    window.__artworkRefresherLoaded = true;

    var ENTRY_ID = 'artworkrefresher-refresh';
    var INJECTED_ATTR = 'data-artwork-refresher-injected';
    var CONTEXT_MAX_AGE_MS = 5000;

    // Item types the server can refresh; the menu entry is not offered for anything else
    // (libraries, playlists, channels...). The item page does not say the type, so there the
    // server decides and answers that there was nothing to do.
    var SUPPORTED_TYPES = ['Movie', 'Series', 'Season', 'Episode', 'MusicAlbum', 'MusicArtist', 'BoxSet', 'Person',
        'Genre', 'MusicGenre', 'Studio', 'Book'];

    // ------------------------------------------------------------------ text

    var STRINGS = {
        en: { entry: 'Refresh artwork', working: 'Looking for new artwork…', done: 'Artwork updated',
            nothing: 'No new artwork found', locked: 'Artwork is locked or protected for this item',
            failed: 'Could not refresh the artwork', slow: 'Wait a little before asking again',
            sources: 'Sources', dry: 'Dry run (nothing saved)' },
        es: { entry: 'Refrescar carátula', working: 'Buscando nuevas imágenes…', done: 'Imágenes actualizadas',
            nothing: 'No se encontraron imágenes nuevas', locked: 'Las imágenes de este elemento están bloqueadas o protegidas',
            failed: 'No se pudieron refrescar las imágenes', slow: 'Espera un poco antes de volver a pedirlo',
            sources: 'Fuentes', dry: 'Simulación (no se guarda nada)' },
        fr: { entry: 'Actualiser les images', working: 'Recherche de nouvelles images…', done: 'Images mises à jour',
            nothing: 'Aucune nouvelle image trouvée', locked: 'Les images de cet élément sont verrouillées ou protégées',
            failed: 'Impossible d’actualiser les images', slow: 'Patientez avant de réessayer',
            sources: 'Sources', dry: 'Simulation (rien n’est enregistré)' },
        de: { entry: 'Bilder aktualisieren', working: 'Suche nach neuen Bildern…', done: 'Bilder aktualisiert',
            nothing: 'Keine neuen Bilder gefunden', locked: 'Die Bilder dieses Eintrags sind gesperrt oder geschützt',
            failed: 'Bilder konnten nicht aktualisiert werden', slow: 'Bitte kurz warten',
            sources: 'Quellen', dry: 'Testlauf (nichts gespeichert)' },
        pt: { entry: 'Atualizar imagens', working: 'À procura de novas imagens…', done: 'Imagens atualizadas',
            nothing: 'Nenhuma imagem nova encontrada', locked: 'As imagens deste item estão bloqueadas ou protegidas',
            failed: 'Não foi possível atualizar as imagens', slow: 'Aguarde um pouco antes de repetir',
            sources: 'Fontes', dry: 'Simulação (nada é guardado)' },
        it: { entry: 'Aggiorna le immagini', working: 'Ricerca di nuove immagini…', done: 'Immagini aggiornate',
            nothing: 'Nessuna nuova immagine trovata', locked: 'Le immagini di questo elemento sono bloccate o protette',
            failed: 'Impossibile aggiornare le immagini', slow: 'Attendi un attimo prima di riprovare',
            sources: 'Fonti', dry: 'Simulazione (nulla viene salvato)' },
        ca: { entry: 'Refresca les imatges', working: 'Cercant noves imatges…', done: 'Imatges actualitzades',
            nothing: 'No s’han trobat imatges noves', locked: 'Les imatges d’aquest element estan bloquejades o protegides',
            failed: 'No s’han pogut refrescar les imatges', slow: 'Espera una mica abans de tornar-ho a demanar',
            sources: 'Fonts', dry: 'Simulació (no es desa res)' }
    };

    function lang() {
        var l = (document.documentElement.lang || navigator.language || 'en').toLowerCase().split('-')[0];
        return STRINGS[l] ? l : 'en';
    }

    function t(key) { return STRINGS[lang()][key] || STRINGS.en[key] || key; }

    // ------------------------------------------------------------------ API

    function api() { return window.ApiClient || null; }

    function call(method, path, body) {
        var client = api();
        if (!client) { return Promise.reject(new Error('no ApiClient')); }
        var options = { type: method, url: client.getUrl(path), dataType: 'json' };
        if (body !== undefined) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
        }
        return client.ajax(options);
    }

    var capabilities = null;
    var capabilitiesAt = 0;

    function getCapabilities() {
        if (capabilities && Date.now() - capabilitiesAt < 60000) { return Promise.resolve(capabilities); }
        return call('GET', 'ArtworkRefresher/Capabilities').then(function (c) {
            capabilities = c;
            capabilitiesAt = Date.now();
            return c;
        }, function () { return null; });
    }

    // ------------------------------------------------------------------ toast

    function toast(text, kind) {
        var el = document.createElement('div');
        el.setAttribute('role', 'status');
        el.textContent = text;
        el.style.cssText = 'position:fixed;left:50%;bottom:4.5em;transform:translateX(-50%);z-index:100000;' +
            'max-width:90vw;padding:.7em 1.2em;border-radius:.4em;font-size:.95em;color:#fff;' +
            'box-shadow:0 .3em 1em rgba(0,0,0,.5);background:' + (kind === 'error' ? '#b3261e' : kind === 'ok' ? '#2e7d32' : '#323232');
        document.body.appendChild(el);
        setTimeout(function () { if (el.parentNode) { el.parentNode.removeChild(el); } }, 4500);
        return el;
    }

    // -------------------------------------------------------- item context

    var lastContext = null;

    function itemIdFromHash() {
        var m = /[?&]id=([0-9a-fA-F-]{32,36})/.exec(window.location.hash || '');
        return m ? m[1] : null;
    }

    // Step 1: the three-dots buttons. The context comes from the element the user pressed, never from a title.
    function rememberContext(event) {
        var target = event.target && event.target.closest ? event.target : null;
        if (!target) { return; }

        var card = target.closest('[data-action="menu"]') ? target.closest('.card[data-id], [data-id][data-type]') : null;
        if (card && card.getAttribute('data-id')) {
            lastContext = { id: card.getAttribute('data-id'), type: card.getAttribute('data-type') || null, at: Date.now() };
            return;
        }

        // The item page's own menu button.
        if (target.closest('.btnMoreCommands')) {
            var id = itemIdFromHash();
            lastContext = id ? { id: id, type: null, at: Date.now() } : null;
        }
    }

    function currentContext() {
        if (!lastContext || Date.now() - lastContext.at > CONTEXT_MAX_AGE_MS) { return null; }
        return lastContext;
    }

    // ------------------------------------------------------ the menu entry

    // Step 2 and 3: notice the dialog and add one entry to it.
    function onSheet(sheet) {
        if (!sheet || sheet.getAttribute(INJECTED_ATTR)) { return; }
        var scroller = sheet.querySelector('.actionSheetScroller');
        if (!scroller || !scroller.querySelector('.actionSheetMenuItem')) { return; }

        var ctx = currentContext();
        if (!ctx) { return; }
        if (ctx.type && SUPPORTED_TYPES.indexOf(ctx.type) < 0) { return; }

        sheet.setAttribute(INJECTED_ATTR, '1');
        getCapabilities().then(function (caps) {
            if (!caps || !caps.SupportsItemRefresh) { return; }
            if (!document.body.contains(sheet) || scroller.querySelector('[data-id="' + ENTRY_ID + '"]')) { return; }
            addEntry(scroller, ctx);
        });
    }

    function addEntry(scroller, ctx) {
        var items = scroller.querySelectorAll('.actionSheetMenuItem');
        var anchor = scroller.querySelector('[data-id="editimages"]') || scroller.querySelector('[data-id="refresh"]') || items[items.length - 1];
        var template = anchor || items[0];

        var button = template.cloneNode(true);
        button.setAttribute('data-id', ENTRY_ID);
        button.removeAttribute('id');
        var icon = button.querySelector('.actionsheetMenuItemIcon');
        if (icon) {
            // The icon font is a per-name class, and only some names are shipped: reuse one jellyfin-web itself uses.
            var tokens = icon.className.split(/\s+/).filter(Boolean);
            tokens.pop();
            icon.className = tokens.concat(['refresh']).join(' ');
            icon.textContent = '';
        }
        var text = button.querySelector('.actionSheetItemText');
        if (text) { text.textContent = t('entry'); }

        // Runs before jellyfin-web's own handler closes the dialog (target phase beats the delegate on the scroller).
        button.addEventListener('click', function () { refresh(ctx); });

        if (anchor && anchor.nextSibling) { scroller.insertBefore(button, anchor.nextSibling); }
        else { scroller.appendChild(button); }
    }

    // ------------------------------------------------------------- refresh

    function bustImages(id) {
        var stamp = String(Date.now());
        var nodes = document.querySelectorAll('.card[data-id="' + id + '"] .cardImageContainer, [data-id="' + id + '"] .cardImageContainer');
        Array.prototype.forEach.call(nodes, function (node) {
            var bg = node.style && node.style.backgroundImage;
            if (bg && bg.indexOf('url(') === 0) {
                node.style.backgroundImage = bg.replace(/([&?])_arf=\d+/, '').replace(/"?\)$/, (bg.indexOf('?') >= 0 ? '&' : '?') + '_arf=' + stamp + '")');
            }
        });
    }

    function summarize(result) {
        var slots = (result && result.Slots) || [];
        var updated = slots.filter(function (s) { return s.Status === 'updated' || s.Status === 'dryrun'; });
        if (updated.length) {
            var names = updated.map(function (s) { return s.Slot.split(':')[0] + (s.Source ? ' (' + s.Source + ')' : ''); });
            return { kind: 'ok', text: t('done') + ': ' + names.join(', ') };
        }
        if (slots.length && slots.every(function (s) { return s.Status === 'locked' || s.Status === 'protected'; })) {
            return { kind: 'info', text: t('locked') };
        }
        return { kind: 'info', text: t('nothing') };
    }

    // Step 4.
    function refresh(ctx) {
        var working = toast(t('working'), 'info');
        call('POST', 'ArtworkRefresher/Item/' + encodeURIComponent(ctx.id), {}).then(function (result) {
            if (working.parentNode) { working.parentNode.removeChild(working); }
            var s = summarize(result);
            toast(s.text, s.kind);
            if (s.kind === 'ok') { bustImages(ctx.id); }
        }, function (error) {
            if (working.parentNode) { working.parentNode.removeChild(working); }
            var status = error && (error.status || (error.response && error.response.status));
            toast(status === 429 ? t('slow') : status === 403 ? t('locked') : t('failed'), 'error');
        });
    }

    // ------------------------------------------------- rotation per page load
    //
    // When the administrator sets an image type to "per page load", the server keeps a pool of images for each
    // item and serves a different one from GET ArtworkRefresher/Rotating/{id}/{type}. Here we only swap the
    // address of card and detail-page images that point at /Items/{id}/Images/{type}: standard Jellyfin rows
    // ("Recently added" included), the item page, and any custom Home that renders ordinary cards or images.
    // No other plugin is needed or read. If the server has nothing for an item the original image stays.

    var IMAGE_URL = /\/Items\/([0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\/Images\/(Primary|Backdrop|Logo|Thumb)(?:\/0)?(?:[?#]|$)/;
    var AVAILABLE_TTL_MS = 60000;
    var SCAN_DELAY_MS = 150;
    var MAX_IDS_PER_CALL = 100;

    var availableAt = {};     // id -> time it was last asked about
    var availableKeys = {};   // "id:Type" -> true when the server can rotate it
    var pendingNodes = [];
    var scanTimer = null;
    var capsFailedAt = 0;

    function cleanUrl(text) {
        if (!text) { return null; }
        var m = /url\((['"]?)(.*?)\1\)/.exec(text);
        return m ? m[2] : text;
    }

    function parseTarget(url) {
        var m = url ? IMAGE_URL.exec(url) : null;
        return m ? { id: m[1].replace(/-/g, '').toLowerCase(), type: m[2] } : null;
    }

    function elementUrl(el) {
        if (!el.tagName) { return null; }
        if (el.tagName === 'IMG') { return el.getAttribute('src'); }
        return cleanUrl(el.style && el.style.backgroundImage);
    }

    function isOurs(url) { return !!url && url.indexOf('ArtworkRefresher/Rotating/') >= 0; }

    function gather(node, out) {
        if (!node || node.nodeType !== 1) { return; }
        var list = [node];
        if (node.querySelectorAll) {
            Array.prototype.push.apply(list, node.querySelectorAll('[style*="/Images/"], img[src*="/Images/"]'));
        }
        list.forEach(function (el) {
            var url = elementUrl(el);
            if (!url || isOurs(url) || el.getAttribute('data-arf-orig') === url) { return; }
            var target = parseTarget(url);
            if (target) { out.push({ el: el, url: url, id: target.id, type: target.type }); }
        });
    }

    function swap(job) {
        var client = api();
        if (!client) { return; }
        // One address per page view: the signed token says the server may serve it, the nonce makes the preload
        // and the display get the same image (the server remembers the draw for a few minutes).
        var nonce = Date.now().toString(36) + Math.floor(Math.random() * 1e9).toString(36);
        var next = client.getUrl('ArtworkRefresher/Rotating/' + job.id + '/' + job.type) + '?s=' + encodeURIComponent(availableKeys[job.id + ':' + job.type]) + '&n=' + nonce;
        job.el.setAttribute('data-arf-orig', job.url);
        // Load first: if the server answers 404 the card keeps its normal image and never flickers.
        var probe = new Image();
        probe.onload = function () {
            if (job.el.getAttribute('data-arf-orig') !== job.url) { return; }
            if (job.el.tagName === 'IMG') {
                job.el.removeAttribute('srcset');
                job.el.setAttribute('src', next);
            } else {
                job.el.style.backgroundImage = 'url("' + next + '")';
            }
        };
        probe.onerror = function () { delete availableKeys[job.id + ':' + job.type]; };
        probe.src = next;
    }

    function runScan() {
        scanTimer = null;
        var nodes = pendingNodes;
        pendingNodes = [];
        var jobs = [];
        nodes.forEach(function (n) { gather(n, jobs); });
        // The same element can arrive through several records (a node and its parent): swap it once.
        jobs = jobs.filter(function (j, i) { return jobs.findIndex(function (k) { return k.el === j.el; }) === i; });
        if (!jobs.length) { return Promise.resolve(0); }

        if (capsFailedAt && Date.now() - capsFailedAt < 15000) { return Promise.resolve(0); }
        return getCapabilities().then(function (caps) {
            var types = (caps && caps.RotationPerLoadTypes) || [];
            if (!caps || !types.length) {
                if (!caps) { capsFailedAt = Date.now(); }
                return 0;
            }
            jobs = jobs.filter(function (j) { return types.indexOf(j.type) >= 0; });
            var now = Date.now();
            var ask = [];
            jobs.forEach(function (j) {
                if ((!availableAt[j.id] || now - availableAt[j.id] > AVAILABLE_TTL_MS) && ask.indexOf(j.id) < 0) { ask.push(j.id); }
            });
            var calls = [];
            for (var i = 0; i < ask.length; i += MAX_IDS_PER_CALL) {
                (function (chunk) {
                    chunk.forEach(function (id) { availableAt[id] = now; });
                    calls.push(call('POST', 'ArtworkRefresher/Rotating/Available', { Ids: chunk }).then(function (r) {
                        chunk.forEach(function (id) { types.forEach(function (t) { delete availableKeys[id + ':' + t]; }); });
                        var keys = (r && r.keys) || {};
                        Object.keys(keys).forEach(function (k) { availableKeys[k] = keys[k]; });
                    }, function () { chunk.forEach(function (id) { delete availableAt[id]; }); }));
                })(ask.slice(i, i + MAX_IDS_PER_CALL));
            }
            return Promise.all(calls).then(function () {
                var done = 0;
                jobs.forEach(function (j) {
                    if (availableKeys[j.id + ':' + j.type]) { swap(j); done++; }
                });
                return done;
            });
        });
    }

    function schedule(node) {
        if (!node) { return; }
        pendingNodes.push(node);
        if (!scanTimer) { scanTimer = setTimeout(runScan, SCAN_DELAY_MS); }
    }

    function startRotation() {
        schedule(document.body);
        var observer = new MutationObserver(function (records) {
            records.forEach(function (record) {
                if (record.type === 'attributes') {
                    // Cheap test first: carousels change style all the time, only image addresses matter.
                    var url = elementUrl(record.target);
                    if (url && url.indexOf('/Images/') >= 0 && !isOurs(url)) { schedule(record.target); }
                    return;
                }
                Array.prototype.forEach.call(record.addedNodes, schedule);
            });
        });
        observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'src'] });
    }

    // Exposed so the page logic can be exercised without a browser (tests/js).
    window.__artworkRefresherRotation = {
        parseTarget: parseTarget,
        scanNow: function (node) { pendingNodes.push(node); return runScan(); },
        reset: function () { availableAt = {}; availableKeys = {}; capabilities = null; capsFailedAt = 0; }
    };

    // ------------------------------------------------------------ bootstrap

    function start() {
        // Capture phase: runs before the button's own handler opens the menu.
        document.addEventListener('click', rememberContext, true);
        document.addEventListener('contextmenu', rememberContext, true);

        // Menus are added directly under <body>, so observing the direct children is enough:
        // no subtree observation and no work on every DOM change of the page.
        var observer = new MutationObserver(function (records) {
            records.forEach(function (record) {
                Array.prototype.forEach.call(record.addedNodes, function (node) {
                    if (node.nodeType !== 1) { return; }
                    var sheet = node.classList && node.classList.contains('actionSheet') ? node : node.querySelector && node.querySelector('.actionSheet');
                    if (sheet) { onSheet(sheet); }
                });
            });
        });
        observer.observe(document.body, { childList: true });

        startRotation();
    }

    if (document.body) { start(); }
    else { document.addEventListener('DOMContentLoaded', start); }
})();
