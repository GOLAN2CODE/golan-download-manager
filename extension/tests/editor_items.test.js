const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const EXT_DIR = path.resolve(__dirname, "..");
const MANIFEST = JSON.parse(fs.readFileSync(path.join(EXT_DIR, "manifest.json"), "utf8"));
const BG_CODE = fs.readFileSync(path.join(EXT_DIR, "background.js"), "utf8");
const CONTENT_CODE = fs.readFileSync(path.join(EXT_DIR, "content.js"), "utf8");
const POPUP_HTML = fs.readFileSync(path.join(EXT_DIR, "popup.html"), "utf8");
const POPUP_CODE = fs.readFileSync(path.join(EXT_DIR, "popup.js"), "utf8");
const OPTIONS_HTML = fs.readFileSync(path.join(EXT_DIR, "options.html"), "utf8");
const OPTIONS_CODE = fs.readFileSync(path.join(EXT_DIR, "options.js"), "utf8");
const EN_MESSAGES = JSON.parse(fs.readFileSync(path.join(EXT_DIR, "_locales", "en", "messages.json"), "utf8"));
const AR_MESSAGES = JSON.parse(fs.readFileSync(path.join(EXT_DIR, "_locales", "ar", "messages.json"), "utf8"));

function makeEvent(options = {}) {
  const listeners = [];
  return {
    listeners,
    addListener(fn, filter, extraInfoSpec) {
      if (options.rejectExtraHeaders && Array.isArray(extraInfoSpec) && extraInfoSpec.includes("extraHeaders")) {
        throw new Error('Invalid value for argument 3. Property "extraHeaders": Value must be one of [requestHeaders].');
      }
      listeners.push({ fn, filter, extraInfoSpec });
    },
    emit(...args) {
      return listeners.map((l) => l.fn(...args));
    }
  };
}

