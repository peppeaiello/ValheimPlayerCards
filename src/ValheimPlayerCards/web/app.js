// ValheimPlayerCards page. Reads api/players.json (relative, so it also works behind a reverse
// proxy under a sub-path) and shows a gallery; #/<id> opens one player's card.
// Texts come from lang.js; dates, durations and numbers are formatted by the browser.
(function () {
  "use strict";

  var LANGS = window.PLAYER_CARDS_LANGS || {};
  var STORAGE_KEY = "playerCardsLang";

  var params = new URLSearchParams(location.search);
  if (params.get("embed") === "1") document.body.classList.add("embed");

  var data = null;
  var lang = "en";
  var app = document.getElementById("app");

  // ---- Language ------------------------------------------------------------

  function pack(part, key) {
    var mine = LANGS[lang] && LANGS[lang][part];
    var en = LANGS.en && LANGS.en[part];
    return (mine && mine[key]) || (en && en[key]) || "";
  }

  function t(key) { return pack("text", key) || key; }

  function supported(code) {
    if (!code) return null;
    code = String(code).toLowerCase();
    if (LANGS[code]) return code;
    code = code.split(/[-_]/)[0];
    return LANGS[code] ? code : null;
  }

  function pickLanguage(server) {
    var stored = null;
    try { stored = localStorage.getItem(STORAGE_KEY); } catch (e) { /* storage blocked */ }
    var configured = server && server.language && server.language !== "auto" ? server.language : null;
    var candidates = [params.get("lang"), stored, configured].concat(navigator.languages || [navigator.language]);
    lang = "en";
    for (var i = 0; i < candidates.length; i++) {
      var found = supported(candidates[i]);
      if (found) { lang = found; break; }
    }
    document.documentElement.lang = lang;
  }

  function languageSelector() {
    var holder = document.getElementById("language");
    if (!holder || Object.keys(LANGS).length < 2) return;
    holder.textContent = "";
    var select = el("select");
    select.setAttribute("aria-label", t("language"));
    Object.keys(LANGS).forEach(function (code) {
      var option = el("option", null, LANGS[code].name || code);
      option.value = code;
      if (code === lang) option.selected = true;
      select.appendChild(option);
    });
    select.addEventListener("change", function () {
      lang = select.value;
      try { localStorage.setItem(STORAGE_KEY, lang); } catch (e) { /* storage blocked */ }
      document.documentElement.lang = lang;
      header();
      route();
    });
    holder.appendChild(select);
  }

  // ---- Formatting ----------------------------------------------------------

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }

  function number(n, digits) {
    return Number(n).toLocaleString(lang, { maximumFractionDigits: digits || 0 });
  }

  function unit(value, name, digits) {
    try {
      return new Intl.NumberFormat(lang, { style: "unit", unit: name, unitDisplay: "short", maximumFractionDigits: digits || 0 }).format(value);
    } catch (e) {
      return number(value, digits) + " " + name;
    }
  }

  function duration(seconds) {
    seconds = Math.max(0, Math.round(seconds));
    var h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60);
    if (h >= 1) return unit(h, "hour") + (m && h < 100 ? " " + unit(m, "minute") : "");
    return unit(m, "minute");
  }

  function since(unix) {
    if (!unix) return "–";
    var s = Math.max(0, data.server.now - unix);
    var value, name;
    if (s < 90) { value = 0; name = "second"; }
    else if (s < 3600) { value = Math.round(s / 60); name = "minute"; }
    else if (s < 86400 * 2) { value = Math.round(s / 3600); name = "hour"; }
    else { value = Math.round(s / 86400); name = "day"; }
    try {
      return new Intl.RelativeTimeFormat(lang, { numeric: "auto" }).format(-value, name);
    } catch (e) {
      return value + " " + name;
    }
  }

  function date(unix) {
    if (!unix) return "–";
    return new Date(unix * 1000).toLocaleDateString(lang, { day: "numeric", month: "short", year: "numeric" });
  }

  function dateTime(unix) {
    return new Date(unix * 1000).toLocaleString(lang, { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  }

  function statValue(id, value) {
    if (/^Distance/.test(id)) return value >= 1000 ? unit(value / 1000, "kilometer", 1) : unit(value, "meter");
    if (/^Time/.test(id)) return duration(value);
    return number(value);
  }

  function statLabel(id) {
    return pack("stats", id) || id.replace(/([a-z])([A-Z])/g, "$1 $2");
  }

  // ---- Pieces --------------------------------------------------------------

  function silhouette() {
    var ns = "http://www.w3.org/2000/svg";
    var svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", "0 0 60 100");
    svg.setAttribute("class", "placeholder");
    svg.setAttribute("aria-hidden", "true");
    var path = document.createElementNS(ns, "path");
    path.setAttribute("fill", "#d9c7ae");
    path.setAttribute("d", "M30 6a11 11 0 1 1 0 22a11 11 0 1 1 0-22zM14 34h32l6 30h-9l-2 32h-8l-1-24h-4l-1 24h-8l-2-32H8z");
    svg.appendChild(path);
    return svg;
  }

  function portraitImg(p, alt) {
    if (!p.portrait) return null;
    var img = el("img");
    img.src = p.portrait;
    img.alt = alt;
    img.loading = "lazy";
    if (p.portraitWidth && p.portraitHeight) { img.width = p.portraitWidth; img.height = p.portraitHeight; }
    return img;
  }

  function fact(value, label) {
    var f = el("div", "fact");
    f.appendChild(el("b", null, value));
    f.appendChild(el("span", null, label));
    return f;
  }

  function sorted(players) {
    var f = data.server.features;
    return players.slice().sort(function (a, b) {
      if (f.playTime && a.online !== b.online) return a.online ? -1 : 1;
      if (f.playTime && b.playSeconds !== a.playSeconds) return b.playSeconds - a.playSeconds;
      return a.name.localeCompare(b.name);
    });
  }

  // ---- Views ---------------------------------------------------------------

  function renderGallery() {
    app.textContent = "";
    var players = data.players;
    if (!players.length) {
      app.appendChild(el("p", "muted empty", t("noPlayers")));
      return;
    }
    var grid = el("div", "grid");
    sorted(players).forEach(function (p, index) {
      var a = el("a", "card");
      a.href = "#/" + encodeURIComponent(p.id);
      var pic = el("div", "pic");
      var img = portraitImg(p, p.name);
      if (img && index < 12) img.loading = "eager";   // the first rows are on screen anyway
      pic.appendChild(img || silhouette());
      a.appendChild(pic);
      var info = el("div", "info");
      var name = el("div", "name");
      if (data.server.features.playTime) name.appendChild(el("span", "dot" + (p.online ? " on" : "")));
      name.appendChild(el("span", null, p.name));
      info.appendChild(name);
      if (data.server.features.playTime) {
        info.appendChild(el("div", "sub", p.online ? t("onlineNow") : t("lastSeen") + ": " + since(p.lastSeen)));
        info.appendChild(el("div", "sub", t("played") + ": " + duration(p.playSeconds)));
      }
      a.appendChild(info);
      grid.appendChild(a);
    });
    app.appendChild(grid);
  }

  function renderPlayer(id) {
    app.textContent = "";
    var back = el("a", "back", "← " + t("back"));
    back.href = "#/";
    app.appendChild(back);

    var p = data.players.filter(function (x) { return x.id === id; })[0];
    if (!p) {
      app.appendChild(el("p", "muted empty", t("notFound")));
      return;
    }
    var f = data.server.features;
    var wrap = el("div", "detail");

    var portrait = el("div", "portrait");
    var img = portraitImg(p, p.name);
    if (img) { img.loading = "eager"; portrait.appendChild(img); }
    else { var box = el("div", "pic"); box.appendChild(silhouette()); portrait.appendChild(box); }
    wrap.appendChild(portrait);

    var side = el("div");
    var head = el("div", "panel head");
    var h2 = el("h2");
    if (f.playTime) h2.appendChild(el("span", "dot" + (p.online ? " on" : "")));
    h2.appendChild(el("span", null, p.name));
    head.appendChild(h2);
    if (f.playTime) {
      head.appendChild(el("p", "status", p.online ? t("onlineNow") + " · " + duration(data.server.now - p.onlineSince) : t("lastSeen") + ": " + since(p.lastSeen)));
      var facts = el("div", "facts");
      facts.appendChild(fact(duration(p.playSeconds), t("played")));
      facts.appendChild(fact(number(p.sessions), t("sessions")));
      facts.appendChild(fact(date(p.firstSeen), t("firstSeen")));
      if (p.cardUpdated) facts.appendChild(fact(since(p.cardUpdated), t("updated")));
      head.appendChild(facts);
    }
    side.appendChild(head);

    if (f.equipment && p.items.length) {
      var eq = el("div", "panel");
      eq.appendChild(el("h3", null, t("equipment")));
      var list = el("ul", "items");
      p.items.forEach(function (item) {
        var li = el("li");
        if (f.itemIcons) {
          var ic = el("div", "icon");
          if (item.icon) { var ii = el("img"); ii.src = item.icon; ii.alt = ""; ic.appendChild(ii); }
          li.appendChild(ic);
        }
        var txt = el("div");
        txt.appendChild(el("div", null, item.name || item.prefab));
        txt.appendChild(el("div", "slot", pack("slots", item.slot) || item.slot));
        li.appendChild(txt);
        if (item.quality > 1) li.appendChild(el("span", "stars", "★".repeat(Math.min(item.quality, 10))));
        list.appendChild(li);
      });
      eq.appendChild(list);
      side.appendChild(eq);
    }

    if (f.skills && p.skills.length) {
      var sk = el("div", "panel");
      sk.appendChild(el("h3", null, t("skills")));
      var grid = el("div", "skills");
      p.skills.filter(function (s) { return s.level >= 1; }).forEach(function (s) {
        var row = el("div", "skill");
        var top = el("div", "row");
        top.appendChild(el("span", null, pack("skills", s.id) || s.name || s.id));
        top.appendChild(el("b", null, Math.floor(s.level)));
        row.appendChild(top);
        var bar = el("div", "bar");
        var fill = el("i");
        fill.style.width = Math.min(100, s.level) + "%";
        bar.appendChild(fill);
        row.appendChild(bar);
        grid.appendChild(row);
      });
      sk.appendChild(grid);
      side.appendChild(sk);
    }

    if (f.stats && p.stats.length) {
      var st = el("div", "panel");
      st.appendChild(el("h3", null, t("stats")));
      var sg = el("div", "stats");
      p.stats.forEach(function (s) { sg.appendChild(fact(statValue(s.id, s.value), statLabel(s.id))); });
      st.appendChild(sg);
      st.appendChild(el("p", "muted", t("statsNote")));
      side.appendChild(st);
    }

    if (f.playTime && p.recent.length) {
      var rs = el("div", "panel");
      rs.appendChild(el("h3", null, t("recent")));
      var ul = el("ul", "sessions");
      p.recent.slice(-10).reverse().forEach(function (s) {
        var li = el("li");
        li.appendChild(el("span", null, dateTime(s.start)));
        li.appendChild(el("span", "muted", duration(s.end - s.start)));
        ul.appendChild(li);
      });
      rs.appendChild(ul);
      side.appendChild(rs);
    }

    wrap.appendChild(side);
    app.appendChild(wrap);
  }

  function route() {
    if (!data) return;
    var m = location.hash.match(/^#\/(.+)$/);
    if (m) renderPlayer(decodeURIComponent(m[1]));
    else renderGallery();
  }

  function header() {
    var s = data.server;
    var title = s.title || t("title");
    document.getElementById("title").textContent = title;
    document.title = title;
    var sub = number(data.players.length) + " " + t("players");
    if (s.features.playTime) sub = number(s.online) + " " + t("online") + " · " + sub;
    document.getElementById("subtitle").textContent = sub;
    document.getElementById("version").textContent = s.version;
  }

  function load(first) {
    fetch("api/players.json", { cache: "no-store" })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (json) {
        data = json;
        if (first) { pickLanguage(json.server); languageSelector(); }
        header();
        var y = window.scrollY;
        route();
        if (!first) window.scrollTo(0, y);
      })
      .catch(function () {
        if (first) {
          pickLanguage(null);
          app.textContent = "";
          app.appendChild(el("p", "muted empty", t("error")));
        }
      });
  }

  window.addEventListener("hashchange", function () { route(); window.scrollTo(0, 0); });
  load(true);
  setInterval(load, 30000);
})();
