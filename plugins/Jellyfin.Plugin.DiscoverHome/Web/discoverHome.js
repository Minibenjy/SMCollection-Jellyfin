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

  // Ordering state is per home container, not per pass: Jellyfin fills the home
  // screen progressively, so later batches must continue the same running order
  // and the same channel-type rotation instead of restarting from zero. But it
  // must not be global either — every visit to Home builds a new container, and
  // a cursor carried over from the last visit runs out of genres and studios
  // after a few round trips, leaving Home with no carousels at all.
  var homeState = new WeakMap();

  function stateFor(container) {
    var st = homeState.get(container);
    if (!st) {
      st = { nextOrder: 0, lastChannelType: null, typeCursor: {} };
      homeState.set(container, st);
    }
    return st;
  }

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
    // JSON.stringify quotes and escapes, so a label with a quote or backslash in
    // it stays a valid CSS string instead of breaking the `content` rule.
    root.style.setProperty("--dh-label-movie", JSON.stringify(s.LabelMovie || "PELÍCULA"));
    root.style.setProperty("--dh-label-series", JSON.stringify(s.LabelSeries || "SERIE"));
    root.style.setProperty("--dh-label-episode", JSON.stringify(s.LabelEpisode || "EPISODIO"));

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
      clearTimeout(suggestDebounce);
      if (!q) {
        // Also invalidate any request already in flight, or its answer lands
        // after the box was cleared and pops the suggestions back open.
        suggestSeq++;
        suggestBox.hidden = true;
        return;
      }
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
      window.location.hash = "#/search?query=" + encodeURIComponent(q);
    });

    wrap.appendChild(input);
    wrap.appendChild(suggestBox);
    headerTop.appendChild(wrap);
  }

  // ---------------------------------------------------------------- channels

  // Names of the studios the server holds a logo for. Asked once per page load,
  // so a tile only ever requests a logo that exists — without this, every studio
  // the upstream repository doesn't cover cost a request and a 404.
  var logoNames = null;

  function fetchLogoNames() {
    if (logoNames) return Promise.resolve(logoNames);
    var c = client();
    if (!c || !settings.EnableStudioLogos) return Promise.resolve({});
    return c.getJSON(c.getUrl("DiscoverHome/Art/Studios"))
      .then(function (names) {
        logoNames = {};
        (names || []).forEach(function (n) { logoNames[n.toLowerCase()] = true; });
        return logoNames;
      })
      .catch(function () { return {}; });
  }

  function hasLogo(name) {
    return !!(logoNames && logoNames[name.toLowerCase()]);
  }

  function toEntries(r) {
    return (r && r.Items || []).map(function (i) { return { id: i.Id, name: i.Name }; });
  }

  function channelTypes() {
    var types = [];

    // Both lists are shuffled once and cached for the session: an alphabetical
    // page of 8 would show the same "A…" genres and studios on every visit.
    if (settings.EnableGenreRow) {
      types.push({
        key: "genre",
        heading: "Géneros",
        cacheKey: "dh_genres_v3",
        link: "genreId",
        fetch: function (c) {
          return c.getGenres(c.getCurrentUserId(), { SortBy: "SortName", Recursive: true })
            .then(function (r) { return shuffle(toEntries(r)); });
        },
        decorate: settings.EnableGenreCollages ? decorateWithCollage : null
      });
    }

    if (settings.EnableStudioRow) {
      types.push({
        key: "studio",
        heading: "Estudios",
        cacheKey: "dh_studios_v3",
        link: "studioId",
        fetch: function (c) {
          return Promise.all([
            c.getStudios(c.getCurrentUserId(), { SortBy: "SortName", Recursive: true }),
            fetchLogoNames()
          ]).then(function (res) {
            // Studios with a real logo first: a row of plain coloured tiles is
            // the fallback, not the point.
            var all = shuffle(toEntries(res[0]));
            return all.filter(function (s) { return hasLogo(s.name); })
              .concat(all.filter(function (s) { return !hasLogo(s.name); }));
          });
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
  // by the server. Only studios on the server's list get an image at all, so
  // the tile can switch to its logo layout straight away — no hidden image
  // waiting on a `load` event. (An earlier version hid the image until it
  // loaded, but a `loading="lazy"` image with `display: none` has no layout
  // box, so the browser never fetches it and the logo never appears.)
  function decorateWithLogo(card, entry) {
    var c = client();
    if (!c || !hasLogo(entry.name)) return;

    var img = new Image();
    img.className = "dh-logo";
    img.alt = entry.name;
    img.loading = "lazy";
    img.decoding = "async";
    img.addEventListener("error", function () {
      // Cache pruned since the list was fetched: fall back to the titled tile.
      card.classList.remove("dh-has-logo");
      img.remove();
    });
    img.src = c.getUrl("DiscoverHome/Art/Studio", { name: entry.name });
    card.classList.add("dh-has-logo");
    card.appendChild(img);
  }

  // Genre tiles: a collage of posters drawn from that genre. Composed in the
  // browser from images this server already serves — nothing to download, and
  // it always reflects what is actually in the library.
  function decorateWithCollage(card, entry) {
    var c = client();
    if (!c) return;

    var cacheKey = "dh_collage_" + entry.id;
    var cached = getCache(cacheKey, CACHE_TTL_MS);

    var render = function (ids) {
      if (!ids || !ids.length) return;
      var collage = document.createElement("div");
      collage.className = "dh-collage";
      ids.slice(0, 3).forEach(function (id) {
        var img = new Image();
        img.loading = "lazy";
        img.decoding = "async";
        img.alt = "";
        img.src = c.getImageUrl(id, { type: "Primary", maxWidth: 180 });
        collage.appendChild(img);
      });
      card.insertBefore(collage, card.firstChild);
    };

    if (cached) { render(cached); return; }

    c.getItems(c.getCurrentUserId(), {
      GenreIds: entry.id,
      IncludeItemTypes: "Movie,Series",
      Recursive: true,
      SortBy: "Random",
      Limit: 3,
      ImageTypes: "Primary",
      EnableImageTypes: "Primary",
      ImageTypeLimit: 1
    }).then(function (r) {
      var ids = (r && r.Items || []).map(function (i) { return i.Id; });
      setCache(cacheKey, ids);
      render(ids);
    }).catch(function () { /* a tile without a collage is still a usable tile */ });
  }

  function buildChannelRow(type, entries) {
    if (!entries.length) return null;

    var c = client();
    var serverId = c && c.serverId ? c.serverId() : "";

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

    entries.forEach(function (entry, i) {
      // A real link rather than a click handler: middle-click, "open in new
      // tab" and keyboard focus all work, and it goes to the genre or studio
      // listing itself — the same place Jellyfin's own details page links to —
      // instead of a free-text search that also matches titles.
      var card = document.createElement("a");
      card.className = "dh-channel-card";
      card.href = "#/list?" + type.link + "=" + encodeURIComponent(entry.id) +
        (serverId ? "&serverId=" + encodeURIComponent(serverId) : "");
      card.style.setProperty("--dh-tint", palette[i % palette.length]);

      var label = document.createElement("span");
      label.className = "dh-channel-label";
      label.textContent = entry.name;
      card.appendChild(label);

      if (type.decorate) type.decorate(card, entry);
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

  function pinnedRank(section) {
    var title = section.querySelector(".sectionTitle, h2");
    if (!title) return -1;
    var text = title.textContent.trim();
    for (var i = 0; i < PINNED_PREFIXES.length; i++) {
      if (text.indexOf(PINNED_PREFIXES[i]) === 0) return i;
    }
    return -1;
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

    var st = stateFor(container);
    var rest = [];

    // Pinned rows get a fixed negative order from their place in the list, not
    // the next running number: "Seguir viendo" is often the slowest row to
    // arrive, and a running number would drop it below rows that beat it in.
    fresh.forEach(function (s) {
      var rank = pinnedRank(s);
      if (rank >= 0) s.style.order = rank - PINNED_PREFIXES.length;
      else rest.push(s);
    });
    if (settings.ShuffleSections) shuffle(rest);

    var types = channelTypes();
    var slots = [];
    if (st.untilNext === undefined) st.untilNext = randomGap();

    rest.forEach(function (s) {
      s.style.order = st.nextOrder++;
      if (!types.length) return;
      st.untilNext--;
      if (st.untilNext <= 0) {
        slots.push(st.nextOrder++);
        st.untilNext = randomGap();
      }
    });

    if (!slots.length) return;

    var dataByType = {};
    Promise.all(types.map(function (t) {
      return fetchChannelData(t).then(function (items) { dataByType[t.key] = items; });
    })).then(function () {
      // Logos are asked for on the genre-only path too, so a studio list
      // served from sessionStorage still knows which tiles have artwork.
      return fetchLogoNames();
    }).then(function () {
      var size = Math.max(1, settings.ChannelRowSize || 8);

      slots.forEach(function (slotOrder) {
        // Random type per slot, never the same type twice running — checked
        // across passes, so a later batch doesn't repeat the previous one. A
        // type that has run out of fresh entries drops out of the draw instead
        // of costing the slot.
        var candidates = types.filter(function (t) {
          var cursor = st.typeCursor[t.key] || 0;
          return (dataByType[t.key] || []).length > cursor * size;
        });
        var varied = candidates.filter(function (t) { return t.key !== st.lastChannelType; });
        if (varied.length) candidates = varied;
        if (!candidates.length) return;

        var type = candidates[Math.floor(Math.random() * candidates.length)];
        var page = st.typeCursor[type.key] || 0;
        st.typeCursor[type.key] = page + 1;
        var entries = dataByType[type.key].slice(page * size, page * size + size);

        var row = buildChannelRow(type, entries);
        if (!row) return;
        row.style.order = slotOrder;
        container.appendChild(row);
        st.lastChannelType = type.key;
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
