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
    errTryAgain: "Failed, please try again",
    errDrmProtected: "Protected videos (DRM) cannot be downloaded."
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

  function isTransientWakeError(err) {
    const msg = String((err && err.message) || err || "");
    return /message port closed|receiving end does not exist/i.test(msg);
  }

  function sendMsg(msg, cb) {
    let settled = false;
    const finish = (val) => {
      if (settled) return;
      settled = true;
      if (cb) cb(val);
    };
    const attempt = (retried) => {
      try {
        if (!extensionAlive()) return finish(null);
        if (globalThis.browser && api.runtime && api.runtime.sendMessage.length <= 1) {
          Promise.resolve(api.runtime.sendMessage(msg)).then(finish, (err) => {
            if (!retried && isTransientWakeError(err)) {
              Promise.resolve().then(() => attempt(true));
            } else {
              finish(null);
            }
          });
          return;
        }
        const p = api.runtime.sendMessage(msg, (res) => {
          const err = api.runtime && api.runtime.lastError;
          if (err) {
            if (!retried && isTransientWakeError(err)) {
              Promise.resolve().then(() => attempt(true));
              return;
            }
            finish(null);
          } else {
            finish(res);
          }
        });
        if (p && typeof p.then === "function") {
          p.then(finish, (err) => {
            if (!retried && isTransientWakeError(err)) {
              Promise.resolve().then(() => attempt(true));
            } else {
              finish(null);
            }
          });
        }
      } catch (e) {
        finish(null);
      }
    };
    attempt(false);
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
  const MUTATION_DEBOUNCE_MS = 250;
  const HOST_ID = "gdm-dl-host";
  const hostname = location.hostname;
  const SHADOW_PLAYER_SELECTORS = [
    "vjs-shadow-player",
    "video-js",
    "mux-player",
    "media-controller",
    "media-theme",
    "shaka-player",
    "jw-player",
    "vm-player",
    "plyr-video",
    "ytd-player",
    ".video-js",
    ".jwplayer",
    ".plyr"
  ];
  const observedShadowHosts = new Set();

  let hiddenForSite = false;
  let captureDisabled = false;
  let siteExcluded = false;
  let watchersActive = false;
  let linkListenersActive = false;
  let localInterceptEnabled = true;
  let localGdmSettings = null;
  let host = null;
  let shadow = null;
  let btn = null;
  let label = null;
  let targetVideo = null;
  let checkTimer = null;
  let observer = null;
  let rafPending = false;
  let busy = false;
  let alive = true;

  function wildcardToRegex(pattern) {
    const esc = String(pattern || "")
      .trim()
      .replace(/[.+^${}()|[\]\\]/g, "\\$&")
      .replace(/\*+/g, ".*")
      .replace(/\?/g, ".");
    return new RegExp("^" + esc + "$", "i");
  }

  function hostMatches(pattern, hostStr) {
    let p = String(pattern || "").trim().toLowerCase();
    if (!p) return false;
    if (p.includes("://")) {
      try {
        p = new URL(p).hostname;
      } catch (e) {
        return false;
      }
    }
    const h = String(hostStr || "").toLowerCase();
    if (!h) return false;
    if (p.includes("*")) return wildcardToRegex(p).test(h);
    return h === p || h.endsWith("." + p);
  }

  function matchesExcludedSettings(settings) {
    if (!settings || typeof settings !== "object") return false;
    const h = (hostname || "").toLowerCase();
    if (h && Array.isArray(settings.exceptionSites) && settings.exceptionSites.some((p) => hostMatches(p, h))) {
      return true;
    }
    const href = String((location && location.href) || "");
    if (href && Array.isArray(settings.exceptionAddresses) && settings.exceptionAddresses.some((p) => wildcardToRegex(p).test(href))) {
      return true;
    }
    return false;
  }

  function isPageInactive() {
    return captureDisabled || siteExcluded;
  }

  // The extension may be reloaded/removed while the page stays open.
  function extensionAlive() {
    try {
      return alive && !!(api.runtime && api.runtime.id);
    } catch (e) {
      return false;
    }
  }

  function stopWatchers() {
    watchersActive = false;
    if (checkTimer) {
      clearInterval(checkTimer);
      checkTimer = null;
    }
    if (mutationTimer) {
      clearTimeout(mutationTimer);
      mutationTimer = null;
    }
    if (observer && typeof observer.disconnect === "function") {
      observer.disconnect();
      observer = null;
    }
    if (typeof document !== "undefined" && typeof document.removeEventListener === "function") {
      document.removeEventListener("encrypted", onEncrypted, true);
      document.removeEventListener("fullscreenchange", schedulePosition);
      document.removeEventListener("visibilitychange", onVisibilityChange);
      document.removeEventListener("yt-navigate-finish", onSpaNavigate);
    }
    if (typeof window !== "undefined" && typeof window.removeEventListener === "function") {
      window.removeEventListener("scroll", schedulePosition, true);
      window.removeEventListener("resize", schedulePosition);
      window.removeEventListener("popstate", onSpaNavigate);
    }
    targetVideo = null;
    if (host) host.remove();
    host = null;
    shadow = null;
    btn = null;
    label = null;
  }

  function detachLinkListeners() {
    if (!linkListenersActive) return;
    linkListenersActive = false;
    if (typeof document !== "undefined" && typeof document.removeEventListener === "function") {
      document.removeEventListener("mousedown", onDocMouseDown, true);
      document.removeEventListener("click", onDocClick, true);
    }
  }

  function attachLinkListeners() {
    if (linkListenersActive || typeof document === "undefined" || typeof document.addEventListener !== "function") return;
    linkListenersActive = true;
    document.addEventListener("mousedown", onDocMouseDown, true);
    document.addEventListener("click", onDocClick, true);
  }

  function shutdown() {
    alive = false;
    detachLinkListeners();
    stopWatchers();
  }

  function isYouTubeVideoPage() {
    if (!/(^|\.)youtube\.com$/i.test(hostname)) return false;
    return (
      location.pathname === "/watch" ||
      location.pathname.startsWith("/shorts/") ||
      location.pathname.startsWith("/live/") ||
      location.pathname.startsWith("/embed/")
    );
  }

  const encryptedVideos = new WeakSet();

  function isKnownDrmSite() {
    return (
      /(^|\.)(netflix|disneyplus|primevideo|hulu|peacocktv)\.com$/i.test(hostname) ||
      /^tv\.apple\.com$/i.test(hostname)
    );
  }

  function isVideoDrmProtected(v) {
    if (isKnownDrmSite()) return true;
    if (!v) return false;
    try {
      if (v.mediaKeys || encryptedVideos.has(v)) return true;
    } catch (e) {
      // ignore cross-realm getter error
    }
    return false;
  }

  function collectVideos(root, out, depth = 0, visited = new Set()) {
    if (!root || depth > 4 || visited.has(root) || typeof root.querySelectorAll !== "function") return;
    visited.add(root);
    for (const v of root.querySelectorAll("video")) out.push(v);
    if (depth >= 4) return;
    for (const sel of SHADOW_PLAYER_SELECTORS) {
      let hosts;
      try {
        hosts = root.querySelectorAll(sel);
      } catch (e) {
        hosts = [];
      }
      for (const el of hosts) {
        if (el && el.shadowRoot) collectVideos(el.shadowRoot, out, depth + 1, visited);
      }
    }
    if (depth === 0 && observedShadowHosts.size > 0) {
      for (const el of observedShadowHosts) {
        if (!el || el.isConnected === false || !el.shadowRoot) {
          observedShadowHosts.delete(el);
        } else {
          collectVideos(el.shadowRoot, out, 1, visited);
        }
      }
    }
  }

  // Biggest visible <video> larger than MIN_W x MIN_H (including open Shadow DOM players and blob:/MSE streams).
  function findVideo() {
    let best = null;
    let bestArea = 0;
    const candidates = [];
    collectVideos(document, candidates, 0);
    for (const v of candidates) {
      try {
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
      } catch (e) {
        // ignore detached or cross-realm video nodes
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
          max-width: calc(100vw - 16px);
          direction: ${dir}; pointer-events: auto;
          font-family: "Segoe UI", Tahoma, Arial, sans-serif;
          opacity: 0; visibility: hidden; transition: opacity .15s ease;
        }
        .wrap.show { opacity: 1; visibility: visible; }
        button { font: inherit; cursor: pointer; border: 0; margin: 0; }
        .main {
          display: inline-flex; align-items: center; gap: 6px;
          max-width: calc(100vw - 46px); overflow: hidden;
          background: #FDD103; color: #131313; font-size: 13px; font-weight: 700;
          line-height: 1; padding: 8px 14px; border-radius: 999px;
          box-shadow: 0 4px 14px rgba(0,0,0,.35), 0 0 0 1px rgba(19,19,19,.12) inset;
          white-space: nowrap;
        }
        .main:hover { background: #E5BC00; }
        .main:focus-visible { outline: 2px solid #131313; outline-offset: 2px; }
        .main:disabled { cursor: default; opacity: .85; }
        .main.ok { background: #16a34a; color: #fff; }
        .main.err { background: #dc2626; color: #fff; }
        .mark { flex: none; display: block; }
        .label { overflow: hidden; text-overflow: ellipsis; }
        .close {
          flex: none; width: 22px; height: 22px; border-radius: 50%;
          background: rgba(19,19,19,.88); color: #FDD103; font-size: 14px; line-height: 22px;
          text-align: center; padding: 0;
          box-shadow: 0 2px 8px rgba(0,0,0,.3);
        }
        .close:hover { background: #131313; }
        .close:focus-visible { outline: 2px solid #FDD103; outline-offset: 2px; }
      </style>
      <div class="wrap" part="wrap">
        <button class="main" type="button"><svg class="mark" width="14" height="14" viewBox="0 0 36 36" fill="none" aria-hidden="true"><rect width="36" height="36" rx="8" fill="#131313"/><path d="M9 11.5L13 11.5L18 16.5L15.2 19.3L9 13.1V11.5ZM27 11.5H23L18 16.5L20.8 19.3L27 13.1V11.5ZM18 18.5L25.5 26H21.2L18 22.8L14.8 26H10.5L18 18.5Z" fill="#FDD103"/></svg><span class="label"></span></button>
        <button class="close" type="button">×</button>
      </div>`;
    btn = shadow.querySelector(".main");
    label = shadow.querySelector(".label");
    const closeBtn = shadow.querySelector(".close");

    const hideTitle = i18nMsg("panelHideOnSite");
    btn.title = i18nMsg("extName");
    label.textContent = "⬇ " + i18nMsg("panelDownloadVideo");
    closeBtn.title = hideTitle;
    closeBtn.ariaLabel = hideTitle;
    if (typeof closeBtn.setAttribute === "function") closeBtn.setAttribute("aria-label", hideTitle);

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
    if (isPageInactive()) return hideButton();
    if (document.hidden === true) return;
    targetVideo = hiddenForSite ? null : findVideo();
    schedulePosition();
  }

  function nodeMayAffectVideo(node) {
    if (!node || typeof node !== "object") return false;
    if (node.id === HOST_ID) return false;
    const tag = typeof node.tagName === "string" ? node.tagName.toLowerCase() : "";
    if (tag === "video" || tag === "source" || tag === "iframe" || tag.includes("-")) {
      if (node.shadowRoot) observedShadowHosts.add(node);
      return true;
    }
    if (node.shadowRoot) {
      observedShadowHosts.add(node);
      return true;
    }
    if (typeof node.querySelector === "function") {
      try {
        if (node.querySelector("video")) return true;
      } catch (e) {
        // ignore
      }
    }
    return false;
  }

  // Debounced check for relevant DOM mutations.
  let mutationTimer = null;
  function onMutation(mutations) {
    if (isPageInactive()) return;
    if (Array.isArray(mutations) && mutations.length > 0) {
      let relevant = false;
      for (const m of mutations) {
        if (!m) continue;
        const added = m.addedNodes || [];
        const removed = m.removedNodes || [];
        if (added.length === 0 && removed.length === 0) {
          relevant = true;
          continue;
        }
        for (const n of added) {
          if (nodeMayAffectVideo(n)) relevant = true;
        }
        if (!relevant) {
          for (const n of removed) {
            if (nodeMayAffectVideo(n)) {
              relevant = true;
              break;
            }
          }
        }
      }
      if (!relevant) return;
    }
    if (mutationTimer) clearTimeout(mutationTimer);
    mutationTimer = setTimeout(() => {
      mutationTimer = null;
      check();
    }, MUTATION_DEBOUNCE_MS);
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
    let frameUrl = "";
    try {
      if (window !== window.top) frameUrl = location.href;
    } catch (err) {
      frameUrl = location.href;
    }
    const drmProtected = isVideoDrmProtected(targetVideo);
    const done = (res) => {
      if (res && res.ok) setLabel("✓ " + i18nMsg("panelSent"), "ok");
      else if (res && res.error === "drm") setLabel(i18nMsg("errDrmProtected"), "err");
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
      sendMsg({ type: "media-from-page", title: document.title, videoSrc, frameUrl, drmProtected }, (res) => {
        done(res);
      });
    } catch (err) {
      done(null);
      shutdown();
    }
  }

  let siteHidden = false;
  let frameHidden = false;
  let panelDisabled = false;

  function isSubFrame() {
    try {
      return Boolean(window.top && window !== window.top);
    } catch (e) {
      return true;
    }
  }

  function onHideClick(e) {
    if (!e || !e.isTrusted) return;
    e.preventDefault();
    e.stopPropagation();
    if (isSubFrame()) {
      frameHidden = true;
      hiddenForSite = true;
      hideButton();
      return;
    }
    siteHidden = true;
    hiddenForSite = true;
    hideButton();
    storageLocalGet("hiddenHosts", (res) => {
      const map = (res && res.hiddenHosts) || {};
      map[hostname] = true;
      storageLocalSet({ hiddenHosts: map });
    });
  }

  // React when the popup/options toggles capture, updates excluded sites, or restores the button.
  function syncActiveState() {
    captureDisabled =
      localInterceptEnabled === false ||
      Boolean(localGdmSettings && localGdmSettings.intercept === false);
    siteExcluded = matchesExcludedSettings(localGdmSettings);
    if (isPageInactive()) {
      detachLinkListeners();
      stopWatchers();
      return;
    }
    attachLinkListeners();
    loadPanelSettings(() => {
      if (isPageInactive()) return;
      if (!watchersActive) start();
      else check();
    });
  }

  try {
    if (api.storage && api.storage.onChanged) {
      api.storage.onChanged.addListener((changes, area) => {
        if (area !== "local" || !changes) return;
        let stateNeedsSync = false;
        if (changes.interceptEnabled) {
          localInterceptEnabled = changes.interceptEnabled.newValue !== false;
          stateNeedsSync = true;
        }
        if (changes.gdmState) {
          const nv = changes.gdmState.newValue;
          localGdmSettings = nv && nv.settings ? nv.settings : null;
          stateNeedsSync = true;
        }
        if (changes.hiddenHosts) {
          const map = changes.hiddenHosts.newValue || {};
          siteHidden = !!map[hostname];
          hiddenForSite = siteHidden || frameHidden || panelDisabled;
          if (!stateNeedsSync && watchersActive) check();
        }
        if (stateNeedsSync) syncActiveState();
      });
    }
  } catch (e) {
    // ignore
  }

  function onEncrypted(ev) {
    if (ev && ev.target) encryptedVideos.add(ev.target);
  }

  function onVisibilityChange() {
    if (!document.hidden && !isPageInactive()) check();
  }

  function onSpaNavigate() {
    if (!isPageInactive()) setTimeout(check, 300);
  }

  function start() {
    if (watchersActive || isPageInactive()) return;
    watchersActive = true;
    document.addEventListener("encrypted", onEncrypted, true);
    check();
    checkTimer = setInterval(check, CHECK_MS);
    if (document.documentElement) {
      observer = new MutationObserver(onMutation);
      observer.observe(document.documentElement, { childList: true, subtree: true });
    }
    window.addEventListener("scroll", schedulePosition, { passive: true, capture: true });
    window.addEventListener("resize", schedulePosition, { passive: true });
    document.addEventListener("fullscreenchange", schedulePosition);
    document.addEventListener("visibilitychange", onVisibilityChange);
    // YouTube is a single-page app.
    document.addEventListener("yt-navigate-finish", onSpaNavigate);
    window.addEventListener("popstate", onSpaNavigate);
  }

  // Special keys on links: Alt + click = let the browser download, Alt + Ctrl + click = force GDM.
  function onDocMouseDown(e) {
    if (!extensionAlive() || isPageInactive()) return;
    if (!e || !e.isTrusted) return;
    if (!e.altKey || e.button !== 0) return;
    const a = e.target && e.target.closest && e.target.closest("a[href]");
    if (!a || !/^(?:https?|ftps?):\/\//i.test(a.href)) return;
    sendMsg({ type: "link-keys", url: a.href, prevent: !e.ctrlKey, force: e.ctrlKey });
  }

  const inFlightMagnets = new Set();

  // Intercept magnet: clicks, record <a download> filename hints (including JS a.click()), and handle Alt+Ctrl force clicks.
  function onDocClick(e) {
    if (!extensionAlive() || isPageInactive()) return;
    if (!e || e.defaultPrevented) return;
    const target = e.target;
    if (!target || typeof target.closest !== "function") return;

    // 1) Magnet links (trusted primary click without Alt-only bypass).
    if (e.isTrusted && e.button === 0 && !(e.altKey && !e.ctrlKey)) {
      const mag = target.closest('a[href^="magnet:" i]') || target.closest('a[href^="magnet:"]');
      if (mag) {
        const href = ((typeof mag.getAttribute === "function" && mag.getAttribute("href")) || mag.href || "").trim();
        if (/^magnet:\?(?:[^#]*&)?xt(?:\.\d+)?=urn:(?:btih:(?:[0-9a-f]{40}|[a-z2-7]{32})|btmh:1220[0-9a-f]{64})(?:[&#]|$)/i.test(href)) {
          e.preventDefault();
          e.stopPropagation();
          if (inFlightMagnets.has(href)) return;
          inFlightMagnets.add(href);
          sendMsg({ type: "magnet-link", url: href }, (res) => {
            inFlightMagnets.delete(href);
            if (!res || !res.intercepted) {
              location.href = href;
            }
          });
          return;
        }
      }
    }

    const a = target.closest("a[href]");
    if (!a || !/^(?:https?|ftps?):\/\//i.test(a.href)) return;
    const dlAttr = typeof a.getAttribute === "function" ? a.getAttribute("download") : null;

    // 2) Alt + Ctrl + Click: hand inline or unlisted links directly to Golan instead of opening a browser tab.
    if (e.isTrusted && e.button === 0 && e.altKey && e.ctrlKey) {
      e.preventDefault();
      e.stopPropagation();
      sendMsg({ type: "force-link", url: a.href, filename: dlAttr || "" }, (res) => {
        if (!res || !res.intercepted) {
          location.href = a.href;
        }
      });
      return;
    }

    // 3) Links with the download attribute (trusted user clicks only).
    if (e.isTrusted && !(e.altKey && !e.ctrlKey) && dlAttr !== null) {
      sendMsg({ type: "download-attr", url: a.href, filename: dlAttr });
    }
  }

  // The desktop app can turn the video button off, or off for some sites.
  function loadPanelSettings(cb) {
    sendMsg({ type: "panel-settings" }, (res) => {
      if (res && (res.captureDisabled === true || res.siteExcluded === true)) {
        if (res.captureDisabled) captureDisabled = true;
        if (res.siteExcluded) siteExcluded = true;
        detachLinkListeners();
        stopWatchers();
        if (cb) cb();
        return;
      }
      panelDisabled = Boolean(res && res.enabled === false);
      hiddenForSite = siteHidden || frameHidden || panelDisabled;
      if (hiddenForSite) hideButton();
      if (cb) cb();
    });
  }

  storageLocalGet(["interceptEnabled", "gdmState", "hiddenHosts"], (res) => {
    localInterceptEnabled = !(res && res.interceptEnabled === false);
    localGdmSettings = res && res.gdmState && res.gdmState.settings ? res.gdmState.settings : null;
    siteHidden = !!(res && res.hiddenHosts && res.hiddenHosts[hostname]);
    hiddenForSite = siteHidden || frameHidden || panelDisabled;
    syncActiveState();
  });
})();
