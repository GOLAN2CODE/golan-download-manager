// Golan download manager - popup UI.

const api = globalThis.browser ?? chrome;
const $ = (id) => document.getElementById(id);

const I18N_FALLBACK = {
  extName: "Golan Download Manager",
  appShortName: "Golan",
  appSubtitle: "Download Manager",
  statusChecking: "Checking…",
  statusConnected: "Connected",
  statusOffline: "Golan is not running",
  popupOfflineBanner: "Golan is not running — open Golan Download Manager and click Retry.",
  popupOfflineHelp: "Start Golan from the Start Menu or Desktop. If it is already open, enable Browser Integration in Options.",
  popupInterceptTitle: "Capture downloads from browser",
  popupInterceptDesc: "Send downloaded files to Golan instead of the browser",
  popupDownloadPageVideo: "Download video from this page",
  popupVideoHint: "Works with YouTube and over a thousand sites",
  popupDownloadAllLinks: "Download all links",
  popupPageVideosTitle: "Videos on this page",
  popupMediaEmpty: "No videos detected yet. Play the video and reopen this menu.",
  popupRestoreVideoBtn: "Restore video download button on this site",
  btnDownload: "Download",
  btnRetry: "Retry",
  errNotRunningShort: "Golan is not running — please start the application",
  errNoLinksFound: "No links found on this page.",
  errReadLinksFailed: "Failed to read links from this page.",
  errInvalidUrl: "This link cannot be downloaded with Golan.",
  errNothingToDownload: "Nothing to download on this page.",
  errDrmProtected: "This video is DRM-protected and cannot be downloaded.",
  errDeclinedByGdm: "Golan skipped this download — check activation or exception settings in Golan.",
  errGenericGdm: "An error occurred in Golan.",
  errGdmReply: "Golan returned an error: $1",
  errTryAgain: "Failed, please try again",
  panelSending: "Sending to Golan…",
  toastConnected: "Connected to Golan ✓",
  toastInterceptOn: "Download capture enabled",
  toastInterceptOff: "Download capture disabled",
  toastButtonRestored: "Button restored for this site",
  toastSentToGdm: "Sent to Golan ✓",
  toastVideoSent: "Video sent to Golan ✓",
  toastLinksCountSent: "Sent $1 links to Golan ✓",
  toastLinksSent: "Links sent to Golan ✓",
  typeStream: "STREAM",
  typeAudio: "AUDIO",
  typeVideo: "VIDEO",
  unitB: "B",
  unitKB: "KB",
  unitMB: "MB",
  unitGB: "GB"
};

function i18nMsg(key, substitutions) {
  try {
    const i18nApi = (api && api.i18n) || (typeof chrome !== "undefined" && chrome && chrome.i18n);
    if (i18nApi && typeof i18nApi.getMessage === "function") {
      const val =
        substitutions !== undefined ? i18nApi.getMessage(key, substitutions) : i18nApi.getMessage(key);
      if (val) return val;
    }
  } catch (e) {
    // ignore
  }
  let fallback = I18N_FALLBACK[key] || key;
  if (substitutions !== undefined) {
    const subs = Array.isArray(substitutions) ? substitutions : [substitutions];
    subs.forEach((s, idx) => {
      fallback = fallback.replace(new RegExp("\\$" + (idx + 1), "g"), String(s));
    });
  }
  return fallback;
}

function applyLocale() {
  let lang = "en";
  try {
    const i18nApi = (api && api.i18n) || (typeof chrome !== "undefined" && chrome && chrome.i18n);
    lang =
      (i18nApi && typeof i18nApi.getUILanguage === "function" && i18nApi.getUILanguage()) ||
      navigator.language ||
      "en";
  } catch (e) {
    lang = navigator.language || "en";
  }
  const isAr = /^ar\b/i.test(lang);
  document.documentElement.lang = isAr ? "ar" : "en";
  document.documentElement.dir = isAr ? "rtl" : "ltr";

  for (const el of document.querySelectorAll("[data-i18n]")) {
    const key = el.getAttribute("data-i18n");
    if (key) el.textContent = i18nMsg(key);
  }
}

