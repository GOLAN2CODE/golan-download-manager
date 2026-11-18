// Golan download manager - background script / service worker (Manifest V3).
// Talks to the desktop app through native messaging: one open port (fast, like IDM), with one-shot
// messages as the fallback.

const api = globalThis.browser ?? chrome;

const HOST = "com.golan.gdm";
const SETTINGS_TTL_MS = 60 * 1000;
const SKIP_TTL_MS = 30 * 1000;
const MAX_MEDIA_PER_TAB = 30;
const MAX_TRACKED_ENTRIES = 200;
const MAX_STORED_URL_LEN = 4096;
const MAX_FILENAME_LEN = 255;
const POST_FLUSH_MS = 1000;
const BADGE_COLOR = "#16a34a";

const FALLBACK_MESSAGES = {
  appShortName: "Golan",
  errNotRunning: "Golan is not running. Please start the application and try again.",
  errGenericGdm: "An error occurred in Golan.",
  errRequestFailed: "Golan could not process the request: $1",
  errInvalidUrl: "This link cannot be downloaded with Golan.",
  errNothingToDownload: "Nothing to download on this page.",
  errCannotReadLinks: "Cannot read links from this page.",
  errReadLinksFailed: "Failed to read links from this page.",
  errNoLinksFound: "No links found on this page.",
  menuDownloadLink: "Download with Golan",
  menuDownloadAll: "Download all links with Golan",
  menuDownloadVideo: "Download video from this page with Golan",
  menuDownloadImage: "Download image with Golan",
  errDrmProtected: "Protected videos (DRM) cannot be downloaded."
};

function i18nMsg(key, substitutions) {
  try {
    const i18n = (typeof chrome !== "undefined" && chrome.i18n) || (api && api.i18n);
    if (i18n && typeof i18n.getMessage === "function") {
      const m = i18n.getMessage(key, substitutions);
      if (m) return m;
    }
  } catch (e) {
    // fallback below
  }
  let out = FALLBACK_MESSAGES[key] || key;
  const list = Array.isArray(substitutions) ? substitutions : substitutions !== undefined ? [substitutions] : [];
  list.forEach((s, idx) => {
    out = out.replace(new RegExp("\\$" + (idx + 1), "g"), String(s));
  });
  return out;
}

// Used only when GDM never answered a ping yet; mirrors CatchExtensions in Golan desktop settings.
const DEFAULT_SETTINGS = {
  intercept: true,
  extensions: [
    "3gp", "7z", "aac", "ace", "aif", "apk", "arj", "asf", "avi", "bin", "bz2",
    "cab", "deb", "dmg", "doc", "docx", "epub", "exe", "flac", "flv", "gz", "gzip",
    "img", "iso", "lzh", "m4a", "m4v", "mkv", "mov", "mp3", "mp4", "mpa", "mpe",
    "mpeg", "mpg", "msi", "msix", "msu", "ogg", "ogv", "pdf", "pps", "ppt", "pptx",
    "qt", "ra", "rar", "rm", "rmvb", "rpm", "sea", "sit", "sitx", "tar", "tbz2",
    "tgz", "tif", "tiff", "torrent", "txz", "wav", "webm", "wma", "wmv", "xls",
    "xlsx", "xz", "z", "zip", "zst", "appx"
  ],
  minSizeKB: 0,
  exceptionSites: [],
  exceptionAddresses: [],
  mediaPanel: true,
  mediaPanelMinSizeKB: 0,
  mediaPanelExceptions: [],
  menuItems: ["link", "all", "video", "image"]
};

// Rough mime -> extension map for downloads whose url has no extension.
const MIME_EXT = {
  "application/zip": "zip",
  "application/x-zip-compressed": "zip",
  "application/x-rar-compressed": "rar",
  "application/vnd.rar": "rar",
  "application/x-rar": "rar",
  "application/x-7z-compressed": "7z",
  "application/gzip": "gz",
  "application/x-gzip": "gz",
  "application/x-tar": "tar",
  "application/x-tgz": "tgz",
  "application/x-compressed": "tgz",
  "application/x-bzip2": "bz2",
  "application/x-bzip": "bz2",
  "application/x-xz": "xz",
  "application/zstd": "zst",
  "application/vnd.ms-cab-compressed": "cab",
  "application/x-bittorrent": "torrent",
  "application/x-msdownload": "exe",
  "application/x-msdos-program": "exe",
  "application/x-executable": "exe",
  "application/vnd.microsoft.portable-executable": "exe",
  "application/x-msi": "msi",
  "application/x-ms-installer": "msi",
  "application/msix": "msix",
  "application/appx": "appx",
  "application/x-iso9660-image": "iso",
  "application/vnd.android.package-archive": "apk",
  "application/x-apple-diskimage": "dmg",
  "application/vnd.debian.binary-package": "deb",
  "application/x-deb": "deb",
  "application/x-rpm": "rpm",
  "application/x-redhat-package-manager": "rpm",
  "application/pdf": "pdf",
  "application/epub+zip": "epub",
  "application/msword": "doc",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document": "docx",
  "application/vnd.ms-excel": "xls",
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": "xlsx",
  "application/vnd.ms-powerpoint": "ppt",
  "application/vnd.openxmlformats-officedocument.presentationml.presentation": "pptx",
  "image/tiff": "tiff",
  "video/mp4": "mp4",
  "video/x-m4v": "m4v",
  "video/x-matroska": "mkv",
  "video/webm": "webm",
  "video/x-msvideo": "avi",
  "video/quicktime": "mov",
  "video/x-ms-wmv": "wmv",
  "video/x-ms-asf": "asf",
  "video/x-flv": "flv",
  "video/3gpp": "3gp",
  "video/mpeg": "mpg",
  "video/ogg": "ogv",
  "audio/mpeg": "mp3",
  "audio/mp4": "m4a",
  "audio/x-m4a": "m4a",
  "audio/aac": "aac",
  "audio/aacp": "aac",
  "audio/ogg": "ogg",
  "audio/x-ms-wma": "wma",
  "audio/wav": "wav",
  "audio/x-wav": "wav",
  "audio/flac": "flac"
};

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

function isHttpUrl(url) {
  return typeof url === "string" && /^https?:\/\//i.test(url);
}

function isFtpUrl(url) {
  return typeof url === "string" && /^ftps?:\/\/[^/\s]+/i.test(url);
}

function isInterceptableUrl(url) {
  return isHttpUrl(url) || isFtpUrl(url);
}

function isDownloadableLink(url) {
  return (
    isInterceptableUrl(url) ||
    (typeof url === "string" && /^data:[^,]*,/i.test(url.trim()))
  );
}

