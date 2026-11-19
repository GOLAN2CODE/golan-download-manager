// Golan download manager - options page UI.
// Reuses storage.local keys ("interceptEnabled" and "gdmState") shared with popup.js and background.js.

const api = globalThis.browser ?? chrome;
const $ = (id) => document.getElementById(id);

const DEFAULT_EXTENSIONS = [
  "3gp", "7z", "aac", "ace", "aif", "apk", "arj", "asf", "avi", "bin", "bz2",
  "cab", "deb", "dmg", "doc", "docx", "epub", "exe", "flac", "flv", "gz", "gzip",
  "img", "iso", "lzh", "m4a", "m4v", "mkv", "mov", "mp3", "mp4", "mpa", "mpe",
  "mpeg", "mpg", "msi", "msix", "msu", "ogg", "ogv", "pdf", "pps", "ppt", "pptx",
  "qt", "ra", "rar", "rm", "rmvb", "rpm", "sea", "sit", "sitx", "tar", "tbz2",
  "tgz", "tif", "tiff", "torrent", "txz", "wav", "webm", "wma", "wmv", "xls",
  "xlsx", "xz", "z", "zip", "zst", "appx"
];

const DEFAULT_SETTINGS = {
  intercept: true,
  extensions: DEFAULT_EXTENSIONS,
  minSizeKB: 0,
  exceptionSites: [],
  exceptionAddresses: [],
  mediaPanel: true,
  mediaPanelMinSizeKB: 0,
  mediaPanelExceptions: [],
  menuItems: ["link", "all", "video", "image"]
};

const I18N_FALLBACK = {
  extName: "Golan Download Manager",
  optionsTitle: "Golan Extension Options",
  popupInterceptTitle: "Capture downloads from browser",
  popupInterceptDesc: "Send downloaded files to Golan instead of the browser",
  optVideoSniffingTitle: "Video sniffing on pages",
  optVideoSniffingDesc: "Detect video streams and show the download button on videos",
  optMinSizeLabel: "Minimum file size (KB)",
  optMinSizeHint: "Capture unlisted files at or above this size (0 disables size threshold)",
  unitKB: "KB",
  optExtensionsLabel: "Captured file types",
  optExtensionsHint: "File extensions separated by spaces or commas",
  optSaveBtn: "Save changes",
  optResetExtBtn: "Restore default types",
  optExcludedSitesTitle: "Excluded sites",
  optExcludedSitesHint: "Do not capture downloads or scan pages on these domains (e.g. example.com or *.example.com)",
  optSitePlaceholder: "example.com",
  optAddSiteBtn: "Add",
  optRemoveSiteBtn: "Remove",
  optSitesEmpty: "No excluded sites.",
  optToastSaved: "Settings saved ✓",
  optErrInvalidSite: "Enter a valid domain name"
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
  for (const el of document.querySelectorAll("[data-i18n-placeholder]")) {
    const key = el.getAttribute("data-i18n-placeholder");
    if (key) el.placeholder = i18nMsg(key);
  }
}

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

async function storageGet(keys) {
  if (!api.storage || !api.storage.local) return {};
  const res = await callApi(api.storage.local.get.bind(api.storage.local), keys);
  return res || {};
}

async function storageSet(items) {
  if (!api.storage || !api.storage.local) return;
  await callApi(api.storage.local.set.bind(api.storage.local), items);
}

function notifyBackgroundSync() {
  if (!api.runtime || typeof api.runtime.sendMessage !== "function") return Promise.resolve(null);
  return callApi(api.runtime.sendMessage.bind(api.runtime), { type: "sync-settings" });
}

let toastTimer = null;
function toast(text, kind = "ok") {
  const el = $("toast");
  if (!el) return;
  el.textContent = "";
  el.textContent = text;
  el.className = "toast show" + (kind ? " " + kind : "");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    el.className = "toast";
    el.textContent = "";
  }, 2400);
}

function parseExtensionsInput(raw) {
  const seen = new Set();
  const out = [];
  for (const part of String(raw || "").split(/[\s,;]+/)) {
    const clean = part.trim().toLowerCase().replace(/^\.+/, "");
    if (!clean || !/^[a-z0-9]{1,16}$/.test(clean) || seen.has(clean)) continue;
    seen.add(clean);
    out.push(clean);
  }
  return out;
}

function parseMinSizeKB(raw) {
  const n = Math.floor(Number(raw));
  if (!isFinite(n) || n < 0) return 0;
  return Math.min(n, 10485760);
}