let currentTab = null;
let toastTimer = null;

function callApi(fn, ...args) {
  if (typeof fn !== "function") return Promise.resolve(null);
  return new Promise((resolve) => {
    try {
      let settled = false;
      const done = (val) => {
        if (settled) return;
        settled = true;
        if (api.runtime && api.runtime.lastError) resolve(null);
        else resolve(val);
      };
      if (globalThis.browser && fn.length <= args.length) {
        Promise.resolve(fn(...args)).then(done, () => resolve(null));
        return;
      }
      const p = fn(...args, (res) => done(res));
      if (p && typeof p.then === "function") {
        p.then(done, () => resolve(null));
      }
    } catch (e) {
      resolve(null);
    }
  });
}

function isTransientWakeError(err) {
  const msg = String((err && err.message) || err || "");
  return /message port closed|receiving end does not exist/i.test(msg);
}

function send(message) {
  if (!api.runtime || typeof api.runtime.sendMessage !== "function") return Promise.resolve(null);
  const fn = api.runtime.sendMessage.bind(api.runtime);
  return new Promise((resolve) => {
    const attempt = (retried) => {
      try {
        let settled = false;
        const done = (val, err) => {
          if (settled) return;
          settled = true;
          if (err) {
            if (!retried && isTransientWakeError(err)) {
              Promise.resolve().then(() => attempt(true));
              return;
            }
            resolve(null);
          } else {
            resolve(val);
          }
        };
        if (globalThis.browser && fn.length <= 1) {
          Promise.resolve(fn(message)).then((v) => done(v, null), (e) => done(null, e));
          return;
        }
        const p = fn(message, (res) => done(res, api.runtime && api.runtime.lastError));
        if (p && typeof p.then === "function") {
          p.then((v) => done(v, null), (e) => done(null, e));
        }
      } catch (e) {
        resolve(null);
      }
    };
    attempt(false);
  });
}

function isHttpUrl(url) {
  return typeof url === "string" && /^https?:\/\//i.test(url);
}

function toast(text, kind) {
  const el = $("toast");
  // Emptied first, so a screen reader announces the same message again when it repeats.
  el.textContent = "";
  el.textContent = text;
  el.className = "toast show" + (kind ? " " + kind : "");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    el.className = "toast";
    el.textContent = ""; // a hidden toast must not stay readable
  }, 2600);
}

