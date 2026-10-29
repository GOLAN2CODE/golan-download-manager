// Golan download manager - background script / service worker (Manifest V3).
// Talks to the desktop app through native messaging: one open port (fast, like IDM), with one-shot
// messages as the fallback.

const api = globalThis.browser ?? chrome;

const HOST = "com.golan.gdm";
const SETTINGS_TTL_MS = 60 * 1000;
const SKIP_TTL_MS = 30 * 1000;
const MAX_MEDIA_PER_TAB = 30;
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
  menuDownloadImage: "Download image with Golan"
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

// Used only when GDM never answered a ping yet.
const DEFAULT_SETTINGS = {
  intercept: true,
  extensions: [
    "zip", "rar", "7z", "gz", "tar", "iso", "exe", "msi", "apk", "dmg",
    "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "mp3", "m4a", "wav", "flac",
    "pdf", "bin", "img"
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
  "application/x-msdownload": "exe",
  "application/x-msdos-program": "exe",
  "application/x-executable": "exe",
  "application/vnd.microsoft.portable-executable": "exe",
  "application/x-msi": "msi",
  "application/x-ms-installer": "msi",
  "application/x-iso9660-image": "iso",
  "application/vnd.android.package-archive": "apk",
  "application/x-apple-diskimage": "dmg",
  "application/pdf": "pdf",
  "video/mp4": "mp4",
  "video/x-matroska": "mkv",
  "video/webm": "webm",
  "video/x-msvideo": "avi",
  "video/quicktime": "mov",
  "video/x-ms-wmv": "wmv",
  "video/x-flv": "flv",
  "audio/mpeg": "mp3",
  "audio/mp4": "m4a",
  "audio/x-m4a": "m4a",
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

function isMagnetUrl(url) {
  return typeof url === "string" &&
    /^magnet:\?(?:[^#]*&)?xt(?:\.\d+)?=urn:(?:btih:(?:[0-9a-f]{40}|[a-z2-7]{32})|btmh:1220[0-9a-f]{64})(?:[&#]|$)/i.test(url.trim());
}

function sameSiteOrHost(urlA, urlB) {
  try {
    const a = new URL(urlA).hostname.replace(/\.$/, "").toLowerCase();
    const b = new URL(urlB).hostname.replace(/\.$/, "").toLowerCase();
    if (!a || !b) return false;
    if (a === b || a.endsWith("." + b) || b.endsWith("." + a)) return true;
    const partsA = a.split(".");
    const partsB = b.split(".");
    if (partsA.length >= 2 && partsB.length >= 2) {
      return partsA.slice(-2).join(".") === partsB.slice(-2).join(".");
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
const PORT_RETRY_MS = 30 * 1000;

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
  });
  nativePort = port;
  return port;
}

function sendThroughPort(message, timeoutMs) {
  const port = openNativePort();
  if (!port) return Promise.resolve(undefined);
  return new Promise((resolve) => {
    const id = ++portSeq;
    const timer = setTimeout(() => {
      portWaiters.delete(id);
      resolve(undefined);
    }, timeoutMs);
    portWaiters.set(id, { resolve, timer });
    try {
      port.postMessage({ ...message, _rid: id });
    } catch (e) {
      portWaiters.delete(id);
      clearTimeout(timer);
      if (nativePort === port) nativePort = null;
      resolve(undefined);
    }
  });
}

// Resolves {connected:boolean, reply?:object}. Never rejects.
async function sendNative(message) {
  const timeoutMs = message && message.action === "ping" ? 5000 : 10 * 60 * 1000;
  const fast = await sendThroughPort(message, timeoutMs);
  if (fast !== undefined && fast !== null) return { connected: true, reply: fast };
  if (!api.runtime || typeof api.runtime.sendNativeMessage !== "function") {
    return { connected: false };
  }
  const reply = await call(api.runtime.sendNativeMessage.bind(api.runtime), HOST, message);
  if (reply === undefined || reply === null) {
    return { connected: false };
  }
  return { connected: true, reply };
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
  const error = typeof reply.error === "string" && reply.error ? reply.error : i18nMsg("errGenericGdm");
  if (!silent) notify(i18nMsg("errRequestFailed", [error]));
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
    gdmState = state;
    await storageSet("local", { gdmState: state });
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
  if (gdmState) gdmState = { ...gdmState, connected: false, ts: Date.now() };
  storageSet("local", { gdmState: gdmState || { connected: false, settings: null, ts: Date.now() } });
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
if (api.webRequest && api.webRequest.onBeforeRequest) {
  api.webRequest.onBeforeRequest.addListener(
    (details) => {
      if (details.method === "POST") postUrls.set(details.url, Date.now() + 120000);
    },
    { urls: ["http://*/*", "https://*/*"], types: ["main_frame", "sub_frame", "other", "xmlhttprequest"] }
  );
}

function isPost(...urls) {
  const now = Date.now();
  for (const [u, exp] of postUrls) if (exp < now) postUrls.delete(u);
  return urls.some((u) => u && postUrls.has(u));
}

// Alt + click = let the browser download it; Alt + Ctrl + click = force GDM (reported by content.js).
const linkKeys = new Map(); // url -> {prevent, force, exp}

function keysFor(...urls) {
  const now = Date.now();
  for (const u of urls) {
    const k = u && linkKeys.get(u);
    if (k && k.exp > now) return k;
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

async function addSkip(url) {
  const now = Date.now();
  skipUrls.set(url, now + SKIP_TTL_MS);
  const { skipUrls: stored } = await storageGet("session", "skipUrls");
  const obj = {};
  for (const [u, exp] of Object.entries(stored || {})) if (exp > now) obj[u] = exp;
  obj[url] = now + SKIP_TTL_MS;
  await storageSet("session", { skipUrls: obj });
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
  if (isHttpUrl(s)) {
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
  const m = /\.([a-z0-9]{1,8})$/i.exec(base);
  return m ? m[1].toLowerCase() : "";
}

function basename(path) {
  if (!path) return "";
  return String(path).split(/[\\/]/).pop() || "";
}

function candidateExts(item) {
  const exts = new Set();
  const e1 = extFromPath(item.finalUrl || "");
  const e2 = extFromPath(item.url || "");
  const e3 = extFromPath(item.filename || "");
  const mime = (item.mime || "").split(";")[0].trim().toLowerCase();
  const e4 = MIME_EXT[mime] || "";
  for (const e of [e1, e2, e3, e4]) if (e) exts.add(e);
  return exts;
}

function sizeOf(item) {
  if (typeof item.totalBytes === "number" && item.totalBytes > 0) return item.totalBytes;
  if (typeof item.fileSize === "number" && item.fileSize > 0) return item.fileSize;
  return -1;
}

const handledIds = new Set(); // download ids already claimed for GDM

// IDM-style hold: the browser's own download is paused the moment it is created and kept in Chrome's
// "choosing a file name" step, so it never shows up in the browser while Golan decides. It is cancelled
// when Golan takes it, or released (name accepted + resumed) when the browser should keep it.
const HOLD_LIMIT_MS = 45 * 1000;
const held = new Map(); // download id -> {state: "deciding"|"taken"|"released", paused, suggest, timer}

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
    h = { state: "deciding", paused: false, suggest: null, timer: null };
    held.set(id, h);
    // Never keep a download hostage: without an answer in time, the browser gets it back.
    h.timer = setTimeout(() => releaseDownload(id), HOLD_LIMIT_MS);
  }
  if (suggest) h.suggest = suggest;
  if (pause && !h.paused && h.state === "deciding") {
    h.paused = true;
    call(api.downloads.pause.bind(api.downloads), id);
  }
  return h;
}

// The browser keeps this download: give it its name back and let it continue.
function releaseDownload(id) {
  const h = held.get(id);
  if (!h || h.state !== "deciding") return Promise.resolve(false);
  h.state = "released";
  clearTimeout(h.timer);
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

function wantedBySettings(item, settings, allowForce) {
  let match = allowForce;
  const wanted = new Set(settings.extensions || []);
  for (const e of candidateExts(item)) if (wanted.has(e)) match = true;
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
  if (!isHttpUrl(url) || !isHttpUrl(item.url)) return "no";
  if (interceptEnabledCache === false) return "no";
  if (isSkippedNow(url) || isSkippedNow(item.url)) return "no";
  const keys = keysFor(item.url, url);
  if (keys && keys.prevent) return "no";
  if (isPost(item.url, url)) return "no";
  const state = gdmState;
  if (interceptEnabledCache === null || !state || !state.connected || !state.settings) return "maybe";
  const settings = state.settings;
  if (!settings.intercept) return "no";
  const allowForce = forceAllowed(item, keys);
  if (!allowForce && (isException(item.url, settings) || (url !== item.url && isException(url, settings)))) return "no";
  return wantedBySettings(item, settings, allowForce) ? "yes" : "no";
}

// Full decision (may read storage and ask GDM). Returns {url, size, force} or null.
async function evaluateDownload(item) {
  if (!item || typeof item.id !== "number") return null;
  if (handledIds.has(item.id)) return null;
  if (item.state && item.state !== "in_progress") return null;
  if (item.byExtensionId && api.runtime && item.byExtensionId === api.runtime.id) return null;

  const url = item.finalUrl || item.url;
  if (!isHttpUrl(url) || !isHttpUrl(item.url)) return null; // never blob:, data:, file:, filesystem:

  if (!(await isInterceptEnabled())) return null;
  if ((await isSkipped(url)) || (await isSkipped(item.url))) return null;

  let state = await getGdmState();
  // GDM may have been started since the last failed ping: re-check quickly.
  if (state && !state.connected && Date.now() - (state.ts || 0) > 5000) state = await refreshSettings();
  if (!state || !state.connected) return null; // GDM not running: let the browser handle it
  const settings = state.settings || DEFAULT_SETTINGS;
  if (!settings.intercept) return null;

  const keys = keysFor(item.url, url);
  if (keys && keys.prevent) return null;
  if (isPost(item.url, url)) return null;
  const allowForce = forceAllowed(item, keys);
  if (!allowForce && (isException(item.url, settings) || (url !== item.url && isException(url, settings)))) return null;
  if (!wantedBySettings(item, settings, allowForce)) return null;
  return { url, size: sizeOf(item), force: allowForce };
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
  await interceptDownload(item, verdict.url, verdict.size, verdict.force, h);
}

async function interceptDownload(item, url, size, force = false, h = holdDownload(item.id)) {
  // Paused (and out of sight) first; the browser copy is removed once GDM really takes the download.
  if (!h.paused) {
    h.paused = true;
    await call(api.downloads.pause.bind(api.downloads), item.id);
  }

  const res = await sendToGdm({
    action: "download",
    url: item.url, // the original address; redirect targets often expire
    finalUrl: url,
    referrer: item.referrer || "",
    filename: basename(item.filename || ""),
    fileSize: size,
    mime: item.mime || "",
    cookies: await cookieHeader(item.url),
    userAgent: navigator.userAgent,
    force: !!force
  });

  if (res.ok && res.accepted) {
    h.state = "taken";
    clearTimeout(h.timer);
    h.suggest = null; // never name it: the browser copy is cancelled before it is ever shown
    forgetHoldLater(item.id, h);
    await call(api.downloads.cancel.bind(api.downloads), item.id);
    await call(api.downloads.erase.bind(api.downloads), { id: item.id });
    return;
  }
  // Declined in GDM, or GDM unreachable: the browser keeps (and resumes) its own download.
  await addSkip(url);
  if (item.url !== url) await addSkip(item.url);
  if (!(await releaseDownload(item.id))) await call(api.downloads.resume.bind(api.downloads), item.id);
  const found = (await call(api.downloads.search.bind(api.downloads), { id: item.id })) || [];
  if (!found[0] || (found[0].state !== "in_progress" && found[0].state !== "complete")) {
    await call(api.downloads.download.bind(api.downloads), { url: item.url });
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
    considerDownload(item, hold);
    return true;
  });
}

// ---------------------------------------------------------------------------
// Media detection (observe only)
// ---------------------------------------------------------------------------

const MEDIA_EXT_RE = /\.(m3u8|mpd|mp4|webm|mp3|m4a|flv|mkv)$/i;
const SEGMENT_EXT_RE = /\.(ts|m4s|m4f|aac|key|vtt)$/i;
const STREAM_MIMES = new Set([
  "application/vnd.apple.mpegurl",
  "application/x-mpegurl",
  "audio/mpegurl",
  "audio/x-mpegurl",
  "application/dash+xml"
]);
const SEGMENT_MIMES = new Set(["video/mp2t", "audio/mp2t", "video/iso.segment", "audio/iso.segment"]);

const mediaCache = new Map(); // tabId -> {pageUrl, items:[{url,mime,size,ts}]}
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
  const entry = stored && Array.isArray(stored.items) ? stored : { pageUrl: "", items: [] };
  mediaCache.set(tabId, entry);
  return entry;
}

async function saveTabMedia(tabId, entry) {
  mediaCache.set(tabId, entry);
  await storageSet("session", { [mediaKey(tabId)]: entry });
  updateBadge(tabId, entry.items.length);
}

async function clearTabMedia(tabId, pageUrl) {
  mediaCache.set(tabId, { pageUrl: pageUrl || "", items: [] });
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

  const mime = headerValue(details.responseHeaders, "content-type").split(";")[0].trim().toLowerCase();
  if (SEGMENT_MIMES.has(mime)) return null;

  const byMime = mime.startsWith("video/") || mime.startsWith("audio/") || STREAM_MIMES.has(mime);
  const byExt = MEDIA_EXT_RE.test(path);
  if (!byMime && !byExt) return null;
  return { url: details.url, mime, size: responseSize(details.responseHeaders) };
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
    const idx = items.findIndex((x) => x.url === media.url);
    if (idx >= 0) {
      const old = items[idx];
      items[idx] = {
        ...old,
        mime: old.mime || media.mime,
        size: media.size > 0 ? media.size : old.size
      };
    } else {
      items.push({ ...media, ts: Date.now() });
      while (items.length > MAX_MEDIA_PER_TAB) items.shift();
    }
    await saveTabMedia(tabId, { pageUrl, items });
  });
}

if (api.webRequest && api.webRequest.onResponseStarted) {
  api.webRequest.onResponseStarted.addListener(
    (details) => {
      if (details.tabId < 0) return;
      const media = classifyMedia(details);
      if (!media) return;
      recordMedia(details.tabId, media, details);
    },
    { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "other"] },
    ["responseHeaders"]
  );
}

function stripHash(url) {
  return String(url || "").split("#")[0];
}

if (api.tabs && api.tabs.onUpdated) {
  api.tabs.onUpdated.addListener((tabId, changeInfo) => {
    if (!changeInfo.url) return;
    withTabQueue(tabId, async () => {
      const entry = await loadTabMedia(tabId);
      if (stripHash(entry.pageUrl) === stripHash(changeInfo.url)) return;
      await clearTabMedia(tabId, changeInfo.url);
    });
  });
}

if (api.tabs && api.tabs.onRemoved) {
  api.tabs.onRemoved.addListener((tabId) => {
    withTabQueue(tabId, async () => {
      mediaCache.delete(tabId);
      await storageRemove("session", mediaKey(tabId));
    });
  });
}

if (api.tabs && api.tabs.onReplaced) {
  api.tabs.onReplaced.addListener((addedTabId, removedTabId) => {
    withTabQueue(removedTabId, async () => {
      mediaCache.delete(removedTabId);
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
  if (!isHttpUrl(url)) {
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
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent
  });
}

async function sendMedia(tab, title, extraUrls = [], onlyUrls = null) {
  if (!tab || !isHttpUrl(tab.url)) {
    notify(i18nMsg("errNothingToDownload"));
    return { ok: false, error: "bad-page" };
  }
  let mediaUrls = onlyUrls || (await getTabMedia(tab.id)).map((m) => ({ url: m.url, mime: m.mime || "", size: m.size || -1 }));
  for (const u of extraUrls) {
    if (isHttpUrl(u) && !mediaUrls.some((m) => m.url === u)) mediaUrls.unshift({ url: u, mime: "", size: -1 });
  }
  return sendToGdm({
    action: "media",
    pageUrl: tab.url,
    title: title || tab.title || "",
    userAgent: navigator.userAgent,
    cookies: await cookieObjectsForApp(tab.url),
    mediaUrls
  });
}

// Runs in the page: collects unique http(s) links.
function collectLinksInPage() {
  const seen = new Set();
  const out = [];
  for (const a of document.querySelectorAll("a[href]")) {
    const href = a.href;
    if (!/^https?:\/\//i.test(href)) continue;
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
    if (!l || !isHttpUrl(l.url)) continue;
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

const CONTENT_SCRIPT_MESSAGE_TYPES = new Set(["media-from-page", "link-keys", "panel-settings", "magnet-link"]);

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
      const state = await getGdmState();
      if (!state || !state.connected || (state.settings && state.settings.intercept === false)) {
        return { ok: false, intercepted: false };
      }
      const res = await sendToGdm({ action: "add", url: msg.url.trim() });
      return { ...res, intercepted: res.ok && res.accepted !== false };
    }
    case "media-from-page": {
      const tab = sender && sender.tab;
      return sendMedia(tab, msg.title, msg.videoSrc ? [msg.videoSrc] : []);
    }
    case "link-keys": {
      const targetUrl = msg.url || msg.href;
      if (isHttpUrl(targetUrl)) {
        const tabUrl = (sender && sender.tab && sender.tab.url) || "";
        linkKeys.set(targetUrl, { prevent: !!msg.prevent, force: !!msg.force, tabUrl, exp: Date.now() + 15000 });
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
      .catch((e) => sendResponse({ ok: false, error: String((e && e.message) || e) }));
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
    refreshSettings();
  });
}