function createFullHarness(options = {}) {
  let nowMs = options.initialNow || 1_750_000_000_000;
  const storageData = options.sharedStorage || {
    local: { ...(options.initialLocal || {}) },
    session: { ...(options.initialSession || {}) }
  };

  const events = {
    onBeforeRequest: makeEvent(),
    onSendHeaders: makeEvent({ rejectExtraHeaders: Boolean(options.firefoxWebRequest) }),
    onBeforeSendHeaders: makeEvent({ rejectExtraHeaders: Boolean(options.firefoxWebRequest) }),
    onResponseStarted: makeEvent(),
    onDownloadCreated: makeEvent(),
    onDeterminingFilename: makeEvent(),
    onDownloadChanged: makeEvent(),
    onDownloadErased: makeEvent(),
    onTabUpdated: makeEvent(),
    onTabRemoved: makeEvent(),
    onTabReplaced: makeEvent(),
    onMenuClicked: makeEvent(),
    onMessage: makeEvent(),
    onInstalled: makeEvent(),
    onStartup: makeEvent(),
    onStorageChanged: makeEvent(),
    onCommand: makeEvent()
  };

  function makeStorageArea(areaName) {
    return {
      get(keys, cb) {
        const store = storageData[areaName];
        let res = {};
        if (keys === null || keys === undefined) {
          res = { ...store };
        } else if (typeof keys === "string") {
          if (Object.prototype.hasOwnProperty.call(store, keys)) res[keys] = structuredClone(store[keys]);
        } else if (Array.isArray(keys)) {
          for (const k of keys) {
            if (Object.prototype.hasOwnProperty.call(store, k)) res[k] = structuredClone(store[k]);
          }
        } else if (typeof keys === "object") {
          for (const [k, defVal] of Object.entries(keys)) {
            res[k] = Object.prototype.hasOwnProperty.call(store, k) ? structuredClone(store[k]) : defVal;
          }
        }
        queueMicrotask(() => cb && cb(res));
      },
      set(items, cb) {
        const changes = {};
        for (const [k, v] of Object.entries(items)) {
          changes[k] = {
            oldValue: Object.prototype.hasOwnProperty.call(storageData[areaName], k)
              ? structuredClone(storageData[areaName][k])
              : undefined,
            newValue: structuredClone(v)
          };
          storageData[areaName][k] = structuredClone(v);
        }
        queueMicrotask(() => {
          if (cb) cb();
          events.onStorageChanged.emit(changes, areaName);
        });
      },
      remove(keys, cb) {
        const list = Array.isArray(keys) ? keys : [keys];
        for (const k of list) delete storageData[areaName][k];
        queueMicrotask(() => cb && cb());
      }
    };
  }

  const calls = {
    nativeMessages: [],
    paused: [],
    cancelled: [],
    erased: [],
    resumed: [],
    downloaded: [],
    notifications: [],
    badges: [],
    optionsOpened: 0
  };

  const downloadItems = new Map();
  let tabsById = options.tabsById || {};
  let activeTabs = options.activeTabs || [];
  let executeScriptImpl = options.executeScriptImpl || null;
  let nativeHandler =
    options.nativeHandler ||
    ((_host, message) => {
      if (message.action === "ping") {
        return {
          ok: true,
          version: options.gdmVersion || "2.7.0",
          settings: options.settings || {}
        };
      }
      if (message.action === "download" || message.action === "media" || message.action === "downloadAll" || message.action === "add") {
        return { ok: true, accepted: true };
      }
      return { ok: true };
    });

  let nextTimerId = 1;
  const timers = new Map();

  const chrome = {
    runtime: {
      id: "fkjplbaglahdiphiodkgidkdfkbebgfh",
      lastError: null,
      getManifest: () => ({ version: MANIFEST.version }),
      getURL: (p) => `chrome-extension://fkjplbaglahdiphiodkgidkdfkbebgfh/${p}`,
      openOptionsPage(cb) {
        calls.optionsOpened += 1;
        queueMicrotask(() => cb && cb());
      },
      sendNativeMessage(host, message, cb) {
        calls.nativeMessages.push({ host, message: structuredClone(message) });
        Promise.resolve()
          .then(() => nativeHandler(host, message))
          .then(
            (reply) => {
              if (reply && reply.__unreachable) {
                chrome.runtime.lastError = { message: "Specified native messaging host not found." };
                cb(undefined);
                chrome.runtime.lastError = null;
              } else {
                chrome.runtime.lastError = null;
                cb(reply);
              }
            },
            (err) => {
              chrome.runtime.lastError = { message: String((err && err.message) || err) };
              cb(undefined);
              chrome.runtime.lastError = null;
            }
          );
      },
      sendMessage(msg, cb) {
        events.onMessage.emit(msg, { id: "fkjplbaglahdiphiodkgidkdfkbebgfh" }, (res) => cb && cb(res));
      },
      onMessage: events.onMessage,
      onInstalled: events.onInstalled,
      onStartup: events.onStartup
    },
    storage: {
      local: makeStorageArea("local"),
      session: makeStorageArea("session"),
      onChanged: events.onStorageChanged
    },
    downloads: {
      onCreated: events.onDownloadCreated,
      ...(options.enableDeterminingFilename === false ? {} : { onDeterminingFilename: events.onDeterminingFilename }),
      onChanged: events.onDownloadChanged,
      onErased: events.onDownloadErased,
      pause(id, cb) {
        calls.paused.push(id);
        const it = downloadItems.get(id);
        if (it) it.state = "paused";
        queueMicrotask(() => cb && cb());
      },
      cancel(id, cb) {
        calls.cancelled.push(id);
        const it = downloadItems.get(id);
        if (it) it.state = "interrupted";
        queueMicrotask(() => cb && cb());
      },
      erase(query, cb) {
        calls.erased.push(structuredClone(query));
        if (query && typeof query.id === "number") downloadItems.delete(query.id);
        queueMicrotask(() => cb && cb([]));
      },
      resume(id, cb) {
        calls.resumed.push(id);
        const it = downloadItems.get(id);
        if (it) it.state = "in_progress";
        queueMicrotask(() => cb && cb());
      },
      search(query, cb) {
        const it = query && typeof query.id === "number" ? downloadItems.get(query.id) : null;
        queueMicrotask(() => cb && cb(it ? [{ ...it }] : []));
      },
      download(opts, cb) {
        calls.downloaded.push(opts);
        queueMicrotask(() => cb && cb(999));
      }
    },
    webRequest: {
      onBeforeRequest: events.onBeforeRequest,
      onSendHeaders: events.onSendHeaders,
      onBeforeSendHeaders: events.onBeforeSendHeaders,
      onResponseStarted: events.onResponseStarted
    },
    cookies: {
      getAll(_details, cb) {
        queueMicrotask(() => cb([{ name: "sid", value: "ok" }]));
      }
    },
    contextMenus: {
      removeAll(cb) {
        queueMicrotask(() => cb && cb());
      },
      create(_props, cb) {
        queueMicrotask(() => cb && cb());
      },
      onClicked: events.onMenuClicked
    },
    notifications: {
      create(id, opts, cb) {
        calls.notifications.push({ id, opts });
        queueMicrotask(() => cb && cb(id));
      }
    },
    scripting: {
      executeScript(injection, cb) {
        if (typeof executeScriptImpl === "function") {
          const out = executeScriptImpl(injection);
          queueMicrotask(() => cb(out));
          return;
        }
        queueMicrotask(() => cb([{ result: null }]));
      }
    },
    tabs: {
      get(tabId, cb) {
        const tab = tabsById[tabId];
        queueMicrotask(() => cb(tab ? structuredClone(tab) : undefined));
      },
      query(_q, cb) {
        queueMicrotask(() => cb(structuredClone(activeTabs)));
      },
      onUpdated: events.onTabUpdated,
      onRemoved: events.onTabRemoved,
      onReplaced: events.onTabReplaced
    },
    action: {
      setBadgeText(details, cb) {
        calls.badges.push({ type: "text", ...details });
        queueMicrotask(() => cb && cb());
      },
      setBadgeBackgroundColor(details, cb) {
        calls.badges.push({ type: "color", ...details });
        queueMicrotask(() => cb && cb());
      }
    },
    commands: {
      onCommand: events.onCommand
    },
    i18n: {
      getUILanguage: () => options.uiLang || "en",
      getMessage(key, subs) {
        const table = String(options.uiLang || "en").startsWith("ar") ? AR_MESSAGES : EN_MESSAGES;
        const entry = table[key] || EN_MESSAGES[key];
        if (!entry) return "";
        let out = entry.message;
        const list = Array.isArray(subs) ? subs : subs !== undefined ? [subs] : [];
        list.forEach((s, idx) => {
          out = out.replace(new RegExp("\\$" + (idx + 1), "g"), String(s));
        });
        return out;
      }
    }
  };

  const FakeDate = class extends Date {
    constructor(...args) {
      if (args.length === 0) super(nowMs);
      else super(...args);
    }
    static now() {
      return nowMs;
    }
  };

  const sandbox = {
    chrome,
    navigator: { userAgent: "GdmTest/2.6" },
    URL,
    Date: FakeDate,
    Promise,
    Set,
    Map,
    RegExp,
    JSON,
    String,
    Number,
    Boolean,
    Array,
    Object,
    Math,
    isFinite,
    parseInt,
    decodeURIComponent,
    encodeURIComponent,
    console,
    setTimeout(fn, ms, ...args) {
      const id = nextTimerId++;
      timers.set(id, { fn, ms: Number(ms) || 0, args });
      return id;
    },
    clearTimeout(id) {
      timers.delete(id);
    },
    setInterval: () => 0,
    clearInterval: () => {}
  };

  vm.createContext(sandbox);
  vm.runInContext(BG_CODE, sandbox, { filename: "background.js" });

  async function flush(turns = 16) {
    for (let i = 0; i < turns; i++) {
      await new Promise((r) => setImmediate(r));
    }
  }

  function sendMessage(msg, sender = { id: "fkjplbaglahdiphiodkgidkdfkbebgfh" }) {
    return new Promise((resolve) => {
      events.onMessage.emit(msg, sender, (res) => resolve(structuredClone(res)));
    });
  }

  async function triggerDownload(item) {
    const fullItem = {
      state: "in_progress",
      mime: "application/octet-stream",
      totalBytes: 10240,
      ...item
    };
    downloadItems.set(fullItem.id, { ...fullItem });
    events.onDownloadCreated.emit(fullItem);
    await flush();
  }

  function fireAllTimers() {
    for (const [id, entry] of [...timers.entries()]) {
      timers.delete(id);
      entry.fn(...entry.args);
    }
  }

  return {
    chrome,
    calls,
    events,
    storageData,
    downloadItems,
    timers,
    advanceTime(ms) {
      nowMs += ms;
    },
    setNativeHandler(fn) {
      nativeHandler = fn;
    },
    setActiveTabs(list) {
      activeTabs = list;
    },
    setExecuteScriptImpl(fn) {
      executeScriptImpl = fn;
    },
    flush,
    sendMessage,
    triggerDownload,
    fireAllTimers
  };
}

