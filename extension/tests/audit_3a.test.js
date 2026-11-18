const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const EXT_DIR = path.resolve(__dirname, "..");
const BG_CODE = fs.readFileSync(path.join(EXT_DIR, "background.js"), "utf8");
const CONTENT_CODE = fs.readFileSync(path.join(EXT_DIR, "content.js"), "utf8");
const POPUP_CODE = fs.readFileSync(path.join(EXT_DIR, "popup.js"), "utf8");
const POPUP_HTML = fs.readFileSync(path.join(EXT_DIR, "popup.html"), "utf8");
const POPUP_CSS = fs.readFileSync(path.join(EXT_DIR, "popup.css"), "utf8");
const AR_MESSAGES = JSON.parse(fs.readFileSync(path.join(EXT_DIR, "_locales", "ar", "messages.json"), "utf8"));
const EN_MESSAGES = JSON.parse(fs.readFileSync(path.join(EXT_DIR, "_locales", "en", "messages.json"), "utf8"));

function makeEvent() {
  const listeners = [];
  return {
    listeners,
    addListener(fn, filter, extraInfoSpec) {
      listeners.push({ fn, filter, extraInfoSpec });
    },
    emit(...args) {
      return listeners.map((l) => l.fn(...args));
    }
  };
}

function createWorkerHarness(options = {}) {
  const storageData = options.sharedStorage || {
    local: { ...(options.initialLocal || {}) },
    session: { ...(options.initialSession || {}) }
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
        queueMicrotask(() => cb(res));
      },
      set(items, cb) {
        Object.assign(storageData[areaName], structuredClone(items));
        queueMicrotask(() => cb && cb());
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
    menusCreated: [],
    notifications: [],
    badges: [],
    intervalsSet: [],
    intervalsCleared: []
  };

  const downloadItems = new Map();
  let nativeHandler =
    options.nativeHandler ||
    ((_host, message) => {
      if (message.action === "ping") {
        return { ok: true, version: "2.5.1", settings: options.settings || {} };
      }
      return { ok: true, accepted: true };
    });

  let tabsById = options.tabsById || {};
  let scriptLinksByTab = options.scriptLinksByTab || {};
  let cookiesByOrigin = options.cookiesByOrigin || {};

  const events = {
    onBeforeRequest: makeEvent(),
    onResponseStarted: makeEvent(),
    onDownloadCreated: makeEvent(),
    onDeterminingFilename: makeEvent(),
    onDownloadChanged: makeEvent(),
    onTabUpdated: makeEvent(),
    onTabRemoved: makeEvent(),
    onTabReplaced: makeEvent(),
    onMenuClicked: makeEvent(),
    onMessage: makeEvent(),
    onInstalled: makeEvent(),
    onStartup: makeEvent(),
    onStorageChanged: makeEvent()
  };

  const downloadsApi = {
    onCreated: events.onDownloadCreated,
    onChanged: events.onDownloadChanged,
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
  };
  if (options.enableDeterminingFilename !== false) {
    downloadsApi.onDeterminingFilename = events.onDeterminingFilename;
  }

  const chrome = {
    runtime: {
      id: "gdm-test-extension-id",
      lastError: null,
      getURL: (p) => `chrome-extension://gdm-test-extension-id/${p}`,
      sendNativeMessage(host, message, cb) {
        calls.nativeMessages.push({ host, message: structuredClone(message) });
        Promise.resolve()
          .then(() => nativeHandler(host, message))
          .then((reply) => {
            if (reply && reply.__unreachable) {
              chrome.runtime.lastError = { message: "Specified native messaging host not found." };
              cb(undefined);
              chrome.runtime.lastError = null;
            } else {
              chrome.runtime.lastError = null;
              cb(reply);
            }
          });
      },
      sendMessage(msg, cb) {
        events.onMessage.emit(msg, { id: "gdm-test-extension-id", tab: { id: 1, url: "https://example.com/" } }, (res) => {
          if (cb) cb(res);
        });
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
    downloads: downloadsApi,
    webRequest: {
      onBeforeRequest: events.onBeforeRequest,
      onResponseStarted: events.onResponseStarted
    },
    cookies: {
      getAll(details, cb) {
        let origin = "";
        try {
          origin = new URL(details.url).origin;
        } catch (e) {
          origin = "";
        }
        queueMicrotask(() => cb(structuredClone(cookiesByOrigin[origin] || [])));
      }
    },
    contextMenus: {
      removeAll(cb) {
        calls.menusCreated.length = 0;
        queueMicrotask(() => cb && cb());
      },
      create(props, cb) {
        calls.menusCreated.push(structuredClone(props));
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
        const tabId = injection && injection.target && injection.target.tabId;
        if (typeof options.executeScriptImpl === "function") {
          queueMicrotask(() => cb(options.executeScriptImpl(injection)));
          return;
        }
        queueMicrotask(() => cb([{ result: structuredClone(scriptLinksByTab[tabId] || []) }]));
      }
    },
    tabs: {
      get(tabId, cb) {
        const tab = tabsById[tabId];
        queueMicrotask(() => cb(tab ? structuredClone(tab) : undefined));
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
    }
  };

  let nextIntervalId = 1;
  const sandbox = {
    chrome,
    navigator: { userAgent: "GdmAuditUA/2.5" },
    URL,
    Date,
    Promise,
    Set,
    Map,
    RegExp,
    JSON,
    String,
    Number,
    Array,
    Object,
    Math,
    Boolean,
    isFinite,
    encodeURIComponent,
    decodeURIComponent,
    console,
    setTimeout: (fn, ms, ...args) => {
      const t = setTimeout(fn, ms, ...args);
      if (t && typeof t.unref === "function") t.unref();
      return t;
    },
    clearTimeout,
    setInterval: (fn, ms) => {
      const id = nextIntervalId++;
      calls.intervalsSet.push({ id, ms, fn });
      return id;
    },
    clearInterval: (id) => {
      calls.intervalsCleared.push(id);
    }
  };

  vm.createContext(sandbox);
  vm.runInContext(BG_CODE, sandbox, { filename: "background.js" });

  async function flush(turns = 16) {
    for (let i = 0; i < turns; i++) {
      await new Promise((r) => setImmediate(r));
    }
  }

  function sendMessage(msg, sender = {}) {
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

  return {
    sandbox,
    chrome,
    calls,
    events,
    storageData,
    downloadItems,
    setNativeHandler(fn) {
      nativeHandler = fn;
    },
    flush,
    sendMessage,
    triggerDownload
  };
}

// ---------------------------------------------------------------------------
// A3A-01: MV3 service worker sleep preserves POST urls, redirected POSTs,
//         link-keys modifiers, download-attr hints, and DRM license flags
// ---------------------------------------------------------------------------
test("A3A-01: MV3 service worker sleep preserves POST forms, redirected POSTs, link-keys, download-attr, and DRM flags", async () => {
  const sharedStorage = { local: {}, session: {} };
  const tabsById = {
    40: { id: 40, url: "https://vod.example.com/watch/40", title: "DRM Episode" }
  };

  // Worker #1 records transient state before going to sleep
  const w1 = createWorkerHarness({
    sharedStorage,
    tabsById,
    settings: { extensions: ["zip", "exe"] }
  });
  await w1.sendMessage({ type: "get-status" });

  // 1) POST form + 302 redirect sharing the same requestId
  w1.events.onBeforeRequest.emit({
    requestId: "req-post-1",
    method: "POST",
    url: "https://reports.example.com/export",
    type: "main_frame"
  });
  w1.events.onResponseStarted.emit({
    requestId: "req-post-1",
    tabId: 1,
    type: "main_frame",
    url: "https://cdn.reports.example.com/tmp/generated.zip",
    responseHeaders: []
  });

  // 2) Alt+Click prevent & Alt+Ctrl+Click force
  await w1.sendMessage(
    { type: "link-keys", url: "https://files.example.com/bypass.zip", prevent: true, force: false },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://files.example.com/" } }
  );
  await w1.sendMessage(
    { type: "link-keys", url: "https://cdn.other.net/table.csv", prevent: false, force: true },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://portal.example.com/exports" } }
  );

  // 3) <a download="Setup.exe"> on an extensionless URL
  await w1.sendMessage(
    { type: "download-attr", url: "https://cdn.example.com/blob-id-9988", filename: "Setup.exe" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://app.example.com/releases" } }
  );

  // 4) Widevine license URL sniffed on tab 40
  w1.events.onResponseStarted.emit({
    tabId: 40,
    url: "https://license.vod.example.com/widevine/license",
    responseHeaders: [{ name: "Content-Type", value: "application/octet-stream" }]
  });
  await w1.flush();

  // Simulate MV3 service worker sleep -> cold-start Worker #2 with fresh in-memory Maps
  const w2 = createWorkerHarness({
    sharedStorage,
    tabsById,
    settings: { extensions: ["zip", "exe"] }
  });
  await w2.flush();

  // POST original URL & redirected POST URL must NOT be intercepted after wakeup
  await w2.triggerDownload({ id: 11, url: "https://reports.example.com/export", filename: "generated.zip" });
  await w2.triggerDownload({ id: 12, url: "https://cdn.reports.example.com/tmp/generated.zip", filename: "generated.zip" });
  assert.deepEqual(w2.calls.paused, [], "POST and redirected POST downloads must stay in browser after SW wakeup");

  // Alt+Click prevent must NOT be intercepted after wakeup
  await w2.triggerDownload({ id: 13, url: "https://files.example.com/bypass.zip", filename: "bypass.zip" });
  assert.deepEqual(w2.calls.paused, [], "Alt+Click prevent must survive SW wakeup");

  // Alt+Ctrl+Click force on unlisted .csv on CDN host IS captured after wakeup and forwards pageUrl
  await w2.triggerDownload({ id: 14, url: "https://cdn.other.net/table.csv", filename: "table.csv", mime: "text/csv" });
  assert.deepEqual(w2.calls.paused, [14]);

  // <a download="Setup.exe"> on extensionless URL IS captured after wakeup with Setup.exe filename
  await w2.triggerDownload({ id: 15, url: "https://cdn.example.com/blob-id-9988", filename: "blob-id-9988" });
  assert.deepEqual(w2.calls.paused, [14, 15]);

  const dlMsgs = w2.calls.nativeMessages.filter((m) => m.message.action === "download").map((m) => m.message);
  assert.equal(dlMsgs.length, 2);
  assert.equal(dlMsgs[0].url, "https://cdn.other.net/table.csv");
  assert.equal(dlMsgs[0].pageUrl, "https://portal.example.com/exports");
  assert.equal(dlMsgs[1].url, "https://cdn.example.com/blob-id-9988");
  assert.equal(dlMsgs[1].filename, "Setup.exe");

  // DRM license flag on tab 40 survives SW wakeup
  const drmRes = await w2.sendMessage(
    { type: "media-from-page", title: "DRM Episode", videoSrc: "", frameUrl: "", drmProtected: false },
    { id: "gdm-test-extension-id", tab: tabsById[40] }
  );
  assert.equal(drmRes.ok, false);
  assert.equal(drmRes.error, "drm");
});

// ---------------------------------------------------------------------------
// A3A-02: MV3 service worker keepalive, port recovery, onStartup menus, and
//         transient wakeup sendMessage retry in content.js and popup.js
// ---------------------------------------------------------------------------
test("A3A-02: MV3 keepalive runs during active port/hold, onStartup recreates menus, and transient wakeup errors retry once", async () => {
  let resolvePortMsg;
  const h = createWorkerHarness();
  let connectAttempts = 0;
  h.chrome.runtime.connectNative = (host) => {
    connectAttempts++;
    if (connectAttempts === 1) throw new Error("Host temporarily unavailable");
    const port = {
      host,
      onMessage: makeEvent(),
      onDisconnect: makeEvent(),
      postMessage(msg) {
        resolvePortMsg = (body) => port.onMessage.emit({ ...body, _rid: msg._rid });
      }
    };
    return port;
  };

  // First call: connectNative throws -> falls back to sendNativeMessage -> resets portRefusedAt to 0
  const st1 = await h.sendMessage({ type: "get-status" });
  assert.equal(st1.connected, true);
  assert.equal(connectAttempts, 1);

  // Second call: persistent port immediately reopens without waiting 30s, and starts 20s keepalive interval
  const initialIntervals = h.calls.intervalsSet.length;
  const pendingStatus = h.sendMessage({ type: "get-status" });
  await h.flush();
  assert.equal(connectAttempts, 2, "persistent port should reopen immediately after successful fallback");
  assert.equal(h.calls.intervalsSet.length, initialIntervals + 1, "keepalive interval must start while waiting on port");
  const kaId = h.calls.intervalsSet.at(-1).id;
  assert.equal(h.calls.intervalsSet.at(-1).ms, 20000);

  resolvePortMsg({ ok: true, version: "2.5.1", settings: {} });
  await pendingStatus;
  assert.ok(h.calls.intervalsCleared.includes(kaId), "keepalive interval must clear as soon as port reply resolves");

  // runtime.onStartup must recreate context menus (needed for Firefox MV3 event pages)
  h.calls.menusCreated.length = 0;
  h.events.onStartup.emit();
  await h.flush();
  assert.equal(h.calls.menusCreated.length, 4, "onStartup must recreate context menus");

  // content.js transient wakeup error ("The message port closed before a response was received.") retries once
  let contentSendCount = 0;
  const contentDocListeners = new Map();
  const contentChrome = {
    runtime: {
      id: "gdm-test-extension-id",
      lastError: null,
      sendMessage(msg, cb) {
        if (msg.type === "panel-settings") return cb({ enabled: true });
        contentSendCount++;
        if (contentSendCount === 1) {
          contentChrome.runtime.lastError = { message: "The message port closed before a response was received." };
          cb(undefined);
          contentChrome.runtime.lastError = null;
          return;
        }
        cb({ ok: true });
      }
    },
    storage: h.chrome.storage
  };
  const winStub = {
    innerWidth: 1280,
    innerHeight: 720,
    location: { hostname: "example.com", pathname: "/", href: "https://example.com/" },
    document: {
      title: "Page",
      documentElement: { appendChild() {} },
      querySelectorAll: () => [],
      addEventListener(type, fn) {
        if (!contentDocListeners.has(type)) contentDocListeners.set(type, []);
        contentDocListeners.get(type).push(fn);
      }
    },
    chrome: contentChrome,
    Promise,
    WeakSet,
    Number,
    Math,
    addEventListener() {},
    requestAnimationFrame() {},
    MutationObserver: class { observe() {} },
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    clearInterval: () => {}
  };
  winStub.window = winStub;
  vm.createContext(winStub);
  vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });
  await h.flush();

  for (const fn of contentDocListeners.get("mousedown") || []) {
    fn({
      isTrusted: true,
      altKey: true,
      ctrlKey: false,
      button: 0,
      target: { closest: (sel) => (sel === "a[href]" ? { href: "https://example.com/a.zip" } : null) }
    });
  }
  await h.flush();
  assert.equal(contentSendCount, 2, "content.js sendMsg must retry once on transient MV3 port-closed error");
});

// ---------------------------------------------------------------------------
// A3A-03: Stale offline magnet-link re-ping + double-click duplicate guard
// ---------------------------------------------------------------------------
test("A3A-03: magnet-link refreshes stale offline gdmState (>5s) and content.js prevents double-click duplicate magnets", async () => {
  let online = true;
  let addDelayResolve = null;
  const h = createWorkerHarness({
    initialLocal: {
      gdmState: { connected: false, version: "2.5.1", settings: { intercept: true }, ts: Date.now() - 10000 }
    },
    nativeHandler(_host, msg) {
      if (msg.action === "ping") {
        return online ? { ok: true, version: "2.5.1", settings: { intercept: true } } : { __unreachable: true };
      }
      if (msg.action === "add") {
        return new Promise((r) => {
          addDelayResolve = () => r({ ok: true, accepted: true });
        });
      }
      return { ok: true };
    }
  });
  await h.flush();

  const docListeners = new Map();
  const winStub = {
    innerWidth: 1280,
    innerHeight: 720,
    location: { hostname: "torrents.example.com", pathname: "/", href: "https://torrents.example.com/" },
    document: {
      title: "Torrents",
      documentElement: { appendChild() {} },
      querySelectorAll: () => [],
      addEventListener(type, fn) {
        if (!docListeners.has(type)) docListeners.set(type, []);
        docListeners.get(type).push(fn);
      }
    },
    chrome: h.chrome,
    Promise,
    WeakSet,
    Set,
    Number,
    Math,
    addEventListener() {},
    requestAnimationFrame() {},
    MutationObserver: class { observe() {} },
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    clearInterval: () => {}
  };
  winStub.window = winStub;
  vm.createContext(winStub);
  vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });
  await h.flush();

  const magnetHref = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=GolanTest";
  const anchor = {
    href: magnetHref,
    getAttribute: (k) => (k === "href" ? magnetHref : null),
    closest(sel) {
      if (sel.includes("magnet:")) return this;
      return null;
    }
  };

  // Rapid double-click while first magnet message is still waiting on Golan startup
  for (let click = 0; click < 2; click++) {
    for (const fn of docListeners.get("click") || []) {
      fn({
        isTrusted: true,
        button: 0,
        altKey: false,
        ctrlKey: false,
        defaultPrevented: false,
        target: anchor,
        preventDefault() {},
        stopPropagation() {}
      });
    }
  }
  await h.flush();

  assert.equal(
    h.calls.nativeMessages.filter((m) => m.message.action === "add").length,
    1,
    "stale offline state must re-ping Golan and double-click must send only one 'add' message"
  );
  addDelayResolve();
  await h.flush();
});

// ---------------------------------------------------------------------------
// A3A-04: <a download>, JS a.click(), S3/Azure disposition redirects, split
//         archives, FTP/FTPS, Firefox onChanged, and newly synced file types
// ---------------------------------------------------------------------------
test("A3A-04: captures JS <a download>, S3/Azure redirects, split archives, FTP/FTPS, Firefox onChanged, and synced file types while leaving blob:/data: to browser", async () => {
  const h = createWorkerHarness({
    tabsById: {
      9: { id: 9, url: "https://mirror.example.org/pub/", title: "Mirror" }
    },
    executeScriptImpl(injection) {
      const fakeDoc = {
        querySelectorAll: () => [
          { href: "https://mirror.example.org/pub/readme.pdf", textContent: "PDF" },
          { href: "ftp://ftp.mirror.example.org/pub/linux.iso", textContent: "Linux ISO" },
          { href: "ftps://ftp.mirror.example.org/pub/checksums.gz", textContent: "Checksums" }
        ]
      };
      const fn = new vm.Script("(" + injection.func.toString() + ")()");
      return [{ result: fn.runInNewContext({ document: fakeDoc }) }];
    }
  });
  await h.sendMessage({ type: "get-status" });

  // Mount content.js to test programmatic JS a.click() (isTrusted: false) with download="CustomApp.exe"
  const docListeners = new Map();
  const winStub = {
    innerWidth: 1280,
    innerHeight: 720,
    location: { hostname: "app.example.com", pathname: "/", href: "https://app.example.com/" },
    document: {
      title: "App",
      documentElement: { appendChild() {} },
      querySelectorAll: () => [],
      addEventListener(type, fn) {
        if (!docListeners.has(type)) docListeners.set(type, []);
        docListeners.get(type).push(fn);
      }
    },
    chrome: h.chrome,
    Promise,
    WeakSet,
    Set,
    Number,
    Math,
    addEventListener() {},
    requestAnimationFrame() {},
    MutationObserver: class { observe() {} },
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    clearInterval: () => {}
  };
  winStub.window = winStub;
  vm.createContext(winStub);
  vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });
  await h.flush();

  // 1) Trusted <a download="CustomApp.exe"> on same-site cross-origin extensionless URL
  const jsUrl = "https://cdn.example.com/objects/99887766";
  for (const fn of docListeners.get("click") || []) {
    fn({
      isTrusted: true,
      button: 0,
      defaultPrevented: false,
      target: {
        closest: (sel) =>
          sel === "a[href]"
            ? {
                href: jsUrl,
                getAttribute: (attr) => (attr === "download" ? "CustomApp.exe" : attr === "href" ? jsUrl : null)
              }
            : null
      }
    });
  }
  await h.flush();
  await h.triggerDownload({ id: 401, url: jsUrl, filename: "99887766" }); // Chrome stripped cross-origin download attr

  // 2) onCreated MIME match (filename: "") enriched by onDeterminingFilename with real Content-Disposition filename & size
  const item402 = {
    id: 402,
    url: "https://billing.example.com/api/pdf?id=77",
    filename: "",
    mime: "application/pdf",
    state: "in_progress"
  };
  h.downloadItems.set(402, { ...item402 });
  h.events.onDownloadCreated.emit(item402);
  h.events.onDeterminingFilename.emit(
    { ...item402, filename: "C:\\Users\\HP\\Downloads\\Invoice_Nov_2026.pdf", totalBytes: 262144 },
    () => {}
  );
  await h.flush();

  // 3) S3 / GitHub signed redirect URL with response-content-disposition query parameter
  await h.triggerDownload({
    id: 403,
    url: "https://releases.example.com/latest",
    finalUrl:
      "https://objects.cdn.example.net/asset/12345?response-content-disposition=attachment%3B%20filename%3D%22Golan_Installer.msi%22",
    filename: ""
  });

  // 4) Split archive (.7z.001), newly synced .torrent / .docx / .xz, and FTP download
  await h.triggerDownload({ id: 404, url: "https://cdn.example.com/backup.7z.001", filename: "backup.7z.001" });
  await h.triggerDownload({ id: 405, url: "https://cdn.example.com/ubuntu.torrent", filename: "ubuntu.torrent" });
  await h.triggerDownload({ id: 406, url: "https://cdn.example.com/guide.docx", filename: "guide.docx" });
  await h.triggerDownload({ id: 407, url: "ftp://ftp.mirror.example.org/pub/linux.iso", filename: "linux.iso" });

  // 5) blob: and data: downloads in onDownloadCreated must NOT be captured (stay in browser)
  await h.triggerDownload({ id: 408, url: "blob:https://app.example.com/1234-5678", filename: "export.zip" });
  await h.triggerDownload({ id: 409, url: "data:application/zip;base64,UEsDBAoAAAAA", filename: "inline.zip" });

  assert.deepEqual(h.calls.paused, [401, 402, 403, 404, 405, 406, 407]);
  const sent = h.calls.nativeMessages.filter((m) => m.message.action === "download").map((m) => m.message);
  assert.equal(sent.length, 7);
  assert.equal(sent[0].filename, "CustomApp.exe");
  assert.equal(sent[1].filename, "Invoice_Nov_2026.pdf");
  assert.equal(sent[1].fileSize, 262144);
  assert.equal(sent[2].filename, "Golan_Installer.msi");
  assert.equal(sent[3].filename, "backup.7z.001");
  assert.equal(sent[4].filename, "ubuntu.torrent");
  assert.equal(sent[5].filename, "guide.docx");
  assert.equal(sent[6].url, "ftp://ftp.mirror.example.org/pub/linux.iso");

  // 6) downloadAllLinks includes ftp:// and ftps:// links alongside https://
  const allRes = await h.sendMessage({ type: "download-all", tabId: 9 });
  assert.equal(allRes.ok, true);
  assert.equal(allRes.count, 3);

  // 7) Firefox (no onDeterminingFilename): extensionless URL captured when filename arrives via downloads.onChanged
  const ff = createWorkerHarness({ enableDeterminingFilename: false });
  await ff.sendMessage({ type: "get-status" });
  await ff.triggerDownload({ id: 501, url: "https://example.org/dl?id=501", filename: "", mime: "application/octet-stream" });
  assert.deepEqual(ff.calls.paused, [], "not paused before filename is known in Firefox");

  ff.downloadItems.set(501, {
    id: 501,
    url: "https://example.org/dl?id=501",
    filename: "/home/user/Downloads/package.deb",
    mime: "application/octet-stream",
    totalBytes: 81920,
    state: "in_progress"
  });
  ff.events.onDownloadChanged.emit({ id: 501, filename: { previous: "", current: "/home/user/Downloads/package.deb" } });
  await ff.flush();
  assert.deepEqual(ff.calls.paused, [501], "Firefox onChanged must capture once Content-Disposition filename is set");
});