function isMagnetUrl(url) {
  return typeof url === "string" &&
    /^magnet:\?(?:[^#]*&)?xt(?:\.\d+)?=urn:(?:btih:(?:[0-9a-f]{40}|[a-z2-7]{32})|btmh:1220[0-9a-f]{64})(?:[&#]|$)/i.test(url.trim());
}

const MULTI_PART_SLDS = new Set(["co", "com", "org", "net", "gov", "edu", "ac", "mil"]);

function sameSiteOrHost(urlA, urlB) {
  try {
    const a = new URL(urlA).hostname.replace(/\.$/, "").toLowerCase();
    const b = new URL(urlB).hostname.replace(/\.$/, "").toLowerCase();
    if (!a || !b) return false;
    if (a === b) return true;
    const isIp = (h) => h.includes(":") || /^\d{1,3}(?:\.\d{1,3}){3}$/.test(h);
    if (isIp(a) || isIp(b)) return false;
    if (a.endsWith("." + b) || b.endsWith("." + a)) return true;
    const partsA = a.split(".");
    const partsB = b.split(".");
    if (partsA.length >= 2 && partsB.length >= 2) {
      const need =
        partsA[partsA.length - 1].length === 2 && MULTI_PART_SLDS.has(partsA[partsA.length - 2]) ? 3 : 2;
      if (partsA.length >= need && partsB.length >= need) {
        return partsA.slice(-need).join(".") === partsB.slice(-need).join(".");
      }
    }
    return false;
  } catch {
    return false;
  }
}

// Wraps a callback-style chrome API call or Promise-style browser API call, swallowing lastError (resolves undefined on error).
function call(fn, ...args) {
  if (typeof fn !== "function") return Promise.resolve(undefined);
  return new Promise((resolve) => {
    try {
      let settled = false;
      const done = (result) => {
        if (settled) return;
        settled = true;
        if (api.runtime && api.runtime.lastError) resolve(undefined);
        else resolve(result);
      };
      if (globalThis.browser && fn.length <= args.length) {
        Promise.resolve(fn(...args)).then(done, () => resolve(undefined));
        return;
      }
      const maybePromise = fn(...args, (result) => done(result));
      if (maybePromise && typeof maybePromise.then === "function") {
        maybePromise.then(done, () => resolve(undefined));
      }
    } catch (e) {
      resolve(undefined);
    }
  });
}

function getStorageArea(area) {
  if (!api.storage) return null;
  return api.storage[area] || api.storage.local || null;
}

async function storageGet(area, keys) {
  const store = getStorageArea(area);
  if (!store || typeof store.get !== "function") return {};
  const res = await call(store.get.bind(store), keys);
  return res && typeof res === "object" ? res : {};
}

async function storageSet(area, items) {
  const store = getStorageArea(area);
  if (!store || typeof store.set !== "function") return;
  await call(store.set.bind(store), items);
}

async function storageRemove(area, keys) {
  const store = getStorageArea(area);
  if (!store || typeof store.remove !== "function") return;
  await call(store.remove.bind(store), keys);
}

function notify(message) {
  try {
    if (!api.notifications || typeof api.notifications.create !== "function") return;
    call(
      api.notifications.create.bind(api.notifications),
      "gdm-" + Date.now(),
      {
        type: "basic",
        iconUrl: api.runtime.getURL("icons/icon128.png"),
        title: i18nMsg("appShortName"),
        message
      }
    );
  } catch (e) {
    // notifications unavailable - ignore
  }
}

// ---------------------------------------------------------------------------
// Native messaging
// ---------------------------------------------------------------------------

// One native port stays open, so a click reaches Golan in milliseconds instead of starting a new helper
// process every time. Replies carry the request number back; an older helper that answers in order
// without numbers still works (oldest request first).
let nativePort = null;
let portSeq = 0;
let portRefusedAt = 0;
const portWaiters = new Map(); // request number -> {resolve, timer}
const held = new Map(); // download id -> {state: "deciding"|"taken"|"released", paused, suggest, timer}
const PORT_RETRY_MS = 30 * 1000;
const SW_KEEPALIVE_MS = 20 * 1000;
let keepAliveTimer = null;
let heldSyncChain = Promise.resolve();

function currentHeldIds() {
  const ids = [];
  for (const [id, h] of held) {
    if (h && h.state === "deciding" && h.paused) ids.push(id);
  }
  return ids;
}

function syncHeldIds() {
  heldSyncChain = heldSyncChain.then(() => storageSet("session", { heldIds: currentHeldIds() }));
  return heldSyncChain;
}

function hasActiveHolds() {
  for (const h of held.values()) {
    if (h && h.state === "deciding") return true;
  }
  return false;
}

// Keep the MV3 service worker alive while waiting on Golan's dialog or holding a browser download.
function syncKeepAlive() {
  const needed = portWaiters.size > 0 || hasActiveHolds();
  if (needed && !keepAliveTimer) {
    keepAliveTimer = setInterval(() => {
      if (portWaiters.size === 0 && !hasActiveHolds()) {
        clearInterval(keepAliveTimer);
        keepAliveTimer = null;
        return;
      }
      storageGet("session", "_ka");
    }, SW_KEEPALIVE_MS);
  } else if (!needed && keepAliveTimer) {
    clearInterval(keepAliveTimer);
    keepAliveTimer = null;
  }
}

function openNativePort() {
  if (nativePort) return nativePort;
  if (!api.runtime || typeof api.runtime.connectNative !== "function") return null;
  if (Date.now() - portRefusedAt < PORT_RETRY_MS) return null;
  let port;
  try {
    port = api.runtime.connectNative(HOST);
  } catch (e) {
    portRefusedAt = Date.now();
    return null;
  }
  if (!port || !port.onMessage || !port.onDisconnect) return null;
  port.onMessage.addListener((msg) => {
    let id = msg && typeof msg === "object" ? msg._rid : undefined;
    if (id === undefined) id = portWaiters.keys().next().value;
    const w = portWaiters.get(id);
    if (!w) return;
    portWaiters.delete(id);
    clearTimeout(w.timer);
    syncKeepAlive();
    if (msg && typeof msg === "object") delete msg._rid;
    w.resolve(msg);
  });
  port.onDisconnect.addListener(() => {
    try {
      void (api.runtime && api.runtime.lastError); // read it so the browser does not log it as unchecked
    } catch (e) {
      // ignore
    }
    if (nativePort === port) nativePort = null;
    if (portWaiters.size > 0) portRefusedAt = Date.now();
    for (const w of portWaiters.values()) {
      clearTimeout(w.timer);
      w.resolve(undefined);
    }
    portWaiters.clear();
    syncKeepAlive();
  });
  nativePort = port;
  return port;
}

function sendThroughPort(message, timeoutMs) {
  const port = openNativePort();
  if (!port) return Promise.resolve({ posted: false, reply: undefined });
  return new Promise((resolve) => {
    const id = ++portSeq;
    let posted = false;
    let earlySettled = false;
    let earlyReply;
    const done = (reply) => {
      if (!posted) {
        earlySettled = true;
        earlyReply = reply;
        return;
      }
      resolve({ posted: true, reply });
    };
    const timer = setTimeout(() => {
      portWaiters.delete(id);
      syncKeepAlive();
      done(undefined);
    }, timeoutMs);
    portWaiters.set(id, { resolve: done, timer });
    syncKeepAlive();
    try {
      port.postMessage({ ...message, _rid: id });
      posted = true;
    } catch (e) {
      portWaiters.delete(id);
      clearTimeout(timer);
      syncKeepAlive();
      if (nativePort === port) nativePort = null;
      resolve({ posted: false, reply: undefined });
      return;
    }
    if (earlySettled) resolve({ posted: true, reply: earlyReply });
  });
}

// Resolves {connected:boolean, reply?:object}. Never rejects.
async function sendNative(message) {
  const isPing = Boolean(message && message.action === "ping");
  const timeoutMs = isPing ? 5000 : 10 * 60 * 1000;
  const fast = await sendThroughPort(message, timeoutMs);
  if (fast.reply !== undefined && fast.reply !== null) return { connected: true, reply: fast.reply };
  if (fast.posted) return { connected: false };
  if (!api.runtime || typeof api.runtime.sendNativeMessage !== "function") {
    return { connected: false };
  }
  const oneShotTimeoutMs = isPing ? 5000 : 60 * 1000;
  let oneShotTimer = null;
  const reply = await Promise.race([
    call(api.runtime.sendNativeMessage.bind(api.runtime), HOST, message),
    new Promise((resolve) => {
      oneShotTimer = setTimeout(() => resolve(undefined), oneShotTimeoutMs);
    })
  ]);
  clearTimeout(oneShotTimer);
  if (reply === undefined || reply === null) {
    return { connected: false };
  }
  portRefusedAt = 0;
  return { connected: true, reply };
}

function sanitizeErrorText(raw) {
  const s = typeof raw === "string" ? raw.trim() : "";
  if (!s) return "";
  if (/\b(?:System\.[A-Z]\w*|[A-Z]\w*Exception|StackTrace)\b/.test(s) || /^\s*at\s+\S+\s*\(/m.test(s)) {
    return "";
  }
  return s.replace(/\s+/g, " ").slice(0, 200);
}

// Sends a message and handles the "not connected" / error notification.
// Returns {ok:boolean, connected:boolean, error?:string}.
async function sendToGdm(message, { silent = false } = {}) {
  const res = await sendNative(message);
  if (!res.connected) {
    markDisconnected();
    const notRunningMsg = i18nMsg("errNotRunning");
    if (!silent) notify(notRunningMsg);
    return { ok: false, connected: false, error: notRunningMsg };
  }
  const reply = res.reply || {};
  if (reply.ok === true) return { ok: true, connected: true, accepted: reply.accepted !== false };
  const cleanErr = sanitizeErrorText(reply.error);
  const error = cleanErr || i18nMsg("errGenericGdm");
  if (!silent) notify(cleanErr ? i18nMsg("errRequestFailed", [cleanErr]) : error);
  return { ok: false, connected: true, error };
}

// ---------------------------------------------------------------------------
// Settings cache (ping)
// ---------------------------------------------------------------------------

let gdmState = null; // {connected, version, settings, ts}
let refreshPromise = null;

function normalizeSettings(s) {
  const out = { ...DEFAULT_SETTINGS };
  if (s && typeof s === "object") {
    if (typeof s.intercept === "boolean") out.intercept = s.intercept;
    if (Array.isArray(s.extensions)) {
      out.extensions = s.extensions
        .filter((x) => typeof x === "string")
        .map((x) => x.trim().toLowerCase().replace(/^\./, ""))
        .filter(Boolean);
    }
    if (typeof s.minSizeKB === "number" && isFinite(s.minSizeKB)) out.minSizeKB = s.minSizeKB;
    const strList = (v) => (Array.isArray(v) ? v.filter((x) => typeof x === "string" && x.trim()).map((x) => x.trim()) : []);
    out.exceptionSites = strList(s.exceptionSites);
    out.exceptionAddresses = strList(s.exceptionAddresses);
    if (typeof s.mediaPanel === "boolean") out.mediaPanel = s.mediaPanel;
    if (typeof s.mediaPanelMinSizeKB === "number" && isFinite(s.mediaPanelMinSizeKB)) out.mediaPanelMinSizeKB = s.mediaPanelMinSizeKB;
    out.mediaPanelExceptions = strList(s.mediaPanelExceptions);
    if (Array.isArray(s.menuItems)) out.menuItems = strList(s.menuItems);
  }
  return out;
}

function sameGdmState(a, b) {
  if (!a || !b) return false;
  if (Boolean(a.connected) !== Boolean(b.connected)) return false;
  if (String(a.version || "") !== String(b.version || "")) return false;
  if (Boolean(a.settings) !== Boolean(b.settings)) return false;
  return JSON.stringify(a.settings) === JSON.stringify(b.settings);
}

async function refreshSettings() {
  if (refreshPromise) return refreshPromise;
  refreshPromise = (async () => {
    const res = await sendNative({ action: "ping" });
    const prev = gdmState || (await storageGet("local", "gdmState")).gdmState || null;
    let state;
    if (res.connected && res.reply && res.reply.ok === true) {
      state = {
        connected: true,
        version: String(res.reply.version || ""),
        settings: normalizeSettings(res.reply.settings),
        ts: Date.now()
      };
    } else {
      // Keep last known settings, but mark as disconnected.
      state = {
        connected: false,
        version: prev ? prev.version : "",
        settings: prev && prev.settings ? prev.settings : null,
        ts: Date.now()
      };
    }
    const oldMenus = prev && prev.settings ? JSON.stringify(prev.settings.menuItems || []) : "";
    const changed = !sameGdmState(prev, state);
    gdmState = state;
    if (changed) await storageSet("local", { gdmState: state });
    if (state.settings && JSON.stringify(state.settings.menuItems || []) !== oldMenus) createMenus();
    return state;
  })();
  try {
    return await refreshPromise;
  } finally {
    refreshPromise = null;
  }
}

async function getGdmState() {
  if (!gdmState) gdmState = (await storageGet("local", "gdmState")).gdmState || null;
  if (!gdmState) return refreshSettings();
  if (Date.now() - (gdmState.ts || 0) > SETTINGS_TTL_MS) {
    refreshSettings(); // refresh in background, use cached copy now
  }
  return gdmState;
}

function markDisconnected() {
  const prev = gdmState;
  const next = prev
    ? { ...prev, connected: false, ts: Date.now() }
    : { connected: false, version: "", settings: null, ts: Date.now() };
  gdmState = next;
  if (!sameGdmState(prev, next)) storageSet("local", { gdmState: next });
}

let interceptEnabledCache = null; // null = not read yet

async function isInterceptEnabled() {
  const { interceptEnabled } = await storageGet("local", "interceptEnabled");
  interceptEnabledCache = interceptEnabled !== false; // default on
  return interceptEnabledCache;
}

// Keep the switch and the last known settings in memory, so a new download can be judged instantly.
if (api.storage && api.storage.onChanged && typeof api.storage.onChanged.addListener === "function") {
  api.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && changes && changes.interceptEnabled) {
      interceptEnabledCache = changes.interceptEnabled.newValue !== false;
    }
  });
}
isInterceptEnabled();
(async () => {
  if (!gdmState) gdmState = (await storageGet("local", "gdmState")).gdmState || null;
})();

// Periodic refresh while the worker is alive (plus lazy refresh in getGdmState).
setInterval(() => { refreshSettings(); }, SETTINGS_TTL_MS);

// ---------------------------------------------------------------------------
// Exceptions, POST downloads and special keys
// ---------------------------------------------------------------------------

function wildcardToRegex(pattern) {
  const esc = String(pattern || "")
    .trim()
    .replace(/[.+^${}()|[\]\\]/g, "\\$&")
    .replace(/\*+/g, ".*")
    .replace(/\?/g, ".");
  return new RegExp("^" + esc + "$", "i");
}

