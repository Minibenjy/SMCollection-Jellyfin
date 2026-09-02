/*
 * AI Assistant — Jellyfin web client entry point.
 *
 * Mounts a floating launcher and a chat panel directly on document.body. Nothing
 * here reaches into Jellyfin's own DOM: the plugin owns every node it creates, so a
 * change to the web client's markup can cost this plugin its styling but cannot
 * break the page it is injected into.
 */
(function () {
    'use strict';

    var LAUNCHER_ID = 'aiAssistantLauncher';
    var PANEL_ID = 'aiAssistantPanel';
    var STORAGE_KEY = 'aiAssistant:conversation';

    function newConversationId() {
        return 'web-' + Math.random().toString(36).slice(2, 10);
    }

    /*
     * The conversation id is remembered per browser so a reload continues where the
     * person left off instead of silently starting over. Storage can throw (private
     * windows, blocked site data), so every access is guarded and falls back to a
     * fresh conversation rather than failing to load.
     */
    function rememberedConversationId() {
        try {
            var stored = window.localStorage.getItem(STORAGE_KEY);
            if (stored) {
                return stored;
            }
        } catch (e) {
            // Storage unavailable; a per-session conversation is the fallback.
        }

        return newConversationId();
    }

    function rememberConversationId(id) {
        try {
            window.localStorage.setItem(STORAGE_KEY, id);
        } catch (e) {
            // Not fatal — the id still lives in memory for this page.
        }
    }

    /*
     * Routes where the launcher stays hidden. Settings and playback are places where a
     * floating button is in the way rather than useful, and the assistant has nothing
     * to offer on an administration screen.
     */
    var HIDDEN_ROUTES = [
        'dashboard',
        'configurationpage',
        'mypreferences',
        'userprofile',
        'wizard',
        'video',
        'login',
        'selectserver',
        'addserver',
        'forgotpassword'
    ];

    var state = {
        conversationId: rememberedConversationId(),
        open: false,
        busy: false,
        view: 'chat',
        checked: false,
        enabled: false,
        reason: null,
        serverLabel: 'Assistant'
    };

    function apiClient() {
        return window.ApiClient;
    }

    function isSignedIn() {
        var client = apiClient();
        return !!(client && typeof client.getCurrentUserId === 'function' && client.getCurrentUserId());
    }

    function currentRoute() {
        var hash = (window.location.hash || '').replace(/^#!?\/?/, '').toLowerCase();
        return hash.split('?')[0].split('/')[0];
    }

    function routeAllowsLauncher() {
        var route = currentRoute();
        for (var i = 0; i < HIDDEN_ROUTES.length; i++) {
            if (route.indexOf(HIDDEN_ROUTES[i]) === 0) {
                return false;
            }
        }
        return true;
    }

    function request(path, options) {
        var client = apiClient();
        if (!client) {
            return Promise.reject(new Error('no api client'));
        }

        var settings = options || {};
        return client.ajax({
            type: settings.type || 'GET',
            url: client.getUrl('AiAssistant/' + path),
            data: settings.data ? JSON.stringify(settings.data) : undefined,
            contentType: settings.data ? 'application/json' : undefined,
            dataType: settings.dataType === null ? undefined : 'json'
        });
    }

    /* ---------------------------------------------------------------- styling */

    function injectStyles() {
        if (document.getElementById('aiAssistantStyles')) {
            return;
        }

        var css = [
            '#' + LAUNCHER_ID + '{position:fixed;right:1.25rem;bottom:1.25rem;z-index:1000;',
            'width:3.25rem;height:3.25rem;border-radius:50%;border:none;cursor:pointer;',
            'display:flex;align-items:center;justify-content:center;',
            'background:var(--accent,#00a4dc);color:#fff;',
            'box-shadow:0 4px 14px rgba(0,0,0,.35);transition:transform .15s ease,opacity .15s ease}',
            '#' + LAUNCHER_ID + ':hover{transform:scale(1.06)}',
            '#' + LAUNCHER_ID + '[hidden]{display:none}',

            '#' + PANEL_ID + '{position:fixed;right:1.25rem;bottom:5.25rem;z-index:1001;',
            'width:min(24rem,calc(100vw - 2.5rem));height:min(32rem,calc(100vh - 8rem));',
            'display:flex;flex-direction:column;border-radius:.75rem;overflow:hidden;',
            'background:#1c1c1c;color:#eee;box-shadow:0 8px 32px rgba(0,0,0,.5);',
            'font-size:.9rem;line-height:1.45}',
            '#' + PANEL_ID + '[hidden]{display:none}',
            /* Every view below sets display:flex, which outranks the hidden
               attribute's default. Without this the views stack instead of swap. */
            '#' + PANEL_ID + ' [hidden]{display:none!important}',

            '.aiaHeader{display:flex;align-items:center;justify-content:space-between;',
            'padding:.75rem 1rem;background:rgba(255,255,255,.06);font-weight:600}',
            '.aiaHeader button{background:none;border:none;color:inherit;cursor:pointer;',
            'font-size:1.1rem;line-height:1;opacity:.7}',
            '.aiaHeader button:hover{opacity:1}',

            '.aiaLog{flex:1;overflow-y:auto;padding:1rem;display:flex;flex-direction:column;gap:.75rem}',
            '.aiaMsg{max-width:85%;padding:.5rem .75rem;border-radius:.65rem;white-space:pre-wrap;',
            'overflow-wrap:anywhere}',
            '.aiaMsg.user{align-self:flex-end;background:var(--accent,#00a4dc);color:#fff}',
            '.aiaMsg.bot{align-self:flex-start;background:rgba(255,255,255,.09)}',
            '.aiaMsg.err{align-self:stretch;background:rgba(220,80,80,.16);color:#ffb3b3}',
            '.aiaMsg.pending{opacity:.6;font-style:italic}',

            '.aiaForm{display:flex;gap:.5rem;padding:.75rem;background:rgba(255,255,255,.04)}',
            '.aiaForm input{flex:1;min-width:0;padding:.5rem .75rem;border-radius:.5rem;',
            'border:1px solid rgba(255,255,255,.16);background:rgba(0,0,0,.3);color:inherit;font:inherit}',
            '.aiaForm input:focus{outline:2px solid var(--accent,#00a4dc);outline-offset:-1px}',
            '.aiaForm button{padding:.5rem .9rem;border-radius:.5rem;border:none;cursor:pointer;',
            'background:var(--accent,#00a4dc);color:#fff;font:inherit}',
            '.aiaForm button:disabled{opacity:.5;cursor:default}',

            '.aiaHeaderActions{display:flex;gap:.35rem;align-items:center}',
            '.aiaSettings{flex:1;overflow-y:auto;padding:1rem;display:flex;flex-direction:column;gap:.85rem}',
            '.aiaField{display:flex;flex-direction:column;gap:.3rem}',
            '.aiaField label{font-size:.8rem;opacity:.75}',
            '.aiaField input,.aiaField select{padding:.45rem .6rem;border-radius:.5rem;font:inherit;',
            'border:1px solid rgba(255,255,255,.16);background:rgba(0,0,0,.3);color:inherit}',
            '.aiaField .aiaHelp{font-size:.75rem;opacity:.6}',
            '.aiaRow{display:flex;gap:.5rem}',
            '.aiaRow>*{flex:1;min-width:0}',
            '.aiaBtn{padding:.5rem .9rem;border-radius:.5rem;border:none;cursor:pointer;font:inherit;',
            'background:var(--accent,#00a4dc);color:#fff}',
            '.aiaBtn.secondary{background:rgba(255,255,255,.14);color:inherit}',
            '.aiaBtn:disabled{opacity:.5;cursor:default}',
            '.aiaNote{font-size:.8rem;opacity:.75;line-height:1.4}',
            '.aiaSaved{font-size:.8rem;color:#7ddc7d}',
            '.aiaConfirm{max-width:100%}',
            '.aiaHistoryList{flex:1;overflow-y:auto;padding:1rem;display:flex;flex-direction:column;gap:.5rem}',
            '.aiaHistoryRow{text-align:left;white-space:normal}',

            '@media (prefers-color-scheme:light){',
            '#' + PANEL_ID + '{background:#fafafa;color:#1a1a1a}',
            '.aiaHeader{background:rgba(0,0,0,.05)}',
            '.aiaMsg.bot{background:rgba(0,0,0,.07)}',
            '.aiaForm{background:rgba(0,0,0,.03)}',
            '.aiaForm input{background:#fff;border-color:rgba(0,0,0,.2)}',
            '.aiaField input,.aiaField select{background:#fff;border-color:rgba(0,0,0,.2)}',
            '.aiaBtn.secondary{background:rgba(0,0,0,.1)}}'
        ].join('');

        var style = document.createElement('style');
        style.id = 'aiAssistantStyles';
        style.textContent = css;
        document.head.appendChild(style);
    }

    /* ------------------------------------------------------------------- ui */

    function buildLauncher() {
        var button = document.createElement('button');
        button.id = LAUNCHER_ID;
        button.type = 'button';
        button.title = 'Assistant';
        button.setAttribute('aria-label', 'Assistant');
        button.innerHTML = '<span class="material-icons" aria-hidden="true">forum</span>';
        button.addEventListener('click', togglePanel);
        document.body.appendChild(button);
        return button;
    }

    function buildPanel() {
        var panel = document.createElement('div');
        panel.id = PANEL_ID;
        panel.hidden = true;
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', 'Assistant');

        panel.innerHTML =
            '<div class="aiaHeader"><span class="aiaTitle">Assistant</span>' +
            '<span class="aiaHeaderActions">' +
            '<button type="button" class="aiaHistory" aria-label="Previous conversations" title="Previous conversations">&#128337;</button>' +
            '<button type="button" class="aiaNew" aria-label="New conversation" title="New conversation">&#10010;</button>' +
            '<button type="button" class="aiaGear" aria-label="Assistant settings" hidden>&#9881;</button>' +
            '<button type="button" class="aiaClose" aria-label="Close">&times;</button>' +
            '</span></div>' +
            '<div class="aiaLog" role="log" aria-live="polite"></div>' +
            '<form class="aiaForm">' +
            '<input type="text" autocomplete="off" placeholder="Ask about your library…" aria-label="Message" />' +
            '<button type="submit">Send</button></form>' +
            '<div class="aiaHistoryList" hidden></div>' +
            '<div class="aiaSettings" hidden>' +
            '<div class="aiaField"><label for="aiaProvider">Provider</label>' +
            '<select id="aiaProvider"></select></div>' +
            '<div class="aiaField"><label for="aiaBaseUrl">Endpoint</label>' +
            '<input type="text" id="aiaBaseUrl" autocomplete="off" placeholder="http://192.168.1.20:11434" />' +
            '<span class="aiaHelp">Leave empty for the provider default.</span></div>' +
            '<div class="aiaField"><label for="aiaModel">Model</label>' +
            '<div class="aiaRow"><input type="text" id="aiaModel" autocomplete="off" list="aiaModelList" />' +
            '<button type="button" class="aiaBtn secondary aiaLoadModels" style="flex:0 0 auto">Load</button></div>' +
            '<datalist id="aiaModelList"></datalist>' +
            '<span class="aiaHelp aiaModelHelp">Pick a provider and endpoint, Save, then Load to list what it offers.</span></div>' +
            '<div class="aiaField"><label for="aiaLanguage">Library language</label>' +
            '<input type="text" id="aiaLanguage" autocomplete="off" placeholder="" />' +
            '<span class="aiaHelp aiaLanguageHelp"></span></div>' +
            '<div class="aiaField aiaKeyField" hidden><label for="aiaApiKey">API key</label>' +
            '<input type="password" id="aiaApiKey" autocomplete="new-password" placeholder="Leave blank to keep the stored key" />' +
            '<span class="aiaHelp aiaKeyHint"></span></div>' +
            '<div class="aiaRow"><button type="button" class="aiaBtn aiaSave">Save</button>' +
            '<button type="button" class="aiaBtn secondary aiaBack">Back to chat</button></div>' +
            '<div class="aiaSaved" hidden>Saved.</div>' +
            '<div class="aiaNote aiaLocked" hidden></div>' +
            '</div>';

        panel.querySelector('.aiaClose').addEventListener('click', togglePanel);
        panel.querySelector('.aiaGear').addEventListener('click', toggleView);
        panel.querySelector('.aiaNew').addEventListener('click', startNewConversation);
        panel.querySelector('.aiaHistory').addEventListener('click', showHistory);
        panel.querySelector('.aiaBack').addEventListener('click', showChat);
        panel.querySelector('.aiaSave').addEventListener('click', saveSettings);
        panel.querySelector('.aiaLoadModels').addEventListener('click', loadModels);
        panel.querySelector('#aiaProvider').addEventListener('change', onProviderChanged);
        panel.querySelector('.aiaForm').addEventListener('submit', onSubmit);
        document.body.appendChild(panel);
        return panel;
    }

    function panel() {
        return document.getElementById(PANEL_ID) || buildPanel();
    }

    function append(text, kind) {
        var log = panel().querySelector('.aiaLog');
        var node = document.createElement('div');
        node.className = 'aiaMsg ' + kind;
        node.textContent = text;
        log.appendChild(node);
        log.scrollTop = log.scrollHeight;
        return node;
    }

    function togglePanel() {
        state.open = !state.open;
        var element = panel();
        element.hidden = !state.open;

        if (!state.open) {
            return;
        }

        setView('chat');
        element.querySelector('.aiaForm input').focus();

        if (!state.checked) {
            state.checked = true;
            refreshStatus();
            restoreTranscript();
        }
    }

    /* Re-renders the remembered conversation so a reload does not look like a reset. */
    function restoreTranscript() {
        var log = panel().querySelector('.aiaLog');
        if (log.childElementCount > 0) {
            return;
        }

        request('Conversations/' + encodeURIComponent(state.conversationId)).then(function (turns) {
            if (!turns || !turns.length) {
                return;
            }

            turns.forEach(function (t) {
                append(t.Text, t.Role === 'user' ? 'user' : 'bot');
            });
        }).catch(function () {
            // A conversation that expired simply starts empty.
        });
    }

    function refreshStatus(quiet) {
        return request('Status').then(function (status) {
            state.enabled = !!status.Enabled;
            state.reason = status.Reason;
            state.serverLabel = status.ServerLabel || 'Assistant';

            var element = panel();
            if (state.view === 'chat') {
                element.querySelector('.aiaTitle').textContent = state.serverLabel;
            }

            // The gear appears only when the administrator permits per-user providers.
            element.querySelector('.aiaGear').hidden = !status.CanConfigure;
            element.querySelector('.aiaForm button').disabled = !state.enabled;

            if (!state.enabled && !quiet) {
                append(state.reason || 'The assistant is not configured yet.', 'err');
                if (status.CanConfigure) {
                    offerSettingsShortcut();
                }
            }
        }).catch(function () {
            if (!quiet) {
                append('The assistant could not be reached.', 'err');
            }
        });
    }

    function offerSettingsShortcut() {
        var log = panel().querySelector('.aiaLog');
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'aiaBtn';
        button.style.alignSelf = 'flex-start';
        button.textContent = 'Set up my provider';
        button.addEventListener('click', showSettings);
        log.appendChild(button);
        log.scrollTop = log.scrollHeight;
    }

    /*
     * A new conversation is forgotten on the server too, not just cleared on screen:
     * the transcript lives server-side, so wiping only the log would leave the model
     * still carrying everything that was said.
     */
    function startNewConversation() {
        var previous = state.conversationId;
        state.conversationId = newConversationId();
        rememberConversationId(state.conversationId);

        var element = panel();
        element.querySelector('.aiaLog').innerHTML = '';
        setView('chat');

        request('Chat/' + encodeURIComponent(previous), { type: 'DELETE', dataType: null })
            .catch(function () {
                // The old transcript expires on its own; nothing to recover here.
            });

        state.checked = false;
        refreshStatus();
        element.querySelector('.aiaForm input').focus();
    }

    /* A write the assistant proposed, shown with its own approve/decline buttons. */
    function askConfirmation(question) {
        var log = panel().querySelector('.aiaLog');

        var wrap = document.createElement('div');
        wrap.className = 'aiaMsg bot aiaConfirm';
        wrap.textContent = question;

        var row = document.createElement('div');
        row.className = 'aiaRow';
        row.style.marginTop = '.5rem';

        var yes = document.createElement('button');
        yes.type = 'button';
        yes.className = 'aiaBtn';
        yes.textContent = 'Yes, do it';

        var no = document.createElement('button');
        no.type = 'button';
        no.className = 'aiaBtn secondary';
        no.textContent = 'No';

        function answer(approved) {
            yes.disabled = true;
            no.disabled = true;
            row.remove();
            send('Chat/Confirm', { ConversationId: state.conversationId, Approved: approved });
        }

        yes.addEventListener('click', function () { answer(true); });
        no.addEventListener('click', function () { answer(false); });

        row.appendChild(yes);
        row.appendChild(no);
        wrap.appendChild(row);
        log.appendChild(wrap);
        log.scrollTop = log.scrollHeight;
    }

    function send(path, payload) {
        state.busy = true;
        var pending = append('Thinking…', 'bot pending');

        return request(path, { type: 'POST', data: payload }).then(function (reply) {
            pending.remove();
            if (reply.NeedsConfirmation) {
                askConfirmation(reply.Reply);
            } else {
                append(reply.Reply, reply.Success ? 'bot' : 'err');
            }
        }).catch(function () {
            pending.remove();
            append('Something went wrong reaching the assistant.', 'err');
        }).then(function () {
            state.busy = false;
        });
    }

    function onSubmit(event) {
        event.preventDefault();

        var input = panel().querySelector('.aiaForm input');
        var message = input.value.trim();
        if (!message || state.busy || !state.enabled) {
            return;
        }

        input.value = '';
        append(message, 'user');
        send('Chat', { ConversationId: state.conversationId, Message: message });
    }


    /* ------------------------------------------------------------ settings */

    /*
     * The plugin's configuration page lives in the Jellyfin dashboard, which only
     * administrators can reach. Everyone else configures their own provider here,
     * inside the panel, against the same per-user endpoints — so a regular user is
     * never dependent on an administrator to point the assistant at their own model.
     */

    var settingsProviders = [];

    /*
     * Chat and settings are two views of one panel, swapped rather than stacked. The
     * gear toggles between them and reports which way it will go, so there is always
     * a way back out of settings.
     */
    function setView(name) {
        var element = panel();
        var settings = name === 'settings';
        var history = name === 'history';

        element.querySelector('.aiaLog').hidden = settings || history;
        element.querySelector('.aiaForm').hidden = settings || history;
        element.querySelector('.aiaSettings').hidden = !settings;
        element.querySelector('.aiaHistoryList').hidden = !history;

        var gear = element.querySelector('.aiaGear');
        gear.innerHTML = settings ? '&#8592;' : '&#9881;';
        gear.setAttribute('aria-label', settings ? 'Back to chat' : 'Assistant settings');
        gear.title = settings ? 'Back to chat' : 'Settings';

        element.querySelector('.aiaTitle').textContent = settings
            ? 'Assistant settings'
            : (history ? 'Previous conversations' : state.serverLabel);

        state.view = name;
    }

    function showHistory() {
        if (state.view === 'history') {
            showChat();
            return;
        }

        setView('history');

        var list = panel().querySelector('.aiaHistoryList');
        list.innerHTML = '<div class="aiaNote">Loading…</div>';

        request('Conversations').then(function (conversations) {
            list.innerHTML = '';

            if (!conversations || !conversations.length) {
                list.innerHTML = '<div class="aiaNote">No earlier conversations. '
                    + 'They are kept in memory for a day and are lost if the server restarts.</div>';
                return;
            }

            conversations.forEach(function (c) {
                var row = document.createElement('button');
                row.type = 'button';
                row.className = 'aiaBtn secondary aiaHistoryRow';
                row.textContent = c.Title || 'Conversation';
                if (c.Id === state.conversationId) {
                    row.textContent += '  (current)';
                }

                row.addEventListener('click', function () { resumeConversation(c.Id); });
                list.appendChild(row);
            });
        }).catch(function () {
            list.innerHTML = '<div class="aiaNote">Could not load your conversations.</div>';
        });
    }

    function resumeConversation(id) {
        state.conversationId = id;
        rememberConversationId(id);

        var log = panel().querySelector('.aiaLog');
        log.innerHTML = '';
        setView('chat');

        request('Conversations/' + encodeURIComponent(id)).then(function (turns) {
            (turns || []).forEach(function (t) {
                append(t.Text, t.Role === 'user' ? 'user' : 'bot');
            });
        }).catch(function () {
            append('That conversation could not be reloaded.', 'err');
        });
    }

    function toggleView() {
        if (state.view === 'settings') {
            showChat();
        } else {
            showSettings();
        }
    }

    function showSettings() {
        setView('settings');
        loadSettings();
    }

    function showChat() {
        setView('chat');

        // Settings may have just made the assistant usable, so re-check.
        refreshStatus(true);
    }

    function onProviderChanged() {
        var element = panel();
        var id = element.querySelector('#aiaProvider').value;
        var selected = null;

        for (var i = 0; i < settingsProviders.length; i++) {
            if (settingsProviders[i].Id === id) {
                selected = settingsProviders[i];
                break;
            }
        }

        // The key field is shown only for providers that actually need one, so a
        // self-hosted backend never prompts for a secret that does not exist.
        element.querySelector('.aiaKeyField').hidden = !(selected && selected.RequiresCredential);
    }

    function loadSettings() {
        var element = panel();

        request('Settings').then(function (settings) {
            settingsProviders = settings.AvailableProviders || [];

            var select = element.querySelector('#aiaProvider');
            select.innerHTML = '';

            if (!settingsProviders.length) {
                var locked = element.querySelector('.aiaLocked');
                locked.hidden = false;
                locked.textContent =
                    'The server administrator has not allowed users to choose their own provider. '
                    + 'The assistant uses the server default, if one is configured.';
                element.querySelector('.aiaSave').disabled = true;
                return;
            }

            var blank = document.createElement('option');
            blank.value = '';
            blank.textContent = 'Select a provider…';
            select.appendChild(blank);

            settingsProviders.forEach(function (p) {
                var option = document.createElement('option');
                option.value = p.Id;
                option.textContent = p.DisplayName;
                select.appendChild(option);
            });

            select.value = settings.ProviderId || '';
            element.querySelector('#aiaBaseUrl').value = settings.BaseUrl || '';
            element.querySelector('#aiaModel').value = settings.Model || '';
            element.querySelector('#aiaLanguage').value = settings.MetadataLanguage || '';
            element.querySelector('.aiaLanguageHelp').textContent = settings.ServerMetadataLanguage
                ? 'The language your titles are catalogued in. Leave empty to use the server setting ('
                    + settings.ServerMetadataLanguage + ').'
                : 'The language your titles are catalogued in, for example Spanish. Leave empty if unsure.';
            element.querySelector('#aiaApiKey').value = '';
            element.querySelector('.aiaKeyHint').textContent = settings.ApiKeyHint
                ? 'Stored: ' + settings.ApiKeyHint
                : 'No key stored yet.';

            onProviderChanged();
        }).catch(function () {
            var locked = element.querySelector('.aiaLocked');
            locked.hidden = false;
            locked.textContent = 'Your assistant settings could not be loaded.';
        });
    }

    function saveSettings() {
        var element = panel();
        var button = element.querySelector('.aiaSave');
        var saved = element.querySelector('.aiaSaved');

        button.disabled = true;
        saved.hidden = true;

        var payload = {
            ProviderId: element.querySelector('#aiaProvider').value,
            BaseUrl: element.querySelector('#aiaBaseUrl').value.trim(),
            Model: element.querySelector('#aiaModel').value.trim(),
            MetadataLanguage: element.querySelector('#aiaLanguage').value.trim(),
            ApiKey: element.querySelector('#aiaApiKey').value.trim() || null
        };

        request('Settings', { type: 'POST', data: payload, dataType: null }).then(function () {
            element.querySelector('#aiaApiKey').value = '';
            saved.hidden = false;
            state.checked = false;
            loadSettings();
        }).catch(function () {
            var locked = element.querySelector('.aiaLocked');
            locked.hidden = false;
            locked.textContent = 'Those settings were rejected by the server.';
        }).then(function () {
            button.disabled = false;
        });
    }

    function loadModels() {
        var element = panel();
        var button = element.querySelector('.aiaLoadModels');
        var help = element.querySelector('.aiaModelHelp');

        button.disabled = true;
        help.textContent = 'Asking the endpoint…';

        request('Models').then(function (models) {
            var list = element.querySelector('#aiaModelList');
            list.innerHTML = '';

            (models || []).forEach(function (name) {
                var option = document.createElement('option');
                option.value = name;
                list.appendChild(option);
            });

            help.textContent = models && models.length
                ? models.length + ' model(s) available — click the field to pick one.'
                : 'No models came back. Save your provider and endpoint first, and check the endpoint is reachable from the server.';
        }).catch(function () {
            help.textContent = 'The endpoint could not be reached.';
        }).then(function () {
            button.disabled = false;
        });
    }

    /* -------------------------------------------------------------- lifecycle */

    function sync() {
        if (!isSignedIn()) {
            hideAll();
            return;
        }

        injectStyles();

        var launcher = document.getElementById(LAUNCHER_ID) || buildLauncher();
        var visible = routeAllowsLauncher();
        launcher.hidden = !visible;

        if (!visible && state.open) {
            state.open = false;
            panel().hidden = true;
        }
    }

    function hideAll() {
        var launcher = document.getElementById(LAUNCHER_ID);
        if (launcher) {
            launcher.hidden = true;
        }

        var element = document.getElementById(PANEL_ID);
        if (element) {
            element.hidden = true;
            state.open = false;
        }
    }

    function start() {
        sync();

        /*
         * Jellyfin is a single-page app: routes change without a page load, and sign-in
         * happens after this script runs. Hash changes cover navigation; the interval is
         * the backstop for sign-in and for route changes that do not touch the hash.
         */
        window.addEventListener('hashchange', sync);
        document.addEventListener('viewshow', sync);
        setInterval(sync, 2000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