// ---------------------------------------------------------------------------
// A3A-05: Alt+Ctrl+Click force-link & ccTLD / IP exception-bypass hardening
// ---------------------------------------------------------------------------
test("A3A-05: Alt+Ctrl+Click force-link hands inline CSV to Golan while blocking .co.uk/.com.sa and IP cross-site exception bypass", async () => {
  const h = createWorkerHarness({
    settings: {
      intercept: true,
      extensions: ["zip"],
      exceptionSites: ["bank.co.uk", "portal.com.sa", "192.168.1.10"]
    }
  });
  await h.sendMessage({ type: "get-status" });

  // Direct Alt+Ctrl+Click on an inline CSV link on a normal site -> intercepted via force-link
  const csvRes = await h.sendMessage(
    { type: "force-link", url: "https://cdn.data.org/report.csv", filename: "report.csv" },
    { id: "gdm-test-extension-id", tab: { id: 2, url: "https://app.example.com/dashboard" } }
  );
  assert.equal(csvRes.intercepted, true);

  // Cross-site force-link from evil.co.uk -> bank.co.uk MUST NOT bypass exceptionSites
  const ukRes = await h.sendMessage(
    { type: "force-link", url: "https://online.bank.co.uk/statement.zip", filename: "" },
    { id: "gdm-test-extension-id", tab: { id: 3, url: "https://evil.co.uk/phish" } }
  );
  assert.equal(ukRes.intercepted, false, "evil.co.uk must NOT be treated as same-site with bank.co.uk");

  // Cross-site link-keys from evil.com.sa -> portal.com.sa and 10.0.1.10 -> 192.168.1.10 MUST NOT bypass exceptionSites
  await h.sendMessage(
    { type: "link-keys", url: "https://portal.com.sa/secret.zip", prevent: false, force: true },
    { id: "gdm-test-extension-id", tab: { id: 4, url: "https://attacker.com.sa/" } }
  );
  await h.triggerDownload({
    id: 601,
    url: "https://portal.com.sa/secret.zip",
    referrer: "https://attacker.com.sa/",
    filename: "secret.zip"
  });

  await h.sendMessage(
    { type: "link-keys", url: "http://192.168.1.10/router.bin", prevent: false, force: true },
    { id: "gdm-test-extension-id", tab: { id: 5, url: "http://10.0.1.10/" } }
  );
  await h.triggerDownload({
    id: 602,
    url: "http://192.168.1.10/router.bin",
    referrer: "http://10.0.1.10/",
    filename: "router.bin"
  });

  assert.deepEqual(h.calls.paused, [], "ccTLD and IP cross-site force must never bypass exceptionSites");
  const dlMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "download");
  assert.equal(dlMsgs.length, 1);
  assert.equal(dlMsgs[0].message.url, "https://cdn.data.org/report.csv");
});