function makeUiDom(htmlText) {
  const elements = new Map();
  const i18nElements = [];
  const placeholderElements = [];

  const makeEl = (id = "", i18nKey = null, phKey = null) => {
    const listeners = new Map();
    const children = [];
    let textVal = "";
    const el = {
      id,
      value: "",
      placeholder: "",
      get textContent() {
        return textVal;
      },
      set textContent(v) {
        textVal = String(v ?? "");
        if (textVal === "") children.length = 0;
      },
      className: "",
      title: "",
      ariaLabel: "",
      hidden: false,
      disabled: false,
      checked: true,
      dataset: {},
      style: {},
      children,
      append(...nodes) {
        children.push(...nodes);
      },
      appendChild(node) {
        children.push(node);
        return node;
      },
      addEventListener(type, fn) {
        listeners.set(type, fn);
      },
      async dispatch(type, extra = {}) {
        const fn = listeners.get(type);
        if (fn) await fn({ target: el, preventDefault() {}, ...extra });
      },
      async click() {
        return el.dispatch("click");
      },
      getAttribute(attr) {
        if (attr === "data-i18n") return i18nKey;
        if (attr === "data-i18n-placeholder") return phKey;
        return null;
      }
    };
    if (i18nKey) i18nElements.push(el);
    if (phKey) placeholderElements.push(el);
    return el;
  };

  for (const tagMatch of htmlText.matchAll(/<([a-z0-9]+)\b([^>]*)>/gi)) {
    const attrs = tagMatch[2];
    const idMatch = /\bid="([^"]+)"/.exec(attrs);
    const i18nMatch = /\bdata-i18n="([^"]+)"/.exec(attrs);
    const phMatch = /\bdata-i18n-placeholder="([^"]+)"/.exec(attrs);
    const isHidden = /\bhidden\b/.test(attrs);
    if (idMatch || i18nMatch || phMatch) {
      const id = idMatch ? idMatch[1] : "";
      const el = makeEl(id, i18nMatch ? i18nMatch[1] : null, phMatch ? phMatch[1] : null);
      el.hidden = isHidden;
      if (id) elements.set(id, el);
    }
  }

  return {
    elements,
    documentElement: { lang: "", dir: "" },
    getElementById(id) {
      if (!elements.has(id)) elements.set(id, makeEl(id));
      return elements.get(id);
    },
    querySelectorAll(sel) {
      if (sel === "[data-i18n]") return i18nElements;
      if (sel === "[data-i18n-placeholder]") return placeholderElements;
      return [];
    },
    createElement: () => makeEl(),
    createTextNode: (t) => ({ textContent: t })
  };
}