function hostMatches(pattern, host) {
  let p = pattern.trim().toLowerCase();
  if (!p) return false;
  if (p.includes("://")) {
    try { p = new URL(p).hostname; } catch (e) { return false; }
  }
  host = host.toLowerCase();
  if (p.includes("*")) return wildcardToRegex(p).test(host);
  return host === p || host.endsWith("." + p);
}

function isException(url, settings) {
  let host = "";
  try { host = new URL(url).hostname; } catch (e) { return false; }
  if ((settings.exceptionSites || []).some((p) => hostMatches(p, host))) return true;
  return (settings.exceptionAddresses || []).some((p) => wildcardToRegex(p).test(url));
}

function isMediaPanelException(url, settings) {
  if (!url || !settings) return false;
  let host = "";
  try { host = new URL(url).hostname; } catch (e) { return false; }
  return (settings.mediaPanelExceptions || []).some((p) => hostMatches(p, host) || wildcardToRegex(p).test(url));
}

// Requests sent with POST (forms): GDM would re-fetch them with GET and get a different answer, so they stay in the browser.
const postUrls = new Map(); // url -> expiry
const postRequestIds = new Map(); // requestId -> expiry
let lastPostFlushAt = 0;
let postFlushTimer = null;
let postFlushInFlight = false;
let postFlushDirty = false;

async function flushPostUrls() {
  if (postFlushInFlight) {
    postFlushDirty = true;
    return;
  }
  postFlushInFlight = true;
  try {
    postFlushDirty = false;
    const { postUrls: stored } = await storageGet("session", "postUrls");
    const now = Date.now();
    postFlushDirty = false;
    const merged = new Map();
    for (const [u, e] of Object.entries(stored || {})) {
      if (e > now) merged.set(u, e);
    }
    for (const [u, e] of postUrls) {
      if (e > now) {
        merged.delete(u);
        merged.set(u, e);
      } else {
        postUrls.delete(u);
      }
    }
    while (merged.size > MAX_TRACKED_ENTRIES) {
      merged.delete(merged.keys().next().value);
    }
    lastPostFlushAt = Date.now();
    await storageSet("session", { postUrls: Object.fromEntries(merged) });
  } finally {
    postFlushInFlight = false;
    if (postFlushDirty) schedulePostUrlsFlush();
  }
}

function schedulePostUrlsFlush() {
  if (postFlushInFlight) {
    postFlushDirty = true;
    return;
  }
  if (postFlushTimer !== null) return;
  const elapsed = Date.now() - lastPostFlushAt;
  if (elapsed >= POST_FLUSH_MS) {
    lastPostFlushAt = Date.now();
    return flushPostUrls();
  }
  postFlushTimer = setTimeout(() => {
    postFlushTimer = null;
    lastPostFlushAt = Date.now();
    flushPostUrls();
  }, POST_FLUSH_MS - elapsed);
}

async function recordPostUrl(url, exp = Date.now() + 120000) {
  if (!url || typeof url !== "string") return;
  const now = Date.now();
  for (const [u, e] of postUrls) if (e <= now) postUrls.delete(u);
  postUrls.delete(url);
  postUrls.set(url, exp);
  while (postUrls.size > MAX_TRACKED_ENTRIES) {
    postUrls.delete(postUrls.keys().next().value);
  }
  await schedulePostUrlsFlush();
}

if (api.webRequest && api.webRequest.onBeforeRequest) {
  api.webRequest.onBeforeRequest.addListener(
    (details) => {
      if (details.method === "POST") {
        const now = Date.now();
        const exp = now + 120000;
        if (details.requestId) {
          for (const [rid, e] of postRequestIds) if (e <= now) postRequestIds.delete(rid);
          postRequestIds.delete(details.requestId);
          postRequestIds.set(details.requestId, exp);
          while (postRequestIds.size > MAX_TRACKED_ENTRIES) postRequestIds.delete(postRequestIds.keys().next().value);
        }
        recordPostUrl(details.url, exp);
      }
    },
    { urls: ["http://*/*", "https://*/*"], types: ["main_frame", "sub_frame", "other", "xmlhttprequest"] }
  );
}

function isPost(...urls) {
  const now = Date.now();
  for (const [u, exp] of postUrls) if (exp <= now) postUrls.delete(u);
  return urls.some((u) => u && postUrls.has(u));
}

async function isPostAsync(...urls) {
  if (isPost(...urls)) return true;
  const now = Date.now();
  const { postUrls: stored } = await storageGet("session", "postUrls");
  if (!stored) return false;
  for (const u of urls) {
    if (u && stored[u] && stored[u] > now) {
      postUrls.delete(u);
      postUrls.set(u, stored[u]);
      while (postUrls.size > MAX_TRACKED_ENTRIES) postUrls.delete(postUrls.keys().next().value);
      return true;
    }
  }
  return false;
}

// Alt + click = let the browser download it; Alt + Ctrl + click = force GDM (reported by content.js).
const linkKeys = new Map(); // url -> {prevent, force, tabUrl, exp}

async function recordLinkKey(url, entry) {
  if (typeof url !== "string" || !url || url.length > MAX_STORED_URL_LEN) return;
  const now = Date.now();
  const cleanEntry = {
    ...entry,
    tabUrl: typeof entry.tabUrl === "string" ? entry.tabUrl.slice(0, MAX_STORED_URL_LEN) : ""
  };
  for (const [u, v] of linkKeys) {
    if (!v || !(v.exp > now) || u.length > MAX_STORED_URL_LEN) linkKeys.delete(u);
  }
  linkKeys.delete(url);
  linkKeys.set(url, cleanEntry);
  while (linkKeys.size > MAX_TRACKED_ENTRIES) {
    linkKeys.delete(linkKeys.keys().next().value);
  }
  const { linkKeys: stored } = await storageGet("session", "linkKeys");
  const merged = new Map();
  for (const [u, v] of Object.entries(stored || {})) {
    if (u.length <= MAX_STORED_URL_LEN && v && v.exp > now) merged.set(u, v);
  }
  for (const [u, v] of linkKeys) {
    if (v && v.exp > now) {
      merged.delete(u);
      merged.set(u, v);
    }
  }
  while (merged.size > MAX_TRACKED_ENTRIES) {
    merged.delete(merged.keys().next().value);
  }
  await storageSet("session", { linkKeys: Object.fromEntries(merged) });
}

function keysFor(...urls) {
  const now = Date.now();
  for (const u of urls) {
    const k = u && linkKeys.get(u);
    if (k && k.exp > now) return k;
  }
  return null;
}

async function keysForAsync(...urls) {
  const mem = keysFor(...urls);
  if (mem) return mem;
  const now = Date.now();
  const { linkKeys: stored } = await storageGet("session", "linkKeys");
  if (!stored) return null;
  for (const u of urls) {
    if (!u || u.length > MAX_STORED_URL_LEN) continue;
    const k = stored[u];
    if (k && k.exp > now) {
      linkKeys.delete(u);
      linkKeys.set(u, k);
      while (linkKeys.size > MAX_TRACKED_ENTRIES) linkKeys.delete(linkKeys.keys().next().value);
      return k;
    }
  }
  return null;
}

// Filename hints from <a href="..." download="name.ext"> (including JS a.click() and cross-origin download attributes).
const downloadAttrs = new Map(); // url -> {name, tabUrl, exp}

async function recordDownloadAttr(url, entry) {
  if (typeof url !== "string" || !url || url.length > MAX_STORED_URL_LEN) return;
  const now = Date.now();
  const cleanEntry = {
    ...entry,
    name: basename(entry.name || "").slice(0, MAX_FILENAME_LEN),
    tabUrl: typeof entry.tabUrl === "string" ? entry.tabUrl.slice(0, MAX_STORED_URL_LEN) : ""
  };
  for (const [u, v] of downloadAttrs) {
    if (!v || !(v.exp > now) || u.length > MAX_STORED_URL_LEN) downloadAttrs.delete(u);
  }
  downloadAttrs.delete(url);
  downloadAttrs.set(url, cleanEntry);
  while (downloadAttrs.size > MAX_TRACKED_ENTRIES) {
    downloadAttrs.delete(downloadAttrs.keys().next().value);
  }
  const { downloadAttrs: stored } = await storageGet("session", "downloadAttrs");
  const merged = new Map();
  for (const [u, v] of Object.entries(stored || {})) {
    if (u.length <= MAX_STORED_URL_LEN && v && v.exp > now) merged.set(u, v);
  }
  for (const [u, v] of downloadAttrs) {
    if (v && v.exp > now) {
      merged.delete(u);
      merged.set(u, v);
    }
  }
  while (merged.size > MAX_TRACKED_ENTRIES) {
    merged.delete(merged.keys().next().value);
  }
  await storageSet("session", { downloadAttrs: Object.fromEntries(merged) });
}

function downloadAttrFor(...urls) {
  const now = Date.now();
  for (const u of urls) {
    const a = u && downloadAttrs.get(u);
    if (a && a.exp > now) return a;
  }
  return null;
}

async function downloadAttrForAsync(...urls) {
  const mem = downloadAttrFor(...urls);
  if (mem) return mem;
  const now = Date.now();
  const { downloadAttrs: stored } = await storageGet("session", "downloadAttrs");
  if (!stored) return null;
  for (const u of urls) {
    if (!u || u.length > MAX_STORED_URL_LEN) continue;
    const a = stored[u];
    if (a && a.exp > now) {
      downloadAttrs.delete(u);
      downloadAttrs.set(u, a);
      while (downloadAttrs.size > MAX_TRACKED_ENTRIES) downloadAttrs.delete(downloadAttrs.keys().next().value);
      return a;
    }
  }
  return null;
}

// ---------------------------------------------------------------------------
// Cookies
// ---------------------------------------------------------------------------

function cleanHeaderToken(val) {
  return String(val || "").replace(/[\r\n\t]+/g, "");
}

async function getCookieObjects(url) {
  if (!isHttpUrl(url) || !api.cookies || typeof api.cookies.getAll !== "function") return [];
  const list = await call(api.cookies.getAll.bind(api.cookies), { url });
  return Array.isArray(list) ? list : [];
}

async function cookieHeader(url) {
  const list = await getCookieObjects(url);
  return list
    .map((c) => cleanHeaderToken(c.name) + "=" + cleanHeaderToken(c.value))
    .filter((pair) => pair !== "=")
    .join("; ");
}