// ---------------------------------------------------------------------------
// A3A-06: Duplicate simultaneous downloads & cache-busted media entries
// ---------------------------------------------------------------------------
test("A3A-06: simultaneous duplicate browser downloads send only one message to Golan and cache-busted streams do not inflate badge", async () => {
  const h = createWorkerHarness({
    tabsById: {
      15: { id: 15, url: "https://live.example.com/channel", title: "Live" }
    }
  });
  await h.sendMessage({ type: "get-status" });

  // Simulate <a href="setup.exe" onclick="location.href='setup.exe'"> firing two DownloadItems at once
  const url = "https://cdn.example.com/releases/GolanSetup.exe";
  const d1 = { id: 801, url, filename: "GolanSetup.exe", mime: "application/x-msdownload", state: "in_progress" };
  const d2 = { id: 802, url, filename: "GolanSetup (1).exe", mime: "application/x-msdownload", state: "in_progress" };
  h.downloadItems.set(801, { ...d1 });
  h.downloadItems.set(802, { ...d2 });
  h.events.onDownloadCreated.emit(d1);
  h.events.onDownloadCreated.emit(d2);
  await h.flush();

  const dlMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "download");
  assert.equal(dlMsgs.length, 1, "must send only ONE download message to Golan for simultaneous duplicate items");
  assert.deepEqual(h.calls.cancelled.sort(), [801, 802], "both browser download copies must be cancelled");
  assert.equal(h.calls.erased.length, 2, "both browser download copies must be erased");

  // Simulate live HLS player polling playlist.m3u8 with changing _ts / rnd cache-busters
  for (let i = 1; i <= 5; i++) {
    h.events.onResponseStarted.emit({
      tabId: 15,
      url: `https://edge.live.example.com/hls/playlist.m3u8?token=abc&_ts=${1000 + i}&rnd=${i}`,
      responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }]
    });
  }
  await h.flush();

  const media = await h.sendMessage({ type: "get-media", tabId: 15 });
  assert.equal(media.items.length, 1, "cache-busted HLS manifest polls must collapse into 1 item in popup & badge");
});