function sanitizeUiError(err) {
  const s = typeof err === "string" ? err.trim() : "";
  if (!s) return "";
  if (
    /^(?:internal-error|unknown-message|forbidden-sender)$/.test(s) ||
    /\b(?:System\.[A-Z]\w*|[A-Z]\w*Exception|StackTrace)\b/.test(s) ||
    /^\s*at\s+\S+\s*\(/m.test(s)
  ) {
    return "";
  }
  return s.replace(/\s+/g, " ").slice(0, 200);
}

function showResult(res, okText) {
  if (res && (res.drm || res.error === "drm")) toast(i18nMsg("errDrmProtected"), "err");
  else if (res && res.ok && res.accepted === false) toast(i18nMsg("errDeclinedByGdm"), "err");
  else if (res && res.ok) toast(okText, "ok");
  else if (res && res.connected === false) {
    setStatus(false);
    toast(i18nMsg("errNotRunningShort"), "err");
  } else if (res && res.error === "no-links") toast(i18nMsg("errNoLinksFound"), "err");
  else if (res && res.error === "script-failed") toast(i18nMsg("errReadLinksFailed"), "err");
  else if (res && res.error === "bad-url") toast(i18nMsg("errInvalidUrl"), "err");
  else if (res && res.error === "bad-page") toast(i18nMsg("errNothingToDownload"), "err");
  else {
    const cleanErr = res && res.connected ? sanitizeUiError(res.error) : "";
    toast(cleanErr ? i18nMsg("errGdmReply", [cleanErr]) : i18nMsg("errTryAgain"), "err");
  }
}

function setStatus(connected) {
  const status = $("status");
  status.dataset.state = connected ? "on" : "off";
  $("statusText").textContent = connected ? i18nMsg("statusConnected") : i18nMsg("statusOffline");
  $("banner").hidden = connected;
}

// ---- Media list helpers ----

function fileNameFromUrl(url) {
  try {
    const u = new URL(url);
    let name = u.pathname.split("/").filter(Boolean).pop() || "";
    if (!/\.[a-z0-9]{2,5}$/i.test(name)) {
      for (const [, val] of u.searchParams.entries()) {
        const clean = val.split(/[\\/]/).pop() || "";
        if (/\.(?:m3u8|mpd|mp4|m4v|mov|webm|mp3|m4a|ogg|opus|wav|flac|mkv|flv)$/i.test(clean)) {
          name = clean;
          break;
        }
      }
    }
    if (!name) name = u.hostname;
    try {
      name = decodeURIComponent(name);
    } catch (e) {
      // keep raw
    }
    return name;
  } catch (e) {
    return url;
  }
}

function typeLabel(item) {
  const mime = (item.mime || "").toLowerCase();
  let path = "";
  try {
    path = new URL(item.url).pathname.toLowerCase();
  } catch (e) {
    // ignore
  }
  if (mime.includes("mpegurl") || path.endsWith(".m3u8")) return i18nMsg("typeStream");
  if (mime.includes("dash") || path.endsWith(".mpd")) return i18nMsg("typeStream");
  const m = /\.([a-z0-9]{2,5})$/.exec(path);
  if (m) return m[1].toUpperCase();
  if (mime.startsWith("audio/")) return i18nMsg("typeAudio");
  if (mime.startsWith("video/")) return (mime.split("/")[1] || i18nMsg("typeVideo")).toUpperCase();
  return i18nMsg("typeVideo");
}

function formatSize(bytes) {
  if (!(typeof bytes === "number" && bytes > 0)) return "";
  const units = [
    i18nMsg("unitB"),
    i18nMsg("unitKB"),
    i18nMsg("unitMB"),
    i18nMsg("unitGB")
  ];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return (i === 0 ? v : v.toFixed(1)) + " " + units[i];
}

function renderMedia(items) {
  const list = $("mediaList");
  list.textContent = "";
  $("mediaCount").textContent = String(items.length);
  $("mediaEmpty").hidden = items.length > 0;

  // Newest first.
  for (const item of items.slice().reverse()) {
    const li = document.createElement("li");
    li.className = "media-item";

    const fileLabel = fileNameFromUrl(item.url);
    const info = document.createElement("div");
    info.className = "media-info";
    const name = document.createElement("span");
    name.className = "media-name";
    name.textContent = fileLabel;
    name.title = item.url;
    const meta = document.createElement("div");
    meta.className = "media-meta";
    const tag = document.createElement("span");
    tag.className = "tag";
    tag.textContent = typeLabel(item);
    meta.appendChild(tag);
    const size = formatSize(item.size);
    if (size) meta.appendChild(document.createTextNode(size));
    info.append(name, meta);

    const dlText = i18nMsg("btnDownload");
    const btn = document.createElement("button");
    btn.className = "item-btn";
    btn.type = "button";
    btn.textContent = dlText;
    btn.title = fileLabel;
    btn.ariaLabel = dlText + " " + fileLabel;
    btn.addEventListener("click", async () => {
      if (!currentTab) return;
      btn.disabled = true;
      btn.textContent = i18nMsg("panelSending");
      try {
        const res = await send({ type: "download-item", tabId: currentTab.id, item });
        showResult(res, i18nMsg("toastSentToGdm"));
      } finally {
        btn.textContent = dlText;
        btn.disabled = false;
      }
    });

    li.append(info, btn);
    list.appendChild(li);
  }
}

// ---- Init ----

async function getActiveTab() {
  if (!api.tabs || typeof api.tabs.query !== "function") return null;
  const tabs = await callApi(api.tabs.query.bind(api.tabs), { active: true, currentWindow: true });
  return (tabs && tabs[0]) || null;
}

async function storageGet(keys) {
  if (!api.storage || !api.storage.local) return {};
  const res = await callApi(api.storage.local.get.bind(api.storage.local), keys);
  return res || {};
}

async function storageSet(items) {
  if (!api.storage || !api.storage.local) return;
  await callApi(api.storage.local.set.bind(api.storage.local), items);
}

function hostOf(url) {
  try {
    return new URL(url).hostname;
  } catch (e) {
    return "";
  }
}

async function init() {
  applyLocale();
  currentTab = await getActiveTab();
  const pageOk = !!(currentTab && isHttpUrl(currentTab.url));
  $("mediaPageBtn").disabled = !pageOk;
  $("allLinksBtn").disabled = !pageOk;

  // Toggle state (default on).
  const { interceptEnabled, hiddenHosts, gdmState } = await storageGet(["interceptEnabled", "hiddenHosts", "gdmState"]);
  $("interceptToggle").checked = interceptEnabled !== false;
  $("interceptToggle").addEventListener("change", async (e) => {
    await storageSet({ interceptEnabled: e.target.checked });
    toast(e.target.checked ? i18nMsg("toastInterceptOn") : i18nMsg("toastInterceptOff"));
  });

  // Show last known status immediately, then ask background for a fresh ping.
  if (gdmState && typeof gdmState.connected === "boolean") setStatus(gdmState.connected);
  send({ type: "get-status" }).then((st) => setStatus(!!(st && st.connected)));

  $("retryBtn").addEventListener("click", async () => {
    const b = $("retryBtn");
    b.disabled = true;
    b.textContent = i18nMsg("statusChecking");
    $("status").dataset.state = "checking";
    $("statusText").textContent = i18nMsg("statusChecking");
    try {
      const st = await send({ type: "get-status" });
      const ok = !!(st && st.connected);
      setStatus(ok);
      toast(ok ? i18nMsg("toastConnected") : i18nMsg("errNotRunningShort"), ok ? "ok" : "err");
    } finally {
      b.textContent = i18nMsg("btnRetry");
      b.disabled = false;
    }
  });

  // "Show the button again on this site".
  const host = pageOk ? hostOf(currentTab.url) : "";
  if (host && hiddenHosts && hiddenHosts[host]) {
    const btn = $("unhideBtn");
    btn.hidden = false;
    btn.addEventListener("click", async () => {
      const { hiddenHosts: map } = await storageGet("hiddenHosts");
      const next = { ...(map || {}) };
      delete next[host];
      await storageSet({ hiddenHosts: next });
      btn.hidden = true;
      toast(i18nMsg("toastButtonRestored"), "ok");
    });
  }

  $("mediaPageBtn").addEventListener("click", async () => {
    if (!currentTab) return;
    const b = $("mediaPageBtn");
    const lbl = $("mediaPageBtnLabel");
    b.disabled = true;
    lbl.textContent = i18nMsg("panelSending");
    try {
      const res = await send({ type: "media-page", tabId: currentTab.id, title: currentTab.title });
      showResult(res, i18nMsg("toastVideoSent"));
    } finally {
      lbl.textContent = i18nMsg("popupDownloadPageVideo");
      b.disabled = false;
    }
  });

  $("allLinksBtn").addEventListener("click", async () => {
    if (!currentTab) return;
    const b = $("allLinksBtn");
    b.disabled = true;
    b.textContent = i18nMsg("panelSending");
    try {
      const res = await send({ type: "download-all", tabId: currentTab.id });
      showResult(
        res,
        res && res.count
          ? i18nMsg("toastLinksCountSent", [String(res.count)])
          : i18nMsg("toastLinksSent")
      );
    } finally {
      b.textContent = i18nMsg("popupDownloadAllLinks");
      b.disabled = false;
    }
  });

  if (currentTab) {
    const res = await send({ type: "get-media", tabId: currentTab.id });
    renderMedia((res && Array.isArray(res.items)) ? res.items : []);
  } else {
    renderMedia([]);
  }
}

init();