// ---------------------------------------------------------------------------
// Item 1: Options page (options_ui in a tab, reuses shared storage keys)
// ---------------------------------------------------------------------------
test("EI-01: options_ui opens in a tab and edits capture, minSizeKB, extensions, excluded sites, and video sniffing using shared storage keys", async () => {
  assert.equal(MANIFEST.version, "2.7.0", "manifest version must not change");
  assert.deepEqual(MANIFEST.options_ui, { page: "options.html", open_in_tab: true });
  assert.deepEqual(MANIFEST.permissions, [
    "nativeMessaging",
    "downloads",
    "contextMenus",
    "cookies",
    "storage",
    "webRequest",
    "tabs",
    "scripting",
    "notifications"
  ]);
  assert.deepEqual(MANIFEST.host_permissions, ["http://*/*", "https://*/*"]);

  const h = createFullHarness({
    uiLang: "ar",
    tabsById: {
      10: { id: 10, url: "https://cinema.example.com/watch", title: "Watch" }
    }
  });
  await h.sendMessage({ type: "get-status" });
  await h.flush();

  const doc = makeUiDom(OPTIONS_HTML);
  const optSandbox = {
    chrome: h.chrome,
    document: doc,
    navigator: { language: "ar" },
    URL,
    Promise,
    Set,
    Array,
    Object,
    String,
    Number,
    Boolean,
    Math,
    RegExp,
    isFinite,
    setTimeout: () => 0,
    clearTimeout: () => {}
  };
  optSandbox.globalThis = optSandbox;
  vm.createContext(optSandbox);
  vm.runInContext(OPTIONS_CODE, optSandbox, { filename: "options.js" });
  await h.flush();

  assert.equal(doc.documentElement.dir, "rtl");
  assert.equal(doc.documentElement.lang, "ar");

  // 1) Set minSizeKB = 500 and captured file types = ".ZIP, custompkg"
  doc.getElementById("optMinSizeKB").value = "500";
  doc.getElementById("optExtensions").value = ".ZIP, custompkg, zip";
  await doc.getElementById("optSaveBtn").click();
  await h.flush();

  assert.deepEqual(h.storageData.local.gdmState.settings.extensions, ["zip", "custompkg"]);
  assert.equal(h.storageData.local.gdmState.settings.minSizeKB, 500);

  // 2) Add excluded sites (normalizing full URL -> hostname) and remove one
  doc.getElementById("optSiteInput").value = "https://SkipMe.Example.com/downloads";
  await doc.getElementById("optAddSiteBtn").click();
  await h.flush();
  doc.getElementById("optSiteInput").value = "temp-ignore.org";
  await doc.getElementById("optAddSiteBtn").click();
  await h.flush();

  assert.deepEqual(h.storageData.local.gdmState.settings.exceptionSites, ["skipme.example.com", "temp-ignore.org"]);
  assert.equal(doc.getElementById("optSitesList").children.length, 2);

  // Remove "temp-ignore.org" via its remove button
  const secondItemRemoveBtn = doc.getElementById("optSitesList").children[1].children[1];
  await secondItemRemoveBtn.click();
  await h.flush();
  assert.deepEqual(h.storageData.local.gdmState.settings.exceptionSites, ["skipme.example.com"]);

  // 3) Disable video sniffing (mediaPanel = false)
  doc.getElementById("optVideoSniffingToggle").checked = false;
  await doc.getElementById("optVideoSniffingToggle").dispatch("change");
  await h.flush();
  assert.equal(h.storageData.local.gdmState.settings.mediaPanel, false);

  // Verify NO parallel storage keys were invented
  assert.deepEqual(Object.keys(h.storageData.local).sort(), ["gdmState"]);

  // Verify background enforces all saved options even after a fresh get-status ping
  await h.sendMessage({ type: "get-status" });
  await h.flush();

  // Excluded site is not captured
  await h.triggerDownload({ id: 1, url: "https://sub.skipme.example.com/a.zip", filename: "a.zip", totalBytes: 10240 });
  // Unlisted small file (< 500 KB) is not captured
  await h.triggerDownload({ id: 2, url: "https://ok.example.com/small.bin", filename: "small.bin", totalBytes: 100 * 1024 });
  // Custom extension ("custompkg") IS captured
  await h.triggerDownload({ id: 3, url: "https://ok.example.com/pkg.custompkg", filename: "pkg.custompkg", totalBytes: 2048 });
  // Unlisted large file (>= 500 KB) IS captured
  await h.triggerDownload({ id: 4, url: "https://ok.example.com/big.bin", filename: "big.bin", totalBytes: 600 * 1024 });
  assert.deepEqual(h.calls.paused, [3, 4]);

  // Video sniffing is off -> HLS response is ignored
  h.events.onResponseStarted.emit({
    tabId: 10,
    url: "https://cdn.example.com/master.m3u8",
    responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }]
  });
  await h.flush();
  assert.equal((await h.sendMessage({ type: "get-media", tabId: 10 })).items.length, 0);

  // 4) Toggle capture OFF in options -> sets interceptEnabled = false and blocks capture
  doc.getElementById("optInterceptToggle").checked = false;
  await doc.getElementById("optInterceptToggle").dispatch("change");
  await h.flush();
  assert.equal(h.storageData.local.interceptEnabled, false);
  assert.deepEqual(Object.keys(h.storageData.local).sort(), ["gdmState", "interceptEnabled"]);

  await h.triggerDownload({ id: 5, url: "https://ok.example.com/another.zip", filename: "another.zip", totalBytes: 10240 });
  assert.deepEqual(h.calls.paused, [3, 4], "disabled capture in Options must stop download interception");
});