// ---------------------------------------------------------------------------
// A3A-07: content.js shutdown() cleans up MutationObserver & event listeners,
//         and floating close button has accessible aria-label
// ---------------------------------------------------------------------------
test("A3A-07: content.js shutdown() disconnects MutationObserver and removes listeners, and close button sets aria-label", async () => {
  let disconnected = 0;
  const docAdded = [];
  const docRemoved = [];
  const winAdded = [];
  const winRemoved = [];
  let checkFn = null;
  let closeBtnAria = "";

  const chromeStub = {
    runtime: {
      id: "gdm-test-extension-id",
      lastError: null,
      sendMessage(_msg, cb) {
        cb({ enabled: true });
      }
    },
    storage: {
      local: { get: (_k, cb) => cb({}), set: (_v, cb) => cb && cb() },
      onChanged: { addListener() {} }
    }
  };

  const makeEl = () => ({
    id: "",
    title: "",
    textContent: "",
    ariaLabel: "",
    offsetWidth: 160,
    isConnected: true,
    style: {},
    classList: { add() {}, remove() {}, contains: () => false },
    addEventListener() {},
    setAttribute(k, v) {
      if (k === "aria-label") closeBtnAria = v;
    },
    remove() {
      this.isConnected = false;
    },
    attachShadow() {
      const wrap = makeEl();
      const main = makeEl();
      const label = makeEl();
      const close = makeEl();
      return {
        innerHTML: "",
        querySelector(sel) {
          if (sel === ".wrap") return wrap;
          if (sel === ".main") return main;
          if (sel === ".label") return label;
          if (sel === ".close") return close;
          return null;
        }
      };
    }
  });

  const videoNode = {
    isConnected: true,
    getBoundingClientRect: () => ({ top: 20, left: 20, right: 660, bottom: 380, width: 640, height: 360 })
  };

  const winStub = {
    innerWidth: 1280,
    innerHeight: 720,
    location: { hostname: "video.example.com", pathname: "/watch", href: "https://video.example.com/watch" },
    document: {
      title: "Video",
      hidden: false,
      fullscreenElement: null,
      documentElement: { appendChild() {} },
      createElement: () => makeEl(),
      querySelectorAll: (sel) => (sel === "video" ? [videoNode] : []),
      addEventListener(type) { docAdded.push(type); },
      removeEventListener(type) { docRemoved.push(type); }
    },
    chrome: chromeStub,
    Promise,
    WeakSet,
    Set,
    Number,
    Math,
    getComputedStyle: () => ({ visibility: "visible", display: "block", opacity: "1" }),
    addEventListener(type) { winAdded.push(type); },
    removeEventListener(type) { winRemoved.push(type); },
    requestAnimationFrame(fn) { fn(); },
    MutationObserver: class {
      observe() {}
      disconnect() { disconnected++; }
    },
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval(fn) {
      checkFn = fn;
      return 77;
    },
    clearInterval: () => {}
  };
  winStub.window = winStub;
  vm.createContext(winStub);
  vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });

  assert.equal(closeBtnAria, "Hide button on this site", "close button must expose aria-label");

  // Simulate extension reload/unload (runtime.id becomes undefined) -> check() triggers shutdown()
  chromeStub.runtime.id = undefined;
  checkFn();

  assert.equal(disconnected, 1, "MutationObserver must be disconnected on shutdown");
  for (const ev of ["encrypted", "fullscreenchange", "visibilitychange", "yt-navigate-finish"]) {
    assert.ok(docRemoved.includes(ev), `document listener '${ev}' must be removed on shutdown`);
  }
  for (const ev of ["scroll", "resize", "popstate"]) {
    assert.ok(winRemoved.includes(ev), `window listener '${ev}' must be removed on shutdown`);
  }
});