function normalizeSiteEntry(raw) {
  let s = String(raw || "").trim().toLowerCase();
  if (!s || s.length > 255) return "";
  if (s.includes("://") && !s.includes("*")) {
    try {
      s = new URL(s).hostname.toLowerCase();
    } catch (e) {
      return "";
    }
  } else {
    s = s.replace(/^[a-z]+:\/\//i, "").split(/[/?#]/)[0];
  }
  s = s.replace(/\.$/, "");
  if (!s || /\s/.test(s) || !/[a-z0-9]/i.test(s)) return "";
  if (!/^[a-z0-9.*?-]+(?::\d+)?$/i.test(s) && !s.includes(":")) return "";
  return s;
}

function effectiveSettingsFromState(gdmState) {
  const s = (gdmState && gdmState.settings) || {};
  return {
    ...DEFAULT_SETTINGS,
    ...s,
    extensions: Array.isArray(s.extensions) ? s.extensions.slice() : DEFAULT_EXTENSIONS.slice(),
    minSizeKB: typeof s.minSizeKB === "number" && isFinite(s.minSizeKB) && s.minSizeKB >= 0 ? s.minSizeKB : 0,
    exceptionSites: Array.isArray(s.exceptionSites) ? s.exceptionSites.slice() : [],
    mediaPanel: s.mediaPanel !== false
  };
}

async function updateGdmSettings(patch) {
  const { gdmState: prev } = await storageGet("gdmState");
  const baseSettings = effectiveSettingsFromState(prev);
  const nextSettings = {
    ...baseSettings,
    ...patch
  };
  const nextState = {
    connected: Boolean(prev && prev.connected),
    version: prev && typeof prev.version === "string" ? prev.version : "",
    settings: nextSettings,
    ts: prev && prev.connected ? Date.now() : (prev && typeof prev.ts === "number" ? prev.ts : 0)
  };
  await storageSet({ gdmState: nextState });
  await notifyBackgroundSync();
  return nextState;
}

function renderSitesList(sites) {
  const list = $("optSitesList");
  const empty = $("optSitesEmpty");
  list.textContent = "";
  empty.hidden = sites.length > 0;

  for (const site of sites) {
    const li = document.createElement("li");
    li.className = "site-item";

    const hostSpan = document.createElement("span");
    hostSpan.className = "site-host";
    hostSpan.textContent = site;

    const rmText = i18nMsg("optRemoveSiteBtn");
    const rmBtn = document.createElement("button");
    rmBtn.className = "remove-btn";
    rmBtn.type = "button";
    rmBtn.textContent = rmText;
    rmBtn.ariaLabel = rmText + " " + site;
    rmBtn.addEventListener("click", async () => {
      const { gdmState } = await storageGet("gdmState");
      const cur = effectiveSettingsFromState(gdmState).exceptionSites;
      const next = cur.filter((x) => x !== site);
      await updateGdmSettings({ exceptionSites: next });
      renderSitesList(next);
      toast(i18nMsg("optToastSaved"), "ok");
    });

    li.append(hostSpan, rmBtn);
    list.appendChild(li);
  }
}

function renderAll(interceptEnabled, gdmState, { preserveFocused = false } = {}) {
  const settings = effectiveSettingsFromState(gdmState);
  $("optInterceptToggle").checked = interceptEnabled !== false;
  $("optVideoSniffingToggle").checked = settings.mediaPanel !== false;
  if (!preserveFocused || document.activeElement !== $("optMinSizeKB")) {
    $("optMinSizeKB").value = String(settings.minSizeKB);
  }
  if (!preserveFocused || document.activeElement !== $("optExtensions")) {
    $("optExtensions").value = settings.extensions.join(", ");
  }
  renderSitesList(settings.exceptionSites);
}

async function saveSizeAndExtensions() {
  const minSizeKB = parseMinSizeKB($("optMinSizeKB").value);
  const extensions = parseExtensionsInput($("optExtensions").value);
  $("optMinSizeKB").value = String(minSizeKB);
  $("optExtensions").value = extensions.join(", ");
  await updateGdmSettings({ minSizeKB, extensions });
  toast(i18nMsg("optToastSaved"), "ok");
}

async function addExcludedSite() {
  const input = $("optSiteInput");
  const site = normalizeSiteEntry(input.value);
  if (!site) {
    toast(i18nMsg("optErrInvalidSite"), "err");
    return;
  }
  const { gdmState } = await storageGet("gdmState");
  const cur = effectiveSettingsFromState(gdmState).exceptionSites;
  const exists = cur.some((x) => x.toLowerCase() === site.toLowerCase());
  const next = exists ? cur : [...cur, site];
  input.value = "";
  await updateGdmSettings({ exceptionSites: next });
  renderSitesList(next);
  toast(i18nMsg("optToastSaved"), "ok");
}

async function init() {
  applyLocale();
  const { interceptEnabled, gdmState } = await storageGet(["interceptEnabled", "gdmState"]);
  renderAll(interceptEnabled, gdmState);

  $("optInterceptToggle").addEventListener("change", async (e) => {
    await storageSet({ interceptEnabled: Boolean(e.target.checked) });
    await notifyBackgroundSync();
    toast(i18nMsg("optToastSaved"), "ok");
  });

  $("optVideoSniffingToggle").addEventListener("change", async (e) => {
    await updateGdmSettings({ mediaPanel: Boolean(e.target.checked) });
    toast(i18nMsg("optToastSaved"), "ok");
  });

  $("optMinSizeKB").addEventListener("change", saveSizeAndExtensions);
  $("optExtensions").addEventListener("change", saveSizeAndExtensions);
  $("optSaveBtn").addEventListener("click", saveSizeAndExtensions);

  $("optResetExtBtn").addEventListener("click", async () => {
    const defaults = DEFAULT_EXTENSIONS.slice();
    $("optExtensions").value = defaults.join(", ");
    await updateGdmSettings({ extensions: defaults });
    toast(i18nMsg("optToastSaved"), "ok");
  });

  $("optAddSiteBtn").addEventListener("click", addExcludedSite);
  $("optSiteInput").addEventListener("keydown", (e) => {
    if (e && e.key === "Enter") {
      e.preventDefault();
      addExcludedSite();
    }
  });

  if (api.storage && api.storage.onChanged && typeof api.storage.onChanged.addListener === "function") {
    api.storage.onChanged.addListener(async (_changes, area) => {
      if (area !== "local") return;
      const latest = await storageGet(["interceptEnabled", "gdmState"]);
      renderAll(latest.interceptEnabled, latest.gdmState, { preserveFocused: true });
    });
  }
}

init();
