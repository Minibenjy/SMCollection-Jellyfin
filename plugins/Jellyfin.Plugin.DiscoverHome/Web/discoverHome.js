// DiscoverHome — a discovery-style layer over Jellyfin's home screen.
//
// Loaded from /DiscoverHome/ClientScript, which the plugin adds to index.html.
// Everything it does is presentation: it reorders and restyles rows Jellyfin
// already rendered for this user and never changes what they are allowed to
// see. All behaviour is driven by /DiscoverHome/Settings, so the administrator
// config page is the single place any of this is tuned.
//
// Performance is a design constraint, not an afterthought: one MutationObserver,
// debounced, no polling, and no node ever moved (ordering is done with CSS
// `order`, because re-parenting Jellyfin's `emby-scroller` elements fires their
// attach/detach callbacks and breaks horizontal scrolling).
(function () {
  "use strict";

  if (window.__discoverHomeLoaded) return;
  window.__discoverHomeLoaded = true;

  var CACHE_TTL_MS = 12 * 60 * 60 * 1000;
  var SETTLE_MS = 1200;

  var settings = null;
  var mediaQuery = null;

  // Ordering state lives at module scope, not per pass: Jellyfin fills the home
  // screen progressively, so later batches must continue the same running order
  // and the same channel-type rotation instead of restarting from zero.
  var nextOrder = 0;
  var lastChannelType = null;
  var typeCursor = {};

  var CHANNEL_PALETTES = {
    genre: ["#c0392b", "#16a085", "#8e44ad", "#2980b9", "#d68910", "#1abc9c", "#7c5cff", "#2c3e50"],
    studio: ["#0f6b5c", "#7c2d54", "#1f4e79", "#8a4b08", "#4a235a", "#0b5345", "#943126", "#154360"]
  };

  function client() {
    return window.ApiClient;
  }

  function signedIn() {
    var c = client();
    return !!(c && typeof c.getCurrentUserId === "function" && c.getCurrentUserId());
  }

  // ---------------------------------------------------------------- storage

  function getCache(key, ttl) {
    try {
      var raw = sessionStorage.getItem(key);
      if (!raw) return null;
      var parsed = JSON.parse(raw);
      if (!parsed || Date.now() - parsed.ts > ttl) return null;
      return parsed.items;
    } catch (e) {
      return null;
    }
  }

  function setCache(key, items) {
    try {
      sessionStorage.setItem(key, JSON.stringify({ ts: Date.now(), items: items }));
    } catch (e) {
      /* storage full or blocked: caching is an optimisation, not a requirement */
    }
  }

  // ---------------------------------------------------------------- settings

  function applySettings(s) {
    var root = document.documentElement;
    root.style.setProperty("--dh-accent", s.AccentColor || "#7c5cff");
    root.style.setProperty("--dh-card-w", (s.CardWidth || 130) + "px");

    // Badge text lives in a custom property rather than an attribute so the
    // pills stay pure CSS — no per-card JavaScript on a page full of cards.
    root.style.setProperty("--dh-label-movie", '"' + (s.LabelMovie || "PELÍCULA") + '"');
    root.style.setProperty("--dh-label-series", '"' + (s.LabelSeries || "SERIE") + '"');
    root.style.setProperty("--dh-label-episode", '"' + (s.LabelEpisode || "EPISODIO") + '"');

    var body = document.body;
    body.classList.toggle("dh-pin-sidebar", !!s.PinSidebar);
    body.classList.toggle("dh-reorder-sidebar", !!s.ReorderSidebar);
    body.classList.toggle("dh-logo-sidebar", !!s.LogoInSidebar);
    body.classList.toggle("dh-centered-search", !!s.CenteredSearch);
    body.classList.toggle("dh-hide-random", !!s.HideRandomButton);
    body.classList.toggle("dh-compact-cards", !!s.CompactCards);
    body.classList.toggle("dh-badges", !!s.ShowTypeBadges);

    // A media query can't read a custom property, so the configured breakpoint
    // is evaluated here and published as a class instead.
    if (!mediaQuery) {
      mediaQuery = window.matchMedia("(min-width: " + (s.SidebarBreakpoint || 1000) + "px)");
      var sync = function () { document.body.classList.toggle("dh-wide", mediaQuery.matches); };
      mediaQuery.addEventListener("change", sync);
      sync();
    }
  }

  function loadSettings() {
    var c = client();
    if (!c) return Promise.resolve(null);
    return c.getJSON(c.getUrl("DiscoverHome/Settings"))
      .then(function (s) { settings = s; applySettings(s); return s; })
      .catch(function () { return null; });
  }

  function injectStylesheet() {
    if (document.getElementById("dh-styles")) return;
    var c = client();
    if (!c) return;
    var link = document.createElement("link");
    link.id = "dh-styles";
    link.rel = "stylesheet";
    link.href = c.getUrl("DiscoverHome/Style");
    document.head.appendChild(link);
  }

  // ---------------------------------------------------------------- sidebar logo

  function setupSidebarLogo() {
    if (!settings || !settings.LogoInSidebar) return;

    // `.mainDrawer` itself is a flex *row* (drawer plus its scroll rail), so a
    // logo added there steals horizontal space from the nav labels and
    // `margin: auto` centres on the wrong axis. The scroll container inside it
    // is plain block flow, where both behave as expected.
    var scrollContainer = document.querySelector(".mainDrawer .mainDrawer-scrollContainer");
    if (!scrollContainer || scrollContainer.querySelector(".dh-sidebar-logo")) return;

    var original = document.querySelector(".headerLeft .pageTitle.pageTitleWithLogo");
    if (!original) return;

    var bg = getComputedStyle(original).backgroundImage;
    if (!bg || bg === "none") return; // not painted yet; a later pass will catch it

    var logo = document.createElement("div");
    logo.className = "dh-sidebar-logo";
    logo.style.backgroundImage = bg;
    scrollContainer.insertBefore(logo, scrollContainer.firstChild);
  }

  // ---------------------------------------------------------------- search

  var suggestDebounce = null;
  var suggestSeq = 0;

  // A local index of every title was tried first, but this server's generic
  // /Items endpoint returns at most 16 rows whatever Limit is asked for.
  // /Search/Hints is Jellyfin's own search endpoint, already indexed, and has
  // no such ceiling — so one small debounced call per query beats a big
  // upfront fetch anyway.
  function fetchSuggestions(term) {
    var c = client();
    if (!c || !term) return Promise.resolve([]);
    var mySeq = ++suggestSeq;
    return c.getSearchHints({ SearchTerm: term, IncludeItemTypes: "Movie,Series", Limit: 8 })
      .then(function (result) {
        if (mySeq !== suggestSeq) return null; // superseded by a newer keystroke
        return (result && result.SearchHints || []).map(function (h) {
          return { id: h.Id, name: h.Name, type: h.Type, year: h.ProductionYear };
        });
      })
      .catch(function () { return []; });
  }

  function renderSuggestions(box, matches) {
    box.innerHTML = "";
    if (!matches.length) { box.hidden = true; return; }

    matches.slice(0, 8).forEach(function (m) {
      var row = document.createElement("div");
      row.className = "dh-suggestion";
      row.textContent = m.name + (m.year ? " (" + m.year + ")" : "");

      var type = document.createElement("span");
      type.className = "dh-suggestion-type";
      type.textContent = m.type === "Series" ? "Serie" : "Película";
      row.appendChild(type);

      row.addEventListener("mousedown", function (e) {
        e.preventDefault(); // keep focus, so blur doesn't eat the click
        window.location.hash = "#/details?id=" + m.id;
        box.hidden = true;
      });

      box.appendChild(row);
    });

    box.hidden = false;
  }

  function setupSearchBar() {
    if (!settings || !settings.CenteredSearch) return;

    var headerTop = document.querySelector(".headerTop");
    if (!headerTop || headerTop.querySelector(".dh-topbar-search")) return;

    var wrap = document.createElement("div");
    wrap.className = "dh-topbar-search";

    var input = document.createElement("input");
    input.type = "search";
    input.placeholder = "Buscar películas y series";
    input.setAttribute("aria-label", "Buscar");

    var suggestBox = document.createElement("div");
    suggestBox.className = "dh-suggestions";
    suggestBox.hidden = true;

    function run() {
      var q = input.value.trim();
      if (!q) { suggestBox.hidden = true; return; }
      clearTimeout(suggestDebounce);
      suggestDebounce = setTimeout(function () {
        fetchSuggestions(q).then(function (matches) {
          if (matches) renderSuggestions(suggestBox, matches);
        });
      }, 200);
    }

    input.addEventListener("input", run);
    input.addEventListener("focus", function () { if (input.value.trim()) run(); });
    input.addEventListener("blur", function () { suggestBox.hidden = true; });
    input.addEventListener("keydown", function (e) {
      if (e.key !== "Enter") return;
      var q = input.value.trim();
      if (!q) return;
      suggestBox.hidden = true;
      window.location.hash = "#/search.html?query=" + encodeURIComponent(q);
    });

    wrap.appendChild(input);
    wrap.appendChild(suggestBox);
    headerTop.appendChild(wrap);
  }

  // ---------------------------------------------------------------- channels

  function channelTypes() {
    var types = [];

    if (settings.EnableGenreRow) {
      types.push({
        key: "genre",
        heading: "Géneros",
        cacheKey: "dh_genres_v2",
        fetch: function (c) {
          return c.getGenres(c.getCurrentUserId(), { SortBy: "SortName", Limit: 24 })
            .then(function (r) { return (r && r.Items || []).map(function (g) { return g.Name; }); });
        },
        decorate: settings.EnableGenreCollages ? decorateWithCollage : null
      });
    }

    if (settings.EnableStudioRow) {
      types.push({
        key: "studio",
        heading: "Estudios",
        cacheKey: "dh_studios_v2",
        fetch: function (c) {
          return c.getStudios(c.getCurrentUserId(), { SortBy: "SortName", Limit: 24 })
            .then(function (r) { return (r && r.Items || []).map(function (s) { return s.Name; }); });
        },
        decorate: settings.EnableStudioLogos ? decorateWithLogo : null
      });
    }

    return types;
  }

  function fetchChannelData(type) {
    var cached = getCache(type.cacheKey, CACHE_TTL_MS);
    if (cached) return Promise.resolve(cached);

    var c = client();
    if (!c) return Promise.resolve([]);

    return type.fetch(c)
      .then(function (items) { setCache(type.cacheKey, items); return items; })
      .catch(function () { return []; });
  }

  // Studio tiles: the logo comes from the plugin's own cache, filled once a day
  // by the server. A miss is normal — the upstream artwork repository doesn't
  // cover every studio — so the coloured, titled tile stays as the fallback and
  // the image is only swapped in once it has actually decoded.
  function decorateWithLogo(card, name) {
    var c = client();
    if (!c) return;

    var img = new Image();
    img.className = "dh-logo";
    img.alt = name;
    img.loading = "lazy";
    img.addEventListener("load", function () {
      card.classList.add("dh-has-logo");
      card.appendChild(img);
    });
    img.src = c.getUrl("DiscoverHome/Art/Studio", { name: name });
  }

  // Genre tiles: a collage of posters drawn from that genre. Composed in the
  // browser from images this server already serves — nothing to download, and
  // it always reflects what is actually in the library.
  function decorateWithCollage(card, name) {
    var c = client();
    if (!c) return;

    var cacheKey = "dh_collage_" + name;
    var cached = getCache(cacheKey, CACHE_TTL_MS);

    var render = function (ids) {
      if (!ids || !ids.length) return;
      var collage = document.createElement("div");
      collage.className = "dh-collage";
      ids.slice(0, 3).forEach(function (id) {
        var img = new Image();
        img.loading = "lazy";
        img.src = c.getImageUrl(id, { type: "Primary", maxWidth: 180 });
        collage.appendChild(img);
      });
      card.insertBefore(collage, card.firstChild);
    };

    if (cached) { render(cached); return; }

    c.getItems(c.getCurrentUserId(), {
      Genres: name,
      IncludeItemTypes: "Movie,Series",
      Recursive: true,
      SortBy: "Random",
      Limit: 3,
      ImageTypeLimit: 1,
      EnableImageTypes: "Primary"
    }).then(function (r) {
      var ids = (r && r.Items || []).map(function (i) { return i.Id; });
      setCache(cacheKey, ids);
      render(ids);
    }).catch(function () { /* a tile without a collage is still a usable tile */ });
  }

  function buildChannelRow(type, names) {
    if (!names.length) return null;

    var section = document.createElement("div");
    section.className = "verticalSection dh-channel-section";

    // Jellyfin's own `padded-left` supplies the gutter. It is a percentage of
    // the content column, not a fixed length, so reusing the class is the only
    // way to stay aligned with the native rows at every width.
    var heading = document.createElement("h2");
    heading.className = "sectionTitle sectionTitle-cards padded-left";
    heading.textContent = type.heading;
    section.appendChild(heading);

    var row = document.createElement("div");
    row.className = "dh-channel-row padded-left";

    var palette = CHANNEL_PALETTES[type.key] || CHANNEL_PALETTES.genre;

    names.forEach(function (name, i) {
      var card = document.createElement("div");
      card.className = "dh-channel-card";
      card.style.setProperty("--dh-tint", palette[i % palette.length]);

      var label = document.createElement("span");
      label.className = "dh-channel-label";
      label.textContent = name;
      card.appendChild(label);

      card.addEventListener("click", function () {
        window.location.hash = "#/search.html?query=" + encodeURIComponent(name);
      });

      if (type.decorate) type.decorate(card, name);
      row.appendChild(card);
    });

    section.appendChild(row);
    return section;
  }

  // ---------------------------------------------------------------- ordering

  var PINNED_PREFIXES = [
    "Mis contenidos", "Mis medios", "Seguir viendo", "A continuación",
    "My Media", "Continue Watching", "Next Up"
  ];

  function isPinned(section) {
    var title = section.querySelector(".sectionTitle, h2");
    if (!title) return false;
    var text = title.textContent.trim();
    return PINNED_PREFIXES.some(function (p) { return text.indexOf(p) === 0; });
  }

  function shuffle(arr) {
    for (var i = arr.length - 1; i > 0; i--) {
      var j = Math.floor(Math.random() * (i + 1));
      var tmp = arr[i]; arr[i] = arr[j]; arr[j] = tmp;
    }
    return arr;
  }

  // Disabling a feature can leave empty placeholder sections behind — a
  // `verticalSection` with no children and no height. Counting those as real
  // rows made the inserted carousels look bunched together, because the gaps
  // between them were filled with invisible elements. A row only counts if it
  // actually rendered cards.
  function hasRealContent(section) {
    return section.querySelector(".card") !== null;
  }

  function randomGap() {
    var min = Math.max(1, settings.MinRowGap || 1);
    var max = Math.max(min, settings.MaxRowGap || 2);
    return min + Math.floor(Math.random() * (max - min + 1));
  }

  function processHome(container) {
    var fresh = Array.prototype.slice.call(container.children).filter(function (s) {
      return !s.classList.contains("dh-channel-section") &&
             !s.dataset.dhDone &&
             hasRealContent(s);
    });
    if (!fresh.length) return;
    fresh.forEach(function (s) { s.dataset.dhDone = "1"; });

    var pinned = fresh.filter(isPinned);
    var rest = fresh.filter(function (s) { return !isPinned(s); });
    if (settings.ShuffleSections) shuffle(rest);

    pinned.forEach(function (s) { s.style.order = nextOrder++; });

    var types = channelTypes();
    var slots = [];
    var untilNext = randomGap();

    rest.forEach(function (s) {
      s.style.order = nextOrder++;
      if (!types.length) return;
      untilNext--;
      if (untilNext <= 0) {
        slots.push(nextOrder++);
        untilNext = randomGap();
      }
    });

    if (!slots.length) return;

    var dataByType = {};
    Promise.all(types.map(function (t) {
      return fetchChannelData(t).then(function (items) { dataByType[t.key] = items; });
    })).then(function () {
      var size = Math.max(1, settings.ChannelRowSize || 8);

      slots.forEach(function (slotOrder) {
        // Random type per slot, never the same type twice running — checked
        // across passes, so a later batch doesn't repeat the previous one.
        var candidates = types.filter(function (t) { return t.key !== lastChannelType; });
        if (!candidates.length) candidates = types;
        var type = candidates[Math.floor(Math.random() * candidates.length)];

        var items = dataByType[type.key] || [];
        if (typeCursor[type.key] === undefined) typeCursor[type.key] = 0;
        var window_ = typeCursor[type.key]++;
        var names = items.slice(window_ * size, window_ * size + size);
        if (!names.length) return; // out of fresh data for this type; skip rather than repeat

        var row = buildChannelRow(type, names);
        if (!row) return;
        row.style.order = slotOrder;
        container.appendChild(row);
        lastChannelType = type.key;
      });
    });
  }

  // ---------------------------------------------------------------- wiring

  var pending = false;

  function tick() {
    if (pending) return;
    pending = true;
    setTimeout(function () {
      pending = false;
      if (!signedIn()) return;

      injectStylesheet();

      if (!settings) {
        loadSettings().then(function (s) { if (s) tick(); });
        return;
      }

      setupSearchBar();
      setupSidebarLogo();

      var container = document.querySelector(".homeSectionsContainer");
      if (container) processHome(container);
    }, SETTLE_MS);
  }

  var observer = new MutationObserver(tick);
  observer.observe(document.body, { childList: true, subtree: true });
  tick();
})();