// ---------------------------------------------------------------------------
// A3A-08: Popup offline state & Retry button, error sanitization, RTL CSS, and
//         per-item accessible names
// ---------------------------------------------------------------------------
test("A3A-08: popup shows clear offline instructions + working Retry button, sanitizes raw exceptions, and sets RTL & accessible names", async () => {
  assert.match(POPUP_HTML, /id="banner"[^>]*role="alert"/, "offline banner must have role=alert");
  assert.match(POPUP_HTML, /id="retryBtn"/, "offline banner must include Retry button");
  assert.match(POPUP_CSS, /html\[dir="rtl"\]\s*\.media-name\s*\{\s*text-align:\s*right;/, "RTL media filenames must right-align");
  assert.match(POPUP_CSS, /button:focus-visible/, "buttons must have visible keyboard focus style");

  let connected = false;
  let nextDownloadItemReply = { ok: true, accepted: false };
  let nextAllLinksReply = {
    ok: false,
    connected: true,
    error: "System.NullReferenceException: Object reference not set\n   at GDM.App.Foo()"
  };

  const elements = new Map();
  const i18nElements = [];
  const makeEl = (id = "", i18nKey = null) => {
    const listeners = new Map();
    const children = [];
    const el = {
      id,
      textContent: "",
      className: "",
      title: "",
      ariaLabel: "",
      hidden: id === "banner" || id === "unhideBtn",
      disabled: false,
      checked: true,
      dataset: {},
      style: {},
      children,
      append(...nodes) { children.push(...nodes); },
      appendChild(node) { children.push(node); return node; },
      addEventListener(type, fn) { listeners.set(type, fn); },
      async click() {
        const fn = listeners.get("click");
        if (fn) await fn({ target: el });
      },
      getAttribute: (k) => (k === "data-i18n" ? i18nKey : null)
    };
    if (i18nKey) i18nElements.push(el);
    return el;
  };

  for (const [id, key] of [
    ["statusText", "statusChecking"],
    ["bannerTitle", "popupOfflineBanner"],
    ["bannerHelp", "popupOfflineHelp"],
    ["retryBtn", "btnRetry"],
    ["mediaPageBtnLabel", "popupDownloadPageVideo"],
    ["allLinksBtn", "popupDownloadAllLinks"]
  ]) {
    elements.set(id, makeEl(id, key));
  }

  const doc = {
    documentElement: { lang: "", dir: "" },
    getElementById(id) {
      if (!elements.has(id)) elements.set(id, makeEl(id));
      return elements.get(id);
    },
    querySelectorAll(sel) {
      return sel === "[data-i18n]" ? i18nElements : [];
    },
    createElement: () => makeEl(),
    createTextNode: (t) => ({ textContent: t })
  };

  const chromeStub = {
    runtime: {
      lastError: null,
      sendMessage(msg, cb) {
        let res = { ok: true };
        if (msg.type === "get-status") res = { connected, version: "2.5.1", interceptEnabled: true };
        else if (msg.type === "get-media") {
          res = {
            items: [{ url: "https://cdn.example.com/api/play?stream=episode_1080p.m3u8&sig=1", mime: "application/x-mpegurl", size: -1 }]
          };
        } else if (msg.type === "download-item") res = nextDownloadItemReply;
        else if (msg.type === "download-all") res = nextAllLinksReply;
        else if (msg.type === "media-page") res = { ok: true, connected: true, drm: true };
        queueMicrotask(() => cb(res));
      }
    },
    tabs: {
      query(_q, cb) {
        queueMicrotask(() => cb([{ id: 7, url: "https://watch.example.com/v/1", title: "Watch" }]));
      }
    },
    storage: {
      local: {
        get(_k, cb) { queueMicrotask(() => cb({})); },
        set(_v, cb) { queueMicrotask(() => cb && cb()); }
      }
    },
    i18n: {
      getUILanguage: () => "ar",
      getMessage(key, subs) {
        const entry = AR_MESSAGES[key] || EN_MESSAGES[key];
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

  const popupSandbox = {
    chrome: chromeStub,
    document: doc,
    navigator: { language: "ar" },
    URL,
    Promise,
    Array,
    Object,
    String,
    Number,
    decodeURIComponent,
    setTimeout: () => 0,
    clearTimeout: () => {}
  };
  popupSandbox.globalThis = popupSandbox;
  vm.createContext(popupSandbox);
  vm.runInContext(POPUP_CODE, popupSandbox, { filename: "popup.js" });

  for (let i = 0; i < 10; i++) await new Promise((r) => setImmediate(r));

  // 1) Offline state: banner visible with Arabic guidance + Retry button
  assert.equal(doc.documentElement.dir, "rtl");
  assert.equal(doc.getElementById("banner").hidden, false, "offline banner must be visible when Golan is not running");
  assert.equal(doc.getElementById("status").dataset.state, "off");
  assert.match(doc.getElementById("bannerHelp").textContent, /تكامل المتصفح/);

  // 2) Start Golan and click Retry -> banner hides, status turns 'on', connected toast shown
  connected = true;
  await doc.getElementById("retryBtn").click();
  assert.equal(doc.getElementById("banner").hidden, true, "Retry button must hide offline banner once Golan connects");
  assert.equal(doc.getElementById("status").dataset.state, "on");
  assert.equal(doc.getElementById("toast").textContent, AR_MESSAGES.toastConnected.message);

  // 3) Media list extracts query stream filename and sets accessible ariaLabel on Download button
  const li = doc.getElementById("mediaList").children[0];
  const infoDiv = li.children[0];
  const itemBtn = li.children[1];
  assert.equal(infoDiv.children[0].textContent, "episode_1080p.m3u8");
  assert.equal(itemBtn.ariaLabel, `${AR_MESSAGES.btnDownload.message} episode_1080p.m3u8`);

  // 4) accepted: false shows clear declined message instead of false success
  await itemBtn.click();
  assert.equal(doc.getElementById("toast").textContent, AR_MESSAGES.errDeclinedByGdm.message);

  // 5) Raw .NET exception text is never exposed in toast
  await doc.getElementById("allLinksBtn").click();
  assert.doesNotMatch(doc.getElementById("toast").textContent, /NullReferenceException|System\./);
  assert.equal(doc.getElementById("toast").textContent, AR_MESSAGES.errTryAgain.message);

  // 6) DRM media-page result shows DRM error instead of 'Video sent to Golan'
  await doc.getElementById("mediaPageBtn").click();
  assert.equal(doc.getElementById("toast").textContent, AR_MESSAGES.errDrmProtected.message);
});
