const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const BG_PATH = path.resolve(__dirname, "..", "background.js");
const CONTENT_PATH = path.resolve(__dirname, "..", "content.js");
const BG_CODE = fs.readFileSync(BG_PATH, "utf8");
const CONTENT_CODE = fs.readFileSync(CONTENT_PATH, "utf8");

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

function createV2Harness(options = {}) {
  const storageData = {
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
          if (Object.prototype.hasOwnProperty.call(store, keys)) {
            res[keys] = structuredClone(store[keys]);
          }
        } else if (Array.isArray(keys)) {
          for (const k of keys) {
            if (Object.prototype.hasOwnProperty.call(store, k)) {
              res[k] = structuredClone(store[k]);
            }
          }
        } else if (typeof keys === "object") {
          for (const [k, defVal] of Object.entries(keys)) {
            res[k] = Object.prototype.hasOwnProperty.call(store, k)
              ? structuredClone(store[k])
              : defVal;
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
    searched: [],
    downloaded: [],
    menusCreated: [],
    menusRemovedCount: 0,
    notifications: [],
    cookiesGetAll: [],
    badges: []
  };

  const downloadItems = new Map();
  let nativeHandler =
    options.nativeHandler ||
    ((host, message) => {
      if (message.action === "ping") {
        return {
          ok: true,
          version: "2.0.0",
          settings: options.settings || {}
        };
      }
      if (message.action === "add") {
        return { ok: true, accepted: true };
      }
      if (message.action === "download") {
        return { ok: true, accepted: true };
      }
      if (message.action === "downloadAll") {
        return { ok: true, accepted: true };
      }
      if (message.action === "media") {
        return { ok: true, accepted: true };
      }
      return { ok: true };
    });

  let cookiesByOrigin = options.cookiesByOrigin || {};
  let tabsById = options.tabsById || {};

  const events = {
    onBeforeRequest: makeEvent(),
    onResponseStarted: makeEvent(),
    onDownloadCreated: makeEvent(),
    onDeterminingFilename: makeEvent(),
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
      getURL(p) {
        return `chrome-extension://gdm-test-extension-id/${p}`;
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
              chrome.runtime.lastError = { message: String(err && err.message ? err.message : err) };
              cb(undefined);
              chrome.runtime.lastError = null;
            }
          );
      },
      sendMessage(msg, cb) {
        events.onMessage.emit(
          msg,
          { id: "gdm-test-extension-id", tab: { id: 1, url: "https://example.com/" } },
          (res) => {
            if (cb) cb(res);
          }
        );
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
        calls.cookiesGetAll.push(details);
        let origin = "";
        try {
          origin = new URL(details.url).origin;
        } catch (e) {
          origin = "";
        }
        const list = cookiesByOrigin[origin] || [];
        queueMicrotask(() => cb(structuredClone(list)));
      }
    },
    contextMenus: {
      removeAll(cb) {
        calls.menusRemovedCount += 1;
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
      executeScript(_injection, cb) {
        queueMicrotask(() => cb([{ result: [] }]));
      }
    },
    tabs: {
      get(tabId, cb) {
        const tab = tabsById[tabId];
        if (!tab) {
          chrome.runtime.lastError = { message: "No tab with id" };
          queueMicrotask(() => {
            cb(undefined);
            chrome.runtime.lastError = null;
          });
          return;
        }
        queueMicrotask(() => cb(structuredClone(tab)));
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

  const sandbox = {
    chrome,
    navigator: { userAgent: "GdmTestUA/2.0" },
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
    isFinite,
    decodeURIComponent,
    console,
    setTimeout: (fn, ms, ...args) => {
      const t = setTimeout(fn, ms, ...args);
      if (t && typeof t.unref === "function") t.unref();
      return t;
    },
    clearTimeout,
    setInterval: () => 0,
    clearInterval: () => {}
  };

  vm.createContext(sandbox);
  vm.runInContext(BG_CODE, sandbox, { filename: "background.js" });

  async function flush(turns = 12) {
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

  function createContentEnvironment(pageUrl = "https://example.com/torrents") {
    const u = new URL(pageUrl);
    const docListeners = new Map();
    const locationState = {
      hostname: u.hostname,
      pathname: u.pathname,
      href: pageUrl
    };

    const documentStub = {
      title: "Torrent Test Page",
      fullscreenElement: null,
      documentElement: {
        appendChild() {}
      },
      querySelectorAll() {
        return [];
      },
      addEventListener(type, fn) {
        if (!docListeners.has(type)) docListeners.set(type, []);
        docListeners.get(type).push(fn);
      }
    };

    const windowStub = {
      innerWidth: 1280,
      innerHeight: 720,
      location: locationState,
      document: documentStub,
      chrome: {
        ...chrome,
        runtime: {
          ...chrome.runtime,
          sendMessage(msg, cb) {
            events.onMessage.emit(
              msg,
              { id: "gdm-test-extension-id", tab: { id: 1, url: pageUrl } },
              (res) => {
                if (cb) cb(res);
              }
            );
          }
        }
      },
      addEventListener() {},
      requestAnimationFrame() {},
      getComputedStyle() {
        return { visibility: "visible", display: "block", opacity: "1" };
      },
      MutationObserver: class {
        observe() {}
      },
      setTimeout: sandbox.setTimeout,
      clearTimeout,
      setInterval: () => 0,
      clearInterval: () => {}
    };
    windowStub.window = windowStub;
    vm.createContext(windowStub);
    vm.runInContext(CONTENT_CODE, windowStub, { filename: "content.js" });

    return {
      location: locationState,
      dispatchClick(eventInit) {
        const handlers = docListeners.get("click") || [];
        for (const fn of handlers) fn(eventInit);
      },
      dispatchMouseDown(eventInit) {
        const handlers = docListeners.get("mousedown") || [];
        for (const fn of handlers) fn(eventInit);
      }
    };
  }

  return {
    sandbox,
    chrome,
    calls,
    events,
    storageData,
    downloadItems,
    flush,
    sendMessage,
    triggerDownload,
    createContentEnvironment
  };
}

// ---------------------------------------------------------------------------
// 1) Magnet link detection: valid btih / btmh vs invalid samples (>= 15 total)
// ---------------------------------------------------------------------------
test("V2-1) magnet link detection matches valid btih/btmh URIs and rejects invalid magnet/non-magnet samples", async () => {
  const h = createV2Harness();
  const isMagnetUrl = h.sandbox.isMagnetUrl;
  assert.equal(typeof isMagnetUrl, "function", "isMagnetUrl must be defined in background.js");

  const validSamples = [
    "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
    "magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&dn=Ubuntu+ISO",
    "magnet:?dn=Debian&xt=urn:btih:53800357b7984909f01a814f52b02a2cd6b90312&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337",
    "MAGNET:?XT=URN:BTIH:AEBAGBAFAYDQQCIKBMGA2DQPCAIREEYU",
    "magnet:?xt.1=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=MultiXt",
    "  magnet:?xt=urn:btih:11223344556677889900aabbccddeeff00112233  ",
    "magnet:?xt=urn:btmh:1220caf1e1c30e81cb361b9ee167c4aa64228a7fa4fa9f6105232b28ad099f3a302e",
    "magnet:?tr=https%3A%2F%2Ftracker.example.com%2Fannounce&dn=Hybrid+Torrent&xt=urn:btmh:1220deadbeefcafebabedeadbeefcafebabedeadbeefcafebabedeadbeefcafebabe",
    "magnet:?xl=104857600&dn=Archive.tar.gz&xt=urn:BtIh:aabbccddeeff00112233445566778899aabbccdd#section",
    "magnet:?as=https%3A%2F%2Fwebseed.example.org%2Ffile.iso&xt=urn:BtMh:12200102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"
  ];

  const invalidSamples = [
    "magnet:",
    "magnet:?",
    "magnet:?dn=OnlyDisplayName&tr=udp://tracker.example.com:80",
    "magnet:?xt=urn:sha1:YNCKHTQCWBTRNJIV4WNAE52SJUQCZO5C",
    "magnet:?xt=urn:ed2k:354B15E68FB8F36D7CD88FF94116CDC1",
    "magnet:?xt=urn:btih",
    "magnet:?xt=urn:btih:",
    "magnet:?xt=urn:btih:short",
    "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef0123456789",
    "magnet:?xt=urn:btih:7DZ23456789ABCDEF0123456789ABCDEF012",
    "magnet:?foo=bar#&xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
    "magnet://?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
    "https://example.com/?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
    "http://magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
    "javascript:alert('magnet:?xt=urn:btih:1234')",
    "",
    "   ",
    null,
    undefined
  ];

  assert.ok(validSamples.length + invalidSamples.length >= 20, "at least 15 valid + invalid samples required");

  for (const sample of validSamples) {
    assert.equal(isMagnetUrl(sample), true, `expected valid magnet to match: ${sample}`);
  }

  for (const sample of invalidSamples) {
    assert.equal(isMagnetUrl(sample), false, `expected invalid sample to be rejected: ${String(sample)}`);
  }

  // Also verify handleMessage("magnet-link") rejects invalid magnet URLs with bad-url
  await h.sendMessage({ type: "get-status" });
  const badRes = await h.sendMessage(
    { type: "magnet-link", url: "magnet:?dn=MissingXtUrn" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://example.com/" } }
  );
  assert.deepEqual(badRes, { ok: false, error: "bad-url" });
});

// ---------------------------------------------------------------------------
// 2) Context-menu magnet action: clicking gdm-link on magnet URL sends
//    { action: "add", url } without cookies/referrer/download fields
// ---------------------------------------------------------------------------
test("V2-2) context-menu gdm-link on magnet URL sends { action: 'add', url } without cookies or download fields", async () => {
  const h = createV2Harness({
    cookiesByOrigin: {
      "https://torrents.example.com": [{ name: "session", value: "secret123" }]
    }
  });

  const tab = { id: 3, url: "https://torrents.example.com/list", title: "Linux Torrents" };
  const magnetUri = "  magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Ubuntu+24.04  ";

  h.events.onMenuClicked.emit(
    { menuItemId: "gdm-link", linkUrl: magnetUri, pageUrl: tab.url },
    tab
  );
  await h.flush();

  const addMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "add");
  assert.equal(addMsgs.length, 1, "should send exactly one 'add' native message");
  assert.equal(addMsgs[0].host, "com.golan.gdm");
  assert.deepEqual(addMsgs[0].message, {
    action: "add",
    url: magnetUri.trim()
  });

  // Verify no download fields or cookie lookups were performed for magnet link
  assert.equal(Object.prototype.hasOwnProperty.call(addMsgs[0].message, "cookies"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(addMsgs[0].message, "referrer"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(addMsgs[0].message, "filename"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(addMsgs[0].message, "fileSize"), false);
  assert.equal(Object.prototype.hasOwnProperty.call(addMsgs[0].message, "userAgent"), false);
  assert.equal(h.calls.cookiesGetAll.length, 0);
});

// ---------------------------------------------------------------------------
// 3) Content-script click on a[href^="magnet:"]: intercepted when connected
//    and intercept enabled; prevented from default navigation; forwards
//    magnet-link to background -> { action: "add", url } to native host
// ---------------------------------------------------------------------------
test("V2-3) content-script click on a[href^='magnet:'] intercepts, prevents default, and forwards { action: 'add', url }", async () => {
  let connected = true;
  let appIntercept = true;
  const h = createV2Harness({
    nativeHandler(_host, message) {
      if (message.action === "ping") {
        if (!connected) return { __unreachable: true };
        return {
          ok: true,
          version: "2.0.0",
          settings: { intercept: appIntercept }
        };
      }
      if (message.action === "add") {
        return { ok: true, accepted: true };
      }
      return { ok: true };
    }
  });

  // Prime GDM connection state via get-status
  const status = await h.sendMessage({ type: "get-status" });
  assert.equal(status.connected, true);

  const content = h.createContentEnvironment("https://releases.example.com/iso");
  await h.flush();

  const btihMagnet = "magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01&dn=Fedora";
  const upperMagnet = "MAGNET:?XT=URN:BTIH:ABCDEF0123456789ABCDEF0123456789ABCDEF01&dn=UpperFedora";
  const makeMagnetAnchor = (href) => ({
    href,
    getAttribute(attr) {
      return attr === "href" ? href : null;
    },
    closest(selector) {
      if (selector === 'a[href^="magnet:" i]' && href.toLowerCase().startsWith("magnet:")) return this;
      if (selector === 'a[href^="magnet:"]' && href.startsWith("magnet:")) return this;
      if (selector === "a[href]") return this;
      return null;
    }
  });

  // Case A: Connected + intercept enabled -> intercepted, preventDefault + stopPropagation called, location.href unchanged
  let prevented = false;
  let stopped = false;
  content.dispatchClick({
    isTrusted: true,
    button: 0,
    altKey: false,
    ctrlKey: false,
    defaultPrevented: false,
    target: makeMagnetAnchor(btihMagnet),
    preventDefault() {
      prevented = true;
    },
    stopPropagation() {
      stopped = true;
    }
  });
  await h.flush();

  assert.equal(prevented, true, "click default navigation must be prevented");
  assert.equal(stopped, true, "click propagation must be stopped");
  assert.equal(content.location.href, "https://releases.example.com/iso", "page location must not navigate when intercepted");

  // Case A2 (T-2): Uppercase MAGNET:?XT=... anchor must also be intercepted via case-insensitive selector
  let upperPrevented = false;
  content.dispatchClick({
    isTrusted: true,
    button: 0,
    altKey: false,
    ctrlKey: false,
    defaultPrevented: false,
    target: makeMagnetAnchor(upperMagnet),
    preventDefault() {
      upperPrevented = true;
    },
    stopPropagation() {}
  });
  await h.flush();
  assert.equal(upperPrevented, true, "uppercase MAGNET: anchor must be intercepted");

  const addMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "add");
  assert.equal(addMsgs.length, 2);
  assert.deepEqual(addMsgs[0].message, {
    action: "add",
    url: btihMagnet
  });
  assert.deepEqual(addMsgs[1].message, {
    action: "add",
    url: upperMagnet
  });

  // Case B: Alt+Click (without Ctrl) bypasses interception
  let altPrevented = false;
  content.dispatchClick({
    isTrusted: true,
    button: 0,
    altKey: true,
    ctrlKey: false,
    defaultPrevented: false,
    target: makeMagnetAnchor(btihMagnet),
    preventDefault() {
      altPrevented = true;
    },
    stopPropagation() {}
  });
  await h.flush();
  assert.equal(altPrevented, false, "Alt+Click must not intercept magnet link");
  assert.equal(h.calls.nativeMessages.filter((m) => m.message.action === "add").length, 2);

  // Case C: Disconnected -> falls back to location.href = href
  connected = false;
  await h.sendMessage({ type: "get-status" }); // updates state.connected = false
  const btmhMagnet = "magnet:?xt=urn:btmh:1220caf1e1c30e81cb361b9ee167c4aa64228a7fa4fa9f6105232b28ad099f3a302e";
  let fallbackPrevented = false;
  content.dispatchClick({
    isTrusted: true,
    button: 0,
    altKey: false,
    ctrlKey: false,
    defaultPrevented: false,
    target: makeMagnetAnchor(btmhMagnet),
    preventDefault() {
      fallbackPrevented = true;
    },
    stopPropagation() {}
  });
  await h.flush();
  assert.equal(fallbackPrevented, true);
  assert.equal(content.location.href, btmhMagnet, "when GDM is disconnected, content script falls back to location.href");

  // Case D: Connected, but desktop settings.intercept === false -> falls back to location.href = href
  connected = true;
  appIntercept = false;
  await h.sendMessage({ type: "get-status" });
  content.location.href = "https://releases.example.com/iso";
  content.dispatchClick({
    isTrusted: true,
    button: 0,
    altKey: false,
    ctrlKey: false,
    defaultPrevented: false,
    target: makeMagnetAnchor(btihMagnet),
    preventDefault() {},
    stopPropagation() {}
  });
  await h.flush();
  assert.equal(content.location.href, btihMagnet, "when desktop intercept is disabled, content script falls back to location.href");
});

// ---------------------------------------------------------------------------
// 4) Non-magnet links unaffected: normal https:// links still go through the
//    existing download/video flow
// ---------------------------------------------------------------------------
test("V2-4) non-magnet https:// links remain unaffected across content script, context menu, and download/video flows", async () => {
  const h = createV2Harness({
    cookiesByOrigin: {
      "https://cdn.example.com": [{ name: "auth", value: "tok999" }]
    },
    tabsById: {
      10: { id: 10, url: "https://cdn.example.com/watch?v=42", title: "Sample Video" }
    }
  });
  await h.sendMessage({ type: "get-status" });

  const content = h.createContentEnvironment("https://cdn.example.com/downloads");
  await h.flush();

  const httpsUrl = "https://cdn.example.com/files/installer.exe";
  const httpsAnchor = {
    href: httpsUrl,
    getAttribute(attr) {
      return attr === "href" ? httpsUrl : null;
    },
    closest(selector) {
      if (selector === 'a[href^="magnet:"]' || selector === 'a[href^="magnet:" i]') return null;
      if (selector === "a[href]") return this;
      return null;
    }
  };

  // 1) Normal click on https:// link is NOT intercepted by magnet click handler
  let httpsClickPrevented = false;
  content.dispatchClick({
    isTrusted: true,
    button: 0,
    altKey: false,
    ctrlKey: false,
    defaultPrevented: false,
    target: httpsAnchor,
    preventDefault() {
      httpsClickPrevented = true;
    },
    stopPropagation() {}
  });
  await h.flush();
  assert.equal(httpsClickPrevented, false, "normal https:// click must not be prevented by magnet handler");

  // 2) Context menu gdm-link on https:// link sends { action: "download", ... } with cookies & referrer
  const tab = { id: 10, url: "https://cdn.example.com/watch?v=42", title: "Sample Video" };
  h.events.onMenuClicked.emit(
    { menuItemId: "gdm-link", linkUrl: httpsUrl, pageUrl: tab.url },
    tab
  );
  await h.flush();

  const dlMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "download");
  assert.equal(dlMsgs.length, 1);
  assert.equal(dlMsgs[0].message.url, httpsUrl);
  assert.equal(dlMsgs[0].message.referrer, tab.url);
  assert.equal(dlMsgs[0].message.cookies, "auth=tok999");
  assert.equal(dlMsgs[0].message.userAgent, "GdmTestUA/2.0");

  // 3) Browser download interception (onDownloadCreated) still pauses and sends action: "download"
  await h.triggerDownload({
    id: 401,
    url: "https://cdn.example.com/files/archive.zip",
    referrer: tab.url,
    filename: "archive.zip",
    mime: "application/zip",
    totalBytes: 204800
  });
  assert.deepEqual(h.calls.paused, [401]);
  assert.deepEqual(h.calls.cancelled, [401]);
  const allDlMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "download");
  assert.equal(allDlMsgs.length, 2);
  assert.equal(allDlMsgs[1].message.url, "https://cdn.example.com/files/archive.zip");

  // 4) Video flow (media-from-page) still sends action: "media"
  const mediaRes = await h.sendMessage(
    { type: "media-from-page", title: "Sample Video", videoSrc: "https://cdn.example.com/stream.mp4" },
    { id: "gdm-test-extension-id", tab }
  );
  assert.equal(mediaRes.ok, true);
  const mediaMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "media");
  assert.equal(mediaMsgs.length, 1);
  assert.equal(mediaMsgs[0].message.pageUrl, tab.url);
  assert.equal(mediaMsgs[0].message.mediaUrls[0].url, "https://cdn.example.com/stream.mp4");
});

// ---------------------------------------------------------------------------
// 5) MEDIUM-04: Cross-extension sender rejected & cross-site link-keys force
//    cannot bypass exceptionSites
// ---------------------------------------------------------------------------
test("V2-5) MEDIUM-04: handleMessage rejects foreign sender.id and blocks cross-site link-keys force from bypassing exceptionSites", async () => {
  const h = createV2Harness({
    nativeHandler(_host, message) {
      if (message.action === "ping") {
        return {
          ok: true,
          version: "2.0.0",
          settings: {
            intercept: true,
            exceptionSites: ["bank.example.com"]
          }
        };
      }
      if (message.action === "download") {
        return { ok: true, accepted: true };
      }
      return { ok: true };
    }
  });
  await h.sendMessage({ type: "get-status" });

  // Foreign extension sender.id is rejected
  const foreignRes = await h.sendMessage(
    { type: "download-url", url: "https://cdn.example.com/file.zip" },
    { id: "foreign-extension-id" }
  );
  assert.deepEqual(foreignRes, { ok: false, error: "forbidden-sender" });

  // Cross-site content script sends link-keys with force: true from evil.attacker.org
  await h.sendMessage(
    { type: "link-keys", href: "https://bank.example.com/statement.zip", alt: true, ctrl: true, force: true },
    { id: "gdm-test-extension-id", tab: { id: 9, url: "https://evil.attacker.org/trap" } }
  );

  await h.triggerDownload({
    id: 501,
    url: "https://bank.example.com/statement.zip",
    referrer: "https://evil.attacker.org/trap",
    filename: "statement.zip",
    mime: "application/zip",
    totalBytes: 4096
  });

  const dlMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "download");
  assert.equal(dlMsgs.length, 0, "cross-site link-keys force must NOT bypass exceptionSites");
});