async function cookiesForDownload(itemUrl, finalUrl) {
  const baseHdr = await cookieHeader(itemUrl);
  if (!finalUrl || finalUrl === itemUrl || !isHttpUrl(itemUrl) || !isHttpUrl(finalUrl)) return baseHdr;
  try {
    const u1 = new URL(itemUrl);
    const u2 = new URL(finalUrl);
    const sameHost = u1.hostname.toLowerCase() === u2.hostname.toLowerCase() && u1.port === u2.port;
    const downgrade = u1.protocol === "https:" && u2.protocol !== "https:";
    if (!sameHost || downgrade) return baseHdr;
  } catch (e) {
    return baseHdr;
  }
  const finalHdr = await cookieHeader(finalUrl);
  if (baseHdr && finalHdr && baseHdr !== finalHdr) {
    return [...new Set([...baseHdr.split("; "), ...finalHdr.split("; ")].filter(Boolean))].join("; ");
  }
  return finalHdr || baseHdr || "";
}

async function cookieObjectsForApp(url) {
  const list = await getCookieObjects(url);
  return list.map((c) => ({
    name: cleanHeaderToken(c.name),
    value: cleanHeaderToken(c.value),
    domain: cleanHeaderToken(c.domain),
    path: cleanHeaderToken(c.path),
    secure: !!c.secure,
    httpOnly: !!c.httpOnly,
    hostOnly: !!c.hostOnly,
    expirationDate: typeof c.expirationDate === "number" ? c.expirationDate : null,
    session: !!c.session
  }));
}

// ---------------------------------------------------------------------------
// Download interception
// ---------------------------------------------------------------------------

// Short-lived set of urls we must not intercept (loop protection).
const skipUrls = new Map(); // url -> expiry
let sessionHydrated = false;

heldSyncChain = (async () => {
  const now = Date.now();
  const s = await storageGet("session", ["skipUrls", "postUrls", "linkKeys", "downloadAttrs", "heldIds"]);
  for (const [u, exp] of Object.entries((s && s.skipUrls) || {})) {
    if (u.length <= MAX_STORED_URL_LEN && exp > now && !skipUrls.has(u) && skipUrls.size < MAX_TRACKED_ENTRIES) skipUrls.set(u, exp);
  }
  for (const [u, exp] of Object.entries((s && s.postUrls) || {})) {
    if (exp > now && !postUrls.has(u) && postUrls.size < MAX_TRACKED_ENTRIES) postUrls.set(u, exp);
  }
  for (const [u, v] of Object.entries((s && s.linkKeys) || {})) {
    if (u.length <= MAX_STORED_URL_LEN && v && v.exp > now && !linkKeys.has(u) && linkKeys.size < MAX_TRACKED_ENTRIES) linkKeys.set(u, v);
  }
  for (const [u, v] of Object.entries((s && s.downloadAttrs) || {})) {
    if (u.length <= MAX_STORED_URL_LEN && v && v.exp > now && !downloadAttrs.has(u) && downloadAttrs.size < MAX_TRACKED_ENTRIES) {
      downloadAttrs.set(u, v);
    }
  }
  sessionHydrated = true;
  const leftoverHeld = Array.isArray(s && s.heldIds) ? s.heldIds : [];
  if (leftoverHeld.length > 0) {
    for (const id of leftoverHeld) {
      if (typeof id === "number" && !held.has(id)) {
        handledIds.add(id);
        if (api.downloads && typeof api.downloads.resume === "function") {
          await call(api.downloads.resume.bind(api.downloads), id);
        }
      }
    }
    await storageSet("session", { heldIds: currentHeldIds() });
  }
})();

async function addSkip(url) {
  if (typeof url !== "string" || !url || url.length > MAX_STORED_URL_LEN) return;
  const now = Date.now();
  for (const [u, exp] of skipUrls) {
    if (!(exp > now) || u.length > MAX_STORED_URL_LEN) skipUrls.delete(u);
  }
  skipUrls.delete(url);
  skipUrls.set(url, now + SKIP_TTL_MS);
  while (skipUrls.size > MAX_TRACKED_ENTRIES) {
    skipUrls.delete(skipUrls.keys().next().value);
  }
  const { skipUrls: stored } = await storageGet("session", "skipUrls");
  const merged = new Map();
  for (const [u, exp] of Object.entries(stored || {})) {
    if (u.length <= MAX_STORED_URL_LEN && exp > now) merged.set(u, exp);
  }
  for (const [u, exp] of skipUrls) {
    if (exp > now) {
      merged.delete(u);
      merged.set(u, exp);
    }
  }
  while (merged.size > MAX_TRACKED_ENTRIES) {
    merged.delete(merged.keys().next().value);
  }
  await storageSet("session", { skipUrls: Object.fromEntries(merged) });
}

async function isSkipped(url) {
  const now = Date.now();
  const exp = skipUrls.get(url);
  if (exp && exp > now) return true;
  const { skipUrls: stored } = await storageGet("session", "skipUrls");
  return !!(stored && stored[url] && stored[url] > now);
}

function extFromPath(str) {
  if (!str) return "";
  let s = String(str);
  if (isInterceptableUrl(s)) {
    try {
      s = new URL(s).pathname;
    } catch (e) {
      return "";
    }
  }
  try {
    s = decodeURIComponent(s);
  } catch (e) {
    // keep raw
  }
  const base = s.split(/[\\/]/).pop() || "";
  const splitArch = /\.([a-z0-9]{1,8})\.\d{3}$/i.exec(base);
  if (splitArch) return splitArch[1].toLowerCase();
  const m = /\.([a-z0-9]{1,8})$/i.exec(base);
  return m ? m[1].toLowerCase() : "";
}

function basename(path) {
  if (!path) return "";
  return String(path).replace(/[\r\n\t\0]+/g, "").split(/[\\/]/).pop().slice(0, 255) || "";
}

