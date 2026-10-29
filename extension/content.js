// Golan download manager - floating "download this video" button (IDM-style).
// Lives inside a Shadow DOM host so page styles can't touch it (and vice versa).
(() => {
  if (window.__gdmContentLoaded) return;
  window.__gdmContentLoaded = true;

  const api = globalThis.browser ?? chrome;

  const I18N_FALLBACK = {
    extName: "Golan Download Manager",
    panelDownloadVideo: "Download this video",
    panelHideOnSite: "Hide button on this site",
    panelSending: "Sending to Golan…",
    panelSent: "Sent to Golan",
    statusOffline: "Golan is not running",
    errTryAgain: "Failed, please try again"
  };

  function i18nMsg(key) {
    try {
      const i18nApi = (api && api.i18n) || (typeof chrome !== "undefined" && chrome && chrome.i18n);
      if (i18nApi && typeof i18nApi.getMessage === "function") {
        const val = i18nApi.getMessage(key);
        if (val) return val;
      }
    } catch (e) {
      // ignore
    }
    return I18N_FALLBACK[key] || key;
  }

  function isRtlLocale() {
    try {
      const i18nApi = (api && api.i18n) || (typeof chrome !== "undefined" && chrome && chrome.i18n);
      const lang =
        (i18nApi && typeof i18nApi.getUILanguage === "function" && i18nApi.getUILanguage()) ||
        navigator.language ||
        "en";
      return /^ar\b/i.test(lang);
    } catch (e) {
      return false;
    }
  }

  function sendMsg(msg, cb) {
    let settled = false;
    const finish = (val) => {
      if (settled) return;
      settled = true;
      if (cb) cb(val);
    };
    try {
      if (globalThis.browser && api.runtime && api.runtime.sendMessage.length <= 1) {
        Promise.resolve(api.runtime.sendMessage(msg)).then(finish, () => finish(null));
        return;
      }
      const p = api.runtime.sendMessage(msg, (res) => {
        if (api.runtime && api.runtime.lastError) finish(null);
        else finish(res);
      });
      if (p && typeof p.then === "function") {
        p.then(finish, () => finish(null));
      }
    } catch (e) {
      finish(null);
    }
  }

  function storageLocalGet(key, cb) {
    let settled = false;
    const finish = (val) => {
      if (settled) return;
      settled = true;
      if (cb) cb(val || {});
    };
    try {
      const area = api.storage && api.storage.local;
      if (!area) return finish({});
      if (globalThis.browser && area.get.length <= 1) {
        Promise.resolve(area.get(key)).then(finish, () => finish({}));
        return;
      }
      const p = area.get(key, (res) => {
        if (api.runtime && api.runtime.lastError) finish({});
        else finish(res || {});
      });
      if (p && typeof p.then === "function") {
        p.then(finish, () => finish({}));
      }
    } catch (e) {
      finish({});
    }
  }

  function storageLocalSet(items) {
    try {
      const area = api.storage && api.storage.local;
      if (!area) return;
      if (globalThis.browser && area.set.length <= 1) {
        Promise.resolve(area.set(items)).catch(() => {});
        return;
      }
      const p = area.set(items, () => void (api.runtime && api.runtime.lastError));
      if (p && typeof p.then === "function") p.catch(() => {});
    } catch (e) {
      // ignore
    }
  }

  const MIN_W = 200;
  const MIN_H = 120;
  const CHECK_MS = 1500;
  const HOST_ID = "gdm-dl-host";
  const hostname = location.hostname;

  let hiddenForSite = false;
  let host = null;
  let shadow = null;
  let btn = null;
  let label = null;
  let targetVideo = null;
  let checkTimer = null;
  let rafPending = false;
  let busy = false;
  let alive = true;

  // The extension may be reloaded/removed while the page stays open.
  function extensionAlive() {
    try {
      return alive && !!(api.runtime && api.runtime.id);
    } catch (e) {
      return false;
    }
  }

  function shutdown() {
    alive = false;
    if (checkTimer) clearInterval(checkTimer);
    if (host) host.remove();
    host = null;
  }

  function isYouTubeVideoPage() {
    if (!/(^|\.)youtube\.com$/i.test(hostname)) return false;
    return location.pathname === "/watch" || location.pathname.startsWith("/shorts/");
  }

  // Biggest visible <video> larger than MIN_W x MIN_H.
  function findVideo() {
    let best = null;
    let bestArea = 0;
    for (const v of document.querySelectorAll("video")) {
      const r = v.getBoundingClientRect();
      if (r.width < MIN_W || r.height < MIN_H) continue;
      if (r.bottom <= 0 || r.top >= window.innerHeight || r.right <= 0 || r.left >= window.innerWidth) continue;
      const cs = getComputedStyle(v);
      if (cs.visibility === "hidden" || cs.display === "none" || Number(cs.opacity) === 0) continue;
      const area = r.width * r.height;
      if (area > bestArea) {
        best = v;
        bestArea = area;
      }
    }
    return best;
  }

  function buildButton() {
    const dir = isRtlLocale() ? "rtl" : "ltr";
    host = document.createElement("div");
    host.id = HOST_ID;
    shadow = host.attachShadow({ mode: "closed" });
    shadow.innerHTML = `
      <style>
        :host { all: initial; }
        .wrap {
          position: fixed; top: 0; left: 0; z-index: 2147483647;
          display: flex; align-items: center; gap: 2px;
          direction: ${dir}; pointer-events: auto;
          font-family: "Segoe UI", Tahoma, Arial, sans-serif;
          opacity: 0; transition: opacity .15s ease;
        }
        .wrap.show { opacity: 1; }
        button { font: inherit; cursor: pointer; border: 0; margin: 0; }
        .main {
          display: inline-flex; align-items: center; gap: 6px;
          background: #FDD103; color: #131313; font-size: 13px; font-weight: 700;
          line-height: 1; padding: 8px 14px; border-radius: 999px;
          box-shadow: 0 4px 14px rgba(0,0,0,.35), 0 0 0 1px rgba(19,19,19,.12) inset;
          white-space: nowrap;
        }
        .main:hover { background: #E5BC00; }
        .main:disabled { cursor: default; opacity: .85; }
        .main.ok { background: #16a34a; color: #fff; }
        .main.err { background: #dc2626; color: #fff; }
        .mark { flex: none; display: block; }
        .close {
          width: 22px; height: 22px; border-radius: 50%;
          background: rgba(19,19,19,.88); color: #FDD103; font-size: 14px; line-height: 22px;
          text-align: center; padding: 0;
          box-shadow: 0 2px 8px rgba(0,0,0,.3);
        }
        .close:hover { background: #131313; }
      </style>
      <div class="wrap" part="wrap">
        <button class="main" type="button"><svg class="mark" width="14" height="14" viewBox="0 0 36 36" fill="none" aria-hidden="true"><rect width="36" height="36" rx="8" fill="#131313"/><path d="M9 11.5L13 11.5L18 16.5L15.2 19.3L9 13.1V11.5ZM27 11.5H23L18 16.5L20.8 19.3L27 13.1V11.5ZM18 18.5L25.5 26H21.2L18 22.8L14.8 26H10.5L18 18.5Z" fill="#FDD103"/></svg><span class="label"></span></button>
        <button class="close" type="button">×</button>
      </div>`;
    btn = shadow.querySelector(".main");
    label = shadow.querySelector(".label");
    const closeBtn = shadow.querySelector(".close");

    btn.title = i18nMsg("extName");
    label.textContent = "⬇ " + i18nMsg("panelDownloadVideo");
    closeBtn.title = i18nMsg("panelHideOnSite");

    btn.addEventListener("click", onDownloadClick);
    closeBtn.addEventListener("click", onHideClick);
    // Keep clicks from reaching the page (players often toggle play on click).
    for (const el of [btn, closeBtn]) {
      el.addEventListener("mousedown", (e) => e.stopPropagation());
      el.addEventListener("pointerdown", (e) => e.stopPropagation());
    }
    (document.documentElement || document.body).appendChild(host);
  }

  function wrapEl() {
    return shadow ? shadow.querySelector(".wrap") : null;
  }

  function setLabel(text, cls) {
    if (!label || !btn) return;
    label.textContent = text;
    btn.classList.remove("ok", "err");
    if (cls) btn.classList.add(cls);
  }

  function hideButton() {
    const w = wrapEl();
    if (w) {
      w.classList.remove("show");
      w.style.visibility = "hidden";
    }
  }

  // Place the button at the top-right corner of the video (or a fixed spot on YouTube).
  function position() {
    rafPending = false;
    if (!extensionAlive()) return shutdown();
    if (hiddenForSite || document.fullscreenElement) return hideButton();

    const video = targetVideo && targetVideo.isConnected ? targetVideo : null;
    const yt = isYouTubeVideoPage();
    if (!video && !yt) return hideButton();

    if (!host || !host.isConnected) buildButton();
    const w = wrapEl();
    const bw = w.offsetWidth || 160;
    let top;
    let left;
    if (video) {
      const r = video.getBoundingClientRect();
      const visibleTop = Math.max(r.top, 0);
      if (r.bottom - visibleTop < 50 || r.right <= 0 || r.left >= window.innerWidth) return hideButton();
      top = visibleTop + 10;
      left = Math.min(r.right, window.innerWidth) - bw - 10;
    } else {
      top = 72;
      left = window.innerWidth - bw - 24;
    }
    w.style.top = Math.round(Math.max(top, 4)) + "px";
    w.style.left = Math.round(Math.max(left, 4)) + "px";
    w.style.visibility = "visible";
    w.classList.add("show");
  }

  function schedulePosition() {
    if (rafPending) return;
    rafPending = true;
    requestAnimationFrame(position);
  }

  function check() {
    if (!extensionAlive()) return shutdown();
    targetVideo = hiddenForSite ? null : findVideo();
    schedulePosition();
  }

  // Throttled check for DOM mutations.
  let mutationTimer = null;
  function onMutation() {
    if (mutationTimer) return;
    mutationTimer = setTimeout(() => {
      mutationTimer = null;
      check();
    }, 400);
  }

  function onDownloadClick(e) {
    if (!e || !e.isTrusted) return;
    e.preventDefault();
    e.stopPropagation();
    if (busy || !extensionAlive()) return;
    busy = true;
    btn.disabled = true;
    setLabel(i18nMsg("panelSending"));
    let videoSrc = "";
    if (targetVideo) {
      const src = targetVideo.currentSrc || targetVideo.src || "";
      if (/^https?:\/\//i.test(src)) videoSrc = src;
    }
    const done = (res) => {
      if (res && res.ok) setLabel("✓ " + i18nMsg("panelSent"), "ok");
      else if (res && res.connected === false) setLabel(i18nMsg("statusOffline"), "err");
      else setLabel(i18nMsg("errTryAgain"), "err");
      setTimeout(() => {
        busy = false;
        if (btn) btn.disabled = false;
        setLabel("⬇ " + i18nMsg("panelDownloadVideo"));
        schedulePosition();
      }, 2500);
      schedulePosition();
    };
    try {
      sendMsg({ type: "media-from-page", title: document.title, videoSrc }, (res) => {
        done(res);
      });
    } catch (err) {
      done(null);
      shutdown();
    }
  }

  let siteHidden = false;
  let panelDisabled = false;

  function onHideClick(e) {
    if (!e || !e.isTrusted) return;
    e.preventDefault();
    e.stopPropagation();
    siteHidden = true;
    hiddenForSite = true;
    hideButton();
    storageLocalGet("hiddenHosts", (res) => {
      const map = (res && res.hiddenHosts) || {};
      map[hostname] = true;
      storageLocalSet({ hiddenHosts: map });
    });
  }

  function loadHidden(cb) {
    storageLocalGet("hiddenHosts", (res) => {
      siteHidden = !!(res && res.hiddenHosts && res.hiddenHosts[hostname]);
      hiddenForSite = siteHidden || panelDisabled;
      cb();
    });
  }

  // React when the popup re-enables the button for this site or GDM updates panel settings.
  try {
    if (api.storage && api.storage.onChanged) {
      api.storage.onChanged.addListener((changes, area) => {
        if (area !== "local") return;
        if (changes.hiddenHosts) {
          const map = changes.hiddenHosts.newValue || {};
          siteHidden = !!map[hostname];
          hiddenForSite = siteHidden || panelDisabled;
          check();
        }
        if (changes.gdmState) {
          loadPanelSettings(check);
        }
      });
    }
  } catch (e) {
    // ignore
  }

  function start() {
    check();
    checkTimer = setInterval(check, CHECK_MS);
    new MutationObserver(onMutation).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener("scroll", schedulePosition, { passive: true, capture: true });
    window.addEventListener("resize", schedulePosition, { passive: true });
    document.addEventListener("fullscreenchange", schedulePosition);
    // YouTube is a single-page app.
    document.addEventListener("yt-navigate-finish", () => setTimeout(check, 300));
    window.addEventListener("popstate", () => setTimeout(check, 300));
  }

  // Special keys on links: Alt + click = let the browser download, Alt + Ctrl + click = force GDM.
  document.addEventListener(
    "mousedown",
    (e) => {
      if (!e || !e.isTrusted) return;
      if (!e.altKey || e.button !== 0) return;
      const a = e.target && e.target.closest && e.target.closest("a[href]");
      if (!a || !/^https?:\/\//i.test(a.href)) return;
      sendMsg({ type: "link-keys", url: a.href, prevent: !e.ctrlKey, force: e.ctrlKey });
    },
    true
  );

  // Intercept clicks on magnet: links and route them to GDM.
  document.addEventListener(
    "click",
    (e) => {
      if (!e || !e.isTrusted || e.button !== 0 || e.defaultPrevented) return;
      if (e.altKey && !e.ctrlKey) return;
      const a = e.target && e.target.closest && (e.target.closest('a[href^="magnet:" i]') || e.target.closest('a[href^="magnet:"]'));
      if (!a) return;
      const href = (a.getAttribute("href") || a.href || "").trim();
      if (!/^magnet:\?(?:[^#]*&)?xt(?:\.\d+)?=urn:(?:btih:(?:[0-9a-f]{40}|[a-z2-7]{32})|btmh:1220[0-9a-f]{64})(?:[&#]|$)/i.test(href)) return;
      e.preventDefault();
      e.stopPropagation();
      sendMsg({ type: "magnet-link", url: href }, (res) => {
        if (!res || !res.intercepted) {
          location.href = href;
        }
      });
    },
    true
  );

  // The desktop app can turn the video button off, or off for some sites.
  function loadPanelSettings(cb) {
    sendMsg({ type: "panel-settings" }, (res) => {
      panelDisabled = Boolean(res && res.enabled === false);
      hiddenForSite = siteHidden || panelDisabled;
      if (hiddenForSite) hideButton();
      if (cb) cb();
    });
  }

  loadHidden(() => loadPanelSettings(start));
})();