// ---------------------------------------------------------------------------
// Item 2: Keyboard shortcuts via commands + current state badge
// ---------------------------------------------------------------------------
test("EI-02: commands toggle capture with badge state updates and send current page link/video while ignoring incognito", async () => {
  assert.ok(MANIFEST.commands && MANIFEST.commands["toggle-capture"], "manifest must declare toggle-capture command");
  assert.ok(MANIFEST.commands && MANIFEST.commands["send-to-golan"], "manifest must declare send-to-golan command");
  assert.match(MANIFEST.commands["toggle-capture"].description, /^__MSG_.*__$/);
  assert.match(MANIFEST.commands["send-to-golan"].description, /^__MSG_.*__$/);

  const videoTab = { id: 21, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ", title: "Music Video" };
  const fileTab = { id: 22, url: "https://releases.example.com/ubuntu.iso", title: "ISO" };
  const linkTab = { id: 23, url: "https://portal.example.com/docs", title: "Docs" };
  const incogTab = { id: 24, url: "https://private.example.com/video", title: "Private", incognito: true };

  const h = createFullHarness({
    tabsById: { 21: videoTab, 22: fileTab, 23: linkTab, 24: incogTab },
    activeTabs: [videoTab]
  });
  await h.flush();

  // Initial badge shows ON
  assert.ok(h.calls.badges.some((b) => b.type === "text" && b.text === ""));

  // 1) Toggle capture OFF via command -> badge shows OFF and downloads are not captured
  h.calls.badges.length = 0;
  h.events.onCommand.emit("toggle-capture", videoTab);
  await h.flush();

  assert.equal(h.storageData.local.interceptEnabled, false);
  assert.ok(h.calls.badges.some((b) => b.type === "text" && b.text === "OFF"));
  await h.triggerDownload({ id: 201, url: "https://releases.example.com/archive.zip", filename: "archive.zip" });
  assert.deepEqual(h.calls.paused, []);

  // 2) Toggle capture back ON via command -> badge shows ON and downloads are captured
  h.calls.badges.length = 0;
  h.events.onCommand.emit("toggle-capture", videoTab);
  await h.flush();

  assert.equal(h.storageData.local.interceptEnabled, true);
  assert.ok(h.calls.badges.some((b) => b.type === "text" && b.text === ""));
  await h.triggerDownload({ id: 202, url: "https://releases.example.com/archive.zip", filename: "archive.zip" });
  assert.deepEqual(h.calls.paused, [202]);

  // 3) send-to-golan on video page -> sends action: "media"
  h.calls.nativeMessages.length = 0;
  h.events.onCommand.emit("send-to-golan", undefined); // queries activeTab (videoTab)
  await h.flush();
  assert.equal(h.calls.nativeMessages.some((m) => m.message.action === "media" && m.message.pageUrl === videoTab.url), true);

  // 4) send-to-golan on direct file URL tab -> sends action: "download"
  h.calls.nativeMessages.length = 0;
  h.events.onCommand.emit("send-to-golan", fileTab);
  await h.flush();
  assert.equal(h.calls.nativeMessages.some((m) => m.message.action === "download" && m.message.url === fileTab.url), true);

  // 5) send-to-golan when a link is focused in the page -> sends action: "download" for focusedHref
  h.setExecuteScriptImpl(() => [{ result: { focusedUrl: "https://cdn.example.com/manual.pdf", videoSrc: "" } }]);
  h.calls.nativeMessages.length = 0;
  h.events.onCommand.emit("send-to-golan", linkTab);
  await h.flush();
  assert.equal(
    h.calls.nativeMessages.some((m) => m.message.action === "download" && m.message.url === "https://cdn.example.com/manual.pdf"),
    true
  );

  // 6) send-to-golan on incognito tab -> never sends to Golan
  h.calls.nativeMessages.length = 0;
  h.events.onCommand.emit("send-to-golan", incogTab);
  await h.flush();
  assert.equal(h.calls.nativeMessages.length, 0, "incognito tab must never be sent to Golan");
});

// ---------------------------------------------------------------------------
// Item 3: Content script performance (no querySelectorAll(*), debounced
//         MutationObserver, inactive on disabled/excluded pages)
// ---------------------------------------------------------------------------
test("EI-03: content.js removes querySelectorAll(*), debounces MutationObserver, and does nothing when disabled or excluded", async () => {
  assert.doesNotMatch(CONTENT_CODE, /querySelectorAll\(\s*["']\*["']\s*\)/, "must not contain whole-document querySelectorAll(*)");

  function mountPerfPage({ pageUrl, initialLocal }) {
    const u = new URL(pageUrl);
    const selectorsQueried = [];
    const docAdded = [];
    let observerInstances = 0;
    let observerCb = null;
    let videoChecks = 0;
    const timers = new Map();
    let tid = 1;

    const storageArea = {
      get(_k, cb) {
        cb(structuredClone(initialLocal));
      },
      set() {}
    };
    const onChanged = makeEvent();

    const winStub = {
      innerWidth: 1280,
      innerHeight: 720,
      location: { href: u.href, hostname: u.hostname, pathname: u.pathname },
      document: {
        title: "Perf Page",
        hidden: false,
        fullscreenElement: null,
        documentElement: { appendChild() {} },
        createElement: () => ({
          id: "",
          style: {},
          classList: { add() {}, remove() {}, contains: () => false },
          addEventListener() {},
          setAttribute() {},
          remove() {},
          attachShadow: () => ({ innerHTML: "", querySelector: () => null })
        }),
        querySelectorAll(sel) {
          selectorsQueried.push(sel);
          if (sel === "video") videoChecks++;
          return [];
        },
        addEventListener(type) {
          docAdded.push(type);
        },
        removeEventListener() {}
      },
      chrome: {
        runtime: {
          id: "fkjplbaglahdiphiodkgidkdfkbebgfh",
          lastError: null,
          sendMessage(_msg, cb) {
            cb({ enabled: true, captureDisabled: false, siteExcluded: false });
          }
        },
        storage: { local: storageArea, onChanged }
      },
      Promise,
      WeakSet,
      Set,
      Number,
      Math,
      RegExp,
      URL,
      String,
      Array,
      Boolean,
      getComputedStyle: () => ({ visibility: "visible", display: "block", opacity: "1" }),
      addEventListener() {},
      removeEventListener() {},
      requestAnimationFrame(fn) {
        fn();
      },
      MutationObserver: class {
        constructor(cb) {
          observerInstances++;
          observerCb = cb;
        }
        observe() {}
        disconnect() {}
      },
      setTimeout(fn, ms) {
        const id = tid++;
        timers.set(id, { fn, ms });
        return id;
      },
      clearTimeout(id) {
        timers.delete(id);
      },
      setInterval: () => 1,
      clearInterval: () => {}
    };
    winStub.window = winStub;
    winStub.top = winStub;
    vm.createContext(winStub);
    vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });

    return {
      selectorsQueried,
      docAdded,
      getObserverInstances: () => observerInstances,
      getVideoChecks: () => videoChecks,
      timers,
      emitMutation: (records) => observerCb && observerCb(records),
      onChanged
    };
  }

  // 1) Disabled capture -> zero DOM queries, zero listeners, zero MutationObserver
  const disabledPage = mountPerfPage({
    pageUrl: "https://video.example.com/watch",
    initialLocal: { interceptEnabled: false }
  });
  assert.equal(disabledPage.selectorsQueried.length, 0);
  assert.equal(disabledPage.docAdded.length, 0);
  assert.equal(disabledPage.getObserverInstances(), 0);

  // 2) Excluded site -> zero DOM queries, zero listeners, zero MutationObserver
  const excludedPage = mountPerfPage({
    pageUrl: "https://sub.excluded.example.com/page",
    initialLocal: {
      interceptEnabled: true,
      gdmState: { connected: true, version: "2.7.0", settings: { intercept: true, exceptionSites: ["excluded.example.com"] } }
    }
  });
  assert.equal(excludedPage.selectorsQueried.length, 0);
  assert.equal(excludedPage.docAdded.length, 0);
  assert.equal(excludedPage.getObserverInstances(), 0);

  // 3) Active page -> never queries "*", ignores irrelevant text/span mutations, and debounces burst video mutations into 1 check
  const activePage = mountPerfPage({
    pageUrl: "https://active.example.com/watch",
    initialLocal: { interceptEnabled: true }
  });
  assert.equal(activePage.selectorsQueried.includes("*"), false, "never queries *");
  assert.equal(activePage.getObserverInstances(), 1);

  activePage.timers.clear();
  const checksBefore = activePage.getVideoChecks();

  // Irrelevant <span> / <div> mutations without video do not schedule a timer
  activePage.emitMutation([{ addedNodes: [{ tagName: "SPAN" }, { tagName: "DIV" }], removedNodes: [] }]);
  assert.equal(activePage.timers.size, 0, "irrelevant non-video mutations must not schedule checks");

  // Rapid burst of 8 <video> mutations resets debounce timer so only 1 pending timer exists
  for (let i = 0; i < 8; i++) {
    activePage.emitMutation([{ addedNodes: [{ tagName: "VIDEO" }], removedNodes: [] }]);
  }
  assert.equal(activePage.timers.size, 1, "debounced MutationObserver must keep at most 1 pending timer");
  const [, pending] = [...activePage.timers.entries()][0];
  activePage.timers.clear();
  pending.fn();
  assert.equal(activePage.getVideoChecks(), checksBefore + 1, "burst of 8 mutations must run findVideo only once");
});

// ---------------------------------------------------------------------------
// Item 4: POST tracking eviction by age & size without dropping in-flight downloads
// ---------------------------------------------------------------------------
test("EI-04: POST tracking map evicts by age and byte size without dropping an entry needed by an in-flight download", async () => {
  const h = createFullHarness({ enableDeterminingFilename: false });
  await h.sendMessage({ type: "get-status" });

  // 1) Start an in-flight POST download whose filename has not arrived yet (Firefox-style delayed Content-Disposition)
  const inFlightPostUrl = "https://reports.example.com/slow-export";
  h.events.onBeforeRequest.emit({
    requestId: "post-inflight",
    method: "POST",
    url: inFlightPostUrl,
    type: "main_frame",
    requestBody: { raw: [{ bytes: { byteLength: 20 * 1024 } }] }
  });
  await h.triggerDownload({
    id: 701,
    url: inFlightPostUrl,
    filename: "",
    mime: "application/octet-stream",
    state: "in_progress"
  });
  assert.deepEqual(h.calls.paused, []);

  // 2) Byte-size eviction (4 x 24 KB POST bodies = 96 KB > 64 KB cap, count = 5 << 200):
  //    oldest UNPINNED POST ("bulk-0") is evicted by byte size, while pinned inFlightPostUrl is preserved!
  for (let i = 0; i < 4; i++) {
    h.events.onBeforeRequest.emit({
      requestId: `bulk-${i}`,
      method: "POST",
      url: `https://api.example.com/upload/${i}`,
      type: "xmlhttprequest",
      requestBody: { raw: [{ bytes: { byteLength: 24 * 1024 } }] }
    });
  }
  h.advanceTime(1500);
  h.fireAllTimers();
  await h.flush();

  assert.equal(
    Object.prototype.hasOwnProperty.call(h.storageData.session.postUrls, "https://api.example.com/upload/0"),
    false,
    "oldest unpinned large POST must be evicted when byte budget (64 KB) is exceeded"
  );
  assert.equal(
    Object.prototype.hasOwnProperty.call(h.storageData.session.postUrls, "https://api.example.com/upload/3"),
    true,
    "newest POST within byte budget is kept"
  );
  assert.equal(
    Object.prototype.hasOwnProperty.call(h.storageData.session.postUrls, inFlightPostUrl),
    true,
    "in-flight download POST entry must survive byte-size eviction"
  );

  // 3) Count eviction (> 200 entries) AND age eviction (> 120s TTL) while download 701 is STILL in flight:
  h.advanceTime(180_000); // 180s > 120s TTL!
  for (let i = 0; i < 210; i++) {
    h.events.onBeforeRequest.emit({
      requestId: `flood-${i}`,
      method: "POST",
      url: `https://api.example.com/flood/${i}`,
      type: "xmlhttprequest"
    });
  }
  h.advanceTime(1500);
  h.fireAllTimers();
  await h.flush();

  // Now Content-Disposition filename ("report.zip") finally arrives for in-flight download 701!
  h.downloadItems.set(701, {
    id: 701,
    url: inFlightPostUrl,
    filename: "/Downloads/report.zip",
    mime: "application/zip",
    totalBytes: 65536,
    state: "in_progress"
  });
  h.events.onDownloadChanged.emit({ id: 701, filename: { previous: "", current: "/Downloads/report.zip" } });
  await h.flush();

  assert.deepEqual(h.calls.paused, [], "in-flight POST download must NOT be captured even after 180s and 210 newer POSTs");

  // 4) Once download 701 completes, its POST entry is unpinned and evicted on next prune
  h.events.onDownloadChanged.emit({ id: 701, state: { current: "complete" } });
  h.events.onBeforeRequest.emit({
    requestId: "after-complete",
    method: "POST",
    url: "https://api.example.com/after-complete",
    type: "xmlhttprequest"
  });
  h.advanceTime(1500);
  h.fireAllTimers();
  await h.flush();

  assert.equal(
    Object.prototype.hasOwnProperty.call(h.storageData.session.postUrls, inFlightPostUrl),
    false,
    "completed download POST entry must be evicted once no longer in flight"
  );
});

// ---------------------------------------------------------------------------
// Item 5: Downloads requiring an Authorization header (left to browser, never stored)
// ---------------------------------------------------------------------------
test("EI-05: downloads carrying an Authorization header are never stored and are left safely to the browser (including Firefox fallback & redirects)", async () => {
  // Use firefoxWebRequest: true to also verify graceful fallback when extraHeaders is rejected
  const h = createFullHarness({ firefoxWebRequest: true });
  await h.sendMessage({ type: "get-status" });

  const secretToken = "Bearer SECRET_JWT_TOKEN_DO_NOT_STORE_98765";
  const authUrl = "https://intranet.example.com/api/v1/export.zip";
  const redirStart = "https://intranet.example.com/api/v1/redirect-export";
  const redirTarget = "https://storage.cdn-partner.net/signed/export2.zip";

  // 1) Direct request with Authorization header
  h.events.onSendHeaders.emit({
    requestId: "auth-1",
    url: authUrl,
    requestHeaders: [{ name: "Authorization", value: secretToken }]
  });
  await h.triggerDownload({ id: 801, url: authUrl, filename: "export.zip", mime: "application/zip" });

  // 2) Redirected request whose initial hop carried Authorization
  h.events.onSendHeaders.emit({
    requestId: "auth-2",
    url: redirStart,
    requestHeaders: [{ name: "authorization", value: secretToken }]
  });
  h.events.onResponseStarted.emit({
    requestId: "auth-2",
    tabId: 1,
    type: "main_frame",
    url: redirTarget,
    responseHeaders: [{ name: "Content-Disposition", value: 'attachment; filename="export2.zip"' }]
  });
  await h.triggerDownload({ id: 802, url: redirTarget, filename: "export2.zip", mime: "application/zip" });

  assert.deepEqual(h.calls.paused, [], "Authorization-protected downloads must be left to the browser");
  assert.equal(
    h.calls.nativeMessages.filter((m) => m.message.action === "download").length,
    0,
    "Authorization-protected downloads must not be sent to Golan"
  );

  // Verify secret token and URLs were NEVER persisted to storage.local or storage.session
  const dump = JSON.stringify(h.storageData) + JSON.stringify(h.calls.nativeMessages);
  assert.equal(dump.includes("SECRET_JWT_TOKEN_DO_NOT_STORE_98765"), false, "Authorization secret must never be stored");
  assert.equal(dump.includes(authUrl), false, "Authorization URL must never be written to storage");

  // 3) Incognito and other-extension downloads remain uncaptured
  await h.triggerDownload({ id: 803, url: "https://intranet.example.com/public/incog.zip", filename: "incog.zip", incognito: true });
  await h.triggerDownload({ id: 804, url: "https://intranet.example.com/public/ext.zip", filename: "ext.zip", byExtensionId: "other-ext-id" });
  assert.deepEqual(h.calls.paused, []);

  // 4) Normal unauthenticated download on the same host is still captured normally
  await h.triggerDownload({
    id: 805,
    url: "https://intranet.example.com/public/manual.zip",
    filename: "manual.zip",
    mime: "application/zip"
  });
  assert.deepEqual(h.calls.paused, [805]);
});

// ---------------------------------------------------------------------------
// Item 6: Popup clear states (connected / not running / outdated) with 1 action button each + i18n check
// ---------------------------------------------------------------------------
test("EI-06: popup renders connected, offline, and outdated states with one action button each, and all i18n keys match AR/EN", async () => {
  let statusReply = { connected: false, version: "", outdated: false, interceptEnabled: true };
  const h = createFullHarness({
    uiLang: "en",
    tabsById: { 1: { id: 1, url: "https://example.com/", title: "Home" } },
    activeTabs: [{ id: 1, url: "https://example.com/", title: "Home" }]
  });

  const doc = makeUiDom(POPUP_HTML);
  const popupChrome = {
    ...h.chrome,
    runtime: {
      ...h.chrome.runtime,
      sendMessage(msg, cb) {
        if (msg.type === "get-status") return queueMicrotask(() => cb(structuredClone(statusReply)));
        if (msg.type === "get-media") return queueMicrotask(() => cb({ items: [] }));
        return queueMicrotask(() => cb({ ok: true }));
      }
    }
  };

  const popSandbox = {
    chrome: popupChrome,
    document: doc,
    navigator: { language: "en-US" },
    URL,
    Promise,
    Array,
    Object,
    String,
    Number,
    Boolean,
    Math,
    decodeURIComponent,
    setTimeout: () => 0,
    clearTimeout: () => {}
  };
  popSandbox.globalThis = popSandbox;
  vm.createContext(popSandbox);
  vm.runInContext(POPUP_CODE, popSandbox, { filename: "popup.js" });
  await h.flush();

  // State A: Golan not running -> only #banner visible with Retry button
  assert.equal(doc.getElementById("status").dataset.state, "off");
  assert.equal(doc.getElementById("banner").hidden, false);
  assert.equal(doc.getElementById("connectedBanner").hidden, true);
  assert.equal(doc.getElementById("outdatedBanner").hidden, true);

  // State B: Golan outdated (2.4.0 < 2.7.0) -> click Retry -> only #outdatedBanner visible with Check again button
  statusReply = { connected: true, version: "2.4.0", outdated: true, interceptEnabled: true };
  await doc.getElementById("retryBtn").click();
  await h.flush();
  assert.equal(doc.getElementById("status").dataset.state, "outdated");
  assert.equal(doc.getElementById("banner").hidden, true);
  assert.equal(doc.getElementById("connectedBanner").hidden, true);
  assert.equal(doc.getElementById("outdatedBanner").hidden, false);
  assert.match(doc.getElementById("outdatedTitle").textContent, /2\.4\.0/);

  // State C: User updates Golan to 2.7.0 and clicks Check again -> only #connectedBanner visible with Options button
  statusReply = { connected: true, version: "2.7.0", outdated: false, interceptEnabled: true };
  await doc.getElementById("checkUpdateBtn").click();
  await h.flush();
  assert.equal(doc.getElementById("status").dataset.state, "on");
  assert.equal(doc.getElementById("banner").hidden, true);
  assert.equal(doc.getElementById("outdatedBanner").hidden, true);
  assert.equal(doc.getElementById("connectedBanner").hidden, false);

  // Connected state's single action button opens Options tab
  await doc.getElementById("openOptionsBtn").click();
  await h.flush();
  assert.equal(h.calls.optionsOpened, 1);

  // Verify each of the 3 state banners in popup.html has exactly ONE <button>
  for (const bannerId of ["connectedBanner", "banner", "outdatedBanner"]) {
    const re = new RegExp(`<div[^>]*\\bid="${bannerId}"[^>]*>[\\s\\S]*?<\\/div>`, "i");
    const block = re.exec(POPUP_HTML);
    assert.ok(block, `${bannerId} block must exist in popup.html`);
    const buttons = [...block[0].matchAll(/<button\b/gi)];
    assert.equal(buttons.length, 1, `${bannerId} must contain exactly one action button`);
  }

  // Verify every i18n key across all HTML, JS, and manifest files exists in both EN and AR
  assert.deepEqual(Object.keys(AR_MESSAGES).sort(), Object.keys(EN_MESSAGES).sort());
  const allReferencedKeys = new Set();
  for (const m of JSON.stringify(MANIFEST).matchAll(/__MSG_([A-Za-z0-9_]+)__/g)) allReferencedKeys.add(m[1]);
  for (const html of [POPUP_HTML, OPTIONS_HTML]) {
    for (const m of html.matchAll(/data-i18n(?:-placeholder)?="([^"]+)"/g)) allReferencedKeys.add(m[1]);
  }
  for (const js of [BG_CODE, CONTENT_CODE, POPUP_CODE, OPTIONS_CODE]) {
    for (const m of js.matchAll(/i18nMsg\(\s*"([^"]+)"/g)) allReferencedKeys.add(m[1]);
  }
  for (const k of allReferencedKeys) {
    assert.ok(EN_MESSAGES[k] && EN_MESSAGES[k].message, `Missing EN key: ${k}`);
    assert.ok(AR_MESSAGES[k] && AR_MESSAGES[k].message, `Missing AR key: ${k}`);
  }

  const legacyRe = new RegExp("\x61dam", "i");
  for (const rel of ["options.html", "options.css", "options.js", "popup.html", "popup.css", "popup.js", "content.js", "background.js"]) {
    const src = fs.readFileSync(path.join(EXT_DIR, rel), "utf8");
    assert.doesNotMatch(src, legacyRe, `${rel} must not contain legacy identifier`);
    assert.doesNotMatch(src, /\beval\s*\(/, `${rel} must not use eval()`);
  }
});