function filenameFromDisposition(disp) {
  if (!disp) return "";
  const star = /filename\*\s*=\s*(?:UTF-8''|utf-8'')?([^;\r\n]+)/i.exec(disp);
  if (star) {
    try {
      return basename(decodeURIComponent(star[1].trim().replace(/^["']|["']$/g, "")));
    } catch (e) {
      // fall through
    }
  }
  const plain = /filename\s*=\s*(?:"([^"\r\n]+)"|'([^'\r\n]+)'|([^;\s\r\n]+))/i.exec(disp);
  if (plain) {
    const raw = plain[1] || plain[2] || plain[3] || "";
    try {
      return basename(decodeURIComponent(raw));
    } catch (e) {
      return basename(raw);
    }
  }
  return "";
}

function filenameFromUrlQuery(url) {
  if (!isInterceptableUrl(url)) return "";
  try {
    const u = new URL(url);
    for (const key of ["response-content-disposition", "rscd"]) {
      const disp = u.searchParams.get(key);
      const fn = filenameFromDisposition(disp);
      if (fn) return fn;
    }
    for (const key of ["filename", "file_name"]) {
      const val = basename(u.searchParams.get(key) || "");
      if (val && extFromPath(val)) return val;
    }
  } catch (e) {
    // ignore
  }
  return "";
}

function candidateExts(item, attrHint = null) {
  const exts = new Set();
  const e1 = extFromPath(item.finalUrl || "");
  const e2 = extFromPath(item.url || "");
  const e3 = extFromPath(item.filename || "");
  const e4 = extFromPath(filenameFromUrlQuery(item.finalUrl || ""));
  const e5 = extFromPath(filenameFromUrlQuery(item.url || ""));
  const e6 = attrHint && attrHint.name ? extFromPath(attrHint.name) : "";
  const mime = (item.mime || "").split(";")[0].trim().toLowerCase();
  const e7 = MIME_EXT[mime] || "";
  for (const e of [e1, e2, e3, e4, e5, e6, e7]) if (e) exts.add(e);
  return exts;
}

function sizeOf(item) {
  if (typeof item.totalBytes === "number" && item.totalBytes > 0) return item.totalBytes;
  if (typeof item.fileSize === "number" && item.fileSize > 0) return item.fileSize;
  return -1;
}

const handledIds = new Set(); // download ids already claimed for GDM
const recentClaims = new Map(); // url -> {ts, promise}
const DUPLICATE_WINDOW_MS = 1500;

// IDM-style hold: the browser's own download is paused the moment it is created and kept in Chrome's
// "choosing a file name" step, so it never shows up in the browser while Golan decides. It is cancelled
// when Golan takes it, or released (name accepted + resumed) when the browser should keep it.
const HOLD_LIMIT_MS = 45 * 1000;

function safeSuggest(suggest) {
  try {
    if (typeof suggest === "function") suggest();
  } catch (e) {
    // already suggested - ignore
  }
}

function forgetHoldLater(id, h) {
  setTimeout(() => {
    if (held.get(id) === h) held.delete(id);
  }, 60 * 1000);
}

function holdDownload(id, { pause = false, suggest = null } = {}) {
  let h = held.get(id);
  if (!h || h.state === "released") {
    h = { state: "deciding", paused: false, suggest: null, timer: null, filename: "", finalUrl: "", mime: "", size: -1 };
    held.set(id, h);
    // Never keep a download hostage: without an answer in time, the browser gets it back.
    h.timer = setTimeout(() => releaseDownload(id), HOLD_LIMIT_MS);
  }
  if (suggest) h.suggest = suggest;
  if (pause && !h.paused && h.state === "deciding") {
    h.paused = true;
    syncHeldIds();
    call(api.downloads.pause.bind(api.downloads), id);
  }
  syncKeepAlive();
  return h;
}

// The browser keeps this download: give it its name back and let it continue.
function releaseDownload(id) {
  const h = held.get(id);
  if (!h || h.state !== "deciding") return Promise.resolve(false);
  h.state = "released";
  clearTimeout(h.timer);
  syncKeepAlive();
  syncHeldIds();
  if (h.suggest) {
    safeSuggest(h.suggest);
    h.suggest = null;
  }
  forgetHoldLater(id, h);
  if (!h.paused) return Promise.resolve(true);
  return call(api.downloads.resume.bind(api.downloads), id).then(() => true);
}

function isSkippedNow(url) {
  const exp = url && skipUrls.get(url);
  return !!(exp && exp > Date.now());
}

function forceAllowed(item, keys) {
  return Boolean(
    keys && keys.force &&
    (!keys.tabUrl || sameSiteOrHost(keys.tabUrl, item.url)) &&
    (!item.referrer || sameSiteOrHost(item.referrer, item.url))
  );
}

function wantedBySettings(item, settings, allowForce, attrHint = null) {
  let match = allowForce;
  const wanted = new Set(settings.extensions || []);
  for (const e of candidateExts(item, attrHint)) if (wanted.has(e)) match = true;
  const size = sizeOf(item);
  if (!match && settings.minSizeKB > 0 && size > 0 && size >= settings.minSizeKB * 1024) match = true;
  return match;
}

// First look without storage or messages, so the browser download can be stopped at once.
// "yes" = hold and pause it, "maybe" = settings not known yet, "no" = leave it to the browser.
function quickVerdict(item) {
  if (!item || typeof item.id !== "number") return "no";
  if (item.state && item.state !== "in_progress") return "no";
  if (item.byExtensionId && api.runtime && item.byExtensionId === api.runtime.id) return "no";
  const url = item.finalUrl || item.url;
  if (!isInterceptableUrl(url) || !isInterceptableUrl(item.url)) return "no";
  if (interceptEnabledCache === false) return "no";
  if (isSkippedNow(url) || isSkippedNow(item.url)) return "no";
  const keys = keysFor(item.url, url);
  if (keys && keys.prevent) return "no";
  if (isPost(item.url, url)) return "no";
  const state = gdmState;
  if (!sessionHydrated || interceptEnabledCache === null || !state || !state.connected || !state.settings) return "maybe";
  const settings = state.settings;
  if (!settings.intercept) return "no";
  const bypassException = forceAllowed(item, keys);
  if (!bypassException && (isException(item.url, settings) || (url !== item.url && isException(url, settings)))) return "no";
  const forceCapture = Boolean(keys && keys.force);
  const attr = downloadAttrFor(item.url, url);
  return wantedBySettings(item, settings, forceCapture, attr) ? "yes" : "no";
}

// Full decision (may read storage and ask GDM). Returns {url, size, force, pageUrl, attrName} or null.
async function evaluateDownload(item) {
  if (!item || typeof item.id !== "number") return null;
  if (handledIds.has(item.id)) return null;
  if (item.state && item.state !== "in_progress") return null;
  if (item.byExtensionId && api.runtime && item.byExtensionId === api.runtime.id) return null;

  const url = item.finalUrl || item.url;
  if (!isInterceptableUrl(url) || !isInterceptableUrl(item.url)) return null; // never blob:, data:, file:, filesystem:

  if (!(await isInterceptEnabled())) return null;
  if ((await isSkipped(url)) || (await isSkipped(item.url))) return null;

  let state = await getGdmState();
  // GDM may have been started since the last failed ping: re-check quickly.
  if (state && !state.connected && Date.now() - (state.ts || 0) > 5000) state = await refreshSettings();
  if (!state || !state.connected) return null; // GDM not running: let the browser handle it
  const settings = state.settings || DEFAULT_SETTINGS;
  if (!settings.intercept) return null;

  const keys = await keysForAsync(item.url, url);
  if (keys && keys.prevent) return null;
  if (await isPostAsync(item.url, url)) return null;
  const bypassException = forceAllowed(item, keys);
  if (!bypassException && (isException(item.url, settings) || (url !== item.url && isException(url, settings)))) return null;
  const forceCapture = Boolean(keys && keys.force);
  const attr = await downloadAttrForAsync(item.url, url);
  if (!wantedBySettings(item, settings, forceCapture, attr)) return null;
  return {
    url,
    size: sizeOf(item),
    force: bypassException,
    pageUrl: (keys && keys.tabUrl) || (attr && attr.tabUrl) || "",
    attrName: (attr && attr.name) || "",
    attrTabUrl: (attr && attr.tabUrl) || ""
  };
}

async function cancelBrowserCopy(id, h) {
  const wasReleased = h.state === "released";
  h.state = "taken";
  clearTimeout(h.timer);
  h.suggest = null; // never name it: the browser copy is cancelled before it is ever shown
  syncKeepAlive();
  syncHeldIds();
  forgetHoldLater(id, h);
  if (wasReleased) {
    const found = (await call(api.downloads.search.bind(api.downloads), { id })) || [];
    if (found[0] && found[0].state === "complete") return;
  }
  await call(api.downloads.cancel.bind(api.downloads), id);
  await call(api.downloads.erase.bind(api.downloads), { id });
}

function rawFileExt(name) {
  const trimmed = String(name || "").replace(/[\s.]+$/, "");
  const dot = trimmed.lastIndexOf(".");
  return dot >= 0 ? trimmed.slice(dot + 1).toLowerCase() : "";
}

function chooseBestFilename(item, h, effectiveFinalUrl, attrName, pageUrl = "") {
  const fromHoldOrItem = basename(h.filename || item.filename || "");
  const cleanAttr = basename(attrName || "");
  if (cleanAttr) {
    // Cross-origin <a download="Custom.ext">: browsers ignore the attribute and fall back to the URL segment.
    let urlBase = "";
    try {
      urlBase = basename(decodeURIComponent(new URL(effectiveFinalUrl || item.url).pathname));
    } catch (e) {
      urlBase = "";
    }
    const fallbackName = fromHoldOrItem || filenameFromUrlQuery(effectiveFinalUrl) || filenameFromUrlQuery(item.url) || urlBase;
    const sameSite = Boolean(
      pageUrl &&
      sameSiteOrHost(pageUrl, item.url) &&
      (!effectiveFinalUrl || effectiveFinalUrl === item.url || sameSiteOrHost(pageUrl, effectiveFinalUrl))
    );
    if (sameSite || rawFileExt(cleanAttr) === rawFileExt(fallbackName)) {
      if (!fromHoldOrItem) return cleanAttr;
      if (!extFromPath(fromHoldOrItem) || (urlBase && fromHoldOrItem === urlBase && cleanAttr !== urlBase)) {
        return cleanAttr;
      }
    }
  }
  if (fromHoldOrItem) return fromHoldOrItem;
  return filenameFromUrlQuery(effectiveFinalUrl) || filenameFromUrlQuery(item.url) || "";
}

async function considerDownload(item, hold = null) {
  const verdict = await evaluateDownload(item);
  if (!verdict) {
    // Only the decision that holds the download may give it back (a later, better-informed one may take it).
    if (hold && held.get(item.id) === hold && !handledIds.has(item.id)) await releaseDownload(item.id);
    return;
  }
  // Atomic check-and-claim (no await between these lines).
  if (handledIds.has(item.id)) return;
  handledIds.add(item.id);
  const h = holdDownload(item.id);
  await interceptDownload(item, verdict, h);
}

async function interceptDownload(item, verdict, h = holdDownload(item.id)) {
  const url = verdict.url;
  const force = !!verdict.force;
  // Paused (and out of sight) first; the browser copy is removed once GDM really takes the download.
  if (!h.paused) {
    h.paused = true;
    syncHeldIds();
    await call(api.downloads.pause.bind(api.downloads), item.id);
  }

  // Suppress duplicate browser downloads fired simultaneously for the same URL (e.g. <a href> + onclick JS).
  const now = Date.now();
  for (const [k, c] of recentClaims) if (now - c.ts > DUPLICATE_WINDOW_MS && c.settled) recentClaims.delete(k);
  const existingClaim = recentClaims.get(item.url);
  if (existingClaim && (!existingClaim.settled || now - existingClaim.ts <= DUPLICATE_WINDOW_MS)) {
    const taken = await existingClaim.promise;
    if (taken) {
      await cancelBrowserCopy(item.id, h);
      return;
    }
  }

  let resolveClaim;
  const claimEntry = {
    ts: now,
    settled: false,
    promise: new Promise((r) => {
      resolveClaim = r;
    })
  };
  recentClaims.set(item.url, claimEntry);

  try {
    const effectiveFinalUrl = h.finalUrl || url;
    const lateKeys = keysFor(item.url, effectiveFinalUrl);
    const settings = gdmState && gdmState.settings;
    if (
      (lateKeys && lateKeys.prevent) ||
      (!force && settings && effectiveFinalUrl !== url && isException(effectiveFinalUrl, settings))
    ) {
      resolveClaim(false);
      claimEntry.settled = true;
      claimEntry.ts = Date.now();
      if (!(await releaseDownload(item.id))) await call(api.downloads.resume.bind(api.downloads), item.id);
      return;
    }

    const cookies = isHttpUrl(item.url) ? await cookiesForDownload(item.url, effectiveFinalUrl) : "";
    const effectiveSize = h.size > 0 ? h.size : verdict.size;
    const effectiveMime = h.mime || item.mime || "";
    const effectiveName = chooseBestFilename(item, h, effectiveFinalUrl, verdict.attrName, verdict.attrTabUrl);

    const payload = {
      action: "download",
      url: item.url, // the original address; redirect targets often expire
      finalUrl: effectiveFinalUrl,
      referrer: item.referrer || "",
      filename: effectiveName,
      fileSize: effectiveSize,
      mime: effectiveMime,
      cookies,
      userAgent: navigator.userAgent,
      force
    };
    if (verdict.pageUrl) payload.pageUrl = verdict.pageUrl;

    const res = await sendToGdm(payload);

    if (res.ok && res.accepted) {
      claimEntry.ts = Date.now();
      claimEntry.settled = true;
      resolveClaim(true);
      await cancelBrowserCopy(item.id, h);
      return;
    }

    claimEntry.ts = Date.now();
    claimEntry.settled = true;
    resolveClaim(false);

    // Declined in GDM, or GDM unreachable: the browser keeps (and resumes) its own download.
    const wasReleased = h.state === "released";
    await addSkip(effectiveFinalUrl);
    if (item.url !== effectiveFinalUrl) await addSkip(item.url);
    const alreadyReleased = wasReleased || h.state === "released";
    if (!alreadyReleased && !(await releaseDownload(item.id))) {
      await call(api.downloads.resume.bind(api.downloads), item.id);
    }
    const found = (await call(api.downloads.search.bind(api.downloads), { id: item.id })) || [];
    const cur = found[0];
    const userCancelled = alreadyReleased
      ? !cur || cur.error === "USER_CANCELED" || cur.error === "USER_SHUTDOWN" || (cur.state === "interrupted" && !cur.error)
      : Boolean(cur && (cur.error === "USER_CANCELED" || cur.error === "USER_SHUTDOWN"));
    if (!userCancelled && (!cur || (cur.state !== "in_progress" && cur.state !== "complete" && cur.state !== "paused"))) {
      await call(api.downloads.download.bind(api.downloads), { url: item.url });
    }
  } catch (err) {
    claimEntry.ts = Date.now();
    claimEntry.settled = true;
    resolveClaim(false);
    await releaseDownload(item.id);
  }
}

if (api.downloads && api.downloads.onCreated) {
  api.downloads.onCreated.addListener((item) => {
    // Stop the browser copy in the same tick when it is clearly for GDM (no storage or messages first).
    const v = quickVerdict(item);
    const hold = v === "yes" ? holdDownload(item.id, { pause: true }) : null;
    considerDownload(item, hold);
  });
}

// Chrome asks every extension for the file name before it shows the download. Holding that answer keeps
// the download out of the browser's list until Golan has decided (what IDM does). Firefox has no such step.
if (
  api.downloads &&
  api.downloads.onDeterminingFilename &&
  typeof api.downloads.onDeterminingFilename.addListener === "function"
) {
  api.downloads.onDeterminingFilename.addListener((item, suggest) => {
    const id = item && item.id;
    const h = held.get(id);
    if (h && h.state !== "released") {
      h.suggest = suggest; // deciding or already taken by GDM: stay out of sight
      if (item.filename) h.filename = item.filename;
      if (item.finalUrl) h.finalUrl = item.finalUrl;
      if (item.mime) h.mime = item.mime;
      if (sizeOf(item) > 0) h.size = sizeOf(item);
      return true;
    }
    if (h || handledIds.has(id)) {
      safeSuggest(suggest);
      return undefined;
    }
    // Second chance: Chrome now knows the real file name and size.
    const v = quickVerdict(item);
    if (v === "no") {
      safeSuggest(suggest);
      return undefined;
    }
    const hold = holdDownload(id, { pause: v === "yes", suggest });
    if (item.filename) hold.filename = item.filename;
    if (item.finalUrl) hold.finalUrl = item.finalUrl;
    if (item.mime) hold.mime = item.mime;
    if (sizeOf(item) > 0) hold.size = sizeOf(item);
    considerDownload(item, hold);
    return true;
  });
} else if (
  api.downloads &&
  api.downloads.onChanged &&
  typeof api.downloads.onChanged.addListener === "function"
) {
  // Firefox lacks onDeterminingFilename; watch onChanged for Content-Disposition filenames on extensionless URLs.
  api.downloads.onChanged.addListener(async (delta) => {
    if (!delta || typeof delta.id !== "number" || handledIds.has(delta.id)) return;
    if (!delta.filename || !delta.filename.current) return;
    const found = (await call(api.downloads.search.bind(api.downloads), { id: delta.id })) || [];
    if (found[0]) considerDownload(found[0], null);
  });
}

// ---------------------------------------------------------------------------
// Media detection (observe only)
// ---------------------------------------------------------------------------

const MEDIA_EXT_RE = /\.(m3u8|mpd|mp4|m4v|mov|webm|mp3|m4a|ogg|ogv|opus|wav|flac|flv|mkv)$/i;
const STREAM_QUERY_RE = /[?&][^=]*=[^&]*\.(m3u8|mpd)(?:[&#]|$)/i;
const SEGMENT_EXT_RE = /\.(ts|m4s|m4f|cmfv|cmfa|f4s|aac|key|vtt)$/i;
const SEGMENT_MP4_PATH_RE = /\/(?:seg|chunk|frag|fragment|part)[-_]?\d+\.(?:mp4|webm|m4a)$/i;
const INIT_SEGMENT_PATH_RE = /\/init(?:[-_][^/]*)?\.(?:mp4|m4a|webm)$/i;
const DRM_LICENSE_URL_RE = /(?:widevine|playready|fairplay|drm\/license|rightsmanager\.asmx|modular-drm)/i;
const STREAM_MIMES = new Set([
  "application/vnd.apple.mpegurl",
  "application/x-mpegurl",
  "audio/mpegurl",
  "audio/x-mpegurl",
  "application/dash+xml",
  "video/vnd.mpeg.dash.mpd"
]);
const SEGMENT_MIMES = new Set(["video/mp2t", "audio/mp2t", "video/iso.segment", "audio/iso.segment"]);

const mediaCache = new Map(); // tabId -> {pageUrl, items:[{url,mime,size,ts}]}
const tabDrm = new Map(); // tabId -> boolean
const tabQueues = new Map(); // tabId -> Promise (serializes updates per tab)

function mediaKey(tabId) {
  return "media_" + tabId;
}

function withTabQueue(tabId, fn) {
  const prev = tabQueues.get(tabId) || Promise.resolve();
  const next = prev.then(fn, fn).catch(() => {});
  tabQueues.set(tabId, next);
  next.then(() => {
    if (tabQueues.get(tabId) === next) tabQueues.delete(tabId);
  });
  return next;
}

async function loadTabMedia(tabId) {
  if (mediaCache.has(tabId)) return mediaCache.get(tabId);
  const key = mediaKey(tabId);
  const stored = (await storageGet("session", key))[key];
  const entry = stored && Array.isArray(stored.items) ? stored : { pageUrl: "", items: [], drm: false };
  if (entry.drm) tabDrm.set(tabId, true);
  mediaCache.set(tabId, entry);
  return entry;
}

async function saveTabMedia(tabId, entry) {
  mediaCache.set(tabId, entry);
  await storageSet("session", { [mediaKey(tabId)]: entry });
  updateBadge(tabId, entry.items.length);
}

async function clearTabMedia(tabId, pageUrl) {
  mediaCache.set(tabId, { pageUrl: pageUrl || "", items: [], drm: false });
  tabDrm.delete(tabId);
  await storageRemove("session", mediaKey(tabId));
  updateBadge(tabId, 0);
}

function updateBadge(tabId, count) {
  try {
    const actionApi = api.action || api.browserAction;
    if (!actionApi) return;
    call(actionApi.setBadgeText.bind(actionApi), { tabId, text: count > 0 ? String(count) : "" });
    if (count > 0) {
      call(actionApi.setBadgeBackgroundColor.bind(actionApi), { tabId, color: BADGE_COLOR });
    }
  } catch (e) {
    // tab gone - ignore
  }
}

function headerValue(headers, name) {
  if (!Array.isArray(headers)) return "";
  const h = headers.find((x) => x.name && x.name.toLowerCase() === name);
  return h && typeof h.value === "string" ? h.value : "";
}

function responseSize(headers) {
  const range = headerValue(headers, "content-range");
  const m = /\/(\d+)\s*$/.exec(range);
  if (m) return Number(m[1]);
  const len = Number(headerValue(headers, "content-length"));
  return len > 0 ? len : -1;
}

function isDrmLicenseUrl(url) {
  if (!isHttpUrl(url)) return false;
  try {
    const u = new URL(url);
    return DRM_LICENSE_URL_RE.test(u.hostname + u.pathname);
  } catch (e) {
    return false;
  }
}

function classifyMedia(details) {
  let u;
  try {
    u = new URL(details.url);
  } catch (e) {
    return null;
  }
  if (u.protocol !== "http:" && u.protocol !== "https:") return null;
  const host = u.hostname.toLowerCase();
  if (host === "googlevideo.com" || host.endsWith(".googlevideo.com")) return null; // YouTube streams
  const path = u.pathname.toLowerCase();
  if (SEGMENT_EXT_RE.test(path)) return null;
  if (SEGMENT_MP4_PATH_RE.test(path)) return null;

  const mime = headerValue(details.responseHeaders, "content-type").split(";")[0].trim().toLowerCase();
  if (SEGMENT_MIMES.has(mime)) return null;

  const size = responseSize(details.responseHeaders);
  if (INIT_SEGMENT_PATH_RE.test(path) && (size <= 0 || size < 512 * 1024)) return null;

  const byMime = mime.startsWith("video/") || mime.startsWith("audio/") || STREAM_MIMES.has(mime);
  const byExt = MEDIA_EXT_RE.test(path) || STREAM_QUERY_RE.test(u.search);
  if (!byMime && !byExt) return null;
  return { url: details.url, mime, size };
}

function parseStreamQualityHint(url, mime = "") {
  const m = String(mime || "").toLowerCase();
  let path = "";
  let full = String(url || "");
  try {
    const u = new URL(full);
    path = u.pathname.toLowerCase();
  } catch (e) {
    path = full.toLowerCase();
  }

  const isDash = m.includes("dash") || /\.mpd$/i.test(path) || /\.mpd(?:[?&#]|$)/i.test(full);
  const isHls = m.includes("mpegurl") || /\.m3u8$/i.test(path) || /\.m3u8(?:[?&#]|$)/i.test(full);
  const isStream = isDash || isHls;

  let height = 0;
  const resMatch = /(?:^|[^0-9])(\d{3,4})x(\d{3,4})(?:[^0-9]|$)/i.exec(full);
  if (resMatch) {
    height = Math.min(Number(resMatch[1]), Number(resMatch[2]));
  } else {
    const pMatch = /(?:^|[\/_\-.=?&])(2160|1440|1080|720|576|540|480|360|240|144)p?(?:[\/_\-.&?]|$)/i.exec(full);
    if (pMatch) height = Number(pMatch[1]);
    else if (/(?:^|[\/_\-.])4k(?:[\/_\-.]|$)/i.test(full)) height = 2160;
    else if (/(?:^|[\/_\-.])2k(?:[\/_\-.]|$)/i.test(full)) height = 1440;
  }

  const isMaster =
    isDash ||
    (isHls &&
      height === 0 &&
      /(?:^|\/)(?:master|manifest|playlist|index|multi)(?:[-_][^/]*)?\.m3u8$/i.test(path.replace(/\s+/g, ""))) ||
    (isHls && /(?:^|\/)master[-_a-z0-9]*\.m3u8$/i.test(path));

  return { isStream, isHls, isDash, isMaster, height };
}

function rankMediaCandidate(item) {
  if (!item || !item.url) return -1;
  const q = parseStreamQualityHint(item.url, item.mime);
  let score = 0;
  if (q.isMaster) score += 1_000_000;
  else if (q.isStream) score += 500_000;
  if (q.height > 0) score += q.height * 100;
  if (typeof item.size === "number" && item.size > 0) {
    score += Math.min(90_000, Math.floor(item.size / 1024));
  }
  return score;
}

function canonicalMediaKey(url) {
  try {
    const u = new URL(url);
    const volatileParams = new Set(["_", "_ts", "rnd", "cachebuster", "cb"]);
    const kept = [];
    for (const [k, v] of u.searchParams.entries()) {
      if (volatileParams.has(k.toLowerCase())) continue;
      kept.push([k, v]);
    }
    kept.sort((a, b) => (a[0] === b[0] ? a[1].localeCompare(b[1]) : a[0].localeCompare(b[0])));
    const qs = kept.map(([k, v]) => encodeURIComponent(k) + "=" + encodeURIComponent(v)).join("&");
    return u.origin.toLowerCase() + u.pathname + (qs ? "?" + qs : "");
  } catch (e) {
    return String(url || "");
  }
}

function dedupeMediaItems(items) {
  if (!Array.isArray(items) || items.length <= 1) return Array.isArray(items) ? items.slice() : [];
  const byKey = new Map();
  for (const item of items) {
    if (!item || !item.url) continue;
    const key = canonicalMediaKey(item.url);
    const prev = byKey.get(key);
    if (!prev) {
      byKey.set(key, item);
    } else {
      const nextSize = typeof item.size === "number" && item.size > 0 ? item.size : prev.size;
      byKey.set(key, {
        ...prev,
        ...item,
        mime: item.mime || prev.mime || "",
        size: nextSize
      });
    }
  }
  return Array.from(byKey.values());
}

function pickBestMediaForTab(items) {
  const deduped = dedupeMediaItems(items);
  if (deduped.length === 0) return null;
  const sorted = deduped.slice().sort((a, b) => rankMediaCandidate(b) - rankMediaCandidate(a));
  return sorted[0];
}

function recordMedia(tabId, media, details) {
  return withTabQueue(tabId, async () => {
    const state = gdmState || (await getGdmState());
    const st = (state && state.settings) || DEFAULT_SETTINGS;
    if (st.mediaPanel === false) return;
    if (st.mediaPanelMinSizeKB > 0 && media.size > 0 && media.size < st.mediaPanelMinSizeKB * 1024) return;
    if (isMediaPanelException(media.url, st)) return;
    const initiatorUrl = (details && (details.documentUrl || details.initiator)) || "";
    if (initiatorUrl && isMediaPanelException(initiatorUrl, st)) return;
    const entry = await loadTabMedia(tabId);
    let pageUrl = entry.pageUrl;
    if (!pageUrl) {
      // Remember which page these belong to, so a later url change clears them correctly.
      const tab = await getTab(tabId);
      pageUrl = (tab && tab.url) || initiatorUrl;
    }
    if (pageUrl && isMediaPanelException(pageUrl, st)) return;
    const items = entry.items.slice();
    const mediaKeyCanon = canonicalMediaKey(media.url);
    const idx = items.findIndex((x) => x.url === media.url || canonicalMediaKey(x.url) === mediaKeyCanon);
    if (idx >= 0) {
      const old = items[idx];
      items[idx] = {
        ...old,
        url: media.url,
        mime: media.mime || old.mime,
        size: media.size > 0 ? media.size : old.size
      };
    } else {
      items.push({ ...media, ts: Date.now() });
      while (items.length > MAX_MEDIA_PER_TAB) items.shift();
    }
    await saveTabMedia(tabId, { pageUrl, items, drm: Boolean(entry.drm || tabDrm.get(tabId)) });
  });
}

if (api.webRequest && api.webRequest.onResponseStarted) {
  api.webRequest.onResponseStarted.addListener(
    (details) => {
      if (details.requestId) {
        const now = Date.now();
        for (const [rid, exp] of postRequestIds) if (exp < now) postRequestIds.delete(rid);
        const postExp = postRequestIds.get(details.requestId);
        if (postExp && postExp > now) recordPostUrl(details.url, postExp);
      }
      if (details.type === "main_frame") {
        if (details.tabId >= 0 && !/^\s*attachment\b/i.test(headerValue(details.responseHeaders, "content-disposition"))) {
          tabDrm.delete(details.tabId);
          withTabQueue(details.tabId, () => clearTabMedia(details.tabId, details.url));
        }
        return;
      }
      if (details.type === "sub_frame") return;
      if (details.tabId < 0) return;
      if (isDrmLicenseUrl(details.url)) {
        tabDrm.set(details.tabId, true);
        withTabQueue(details.tabId, async () => {
          tabDrm.set(details.tabId, true);
          const entry = await loadTabMedia(details.tabId);
          let pageUrl = entry.pageUrl;
          if (!pageUrl) {
            const tab = await getTab(details.tabId);
            pageUrl = (tab && tab.url) || details.documentUrl || details.initiator || "";
          }
          await saveTabMedia(details.tabId, { pageUrl, items: entry.items.slice(), drm: true });
        });
        return;
      }
      const media = classifyMedia(details);
      if (!media) return;
      recordMedia(details.tabId, media, details);
    },
    { urls: ["http://*/*", "https://*/*"], types: ["main_frame", "sub_frame", "media", "xmlhttprequest", "other"] },
    ["responseHeaders"]
  );
}

function stripHash(url) {
  return String(url || "").split("#")[0];
}

if (api.tabs && api.tabs.onUpdated) {
  api.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
    const isLoading = Boolean(changeInfo && changeInfo.status === "loading");
    if (!changeInfo || (!changeInfo.url && !isLoading)) return;
    withTabQueue(tabId, async () => {
      const entry = await loadTabMedia(tabId);
      const nextUrl = changeInfo.url || (tab && tab.url) || entry.pageUrl || "";
      if (!isLoading && changeInfo.url && stripHash(entry.pageUrl) === stripHash(changeInfo.url)) return;
      await clearTabMedia(tabId, nextUrl);
    });
  });
}

if (api.tabs && api.tabs.onRemoved) {
  api.tabs.onRemoved.addListener((tabId) => {
    withTabQueue(tabId, async () => {
      mediaCache.delete(tabId);
      tabDrm.delete(tabId);
      await storageRemove("session", mediaKey(tabId));
    });
  });
}

if (api.tabs && api.tabs.onReplaced) {
  api.tabs.onReplaced.addListener((addedTabId, removedTabId) => {
    withTabQueue(removedTabId, async () => {
      mediaCache.delete(removedTabId);
      tabDrm.delete(removedTabId);
      await storageRemove("session", mediaKey(removedTabId));
    });
  });
}

async function getTabMedia(tabId) {
  let entry;
  await withTabQueue(tabId, async () => {
    entry = await loadTabMedia(tabId);
  });
  return entry ? entry.items.slice() : [];
}

// ---------------------------------------------------------------------------
// Actions sent to GDM
// ---------------------------------------------------------------------------

async function downloadUrl(url, referrer, extra = {}) {
  if (isMagnetUrl(url)) {
    return sendToGdm({
      action: "add",
      url: url.trim()
    });
  }
  if (!isDownloadableLink(url)) {
    notify(i18nMsg("errInvalidUrl"));
    return { ok: false, error: "bad-url" };
  }
  return sendToGdm({
    action: "download",
    url,
    referrer: referrer || "",
    filename: extra.filename || "",
    fileSize: typeof extra.size === "number" && extra.size > 0 ? extra.size : -1,
    mime: extra.mime || "",
    cookies: isHttpUrl(url) ? await cookieHeader(url) : "",
    userAgent: navigator.userAgent
  });
}

async function collectCookiesForMediaUrls(primaryUrl, extraUrls) {
  const baseCookies = await cookieObjectsForApp(primaryUrl);
  const extraOrigins = [];
  let primaryOrigin = "";
  try {
    primaryOrigin = new URL(primaryUrl).origin.toLowerCase();
  } catch (e) {
    primaryOrigin = "";
  }
  const seenOrigins = new Set(primaryOrigin ? [primaryOrigin] : []);
  for (const raw of extraUrls) {
    if (!isHttpUrl(raw)) continue;
    try {
      const u = new URL(raw);
      const orig = u.origin.toLowerCase();
      if (!seenOrigins.has(orig) && extraOrigins.length < 5) {
        seenOrigins.add(orig);
        extraOrigins.push(u.origin + "/");
      }
    } catch (e) {
      // ignore invalid URL
    }
  }
  if (extraOrigins.length === 0) return baseCookies;

  const merged = [];
  const seenCookieKeys = new Set();
  const addAll = (list) => {
    if (!Array.isArray(list)) return;
    for (const c of list) {
      if (!c || !c.name) continue;
      const key = `${c.domain || ""}\t${c.path || "/"}\t${c.name}`;
      if (seenCookieKeys.has(key)) continue;
      seenCookieKeys.add(key);
      merged.push(c);
    }
  };
  addAll(baseCookies);
  for (const originUrl of extraOrigins) {
    addAll(await cookieObjectsForApp(originUrl));
  }
  return merged;
}

async function sendMedia(tab, title, extraUrls = [], onlyUrls = null, options = {}) {
  if (!tab || !isHttpUrl(tab.url)) {
    notify(i18nMsg("errNothingToDownload"));
    return { ok: false, error: "bad-page" };
  }
  const frameUrl = options && isHttpUrl(options.frameUrl) ? options.frameUrl : "";
  let tabEntry;
  await withTabQueue(tab.id, async () => {
    tabEntry = await loadTabMedia(tab.id);
  });
  const hasTabDrm = Boolean(tabDrm.get(tab.id) || (tabEntry && tabEntry.drm));
  const isDrm = Boolean((options && options.drmProtected) || (!onlyUrls && hasTabDrm && extraUrls.length === 0));
  if (isDrm && options && options.fromContentScript) {
    notify(i18nMsg("errDrmProtected"));
    return { ok: false, error: "drm", message: i18nMsg("errDrmProtected") };
  }

  let mediaUrls;
  if (onlyUrls) {
    mediaUrls = onlyUrls;
  } else {
    const rawItems = await getTabMedia(tab.id);
    const deduped = dedupeMediaItems(rawItems);
    deduped.sort((a, b) => rankMediaCandidate(b) - rankMediaCandidate(a));
    mediaUrls = deduped.map((m) => ({ url: m.url, mime: m.mime || "", size: m.size || -1 }));
  }
  for (const u of extraUrls) {
    if (isHttpUrl(u) && !mediaUrls.some((m) => m.url === u)) mediaUrls.unshift({ url: u, mime: "", size: -1 });
  }

  const cookieTargetUrls = [];
  if (frameUrl && frameUrl !== tab.url) cookieTargetUrls.push(frameUrl);
  for (const m of mediaUrls) cookieTargetUrls.push(m.url);

  const payload = {
    action: "media",
    pageUrl: tab.url,
    title: title || tab.title || "",
    userAgent: navigator.userAgent,
    cookies: await collectCookiesForMediaUrls(tab.url, cookieTargetUrls),
    mediaUrls
  };
  if (frameUrl && frameUrl !== tab.url) payload.referrer = frameUrl;
  if (isDrm) {
    payload.drm = true;
    notify(i18nMsg("errDrmProtected"));
  }
  const res = await sendToGdm(payload);
  return isDrm ? { ...res, drm: true } : res;
}

// Runs in the page: collects unique http(s) and ftp(s) links.
function collectLinksInPage() {
  const seen = new Set();
  const out = [];
  for (const a of document.querySelectorAll("a[href]")) {
    const href = a.href;
    if (!/^(?:https?|ftps?):\/\/[^/\s]+/i.test(href)) continue;
    const key = href.split("#")[0];
    if (seen.has(key)) continue;
    seen.add(key);
    out.push({ url: key, text: (a.textContent || a.title || "").trim().replace(/\s+/g, " ").slice(0, 200) });
  }
  return out;
}

async function downloadAllLinks(tab) {
  if (!tab || !isHttpUrl(tab.url)) {
    notify(i18nMsg("errCannotReadLinks"));
    return { ok: false, error: "bad-page" };
  }
  let links = null;
  if (api.scripting && typeof api.scripting.executeScript === "function") {
    const results = await call(api.scripting.executeScript.bind(api.scripting), {
      target: { tabId: tab.id },
      func: collectLinksInPage
    });
    links = results && results[0] && Array.isArray(results[0].result) ? results[0].result : null;
  } else if (api.tabs && typeof api.tabs.executeScript === "function") {
    const results = await call(api.tabs.executeScript.bind(api.tabs), tab.id, {
      code: "(" + collectLinksInPage.toString() + ")()"
    });
    links = results && Array.isArray(results[0]) ? results[0] : null;
  }
  if (!links) {
    notify(i18nMsg("errReadLinksFailed"));
    return { ok: false, error: "script-failed" };
  }
  if (links.length === 0) {
    notify(i18nMsg("errNoLinksFound"));
    return { ok: false, error: "no-links" };
  }
  // Each link only gets the cookies of its own site (never the page's cookies on another host).
  const byOrigin = new Map();
  const byUrl = new Map();
  const validLinks = [];
  for (const l of links) {
    if (!l || !isInterceptableUrl(l.url)) continue;
    if (!isHttpUrl(l.url)) {
      l.cookies = "";
      validLinks.push(l);
      continue;
    }
    let origin = "";
    try { origin = new URL(l.url).origin; } catch (e) { continue; }
    if (!byOrigin.has(origin)) byOrigin.set(origin, await cookieHeader(origin + "/"));
    const rootHdr = byOrigin.get(origin);
    let urlHdr = rootHdr;
    if (l.url !== origin + "/") {
      if (!byUrl.has(l.url)) byUrl.set(l.url, await cookieHeader(l.url));
      urlHdr = byUrl.get(l.url);
    }
    if (rootHdr && urlHdr && rootHdr !== urlHdr) {
      l.cookies = [...new Set([...rootHdr.split("; "), ...urlHdr.split("; ")])].join("; ");
    } else {
      l.cookies = urlHdr || rootHdr || "";
    }
    validLinks.push(l);
  }
  if (validLinks.length === 0) {
    notify(i18nMsg("errNoLinksFound"));
    return { ok: false, error: "no-links" };
  }
  const res = await sendToGdm({
    action: "downloadAll",
    referrer: tab.url,
    userAgent: navigator.userAgent,
    links: validLinks
  });
  return { ...res, count: validLinks.length };
}

function isStreamItem(item) {
  const mime = (item.mime || "").toLowerCase();
  if (STREAM_MIMES.has(mime)) return true;
  try {
    return /\.(m3u8|mpd)$/i.test(new URL(item.url).pathname);
  } catch (e) {
    return false;
  }
}

// ---------------------------------------------------------------------------
// Context menus
// ---------------------------------------------------------------------------

function createMenus() {
  const menusApi = api.contextMenus || api.menus;
  if (!menusApi || typeof menusApi.removeAll !== "function") return;
  call(menusApi.removeAll.bind(menusApi)).then(() => {
    const all = [
      { key: "link", id: "gdm-link", title: i18nMsg("menuDownloadLink"), contexts: ["link"] },
      { key: "all", id: "gdm-all-links", title: i18nMsg("menuDownloadAll"), contexts: ["page"] },
      { key: "video", id: "gdm-media", title: i18nMsg("menuDownloadVideo"), contexts: ["page", "video", "audio"] },
      { key: "image", id: "gdm-image", title: i18nMsg("menuDownloadImage"), contexts: ["image"] }
    ];
    const chosen = (gdmState && gdmState.settings && gdmState.settings.menuItems) || DEFAULT_SETTINGS.menuItems;
    const menus = all.filter((m) => chosen.includes(m.key)).map(({ key, ...m }) => m);
    for (const m of menus) {
      try {
        menusApi.create(
          { ...m, documentUrlPatterns: ["http://*/*", "https://*/*"] },
          () => void (api.runtime && api.runtime.lastError)
        );
      } catch (e) {
        try {
          menusApi.create({ ...m, documentUrlPatterns: ["http://*/*", "https://*/*"] });
        } catch (e2) {
          // ignore
        }
      }
    }
  });
}

const menusApiGlobal = api.contextMenus || api.menus;
if (menusApiGlobal && menusApiGlobal.onClicked) {
  menusApiGlobal.onClicked.addListener((info, tab) => {
    const referrer = info.pageUrl || (tab && tab.url) || "";
    switch (info.menuItemId) {
      case "gdm-link":
        downloadUrl(info.linkUrl, referrer);
        break;
      case "gdm-image":
        downloadUrl(info.srcUrl, referrer);
        break;
      case "gdm-all-links":
        downloadAllLinks(tab);
        break;
      case "gdm-media":
        sendMedia(tab, tab && tab.title, info.srcUrl ? [info.srcUrl] : []);
        break;
      default:
        break;
    }
  });
}

// ---------------------------------------------------------------------------
// Messages from popup and content script
// ---------------------------------------------------------------------------

async function getTab(tabId) {
  if (typeof tabId !== "number" || !api.tabs || typeof api.tabs.get !== "function") return undefined;
  return call(api.tabs.get.bind(api.tabs), tabId);
}

const CONTENT_SCRIPT_MESSAGE_TYPES = new Set([
  "media-from-page",
  "link-keys",
  "download-attr",
  "force-link",
  "panel-settings",
  "magnet-link"
]);

async function handleMessage(msg, sender) {
  const type = msg && msg.type;
  if (sender && sender.id && api.runtime && api.runtime.id && sender.id !== api.runtime.id) {
    return { ok: false, error: "forbidden-sender" };
  }
  const fromTab = Boolean(sender && sender.tab);
  if (fromTab && !CONTENT_SCRIPT_MESSAGE_TYPES.has(type)) {
    return { ok: false, error: "forbidden-sender" };
  }
  switch (type) {
    case "magnet-link": {
      if (!isMagnetUrl(msg.url)) return { ok: false, error: "bad-url" };
      if (!(await isInterceptEnabled())) return { ok: false, intercepted: false };
      let state = await getGdmState();
      if (state && !state.connected && Date.now() - (state.ts || 0) > 5000) state = await refreshSettings();
      if (!state || !state.connected || (state.settings && state.settings.intercept === false)) {
        return { ok: false, intercepted: false };
      }
      const res = await sendToGdm({ action: "add", url: msg.url.trim() });
      return { ...res, intercepted: res.ok && res.accepted !== false };
    }
    case "force-link": {
      const targetUrl = typeof msg.url === "string" ? msg.url.trim() : "";
      if (!isInterceptableUrl(targetUrl)) return { ok: false, intercepted: false, error: "bad-url" };
      if (!(await isInterceptEnabled())) return { ok: false, intercepted: false };
      let state = await getGdmState();
      if (state && !state.connected && Date.now() - (state.ts || 0) > 5000) state = await refreshSettings();
      const settings = (state && state.settings) || DEFAULT_SETTINGS;
      if (!state || !state.connected || settings.intercept === false) {
        return { ok: false, intercepted: false };
      }
      const tabUrl = (sender && sender.tab && sender.tab.url) || "";
      const sameSite = Boolean(tabUrl && sameSiteOrHost(tabUrl, targetUrl));
      if (!sameSite && isException(targetUrl, settings)) {
        return { ok: false, intercepted: false };
      }
      const payload = {
        action: "download",
        url: targetUrl,
        referrer: tabUrl,
        filename: basename(msg.filename || ""),
        fileSize: -1,
        mime: "",
        cookies: isHttpUrl(targetUrl) ? await cookieHeader(targetUrl) : "",
        userAgent: navigator.userAgent,
        force: sameSite
      };
      if (tabUrl) payload.pageUrl = tabUrl;
      const res = await sendToGdm(payload);
      const intercepted = Boolean(res.ok && res.accepted !== false);
      if (!intercepted) await addSkip(targetUrl);
      return { ...res, intercepted };
    }
    case "media-from-page": {
      const tab = sender && sender.tab;
      return sendMedia(tab, msg.title, msg.videoSrc ? [msg.videoSrc] : [], null, {
        frameUrl: msg.frameUrl || "",
        drmProtected: !!msg.drmProtected,
        fromContentScript: true
      });
    }
    case "link-keys": {
      const targetUrl = msg.url || msg.href;
      if (isInterceptableUrl(targetUrl) && targetUrl.length <= MAX_STORED_URL_LEN) {
        const tabUrl = ((sender && sender.tab && sender.tab.url) || "").slice(0, MAX_STORED_URL_LEN);
        await recordLinkKey(targetUrl, {
          prevent: !!msg.prevent,
          force: !!msg.force,
          tabUrl,
          exp: Date.now() + 30000
        });
      }
      return { ok: true };
    }
    case "download-attr": {
      const targetUrl = msg.url || msg.href;
      if (isInterceptableUrl(targetUrl) && targetUrl.length <= MAX_STORED_URL_LEN) {
        const tabUrl = ((sender && sender.tab && sender.tab.url) || "").slice(0, MAX_STORED_URL_LEN);
        await recordDownloadAttr(targetUrl, {
          name: basename(msg.filename || "").slice(0, MAX_FILENAME_LEN),
          tabUrl,
          exp: Date.now() + 30000
        });
      }
      return { ok: true };
    }
    case "panel-settings": {
      const state = await getGdmState();
      const st = (state && state.settings) || DEFAULT_SETTINGS;
      const tabUrl = (sender && sender.tab && sender.tab.url) || "";
      const blocked = isMediaPanelException(tabUrl, st);
      return { enabled: st.mediaPanel !== false && !blocked };
    }
    case "get-status": {
      const state = await refreshSettings();
      return { connected: !!state.connected, version: state.version || "", interceptEnabled: await isInterceptEnabled() };
    }
    case "get-media":
      return { items: await getTabMedia(msg.tabId) };
    case "download-item": {
      const tab = await getTab(msg.tabId);
      const item = msg.item || {};
      if (isStreamItem(item)) {
        return sendMedia(tab, tab && tab.title, [], [{ url: item.url, mime: item.mime || "", size: item.size || -1 }]);
      }
      return downloadUrl(item.url, tab && tab.url, { mime: item.mime, size: item.size });
    }
    case "media-page":
      return sendMedia(await getTab(msg.tabId), msg.title);
    case "download-all":
      return downloadAllLinks(await getTab(msg.tabId));
    default:
      return { ok: false, error: "unknown-message" };
  }
}

if (api.runtime && api.runtime.onMessage) {
  api.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    handleMessage(msg, sender)
      .then((res) => sendResponse(res))
      .catch(() => sendResponse({ ok: false, error: "internal-error" }));
    return true; // async response
  });
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

if (api.runtime && api.runtime.onInstalled) {
  api.runtime.onInstalled.addListener(() => {
    createMenus();
    refreshSettings();
  });
}

if (api.runtime && api.runtime.onStartup) {
  api.runtime.onStartup.addListener(() => {
    createMenus();
    refreshSettings();
  });
}
