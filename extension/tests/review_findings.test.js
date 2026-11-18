const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const EXT_DIR = path.resolve(__dirname, "..");
const BG_CODE = fs.readFileSync(path.join(EXT_DIR, "background.js"), "utf8");
const CONTENT_CODE = fs.readFileSync(path.join(EXT_DIR, "content.js"), "utf8");
const MANIFEST = JSON.parse(fs.readFileSync(path.join(EXT_DIR, "manifest.json"), "utf8"));

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

function createReviewHarness(options = {}) {
  const storageData = options.sharedStorage || {
    local: { ...(options.initialLocal || {}) },
    session: { ...(options.initialSession || {}) }
  };

  const storageWrites = {
    local: [],
    session: []
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
        storageWrites[areaName].push(structuredClone(items));
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
    searched: [],
    downloaded: [],
    menusCreated: [],
    notifications: [],
    badges: []
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
        const tab = options.defaultSenderTab || { id: 1, url: "https://example.com/" };
        events.onMessage.emit(msg, { id: "gdm-test-extension-id", tab }, (res) => {
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
    downloads: {
      onCreated: events.onDownloadCreated,
      onDeterminingFilename: events.onDeterminingFilename,
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
        if (it && it.state === "paused") it.state = "in_progress";
        queueMicrotask(() => cb && cb());
      },
      search(query, cb) {
        calls.searched.push(query);
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

  let nowMs = options.initialNow || 1_700_000_000_000;
  const FakeDate = class extends Date {
    constructor(...args) {
      if (args.length === 0) super(nowMs);
      else super(...args);
    }
    static now() {
      return nowMs;
    }
  };

  let nextTimerId = 1;
  const timers = new Map();
  const intervals = new Map();

  const sandbox = {
    chrome,
    navigator: { userAgent: "GdmReviewUA/2.5" },
    URL,
    Date: FakeDate,
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
      const id = nextTimerId++;
      timers.set(id, { id, fn, ms, args });
      return id;
    },
    clearTimeout: (id) => {
      timers.delete(id);
    },
    setInterval: (fn, ms) => {
      const id = nextTimerId++;
      intervals.set(id, { id, fn, ms });
      return id;
    },
    clearInterval: (id) => {
      intervals.delete(id);
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

  function fireTimersByDelay(ms) {
    for (const [id, entry] of [...timers.entries()]) {
      if (entry.ms === ms) {
        timers.delete(id);
        entry.fn(...entry.args);
      }
    }
  }

  function fireAllTimers() {
    for (const [id, entry] of [...timers.entries()]) {
      timers.delete(id);
      entry.fn(...entry.args);
    }
  }

  return {
    sandbox,
    chrome,
    calls,
    events,
    storageData,
    storageWrites,
    downloadItems,
    timers,
    intervals,
    advanceTime(deltaMs) {
      nowMs += deltaMs;
    },
    setNativeHandler(fn) {
      nativeHandler = fn;
    },
    flush,
    sendMessage,
    triggerDownload,
    fireTimersByDelay,
    fireAllTimers
  };
}

// ---------------------------------------------------------------------------
// Finding 1: Late reply after 45s hold release
// ---------------------------------------------------------------------------
test("RF-01: late reply after 45s release does not erase completed download on accept, and does not restart user-cancelled download on decline", async () => {
  const pendingReplies = new Map();
  const h = createReviewHarness({
    nativeHandler(_host, msg) {
      if (msg.action === "ping") return { ok: true, version: "2.5.1", settings: {} };
      if (msg.action === "download") {
        return new Promise((resolve) => {
          pendingReplies.set(msg.url, resolve);
        });
      }
      return { ok: true };
    }
  });
  await h.sendMessage({ type: "get-status" });

  // 1a) Released at 45s -> completes in browser -> late accept MUST NOT erase or cancel it
  await h.triggerDownload({ id: 101, url: "https://cdn.example.com/done.zip", filename: "done.zip" });
  h.fireTimersByDelay(45000);
  await h.flush();
  assert.deepEqual(h.calls.resumed, [101]);
  h.downloadItems.set(101, { id: 101, url: "https://cdn.example.com/done.zip", filename: "done.zip", state: "complete" });
  pendingReplies.get("https://cdn.example.com/done.zip")({ ok: true, accepted: true });
  await h.flush();
  assert.deepEqual(h.calls.cancelled, [], "completed download must not be cancelled on late accept");
  assert.deepEqual(h.calls.erased, [], "completed download must not be erased on late accept");

  // 1b) Released at 45s -> still in_progress in browser -> late accept cancels and erases browser copy
  await h.triggerDownload({ id: 102, url: "https://cdn.example.com/big.iso", filename: "big.iso" });
  h.fireTimersByDelay(45000);
  await h.flush();
  assert.equal(h.downloadItems.get(102).state, "in_progress");
  pendingReplies.get("https://cdn.example.com/big.iso")({ ok: true, accepted: true });
  await h.flush();
  assert.deepEqual(h.calls.cancelled, [102]);
  assert.deepEqual(h.calls.erased, [{ id: 102 }]);

  // 1c) Released at 45s -> user cancels in browser (USER_CANCELED) -> late decline MUST NOT restart it
  await h.triggerDownload({ id: 103, url: "https://cdn.example.com/cancelled.zip", filename: "cancelled.zip" });
  h.fireTimersByDelay(45000);
  await h.flush();
  h.downloadItems.set(103, {
    id: 103,
    url: "https://cdn.example.com/cancelled.zip",
    filename: "cancelled.zip",
    state: "interrupted",
    error: "USER_CANCELED"
  });
  pendingReplies.get("https://cdn.example.com/cancelled.zip")({ ok: true, accepted: false });
  await h.flush();
  assert.deepEqual(h.calls.downloaded, [], "user-cancelled download must not be restarted on late decline");

  // 1d) Released at 45s -> user cancels and removes from history -> late decline MUST NOT restart it
  await h.triggerDownload({ id: 104, url: "https://cdn.example.com/removed.zip", filename: "removed.zip" });
  h.fireTimersByDelay(45000);
  await h.flush();
  h.downloadItems.delete(104);
  pendingReplies.get("https://cdn.example.com/removed.zip")({ ok: true, accepted: false });
  await h.flush();
  assert.deepEqual(h.calls.downloaded, [], "removed download must not be restarted on late decline");
});

// ---------------------------------------------------------------------------
// Finding 2: sendNative port post vs fallback & one-shot timeout
// ---------------------------------------------------------------------------
test("RF-02: sendNative falls back to one-shot only when nothing was posted on port, and times out hung one-shot calls (5s ping, 60s others)", async () => {
  const h = createReviewHarness();
  let mode = "disconnect-after-post";
  let lastPort = null;

  h.chrome.runtime.connectNative = (host) => {
    if (mode === "no-port") return null;
    const port = {
      host,
      onMessage: makeEvent(),
      onDisconnect: makeEvent(),
      postMessage() {
        if (mode === "post-throws") throw new Error("Port closed");
      }
    };
    lastPort = port;
    return port;
  };

  // 2a) Disconnect AFTER postMessage succeeded -> must NOT resend via sendNativeMessage
  const p1 = h.sendMessage({ type: "get-status" });
  await h.flush(4);
  lastPort.onDisconnect.emit(lastPort);
  const st1 = await p1;
  assert.equal(st1.connected, false);
  assert.equal(h.calls.nativeMessages.length, 0, "must not resend via sendNativeMessage after port disconnect post-send");

  // 2b) Timeout AFTER postMessage succeeded -> must NOT resend via sendNativeMessage
  h.advanceTime(31000); // expire portRefusedAt
  mode = "hang-port";
  const p2 = h.sendMessage({ type: "get-status" });
  await h.flush(4);
  h.fireTimersByDelay(5000);
  const st2 = await p2;
  assert.equal(st2.connected, false);
  assert.equal(h.calls.nativeMessages.length, 0, "must not resend via sendNativeMessage after port timeout");

  // 2c) postMessage throws -> falls back to sendNativeMessage
  h.advanceTime(31000);
  mode = "post-throws";
  const st3 = await h.sendMessage({ type: "get-status" });
  assert.equal(st3.connected, true);
  assert.equal(h.calls.nativeMessages.length, 1);

  // 2d) Hung one-shot sendNativeMessage times out at 5s for ping and 60s for others, unblocking refreshPromise
  mode = "no-port";
  h.setNativeHandler(() => new Promise(() => {})); // hangs forever

  const hungPing = h.sendMessage({ type: "get-status" });
  await h.flush(4);
  assert.ok([...h.timers.values()].some((t) => t.ms === 5000), "one-shot ping must set a 5s timeout");
  h.fireTimersByDelay(5000);
  const hungPingRes = await hungPing;
  assert.equal(hungPingRes.connected, false);

  // Verify refreshPromise was cleared and a subsequent ping succeeds once native host recovers
  h.setNativeHandler((_host, msg) =>
    msg.action === "ping" ? { ok: true, version: "2.5.1", settings: {} } : new Promise(() => {})
  );
  const recoveredPing = await h.sendMessage({ type: "get-status" });
  assert.equal(recoveredPing.connected, true, "refreshPromise must recover after timed-out one-shot ping");

  // Non-ping one-shot call sets a 60s timeout
  const hungDl = h.sendMessage(
    { type: "force-link", url: "https://example.com/item.bin", filename: "item.bin" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://example.com/" } }
  );
  await h.flush(4);
  assert.ok([...h.timers.values()].some((t) => t.ms === 60000), "non-ping one-shot call must set a 60s timeout");
  h.fireTimersByDelay(60000);
  const hungDlRes = await hungDl;
  assert.equal(hungDlRes.intercepted, false);
});

// ---------------------------------------------------------------------------
// Finding 3: Sub-frame X button hides only in memory and does not write hiddenHosts
// ---------------------------------------------------------------------------
test("RF-03: clicking X inside an iframe hides button in memory only and does not write hiddenHosts, whereas top frame writes hiddenHosts", async () => {
  const h = createReviewHarness();

  function mountFrame({ url, isSubFrame }) {
    const u = new URL(url);
    const makeNode = () => {
      const classes = new Set();
      const listeners = new Map();
      return {
        id: "",
        title: "",
        textContent: "",
        ariaLabel: "",
        offsetWidth: 160,
        isConnected: true,
        style: {},
        classList: {
          add: (c) => classes.add(c),
          remove: (c) => classes.delete(c),
          contains: (c) => classes.has(c)
        },
        setAttribute() {},
        remove() { this.isConnected = false; },
        addEventListener(type, fn) {
          if (!listeners.has(type)) listeners.set(type, []);
          listeners.get(type).push(fn);
        },
        clickTrusted() {
          for (const fn of listeners.get("click") || []) {
            fn({ isTrusted: true, preventDefault() {}, stopPropagation() {} });
          }
        }
      };
    };

    const wrap = makeNode();
    const main = makeNode();
    const label = makeNode();
    const close = makeNode();
    const hostEl = {
      ...makeNode(),
      attachShadow: () => ({
        innerHTML: "",
        querySelector(sel) {
          if (sel === ".wrap") return wrap;
          if (sel === ".main") return main;
          if (sel === ".label") return label;
          if (sel === ".close") return close;
          return null;
        }
      })
    };
    const videoEl = {
      isConnected: true,
      getBoundingClientRect: () => ({ top: 10, left: 10, right: 650, bottom: 370, width: 640, height: 360 })
    };

    let checkFn = null;
    const winStub = {
      innerWidth: 1280,
      innerHeight: 720,
      location: { hostname: u.hostname, pathname: u.pathname, href: u.href },
      document: {
        title: "Frame",
        hidden: false,
        fullscreenElement: null,
        documentElement: { appendChild() { hostEl.isConnected = true; } },
        createElement: () => hostEl,
        querySelectorAll: (sel) => (sel === "video" ? [videoEl] : []),
        addEventListener() {},
        removeEventListener() {}
      },
      chrome: h.chrome,
      Promise,
      WeakSet,
      Set,
      Number,
      Math,
      getComputedStyle: () => ({ visibility: "visible", display: "block", opacity: "1" }),
      addEventListener() {},
      removeEventListener() {},
      requestAnimationFrame(fn) { fn(); },
      MutationObserver: class { observe() {} disconnect() {} },
      setTimeout: () => 0,
      clearTimeout: () => {},
      setInterval(fn) { checkFn = fn; return 1; },
      clearInterval: () => {}
    };
    winStub.window = winStub;
    winStub.top = isSubFrame ? { location: { href: "https://top.example.com/" } } : winStub;
    vm.createContext(winStub);
    vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });
    return { wrap, close, triggerCheck: () => checkFn && checkFn() };
  }

  const iframe = mountFrame({ url: "https://player.embed-cdn.net/embed/99", isSubFrame: true });
  await h.flush();
  assert.equal(iframe.wrap.classList.contains("show"), true);

  iframe.close.clickTrusted();
  await h.flush();
  assert.equal(iframe.wrap.classList.contains("show"), false, "iframe button must hide in memory");
  iframe.triggerCheck();
  await h.flush();
  assert.equal(iframe.wrap.classList.contains("show"), false, "iframe button must stay hidden for current page");
  assert.equal(h.storageData.local.hiddenHosts, undefined, "sub-frame hide must not write hiddenHosts");

  const top = mountFrame({ url: "https://top.example.com/watch", isSubFrame: false });
  await h.flush();
  top.close.clickTrusted();
  await h.flush();
  assert.deepEqual(h.storageData.local.hiddenHosts, { "top.example.com": true });
});

// ---------------------------------------------------------------------------
// Finding 4: Declined force-link adds URL to skip list before responding
// ---------------------------------------------------------------------------
test("RF-04: declined Alt+Ctrl force-link calls addSkip(targetUrl) before responding so fallback navigation is not captured a second time", async () => {
  let downloadCalls = 0;
  const h = createReviewHarness({
    nativeHandler(_host, msg) {
      if (msg.action === "ping") return { ok: true, version: "2.5.1", settings: { extensions: ["zip"] } };
      if (msg.action === "download") {
        downloadCalls++;
        return { ok: true, accepted: false };
      }
      return { ok: true };
    }
  });
  await h.sendMessage({ type: "get-status" });

  const targetUrl = "https://example.com/files/archive.zip";
  const sender = { id: "gdm-test-extension-id", tab: { id: 1, url: "https://example.com/downloads" } };

  // Alt+Ctrl mousedown records link-keys { force: true }
  await h.sendMessage({ type: "link-keys", url: targetUrl, prevent: false, force: true }, sender);
  // Alt+Ctrl click sends force-link -> declined by GDM
  const res = await h.sendMessage({ type: "force-link", url: targetUrl, filename: "" }, sender);
  assert.equal(res.intercepted, false);
  assert.equal(downloadCalls, 1);

  // Content script navigates to targetUrl -> browser starts download -> must be skipped!
  await h.triggerDownload({ id: 401, url: targetUrl, referrer: sender.tab.url, filename: "archive.zip" });
  assert.equal(downloadCalls, 1, "fallback browser download after declined force-link must not be sent to GDM a second time");
  assert.deepEqual(h.calls.paused, []);
});

// ---------------------------------------------------------------------------
// Finding 5: Untrusted download-attr ignored + eviction & caps (200 / 4096 / 255)
// ---------------------------------------------------------------------------
test("RF-05: content.js ignores untrusted download-attr events and background.js prunes & caps downloadAttrs, linkKeys, and skipUrls", async () => {
  const h = createReviewHarness();
  await h.sendMessage({ type: "get-status" });

  const docListeners = new Map();
  const winStub = {
    innerWidth: 1280,
    innerHeight: 720,
    location: { hostname: "example.com", pathname: "/", href: "https://example.com/" },
    document: {
      title: "Page",
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
  winStub.top = winStub;
  vm.createContext(winStub);
  vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });
  await h.flush();

  const spoofUrl = "https://example.com/untrusted";
  for (const fn of docListeners.get("click") || []) {
    fn({
      isTrusted: false,
      button: 0,
      defaultPrevented: false,
      target: {
        closest: (sel) =>
          sel === "a[href]"
            ? { href: spoofUrl, getAttribute: (k) => (k === "download" ? "Spoof.exe" : spoofUrl) }
            : null
      }
    });
  }
  await h.flush();
  assert.equal(h.storageData.session.downloadAttrs, undefined, "untrusted click must not record download-attr");

  // URL > 4096 ignored; filename capped at 255; tabUrl capped at 4096
  const senderHugeTab = {
    id: "gdm-test-extension-id",
    tab: { id: 1, url: "https://example.com/" + "t".repeat(5000) }
  };
  const overLimitUrl = "https://example.com/" + "u".repeat(4100);
  await h.sendMessage({ type: "download-attr", url: overLimitUrl, filename: "ok.zip" }, senderHugeTab);
  assert.equal(h.storageData.session.downloadAttrs, undefined, "URL longer than 4096 must not be stored");

  const validUrl = "https://example.com/valid-dl";
  const longName = "n".repeat(320) + ".zip";
  await h.sendMessage({ type: "download-attr", url: validUrl, filename: longName }, senderHugeTab);
  assert.equal(h.storageData.session.downloadAttrs[validUrl].name.length, 255);
  assert.equal(h.storageData.session.downloadAttrs[validUrl].tabUrl.length, 4096);

  // Expire validUrl and verify pruning on next insert
  h.advanceTime(35000);
  await h.sendMessage({ type: "download-attr", url: "https://example.com/fresh", filename: "f.zip" }, senderHugeTab);
  assert.equal(Object.prototype.hasOwnProperty.call(h.storageData.session.downloadAttrs, validUrl), false, "expired downloadAttrs must be pruned on insert");

  // Cap downloadAttrs, linkKeys, and skipUrls at 200 entries each
  for (let i = 0; i < 210; i++) {
    await h.sendMessage({ type: "download-attr", url: `https://example.com/da/${i}`, filename: `f${i}.zip` }, senderHugeTab);
    await h.sendMessage({ type: "link-keys", url: `https://example.com/lk/${i}`, prevent: true }, senderHugeTab);
  }
  h.setNativeHandler((_host, msg) =>
    msg.action === "ping" ? { ok: true, version: "2.5.1", settings: {} } : { ok: true, accepted: false }
  );
  for (let i = 0; i < 210; i++) {
    await h.sendMessage({ type: "force-link", url: `https://example.com/sk/${i}`, filename: "" }, senderHugeTab);
  }

  assert.equal(Object.keys(h.storageData.session.downloadAttrs).length, 200);
  assert.equal(Object.keys(h.storageData.session.linkKeys).length, 200);
  assert.equal(Object.keys(h.storageData.session.skipUrls).length, 200);
  assert.equal(Object.prototype.hasOwnProperty.call(h.storageData.session.downloadAttrs, "https://example.com/da/0"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(h.storageData.session.downloadAttrs, "https://example.com/da/209"), true);
  assert.equal(Object.prototype.hasOwnProperty.call(h.storageData.session.linkKeys, "https://example.com/lk/0"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(h.storageData.session.linkKeys, "https://example.com/lk/209"), true);
  assert.equal(Object.prototype.hasOwnProperty.call(h.storageData.session.skipUrls, "https://example.com/sk/0"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(h.storageData.session.skipUrls, "https://example.com/sk/209"), true);
});

// ---------------------------------------------------------------------------
// Finding 6: recordPostUrl prunes, caps at 200, and coalesces storage.session writes (<= 1/s)
// ---------------------------------------------------------------------------
test("RF-06: recordPostUrl prunes expired entries, caps at 200 entries, and writes storage.session at most once per second", async () => {
  const h = createReviewHarness();
  await h.flush();
  h.storageWrites.session.length = 0;

  // Emit 215 POST URLs across multiple ticks within < 1 second
  for (let i = 0; i < 215; i++) {
    h.events.onBeforeRequest.emit({
      requestId: `r-${i}`,
      method: "POST",
      url: `https://example.com/post/${i}`,
      type: "xmlhttprequest"
    });
    if (i === 0 || i === 50 || i === 150) {
      h.advanceTime(100);
      await h.flush(4);
    }
  }
  await h.flush();

  const postWritesInFirstSecond = h.storageWrites.session.filter((w) => Object.prototype.hasOwnProperty.call(w, "postUrls"));
  assert.equal(postWritesInFirstSecond.length, 1, "must write postUrls to storage.session at most once per second");

  // Advance past 1s window and fire coalesced trailing timer
  h.advanceTime(1000);
  h.fireAllTimers();
  await h.flush();

  const postWritesAfterFlush = h.storageWrites.session.filter((w) => Object.prototype.hasOwnProperty.call(w, "postUrls"));
  assert.equal(postWritesAfterFlush.length, 2);
  const storedPosts = h.storageData.session.postUrls;
  assert.equal(Object.keys(storedPosts).length, 200, "postUrls must be capped at 200 entries");
  assert.equal(Object.prototype.hasOwnProperty.call(storedPosts, "https://example.com/post/0"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(storedPosts, "https://example.com/post/214"), true);

  // Advance past 120s expiry and record one new POST -> expired entries pruned inside recordPostUrl
  h.advanceTime(130000);
  h.events.onBeforeRequest.emit({
    requestId: "r-fresh",
    method: "POST",
    url: "https://example.com/post/fresh",
    type: "main_frame"
  });
  await h.flush();
  assert.deepEqual(Object.keys(h.storageData.session.postUrls), ["https://example.com/post/fresh"]);
});

// ---------------------------------------------------------------------------
// Finding 7: refreshSettings writes gdmState only when connected, version, or settings changed
// ---------------------------------------------------------------------------
test("RF-07: refreshSettings writes gdmState to storage.local only when connected, version, or settings actually changed", async () => {
  let replyState = { ok: true, version: "2.5.1", settings: { minSizeKB: 0, extensions: ["zip"] } };
  const h = createReviewHarness({
    nativeHandler(_host, msg) {
      if (msg.action === "ping") return replyState;
      return { ok: true };
    }
  });
  await h.flush();
  h.storageWrites.local.length = 0;

  const gdmStateWrites = () => h.storageWrites.local.filter((w) => Object.prototype.hasOwnProperty.call(w, "gdmState")).length;

  await h.sendMessage({ type: "get-status" });
  assert.equal(gdmStateWrites(), 1, "initial refresh writes gdmState");

  h.advanceTime(60000);
  await h.sendMessage({ type: "get-status" });
  h.advanceTime(60000);
  await h.sendMessage({ type: "get-status" });
  assert.equal(gdmStateWrites(), 1, "unchanged ping replies must not rewrite gdmState");

  replyState = { ok: true, version: "2.5.2", settings: { minSizeKB: 0, extensions: ["zip"] } };
  await h.sendMessage({ type: "get-status" });
  assert.equal(gdmStateWrites(), 2, "version change writes gdmState");

  replyState = { ok: true, version: "2.5.2", settings: { minSizeKB: 128, extensions: ["zip"] } };
  await h.sendMessage({ type: "get-status" });
  assert.equal(gdmStateWrites(), 3, "settings change writes gdmState");

  replyState = { __unreachable: true };
  await h.sendMessage({ type: "get-status" });
  assert.equal(gdmStateWrites(), 4, "connected->disconnected writes gdmState");

  h.advanceTime(60000);
  await h.sendMessage({ type: "get-status" });
  assert.equal(gdmStateWrites(), 4, "staying disconnected must not rewrite gdmState");
});

// ---------------------------------------------------------------------------
// Finding 8: DRM regex tests hostname + pathname only, drops bare "wvls", clears on reload/navigate
// ---------------------------------------------------------------------------
test("RF-08: DRM check ignores query strings and bare 'wvls', and clears tab DRM flag on reload or navigation", async () => {
  const tab = { id: 88, url: "https://cinema.example.com/watch/1", title: "Show" };
  const h = createReviewHarness({
    tabsById: { 88: tab }
  });
  await h.sendMessage({ type: "get-status" });

  // 1) Query-string DRM keywords and bare "wvls" in host/path must NOT flag tab as DRM
  h.events.onResponseStarted.emit({
    tabId: 88,
    url: "https://wvls.example.com/abwvls/clip.mp4?drm=widevine&lic=playready#fairplay",
    responseHeaders: [{ name: "Content-Type", value: "video/mp4" }, { name: "Content-Length", value: "2097152" }]
  });
  await h.flush();

  const okRes = await h.sendMessage(
    { type: "media-from-page", title: "Show", videoSrc: "", frameUrl: "", drmProtected: false },
    { id: "gdm-test-extension-id", tab }
  );
  assert.equal(okRes.ok, true, "query-string widevine and bare wvls must not block media");

  // 2) Real Widevine license path sets DRM flag
  h.events.onResponseStarted.emit({
    tabId: 88,
    url: "https://lic.cinema.example.com/widevine/license",
    responseHeaders: [{ name: "Content-Type", value: "application/octet-stream" }]
  });
  await h.flush();

  const drmRes = await h.sendMessage(
    { type: "media-from-page", title: "Show", videoSrc: "", frameUrl: "", drmProtected: false },
    { id: "gdm-test-extension-id", tab }
  );
  assert.equal(drmRes.error, "drm");

  // 3) Same-URL tab reload (status: "loading" without url change) clears DRM flag
  h.events.onTabUpdated.emit(88, { status: "loading" }, tab);
  await h.flush();

  const afterReload = await h.sendMessage(
    { type: "media-from-page", title: "Show", videoSrc: "https://cdn.example.com/free.mp4", frameUrl: "", drmProtected: false },
    { id: "gdm-test-extension-id", tab }
  );
  assert.equal(afterReload.ok, true, "tab reload must clear DRM flag");

  // 4) Set DRM again, then navigate to a new URL -> clears DRM flag
  h.events.onResponseStarted.emit({
    tabId: 88,
    url: "https://lic.cinema.example.com/playready/rightsmanager.asmx",
    responseHeaders: [{ name: "Content-Type", value: "text/xml" }]
  });
  await h.flush();
  assert.equal(
    (await h.sendMessage({ type: "media-from-page", title: "Show", videoSrc: "", frameUrl: "", drmProtected: false }, { id: "gdm-test-extension-id", tab })).error,
    "drm"
  );

  const nextTab = { id: 88, url: "https://cinema.example.com/watch/2", title: "Show 2" };
  h.events.onTabUpdated.emit(88, { url: nextTab.url }, nextTab);
  await h.flush();

  const afterNav = await h.sendMessage(
    { type: "media-from-page", title: "Show 2", videoSrc: "", frameUrl: "", drmProtected: false },
    { id: "gdm-test-extension-id", tab: nextTab }
  );
  assert.equal(afterNav.ok, true, "tab navigation must clear DRM flag");
});

// ---------------------------------------------------------------------------
// Finding 9: Cross-origin <a download> cannot change file extension
// ---------------------------------------------------------------------------
test("RF-09: cross-origin download attribute cannot change file extension, while same-extension or same-site names are honored", async () => {
  const h = createReviewHarness({
    settings: { extensions: ["zip", "pdf", "exe"] }
  });
  await h.sendMessage({ type: "get-status" });

  // 9a) Cross-site page trying to rename .pdf -> .exe keeps browser's "report.pdf"
  await h.sendMessage(
    { type: "download-attr", url: "https://cdn.trusted.org/docs/report.pdf", filename: "Update.exe" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://evil.attacker.net/page" } }
  );
  await h.triggerDownload({ id: 901, url: "https://cdn.trusted.org/docs/report.pdf", filename: "report.pdf" });

  // 9b) Cross-site page trying to add .exe to extensionless URL keeps browser's "998877"
  await h.sendMessage(
    { type: "download-attr", url: "https://cdn.trusted.org/blobs/998877", filename: "Installer.exe" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://evil.attacker.net/page" } }
  );
  await h.triggerDownload({ id: 902, url: "https://cdn.trusted.org/blobs/998877", filename: "998877" });

  // 9c) Cross-site page keeping the SAME .zip extension IS allowed to customize basename
  await h.sendMessage(
    { type: "download-attr", url: "https://cdn.trusted.org/blobs/12345.zip", filename: "Friendly_Name.zip" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://blog.other.net/post" } }
  );
  await h.triggerDownload({ id: 903, url: "https://cdn.trusted.org/blobs/12345.zip", filename: "12345.zip" });

  // 9d) Same-site page (app.trusted.org -> cdn.trusted.org) CAN set "Setup.exe" on extensionless URL
  await h.sendMessage(
    { type: "download-attr", url: "https://cdn.trusted.org/blobs/554433", filename: "Setup.exe" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://app.trusted.org/download" } }
  );
  await h.triggerDownload({ id: 904, url: "https://cdn.trusted.org/blobs/554433", filename: "554433" });

  const sent = h.calls.nativeMessages.filter((m) => m.message.action === "download").map((m) => m.message);
  assert.equal(sent.length, 4);
  assert.equal(sent[0].filename, "report.pdf", "cross-site .pdf -> .exe must keep browser's filename");
  assert.equal(sent[1].filename, "998877", "cross-site extensionless -> .exe must keep browser's filename");
  assert.equal(sent[2].filename, "Friendly_Name.zip", "cross-site .zip -> .zip custom name is allowed");
  assert.equal(sent[3].filename, "Setup.exe", "same-site custom filename with extension is allowed");
});

// ---------------------------------------------------------------------------
// Finding 10: manifest.json gecko.strict_min_version === "121.0"
// ---------------------------------------------------------------------------
test("RF-10: manifest.json sets gecko.strict_min_version to 121.0", () => {
  assert.equal(MANIFEST.browser_specific_settings.gecko.strict_min_version, "121.0");
});

// ---------------------------------------------------------------------------
// Finding 11: Persist held download IDs in storage.session and resume leftovers on SW start
// ---------------------------------------------------------------------------
test("RF-11: held paused download IDs are persisted in storage.session and leftover ones are resumed on service-worker restart", async () => {
  const sharedStorage = { local: {}, session: {} };
  let resolve91;

  const w1 = createReviewHarness({
    sharedStorage,
    nativeHandler(_host, msg) {
      if (msg.action === "ping") return { ok: true, version: "2.5.1", settings: {} };
      if (msg.action === "download") {
        if (msg.url.endsWith("one.zip")) {
          return new Promise((r) => { resolve91 = r; });
        }
        return new Promise(() => {}); // stays held when SW terminates
      }
      return { ok: true };
    }
  });
  await w1.sendMessage({ type: "get-status" });

  await w1.triggerDownload({ id: 91, url: "https://cdn.example.com/one.zip", filename: "one.zip" });
  await w1.triggerDownload({ id: 92, url: "https://cdn.example.com/two.zip", filename: "two.zip" });
  assert.deepEqual(sharedStorage.session.heldIds, [91, 92]);

  // Settle 91 (accepted), leaving only 92 held when w1 dies
  resolve91({ ok: true, accepted: true });
  await w1.flush();
  assert.deepEqual(sharedStorage.session.heldIds, [92]);

  // Cold-start Worker #2 -> must resume leftover download 92 and clear heldIds
  const w2 = createReviewHarness({ sharedStorage });
  await w2.flush();
  assert.deepEqual(w2.calls.resumed, [92], "service-worker startup must resume leftover paused downloads");
  assert.deepEqual(sharedStorage.session.heldIds, []);
});
